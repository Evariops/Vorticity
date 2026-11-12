using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Vorticity.Types.Variants;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.variant</c>: a logical variant column over Variant-typed storage, and a second
/// child, when there is one, holding paths shredded out of it. The storage child carries the whole
/// of an unshredded variant's work, so this decoder only checks the shape it produced. A shredded
/// child is put back into the storage's values (<see cref="ShreddedTree"/>), so a variant reads the
/// same shredded or not.
/// </summary>
internal sealed class VariantDecoder : ArrayDecoder
{
    private const string Id = "vortex.variant";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly VariantDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.variant"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Variant;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, ChildRows.Whole, 0, length);
    }

    /// <summary>
    /// A range of the rows is its storage's range and its shredded child's, put back together, when both decode one: the
    /// flat reader then decodes a chunk a window at a time. Decoded whole, a chunk of a log's attributes, half a million rows
    /// and a run-end column of views for each of its fields, held one input of a merge at 8 GB.
    /// </summary>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ChildrenDecodeRange(in node);
    }

    /// <inheritdoc/>
    public override int DecodeRange(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, ChildRows.Range, start, count);
    }

    /// <summary>
    /// The logical spelling is transparent to a take: it hands back its one child's node with a
    /// shape check and nothing else, so the rows it is asked for are the rows the child is asked for.
    /// </summary>
    /// <remarks>
    /// Without this the default route would decode the whole node and then gather, expanding every
    /// row of the storage to deliver a handful. The shape check runs against the selected length,
    /// because that is the node's length on this path; everything above it is the same code, which
    /// is why both routes share one core.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, ChildRows.Selected, 0, wanted.Length);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, ChildRows rows, int start, int count)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        VariantMetadata metadata = VariantMetadata.Read(node.Metadata);
        bool residual = context.TakeResidual();

        if (dtype.Kind != DTypeKind.Variant)
        {
            CompressedThrow.Format($"{Id} requires a variant dtype; the node declares {dtype.Kind}.");
        }

        ArrayDecodeContext.RequireChildCount(node.ChildCount, metadata.HasShreddedDType ? 2 : 1, Id);

        // The core storage wears the same dtype as the node, so the child decodes at the variant
        // dtype and whatever encoding it uses has to produce the `Struct{metadata, value}` form.
        // An encoding that does not is refused below rather than reinterpreted.
        int core = context.DecodeChildRows(in node, 0, dtype, length, start, count, wanted, rows);
        CanonicalNode storage = context.Canonical.GetNode(core);
        VariantStorage.Require(storage, count, Id);
        if (!metadata.HasShreddedDType)
        {
            return core;
        }

        // A chunk read for keys its shredding does not hold gives its residual alone, where such a key lies and nowhere
        // else: putting the shredded keys back, row by row, took two thirds of a question over one key of the residual.
        DType shreddedType = DTypeProtobuf.Read(metadata.ShreddedDType, context.Types);

        // A census of the paths takes the shredding as it lies, the standard's group of the storage and its typed columns: putting
        // a log's four hundred keys back, row by row, cost what reading an edge granule again cost (Types.Variants.ApartCensus).
        if (context.Scan.VariantsApart)
        {
            // The column's own shredding alone: a key's values of several forms, a variant of its own below, put back as they are.
            // Of its keys, those the read names alone, a projection the struct of them takes: no row read holds the others.
            Layouts.FieldMask keys = context.Scan.ApartKeys is { } named && shreddedType.Kind == DTypeKind.Struct
                && node.GetChild(1).Encoding == ArrayEncodingId.Struct
                ? named.MaskOf(shreddedType)
                : Layouts.FieldMask.All;
            Layouts.FieldMask pushed = keys.IsAll ? keys : context.Scan.ExchangePushedFields(keys);
            bool honoured = !keys.IsAll && context.Scan.FieldsHonoured;
            int apart;
            context.Scan.VariantsApart = false;
            try
            {
                apart = context.DecodeChildRows(in node, 1, shreddedType, length, start, count, wanted, rows);
            }
            finally
            {
                context.Scan.VariantsApart = true;
                if (!keys.IsAll)
                {
                    context.Scan.ExchangePushedFields(pushed);
                    context.Scan.FieldsHonoured = honoured;
                }
            }

            return ShreddedTree.Group(context, dtype, core, apart, out _);
        }

        if (residual && context.Scan.VariantKeys is { } reads && reads.LeavesUnshredded(shreddedType))
        {
            context.Scan.Metrics?.AddResidualChunk();
            return core;
        }

        bool sliceable = rows == ChildRows.Selected
            ? context.ChildSelectsWithoutFullDecode(in node, 1, shreddedType)
            : context.ChildDecodesRange(in node, 1);
        if (sliceable && ShreddedSlices.TryBegin(context, count, shreddedType, ShreddedTree.Malformed, out ShreddedSlices slices))
        {
            try
            {
                for (int at = 0; at < count; at += slices.Rows)
                {
                    int part = Math.Min(slices.Rows, count - at);
                    int coreSlice = slices.Narrow(context, core, at, part);
                    int shreddedSlice = rows == ChildRows.Selected
                        ? context.DecodeChildSelected(in node, 1, shreddedType, length, wanted.Slice(at, part))
                        : context.DecodeChildRange(in node, 1, shreddedType, length, start + at, part);
                    int group = ShreddedTree.Group(context, dtype, coreSlice, shreddedSlice, out DType groupType);
                    slices.Write(context, group, groupType, at);
                }

                return slices.Complete(dtype, storage.GetFieldIndex(0), storage.GetFieldIndex(1), storage.Validity);
            }
            finally
            {
                slices.End(context);
            }
        }

        int shredded = context.DecodeChildRows(in node, 1, shreddedType, length, start, count, wanted, rows);
        return ShreddedTree.Assemble(context, dtype, core, shredded);
    }
}

/// <summary>
/// A shredded variant's rows rebuilt a slice at a time: its shredded columns decoded for a slice of the rows in an arena
/// the scan clears between slices (<see cref="ScanContext.BeginSlice"/>), and each slice's values written after the last
/// one's, so that what a decode builds to throw away is a slice of a wide shredding and not every row of it. The columns
/// left whole -- the metadata, the residual values, the validity -- are decoded for every row first, and each slice reads
/// a window of them.
/// </summary>
/// <remarks>
/// The rebuild reads a shredded column a view or a value a row, so a column a run-end or a dictionary holds in a few bytes
/// is expanded for it: a log's attributes shredded into some four hundred keys came to seven kilobytes a row, and the
/// window of 131 072 rows a merge's input decodes to 900 MB built and thrown away.
/// </remarks>
internal ref struct ShreddedSlices
{
    /// <summary>A slice is whole blocks of this many rows, a FastLanes block's.</summary>
    private const int BlockRows = 1024;

    private const int ViewSize = CanonicalSupport.ViewSize;

    private readonly CanonicalArena _destination;
    private readonly VortexBuffer _views;
    private readonly Span<byte> _into;
    private readonly int _rows;
    private readonly string _refusal;
    private ShreddedVariant? _plan;
    private DType _planned;

    private ShreddedSlices(CanonicalArena destination, VortexBuffer views, Span<byte> into, int rows, int slice, string refusal)
    {
        _destination = destination;
        _views = views;
        _into = into;
        _rows = rows;
        _refusal = refusal;
        Rows = slice;
    }

    /// <summary>The rows of a slice, the last one's at most.</summary>
    internal int Rows { get; }

    /// <summary>The arena the rows are rebuilt into, where the columns left whole lie.</summary>
    internal readonly CanonicalArena Destination => _destination;

    /// <summary>
    /// Begins rebuilding the <paramref name="rows"/> rows of a variant shredded into <paramref name="typed"/> a slice at a
    /// time, when they are more than a slice holds and the scan can redirect its arena; the decode builds them whole
    /// otherwise.
    /// </summary>
    /// <param name="context">The decode context, whose arena the rows are rebuilt into.</param>
    /// <param name="rows">The rows the decode produces.</param>
    /// <param name="typed">The type of the columns shredded out of the variant.</param>
    /// <param name="refusal">What a malformed shredding is refused as.</param>
    /// <param name="slices">The slices, to be ended by <see cref="End"/>.</param>
    internal static bool TryBegin(ArrayDecodeContext context, int rows, DType typed, string refusal, out ShreddedSlices slices)
    {
        slices = default;
        int slice = SliceRows(typed, context.Scan.Options.VariantSliceBytes);
        if (rows <= slice)
        {
            return false;
        }

        CanonicalArena destination = context.Canonical;
        VortexBuffer views = destination.AllocateUninitialized(checked(rows * ViewSize), 64, out Span<byte> into);
        if (!context.Scan.BeginSlice())
        {
            return false;
        }

        slices = new ShreddedSlices(destination, views, into, rows, slice, refusal);
        return true;
    }

    /// <summary>The destination's node <paramref name="node"/>, a column left whole, narrowed to a slice's rows in the slice's arena.</summary>
    internal readonly int Narrow(ArrayDecodeContext context, int node, int at, int count) =>
        Layouts.CanonicalSlice.SliceAcross(_destination, context.Canonical, node, at, count);

    /// <summary>
    /// Writes the values of the slice whose group node, in the slice's arena, is <paramref name="group"/> of
    /// <paramref name="type"/>, the slice starting at row <paramref name="at"/>, and clears the slice's arena.
    /// </summary>
    internal void Write(ArrayDecodeContext context, int group, DType type, int at)
    {
        CanonicalArena arena = context.Canonical;
        try
        {
            if (_plan is null)
            {
                _plan = ShreddedVariant.For(context.Scan, type);
                _plan.BeginSlices();
                _planned = type;
            }
            else if (type != _planned)
            {
                throw new VortexFormatException("a slice of its rows is shredded otherwise than the one before.");
            }

            int rows = arena.GetNode(group).Length;
            _plan.WriteSlice(arena, group, _into.Slice(at * ViewSize, rows * ViewSize));
        }
        catch (VortexFormatException malformed)
        {
            CompressedThrow.Format($"{_refusal}: {malformed.Message}");
            throw;
        }

        context.Scan.NextSlice();
        context.Scan.Metrics?.AddVariantSlice();
    }

    /// <summary>
    /// The variant node of the rows the slices rebuilt, in the destination: a struct of the <paramref name="metadata"/>
    /// column and of the values, valid as <paramref name="validity"/> says, over the buffers of the residual
    /// <paramref name="value"/> column when there is one (-1 otherwise).
    /// </summary>
    internal readonly int Complete(DType variant, int metadata, int value, Validity validity)
    {
        ReadOnlySpan<VortexBuffer> buffers = value >= 0 ? _destination.GetNode(value).DataBuffers : default;
        Span<int> fields = stackalloc int[2];
        fields[0] = metadata;
        fields[1] = _plan!.CompleteSlices(_destination, _rows, _views, buffers);
        return _destination.AddStruct(variant, _rows, validity, fields);
    }

    /// <summary>Ends the slices however they ended: the plan's scratch given back, the scan's arena redirected back.</summary>
    internal readonly void End(ArrayDecodeContext context)
    {
        _plan?.EndSlices();
        context.Scan.EndSlice();
    }

    /// <summary>The rows of a slice of a shredding of <paramref name="typed"/>: whole blocks, as many as <paramref name="bytes"/> reads, one at least.</summary>
    private static int SliceRows(DType typed, long bytes)
    {
        long rows = bytes / Math.Max(ViewSize, RowBytes(typed));
        return (int)Math.Max(BlockRows, Math.Min(rows, int.MaxValue) / BlockRows * BlockRows);
    }

    /// <summary>
    /// The bytes a row of a shredded column of <paramref name="type"/> is read in, about: a view or a value for each column,
    /// two views for a nested variant, whose own shredding the type does not tell, and a list's offset beside an element.
    /// </summary>
    private static long RowBytes(DType type)
    {
        switch (type.Kind)
        {
            case DTypeKind.Struct:
            {
                long bytes = 0;
                for (int i = 0; i < type.FieldCount; i++)
                {
                    bytes += RowBytes(type.GetField(i));
                }

                return bytes;
            }

            case DTypeKind.List:
                return ViewSize + RowBytes(type.ElementType);

            case DTypeKind.Extension:
                return RowBytes(type.StorageType);

            case DTypeKind.Variant:
                return 2 * ViewSize;

            default:
                return ViewSize;
        }
    }
}

/// <summary>
/// The shredded child of a <c>vortex.variant</c> put back into its core storage's values. The child
/// is the tree the reference builds from a Parquet shredding: an object is a struct by field name,
/// an array a list, a primitive its typed column, and a field that kept values of its own beside its
/// typed column is a nested variant, which its own decoder has already put back together.
/// </summary>
/// <remarks>
/// Each node of the tree maps onto the group the Parquet standard shreds into, a value and a typed
/// value: a nested variant is a group of its value alone, valid where the variant is, and any other
/// node a group of it as the typed value. The core storage's value is the residual of the variant's
/// own group. The rows are then rebuilt by the plan the Parquet reader uses
/// (<see cref="ShreddedVariant"/>), which no row of this mapping is copied for: the new nodes share
/// the decoded ones' buffers. A nested variant's values are read against the variant's metadata,
/// which the reference gives every field of an object.
/// </remarks>
internal static class ShreddedTree
{
    /// <summary>What a shredded child no shredding builds is refused as.</summary>
    internal const string Malformed = Id + "'s shredded child is not what a shredding builds";

    private const string Id = "vortex.variant";

    /// <summary>Fields a struct holds before its scratch goes to the heap.</summary>
    private const int StackFields = 32;

    /// <summary>The variant node of the core storage <paramref name="core"/> and the shredded tree <paramref name="shredded"/>.</summary>
    /// <param name="context">The decode context both were decoded in.</param>
    /// <param name="variant">The variant dtype the result wears.</param>
    /// <param name="core">The core storage, <c>Struct{metadata, value}</c>.</param>
    /// <param name="shredded">The shredded tree, of the same rows.</param>
    /// <returns>A <c>Struct{metadata, value}</c> node wearing <paramref name="variant"/>.</returns>
    /// <exception cref="VortexFormatException">The tree is not one a shredding builds.</exception>
    internal static int Assemble(ArrayDecodeContext context, DType variant, int core, int shredded)
    {
        int node = Group(context, variant, core, shredded, out DType group);
        try
        {
            return ShreddedVariant.For(context.Scan, group).Assemble(context.Canonical, variant, node);
        }
        catch (VortexFormatException malformed)
        {
            CompressedThrow.Format($"{Malformed}: {malformed.Message}");
            throw;
        }
    }

    /// <summary>
    /// The group the Parquet standard shreds a variant into, of the core storage <paramref name="core"/> and the shredded
    /// tree <paramref name="shredded"/>: a struct of the storage's metadata and value and of the tree as the typed value,
    /// valid where the storage is.
    /// </summary>
    /// <param name="context">The decode context both were decoded in.</param>
    /// <param name="variant">The variant dtype the rows wear.</param>
    /// <param name="core">The core storage, <c>Struct{metadata, value}</c>.</param>
    /// <param name="shredded">The shredded tree, of the same rows.</param>
    /// <param name="group">The group's type, which the plan rebuilding its rows is compiled from.</param>
    /// <returns>The group node.</returns>
    internal static int Group(ArrayDecodeContext context, DType variant, int core, int shredded, out DType group)
    {
        CanonicalArena arena = context.Canonical;
        DTypeArena types = context.Types;
        CanonicalNode storage = arena.GetNode(core);
        if (arena.GetNode(shredded).Length != storage.Length)
        {
            CompressedThrow.ChildLength(Id, "shredded", arena.GetNode(shredded).Length, storage.Length);
        }

        int metadata = storage.GetFieldIndex(0);
        int value = storage.GetFieldIndex(1);
        int typed = Typed(arena, types, shredded, depth: 1, out DType typedType);
        group = types.Struct(
            ["metadata", "value", "typed_value"],
            [arena.GetNode(metadata).DType, arena.GetNode(value).DType, typedType],
            variant.Nullability);
        Span<int> fields = [metadata, value, typed];
        return arena.AddStruct(group, storage.Length, storage.Validity, fields);
    }

    /// <summary>The group a node of the tree stands for: of its value alone when it is a nested variant, of it as the typed value otherwise.</summary>
    private static int Group(CanonicalArena arena, DTypeArena types, int node, int depth, out DType group)
    {
        node = Plain(arena, node);
        CanonicalNode n = arena.GetNode(node);
        if (n.DType.Kind == DTypeKind.Variant)
        {
            VariantStorage.Require(n, n.Length, Id);
            int value = n.GetFieldIndex(1);
            group = types.Struct(["value"], [arena.GetNode(value).DType], Nullability.Nullable);
            return arena.AddStruct(group, n.Length, n.Validity, [value]);
        }

        int typed = Typed(arena, types, node, depth, out DType typedType);
        group = types.Struct(["typed_value"], [typedType], Nullability.Nullable);
        return arena.AddStruct(group, n.Length, Validity.FromNullability(Nullability.Nullable), [typed]);
    }

    /// <summary>The typed value a node of the tree is: an object's fields and an array's elements each made a group, a primitive as it is.</summary>
    private static int Typed(CanonicalArena arena, DTypeArena types, int node, int depth, out DType typed)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Shredded variant");
        node = Plain(arena, node);
        CanonicalNode n = arena.GetNode(node);
        switch (n.DType.Kind)
        {
            case DTypeKind.Struct:
            {
                int count = n.FieldCount;
                Span<int> fieldStack = stackalloc int[StackFields];
                Span<int> nameStack = stackalloc int[StackFields];
                using Scratch<int> fields = new(count, fieldStack);
                using Scratch<int> names = new(count, nameStack);
                // The groups' types rented: the arena copies them, and an array of them a batch was 7 MB of a read of
                // 65,536 rows of 200 shredded keys.
                DType[] groups = ArrayPool<DType>.Shared.Rent(count);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        fields.Span[i] = Group(arena, types, n.GetFieldIndex(i), depth + 1, out groups[i]);
                        names.Span[i] = types.InternName(n.DType.GetFieldNameUtf8(i));
                    }

                    typed = types.Struct(names.Span, groups.AsSpan(0, count), n.DType.Nullability);
                }
                finally
                {
                    ArrayPool<DType>.Shared.Return(groups, clearArray: true);
                }

                return arena.AddStruct(typed, n.Length, n.Validity, fields.Span);
            }

            case DTypeKind.List when n.Kind == CanonicalKind.ListView:
            {
                int elements = Group(arena, types, n.ElementsIndex, depth + 1, out DType element);
                typed = types.List(element, n.DType.Nullability);
                VortexBuffer offsets = Int32s(arena, n.Offsets, n.OffsetPType, n.Length);
                VortexBuffer sizes = Int32s(arena, n.Sizes, n.SizePType, n.Length);
                return arena.AddListView(typed, n.Length, n.Validity, elements, offsets, PType.I32, sizes, PType.I32);
            }

            default:
                typed = n.DType;
                return node;
        }
    }

    /// <summary>
    /// A list's offsets or sizes as the i32 the plan reads: the buffer itself when they are, a copy
    /// otherwise, since a writer picks the narrowest type that holds them.
    /// </summary>
    private static VortexBuffer Int32s(CanonicalArena arena, VortexBuffer source, PType ptype, int rows) => ptype switch
    {
        PType.I32 => source,
        PType.U8 => Int32s<byte>(arena, source, rows),
        PType.U16 => Int32s<ushort>(arena, source, rows),
        PType.U32 => Int32s<uint>(arena, source, rows),
        PType.U64 => Int32s<ulong>(arena, source, rows),
        PType.I8 => Int32s<sbyte>(arena, source, rows),
        PType.I16 => Int32s<short>(arena, source, rows),
        PType.I64 => Int32s<long>(arena, source, rows),
        _ => throw new VortexFormatException($"A shredded list's offsets are of {ptype}, not of integers."),
    };

    private static VortexBuffer Int32s<T>(CanonicalArena arena, VortexBuffer source, int rows)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source.Span)[..rows];
        VortexBuffer buffer = arena.AllocateUninitialized(rows * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> into = MemoryMarshal.Cast<byte, int>(bytes);
        for (int i = 0; i < values.Length; i++)
        {
            // A u64 past the signed range wraps negative here and is refused with the rest.
            long value = long.CreateTruncating(values[i]);
            if ((ulong)value > int.MaxValue)
            {
                throw new VortexFormatException($"A shredded list's offset {values[i]} is past what a list of elements holds.");
            }

            into[i] = (int)value;
        }

        return buffer;
    }

    /// <summary>The node in a form whose rows a plan reads in place: a dictionary, a run-end or a constant expanded.</summary>
    private static int Plain(CanonicalArena arena, int node)
    {
        node = arena.Decoded(node);
        return arena.GetNode(node).Kind == CanonicalKind.Constant ? arena.MaterializeConstant(node) : node;
    }
}

/// <summary>
/// Decodes <c>vortex.parquet.variant</c>: the Arrow-compatible binary spelling, a binary child
/// holding each row's variant metadata, and a binary child of its value, a typed child of the paths
/// shredded out of it, or both. A shredded variant's rows are put back together as the Parquet
/// standard's <c>construct_variant</c> builds them (<see cref="ShreddedVariant"/>), so a variant reads
/// the same shredded or not.
/// </summary>
internal sealed class ParquetVariantDecoder : ArrayDecoder
{
    private const string Id = "vortex.parquet.variant";

    private const string Malformed = Id + "'s shredded children are not what the Parquet standard allows";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ParquetVariantDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.parquet.variant"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.ParquetVariant;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, ChildRows.Whole, 0, length);
    }

    /// <summary>
    /// A range is each child's range, put back together as a whole node is, so a chunk is read window by window instead
    /// of whole, the same as <see cref="VariantDecoder"/>.
    /// </summary>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ChildrenDecodeRange(in node);
    }

    /// <inheritdoc/>
    public override int DecodeRange(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, ChildRows.Range, start, count);
    }

    /// <summary>
    /// The selection goes into the two binary children, which is where the work is.
    /// </summary>
    /// <remarks>
    /// Nothing here is per-row: the node is a struct of two binary columns, so pushing the wanted
    /// rows down is the whole of the work and the saving is the children's, which then build a view
    /// per delivered row instead of one per row of the node.
    /// <para>
    /// This is only safe because the binary child decoder is specialized too. Declaring
    /// <see cref="SelectsWithoutFullDecode"/> takes the pushed route, which has no retained-chunk
    /// cache, so an unspecialized child on it would decode its whole node once per batch and make
    /// the take quadratic. That pairing spans two decoders, so no unit test can see it.
    /// </para>
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, ChildRows.Selected, 0, wanted.Length);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, ChildRows rows, int start, int count)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ParquetVariantMetadata metadata = ParquetVariantMetadata.Read(node.Metadata);

        if (dtype.Kind != DTypeKind.Variant)
        {
            CompressedThrow.Format($"{Id} requires a variant dtype; the node declares {dtype.Kind}.");
        }

        if (!metadata.HasValue && !metadata.HasTypedValue)
        {
            CompressedThrow.Format($"{Id} holds neither a value nor a typed_value child.");
        }

        // One child more than expected is allowed: the extra one is an explicit validity child,
        // and it comes first.
        int expected = 1 + (metadata.HasValue ? 1 : 0) + (metadata.HasTypedValue ? 1 : 0);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, expected, expected + 1, Id);
        int first = node.ChildCount == expected + 1 ? 1 : 0;

        DTypeArena types = context.Types;
        DType metadataType = types.Binary(Nullability.NonNullable);
        DType valueType = types.Binary(
            metadata.ValueNullable ? Nullability.Nullable : Nullability.NonNullable);

        // The validity child comes first, where the shared helpers expect it last.
        Validity validity = first == 0
            ? Validity.FromNullability(dtype.Nullability)
            : context.DecodeValidityChildRows(in node, 0, length, start, count, wanted, rows);

        int produced = count;

        int metadataChild = context.DecodeChildRows(in node, first, metadataType, length, start, count, wanted, rows);
        metadataChild = RequireBinary(context, metadataChild, produced, "metadata");
        int valueChild = -1;
        if (metadata.HasValue)
        {
            valueChild = context.DecodeChildRows(in node, first + 1, valueType, length, start, count, wanted, rows);
            valueChild = RequireBinary(context, valueChild, produced, "value");
        }

        if (!metadata.HasTypedValue)
        {
            Span<int> fields = stackalloc int[2];
            fields[0] = metadataChild;
            fields[1] = valueChild;

            // A struct wearing the variant dtype: the canonical shape is `Struct{metadata, value}`
            // while the schema still says variant.
            return context.Canonical.AddStruct(dtype, produced, validity, fields);
        }

        // Shredded: the children make the group the Parquet standard describes, a struct of the
        // metadata, the residual value when there is one and the typed value, valid where the
        // variant is; the plan that rebuilds each row's value from them is the Parquet reader's.
        DType typedType = DTypeProtobuf.Read(metadata.TypedValueDType, types);
        int typed = first + (metadata.HasValue ? 2 : 1);
        DType group = metadata.HasValue
            ? types.Struct(["metadata", "value", "typed_value"], [metadataType, valueType, typedType], dtype.Nullability)
            : types.Struct(["metadata", "typed_value"], [metadataType, typedType], dtype.Nullability);
        int width = metadata.HasValue ? 2 : 1;
        Span<int> children = stackalloc int[3];
        children[0] = metadataChild;
        children[1] = valueChild;
        bool sliceable = rows == ChildRows.Selected
            ? context.ChildSelectsWithoutFullDecode(in node, typed, typedType)
            : context.ChildDecodesRange(in node, typed);
        if (sliceable && ShreddedSlices.TryBegin(context, produced, typedType, Malformed, out ShreddedSlices slices))
        {
            try
            {
                // The columns left whole, in one struct with the validity, which each slice reads a window of.
                DType whole = metadata.HasValue
                    ? types.Struct(["metadata", "value"], [metadataType, valueType], dtype.Nullability)
                    : types.Struct(["metadata"], [metadataType], dtype.Nullability);
                int left = slices.Destination.AddStruct(whole, produced, validity, children[..width]);
                for (int at = 0; at < produced; at += slices.Rows)
                {
                    int part = Math.Min(slices.Rows, produced - at);
                    int columns = slices.Narrow(context, left, at, part);
                    int typedSlice = rows == ChildRows.Selected
                        ? context.DecodeChildSelected(in node, typed, typedType, length, wanted.Slice(at, part))
                        : context.DecodeChildRange(in node, typed, typedType, length, start + at, part);
                    CanonicalArena arena = context.Canonical;
                    if (arena.GetNode(typedSlice).Kind == CanonicalKind.Constant)
                    {
                        typedSlice = arena.MaterializeConstant(typedSlice);
                    }

                    CanonicalNode window = arena.GetNode(columns);
                    for (int i = 0; i < width; i++)
                    {
                        children[i] = window.GetFieldIndex(i);
                    }

                    children[width] = typedSlice;
                    slices.Write(context, arena.AddStruct(group, part, window.Validity, children[..(width + 1)]), group, at);
                }

                return slices.Complete(dtype, metadataChild, valueChild, validity);
            }
            finally
            {
                slices.End(context);
            }
        }

        int typedChild = context.DecodeChildRows(in node, typed, typedType, length, start, count, wanted, rows);
        if (context.Canonical.GetNode(typedChild).Kind == CanonicalKind.Constant)
        {
            typedChild = context.Canonical.MaterializeConstant(typedChild);
        }

        children[width] = typedChild;
        int groupNode = context.Canonical.AddStruct(group, produced, validity, children[..(width + 1)]);
        try
        {
            return ShreddedVariant.For(context.Scan, group).Assemble(context.Canonical, dtype, groupNode);
        }
        catch (VortexFormatException malformed)
        {
            CompressedThrow.Format($"{Malformed}: {malformed.Message}");
            throw;
        }
    }

    /// <summary>
    /// The child as a dense binary column: one checked for its kind and length, a constant -- one
    /// metadata for every row is the common case -- expanded as a dense child would have held it.
    /// </summary>
    private static int RequireBinary(ArrayDecodeContext context, int child, int length, string what)
    {
        if (context.Canonical.GetNode(child).Kind == CanonicalKind.Constant)
        {
            child = context.Canonical.MaterializeConstant(child);
        }

        CanonicalNode node = context.Canonical.GetNode(child);
        if (node.Kind != CanonicalKind.VarBinView)
        {
            CompressedThrow.ChildKind(Id, what, node.Kind, "a VarBinView");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(Id, what, node.Length, length);
        }

        return child;
    }
}

/// <summary>
/// The shape both variant spellings canonicalize to. There is no canonical variant kind: a variant
/// node is a two-field struct of the Parquet Variant metadata and value bytes wearing the variant
/// dtype, so both spellings end at the same two binary columns and the writer and the conformance
/// comparer each have one form to handle.
/// </summary>
internal static class VariantStorage
{
    /// <summary>Refuses a node that is not <c>Struct{metadata, value}</c> of the right length.</summary>
    /// <param name="node">The decoded node.</param>
    /// <param name="length">The row count it must have.</param>
    /// <param name="encodingId">The encoding asking, for the message.</param>
    internal static void Require(CanonicalNode node, int length, string encodingId)
    {
        if (node.Kind != CanonicalKind.Struct || node.FieldCount != 2)
        {
            CompressedThrow.Format(
                $"{encodingId}'s storage decoded as {node.Kind}; a variant's canonical form is a " +
                "two-field struct of {metadata, value}.");
        }

        if (node.Length != length)
        {
            CompressedThrow.ChildLength(encodingId, "core_storage", node.Length, length);
        }
    }
}
