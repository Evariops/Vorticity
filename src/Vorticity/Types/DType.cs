using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Types;

/// <summary>
/// A Vortex logical type. This is a 16-byte handle — an arena, a node index and the arena
/// generation the index was issued under — not an object: building a 1000-column schema allocates
/// the arena's arrays and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The generation fits in padding that the arena reference and the index already forced, so
/// carrying it costs nothing. Every accessor is a lookup into the arena's arrays and allocates
/// nothing, apart from the few members documented as allocating, which exist for diagnostics.
/// </para>
/// <para>
/// Equality and hashing are structural and cross-arena: two arenas that independently built the
/// same nested struct compare equal and hash equal, with no allocation on either path.
/// </para>
/// </remarks>
internal readonly struct DType : IEquatable<DType>
{
    private readonly DTypeArena? _arena;
    private readonly int _index;
    private readonly int _generation;

    internal DType(DTypeArena arena, int index)
    {
        _arena = arena;
        _index = index;
        _generation = arena.Generation;
    }

    /// <summary>True for <c>default(DType)</c>, which belongs to no arena and has no kind.</summary>
    public bool IsDefault => _arena is null;

    /// <summary>The arena backing this handle. <c>null</c> only for <c>default(DType)</c>.</summary>
    public DTypeArena Arena => _arena!;

    /// <summary>Index of this node inside <see cref="Arena"/>. Meaningless for a default handle.</summary>
    public int NodeIndex => _index;

    internal DTypeArena? ArenaOrNull => _arena;

    /// <summary>The union discriminant.</summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    public DTypeKind Kind => NodeRef().Kind;

    /// <summary>
    /// Whether this dtype admits nulls. <see cref="DTypeKind.Extension"/> has no nullability of its
    /// own and reports its storage dtype's; <see cref="DTypeKind.Null"/> is always nullable.
    /// Neither declares a <c>nullable</c> field on the wire.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    public Nullability Nullability => NodeRef().Nullability;

    /// <summary>Shorthand for <c><see cref="Nullability"/> == Nullability.Nullable</c>.</summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    public bool IsNullable => NodeRef().Nullability == Nullability.Nullable;

    /// <summary>The physical type of a <see cref="DTypeKind.Primitive"/> dtype.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Primitive.</exception>
    public PType PType
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Primitive);
            return n.PType;
        }
    }

    /// <summary>
    /// Precision of a <see cref="DTypeKind.Decimal"/> dtype: 1..<see cref="DTypeArena.MaxDecimalPrecision"/>
    /// (76), not 1..38. Precision 39 to 76 selects 256-bit storage, so a reader that rejects
    /// anything above 38 refuses legal files.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is not Decimal.</exception>
    public byte Precision
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Decimal);
            return n.Precision;
        }
    }

    /// <summary>
    /// Scale of a <see cref="DTypeKind.Decimal"/> dtype: at most
    /// <see cref="DTypeArena.MaxDecimalScale"/> (76), and at most <see cref="Precision"/> only when
    /// positive. There is no lower bound beyond <see cref="sbyte"/>: the
    /// <c>scale &lt;= precision</c> check applies only to a positive scale, so a negative scale
    /// (digits before the point) is legal down to -128, and a reader that requires
    /// <c>scale &gt;= -precision</c> refuses legal files.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is not Decimal.</exception>
    public sbyte Scale
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Decimal);
            return n.Scale;
        }
    }

    /// <summary>Element count of a <see cref="DTypeKind.FixedSizeList"/> dtype.</summary>
    /// <exception cref="InvalidOperationException">The kind is not FixedSizeList.</exception>
    public uint FixedSize
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.FixedSizeList);
            return n.FixedSize;
        }
    }

    /// <summary>Whether a <see cref="DTypeKind.Map"/> dtype declares its keys sorted.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Map.</exception>
    public bool KeysSorted
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Map);
            return n.KeysSorted;
        }
    }

    /// <summary>Number of named fields: the field count for Struct and Union, 0 for every other kind.</summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    public int FieldCount
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            return n.Kind is DTypeKind.Struct or DTypeKind.Union ? n.ChildCount : 0;
        }
    }

    /// <summary>The dtype of field <paramref name="index"/> of a Struct or Union.</summary>
    /// <exception cref="InvalidOperationException">The kind is neither Struct nor Union.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, FieldCount)</c>.</exception>
    public DType GetField(int index)
    {
        ref readonly DTypeNode n = ref NodeRef();
        RequireNamed(in n);
        CheckIndex(index, n.ChildCount);
        return new DType(_arena!, _arena!.ChildIndex(in n, index));
    }

    /// <summary>
    /// UTF-8 bytes of field <paramref name="index"/>'s name. The span points into arena-owned
    /// memory and is invalidated by later mutation of the arena.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is neither Struct nor Union.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, FieldCount)</c>.</exception>
    public ReadOnlySpan<byte> GetFieldNameUtf8(int index)
    {
        ref readonly DTypeNode n = ref NodeRef();
        RequireNamed(in n);
        CheckIndex(index, n.ChildCount);
        return _arena!.NameSpan(_arena.FieldNameHandle(in n, index));
    }

    /// <summary>Field name as a <see cref="string"/>. Allocates; for diagnostics only.</summary>
    /// <exception cref="InvalidOperationException">The kind is neither Struct nor Union.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, FieldCount)</c>.</exception>
    public string GetFieldName(int index) => Encoding.UTF8.GetString(GetFieldNameUtf8(index));

    /// <summary>
    /// Index of the field whose name equals <paramref name="nameUtf8"/>, or -1. Allocation-free:
    /// the arena interns names, so this resolves the bytes to a handle once and then compares ints.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is neither Struct nor Union.</exception>
    public int IndexOfField(ReadOnlySpan<byte> nameUtf8)
    {
        ref readonly DTypeNode n = ref NodeRef();
        RequireNamed(in n);
        if (!_arena!.TryGetName(nameUtf8, out int handle))
        {
            // The arena has never seen these bytes, so no field can carry them.
            return -1;
        }

        ReadOnlySpan<int> handles = _arena.FieldNameHandles(in n);
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i] == handle)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the field named <paramref name="name"/>, or -1.</summary>
    /// <exception cref="InvalidOperationException">The kind is neither Struct nor Union.</exception>
    public int IndexOfField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> buffer = new Scratch<byte>(Encoding.UTF8.GetByteCount(name), stack);
        int written = Encoding.UTF8.GetBytes(name, buffer.Span);
        return IndexOfField(buffer.Span[..written]);
    }

    /// <summary>
    /// The wire type id of union alternative <paramref name="index"/>. The wire field is a signed
    /// byte that the format interprets as unsigned, hence <see cref="byte"/> here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is not Union.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, FieldCount)</c>.</exception>
    public byte GetTypeId(int index)
    {
        ref readonly DTypeNode n = ref NodeRef();
        Require(in n, DTypeKind.Union);
        CheckIndex(index, n.ChildCount);
        return _arena!.TypeIdAt(in n, index);
    }

    /// <summary>Element dtype of a List or FixedSizeList.</summary>
    /// <exception cref="InvalidOperationException">The kind is neither List nor FixedSizeList.</exception>
    public DType ElementType
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            if (n.Kind is not (DTypeKind.List or DTypeKind.FixedSizeList))
            {
                ThrowKind(n.Kind, "List or FixedSizeList");
            }

            return new DType(_arena!, _arena!.ChildIndex(in n, 0));
        }
    }

    /// <summary>Key dtype of a Map.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Map.</exception>
    public DType KeyType
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Map);
            return new DType(_arena!, _arena!.ChildIndex(in n, 0));
        }
    }

    /// <summary>Value dtype of a Map.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Map.</exception>
    public DType ValueType
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Map);
            return new DType(_arena!, _arena!.ChildIndex(in n, 1));
        }
    }

    /// <summary>UTF-8 bytes of an extension's id, e.g. <c>vortex.date</c>.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Extension.</exception>
    public ReadOnlySpan<byte> ExtensionIdUtf8
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Extension);
            return _arena!.NameSpan(n.NameHandle);
        }
    }

    /// <summary>Extension id as a <see cref="string"/>. Allocates; for diagnostics only.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Extension.</exception>
    public string ExtensionId => Encoding.UTF8.GetString(ExtensionIdUtf8);

    /// <summary>Storage dtype of an extension.</summary>
    /// <exception cref="InvalidOperationException">The kind is not Extension.</exception>
    public DType StorageType
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Extension);
            return new DType(_arena!, _arena!.ChildIndex(in n, 0));
        }
    }

    /// <summary>
    /// Opaque extension metadata. Empty when the file carried none; the model does not distinguish
    /// an absent <c>optional bytes metadata</c> from a present but empty one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is not Extension.</exception>
    public ReadOnlySpan<byte> ExtensionMetadata
    {
        get
        {
            ref readonly DTypeNode n = ref NodeRef();
            Require(in n, DTypeKind.Extension);
            return _arena!.MetaSpan(in n);
        }
    }

    /// <summary>
    /// Number of child dtypes, in the order the wire format stores them: struct and union fields,
    /// the element of a list, the storage dtype of an extension, then key and value for a map.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    public int ChildCount => NodeRef().ChildCount;

    /// <summary>Child <paramref name="index"/>, in wire order.</summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, ChildCount)</c>.</exception>
    public DType GetChild(int index)
    {
        ref readonly DTypeNode n = ref NodeRef();
        CheckIndex(index, n.ChildCount);
        return new DType(_arena!, _arena!.ChildIndex(in n, index));
    }

    /// <summary>
    /// Returns a node in the same arena with the requested nullability, creating one only if the
    /// arena does not already hold it. <see cref="DTypeKind.Extension"/> has no nullability of its
    /// own, so the request is delegated to the storage dtype;
    /// <see cref="DTypeKind.Null"/> is always nullable and returns itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is default.</exception>
    /// <exception cref="VortexFormatException"><paramref name="nullability"/> is not 0 or 1.</exception>
    public DType WithNullability(Nullability nullability)
    {
        ref readonly DTypeNode n = ref NodeRef();
        if ((byte)nullability > 1)
        {
            ThrowBadNullability(nullability);
        }

        switch (n.Kind)
        {
            case DTypeKind.Null:
                return this;

            case DTypeKind.Extension:
            {
                DType storage = StorageType;
                if (storage.Nullability == nullability)
                {
                    return this;
                }

                // Re-create the extension over the re-nullabled storage: the extension's own
                // nullability is derived, so this is the only way to change it.
                DType newStorage = storage.WithNullability(nullability);
                return _arena!.Extension(ExtensionIdUtf8, newStorage, ExtensionMetadata);
            }

            default:
                return n.Nullability == nullability
                    ? this
                    : _arena!.CloneWithNullability(_index, nullability);
        }
    }

    /// <summary>Structural, cross-arena equality: kind, nullability, payload, field names and children.</summary>
    public bool Equals(DType other)
    {
        DTypeArena? a = _arena;
        DTypeArena? b = other._arena;
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Checked here too, not only in NodeRef: the walk below reads the node arrays directly, and
        // an equality that quietly compared a dead handle would disagree with GetHashCode.
        a.CheckGeneration(_generation);
        b.CheckGeneration(other._generation);
        return DTypeArena.StructurallyEqual(a, _index, b, other._index);
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is DType other && Equals(other);

    /// <summary>
    /// Structural hash matching <see cref="Equals(DType)"/> across arenas. The value is cached on
    /// the node at construction, so this is a single array read and allocates nothing.
    /// </summary>
    public override int GetHashCode() => _arena is null ? 0 : _arena.NodeRef(_index, _generation).Hash;

    /// <summary>Structural equality. See <see cref="Equals(DType)"/>.</summary>
    public static bool operator ==(DType a, DType b) => a.Equals(b);

    /// <summary>Structural inequality. See <see cref="Equals(DType)"/>.</summary>
    public static bool operator !=(DType a, DType b) => !a.Equals(b);

    /// <summary>
    /// Human-readable, stable, culture-invariant rendering. This is what <c>vxdump</c> prints and
    /// what a failing assertion shows, so it is deliberately compact:
    /// <c>"i32"</c>, <c>"f64?"</c>, <c>"decimal(10,2)"</c>, <c>"struct{a: i32, b: utf8?}"</c>,
    /// <c>"list(i32)"</c>, <c>"fsl(f32, 3)"</c>, <c>"ext(vortex.date, i32)"</c>,
    /// <c>"map(utf8, i64)"</c>, <c>"union{a: i32 = 0}"</c>.
    /// </summary>
    public override string ToString() => DTypeFormatter.Format(this);

    private ref readonly DTypeNode NodeRef()
    {
        DTypeArena? arena = _arena;
        if (arena is null)
        {
            ThrowDefault();
        }

        return ref arena.NodeRef(_index, _generation);
    }

    private static void Require(in DTypeNode n, DTypeKind expected)
    {
        if (n.Kind != expected)
        {
            ThrowKind(n.Kind, expected.ToString());
        }
    }

    private static void RequireNamed(in DTypeNode n)
    {
        if (n.Kind is not (DTypeKind.Struct or DTypeKind.Union))
        {
            ThrowKind(n.Kind, "Struct or Union");
        }
    }

    private static void CheckIndex(int index, int count)
    {
        if ((uint)index >= (uint)count)
        {
            ThrowIndex(index, count);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefault() =>
        throw new InvalidOperationException("The DType is default: it belongs to no arena.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowKind(DTypeKind actual, string expected) =>
        throw new InvalidOperationException($"DType kind is {actual}; this member requires {expected}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex(int index, int count) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"Valid range is [0, {count}).");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBadNullability(Nullability nullability) =>
        throw new VortexFormatException(
            $"Nullability value {(byte)nullability} is not 0 (NonNullable) or 1 (Nullable).");
}
