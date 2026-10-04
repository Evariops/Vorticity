using System;
using System.Buffers;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scanning;

/// <summary>
/// A compiled projection: the set of leaf paths a scan will materialize, as a
/// <see cref="FieldMask"/> tree over the file schema.
/// </summary>
/// <remarks>
/// <para>
/// <c>default(ScanProjection)</c> is <see cref="All"/>, matching <c>default(FieldMask)</c>: a scan
/// handed a default projection materializes everything, which is slower than intended but never
/// wrong. The opposite default would silently drop columns.
/// </para>
/// <para>
/// A projection is compiled once per scan, against the file's schema, and never touched again on a
/// decode path, so compiling allocates freely; the result is a small immutable tree whose size
/// follows the projection and never the row count.
/// </para>
/// <para>
/// The dotted grammar has no escaping syntax, and a field name may itself contain a <c>.</c> or be
/// empty, so it cannot address every column; naming fields by index does. Nor does a projection
/// ever reorder: <see cref="ProjectedSchema"/> keeps the schema's own field order and nullability,
/// because a reader that reordered columns would disagree with every other Vortex reader about
/// which one is column zero.
/// </para>
/// </remarks>
internal readonly struct ScanProjection
{
    private readonly FieldMask _mask;
    private readonly int _leafCount;

    private ScanProjection(FieldMask mask, int leafCount)
    {
        _mask = mask;
        _leafCount = leafCount;
    }

    /// <summary>Every field of every struct, recursively. Also <c>default(ScanProjection)</c>.</summary>
    public static ScanProjection All => default;

    /// <summary>
    /// Compiles <paramref name="paths"/> against <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The file's schema, normally <see cref="VortexFile.DType"/>.</param>
    /// <param name="paths">
    /// <c>.</c>-separated field names, matching the sidecar's <c>null_counts</c> paths - <c>"id"</c>,
    /// <c>"payload.size"</c>. An empty list is <see cref="All"/>: naming nothing narrows nothing.
    /// </param>
    /// <returns>The compiled projection.</returns>
    /// <exception cref="ArgumentException">
    /// A path does not resolve against <paramref name="schema"/>, descends through a non-struct,
    /// or names a field of a non-struct root. The message names the offending path. This is a
    /// caller error, not a malformed file.
    /// </exception>
    /// <exception cref="ArgumentNullException">A path is <see langword="null"/>.</exception>
    public static ScanProjection Parse(DType schema, ReadOnlySpan<string> paths)
    {
        if (paths.Length == 0)
        {
            return All;
        }

        FieldMaskBuilder builder = new FieldMaskBuilder();
        for (int i = 0; i < paths.Length; i++)
        {
            IncludePath(schema, paths[i], builder, nameof(paths));
        }

        return Create(builder.Build());
    }

    /// <summary><see langword="true"/> when nothing is narrowed.</summary>
    public bool IsAll => _mask.IsAll;

    /// <summary>
    /// How many distinct leaf paths this projection names, or <c>-1</c> when it is
    /// <see cref="All"/> and the count depends on the schema it is applied to. The <c>-1</c>
    /// spelling matches <see cref="FieldMask.NamedFieldCount"/>, which uses it for the same reason.
    /// </summary>
    public int LeafCount => _mask.IsAll ? -1 : _leafCount;

    /// <summary>The mask handed to the root layout reader.</summary>
    public FieldMask RootMask => _mask;

    /// <summary>
    /// The dtype of the batch a scan with this projection produces: <paramref name="schema"/> with
    /// unselected fields removed, preserving field order and nullability.
    /// </summary>
    /// <param name="schema">The file's schema.</param>
    /// <param name="arena">The arena to build the result in; normally <c>ScanContext.Types</c>.</param>
    /// <returns>The projected schema, a node of <paramref name="arena"/>.</returns>
    /// <remarks>
    /// This mirrors <c>StructLayoutReader.Execute</c> exactly: a whole (<see cref="FieldMask.All"/>)
    /// mask keeps the node's own dtype, and any narrowing rebuilds the struct from the selected
    /// fields in schema order. The two must agree, because the batch's schema is what the reader
    /// produced and this is what a caller was promised.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is <c>default</c>.</exception>
    public DType ProjectedSchema(DType schema, DTypeArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (schema.IsDefault)
        {
            throw new ArgumentException("The schema has not been read.", nameof(schema));
        }

        return Project(arena, schema, in _mask, depth: 1);
    }

    /// <summary>Wraps a mask a builder produced, counting its leaves once.</summary>
    /// <param name="mask">The compiled mask.</param>
    /// <returns>The projection.</returns>
    internal static ScanProjection Create(FieldMask mask) => new ScanProjection(mask, CountLeaves(in mask, depth: 1));

    /// <summary>
    /// Resolves one dotted path against <paramref name="schema"/> and adds it to
    /// <paramref name="builder"/>.
    /// </summary>
    /// <param name="schema">The schema to resolve against.</param>
    /// <param name="path">The dotted path.</param>
    /// <param name="builder">The mask under construction.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    internal static void IncludePath(DType schema, string path, FieldMaskBuilder builder, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        ArgumentNullException.ThrowIfNull(builder);

        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            ScanThrow.NonStructRoot(parameterName);
        }

        // MaxDTypeDepth bounds the schema, so it bounds any path that resolves against one: a
        // longer path must have failed on a leaf before it got here. The bound check below is
        // therefore defensive - it is what keeps a hostile path from writing past this buffer if
        // that reasoning ever stops holding.
        Span<int> indices = stackalloc int[VortexLimits.MaxDTypeDepth];
        int count = 0;

        // One rented byte buffer for the whole path: DType.IndexOfField matches UTF-8, and
        // transcoding a substring per segment would allocate a string per segment.
        int maxBytes = Encoding.UTF8.GetMaxByteCount(path.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes == 0 ? 1 : maxBytes);
        try
        {
            DType current = schema;
            int start = 0;
            while (true)
            {
                int dot = path.IndexOf('.', start);
                int end = dot < 0 ? path.Length : dot;
                ReadOnlySpan<char> segment = path.AsSpan(start, end - start);

                if (current.IsDefault || current.Kind != DTypeKind.Struct)
                {
                    ScanThrow.PathThroughLeaf(path, new string(segment), parameterName);
                }

                int written = Encoding.UTF8.GetBytes(segment, rented);
                int field = current.IndexOfField(new ReadOnlySpan<byte>(rented, 0, written));
                if (field < 0)
                {
                    ScanThrow.UnknownPath(path, parameterName);
                }

                if (count == indices.Length)
                {
                    ScanThrow.PathTooDeep(path, parameterName);
                }

                indices[count++] = field;
                current = current.GetField(field);

                if (dot < 0)
                {
                    break;
                }

                start = dot + 1;
            }

            builder.Include(indices[..count]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Resolves a column reference by its name segments and adds it to <paramref name="builder"/>:
    /// a name that holds a dot resolves, since the segments were never split from a path.
    /// </summary>
    internal static void IncludeField(DType schema, Expressions.FieldExpr field, FieldMaskBuilder builder, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(field, parameterName);
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            ScanThrow.NonStructRoot(parameterName);
        }

        byte[][] segments = field.SegmentsUtf8;
        Span<int> indices = segments.Length <= VortexLimits.MaxDTypeDepth ? stackalloc int[segments.Length] : new int[segments.Length];
        DType current = schema;
        for (int i = 0; i < segments.Length; i++)
        {
            while (!current.IsDefault && current.Kind == DTypeKind.Extension)
            {
                current = current.StorageType;
            }

            if (current.IsDefault || current.Kind != DTypeKind.Struct)
            {
                ScanThrow.PathThroughLeaf(field.Path, Encoding.UTF8.GetString(segments[i]), parameterName);
            }

            int index = current.IndexOfField(segments[i]);
            if (index < 0)
            {
                ScanThrow.UnknownPath(field.Path, parameterName);
            }

            indices[i] = index;
            current = current.GetField(index);
        }

        builder.Include(indices);
    }

    private static int CountLeaves(in FieldMask mask, int depth)
    {
        if (mask.IsAll)
        {
            return 1;
        }

        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Projection");

        int named = mask.NamedFieldCount;
        int total = 0;
        for (int i = 0; i < named; i++)
        {
            FieldMask child = mask.Descend(mask.GetNamedField(i));
            total += CountLeaves(in child, depth + 1);
        }

        return total;
    }

    private static DType Project(DTypeArena arena, DType dtype, in FieldMask mask, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Projection");

        // A mask names struct fields; anything else it reaches is a leaf and travels whole. This is
        // the same rule MaskProjection.Apply follows on the decoded side.
        if (mask.IsAll || dtype.IsDefault || dtype.Kind != DTypeKind.Struct)
        {
            return DTypeImport.Into(arena, dtype);
        }

        int selected = mask.SelectedCount(dtype.FieldCount);
        int[] names = new int[selected];
        DType[] fields = new DType[selected];
        for (int s = 0; s < selected; s++)
        {
            int i = mask.SelectedField(s);
            FieldMask child = mask.SelectedMask(s);
            names[s] = arena.InternName(dtype.GetFieldNameUtf8(i));
            fields[s] = Project(arena, dtype.GetField(i), in child, depth + 1);
        }

        return arena.Struct(new ReadOnlySpan<int>(names), new ReadOnlySpan<DType>(fields), dtype.Nullability);
    }
}
