using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Types;

/// <summary>
/// An untyped scalar value: a handle into a <see cref="ScalarStore"/>. Pair it with a
/// <see cref="DType"/> to get a <see cref="Scalar"/>.
/// </summary>
/// <remarks>
/// <para>
/// The handle is a store reference, a biased node index and the store generation the index was
/// issued under. The bias is what makes <c>default</c> mean absence rather than node 0 of a null
/// store, absence being a first-class, storeless value; the generation fits in padding the other
/// two already force, so carrying it costs nothing.
/// </para>
/// <para>
/// Equality is structural and works across stores. Floating-point values compare by raw bits, so
/// a NaN equals a NaN with the same payload and <c>+0.0</c> does not equal <c>-0.0</c>: this is
/// value identity, not the IEEE-754 comparison that filter evaluation uses.
/// </para>
/// </remarks>
public readonly struct ScalarValue : IEquatable<ScalarValue>
{
    private readonly ScalarStore? _store;

    // Node index + 1, so that the all-zero default is Absent.
    private readonly int _indexPlusOne;

    private readonly int _generation;

    internal ScalarValue(ScalarStore store, int nodeIndex)
    {
        _store = store;
        _indexPlusOne = nodeIndex + 1;
        _generation = store.Generation;
    }

    /// <summary>The wire case this value carries, or <see cref="ScalarValueKind.Absent"/>.</summary>
    public ScalarValueKind Kind =>
        _indexPlusOne == 0 ? ScalarValueKind.Absent : _store!.NodeRef(_indexPlusOne - 1, _generation).Kind;

    /// <summary>
    /// True when no value is present at all. Never true for a null value: an absent statistic
    /// licenses nothing, while a null one asserts that the value is null.
    /// </summary>
    public bool IsAbsent => _indexPlusOne == 0;

    /// <summary>True only for <see cref="ScalarValueKind.Null"/>. See <see cref="IsAbsent"/>.</summary>
    public bool IsNull => Kind == ScalarValueKind.Null;

    /// <summary>The backing store, or <c>null</c> when this value is <see cref="IsAbsent"/>.</summary>
    public ScalarStore? Store => _store;

    internal int NodeIndexOrAbsent => _indexPlusOne - 1;

    /// <summary>The boolean payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.Bool"/>.</exception>
    public bool AsBool => Require(ScalarValueKind.Bool).Bits != 0;

    /// <summary>The signed 64-bit payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.Int64"/>.</exception>
    public long AsInt64 => unchecked((long)Require(ScalarValueKind.Int64).Bits);

    /// <summary>The unsigned 64-bit payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.UInt64"/>.</exception>
    public ulong AsUInt64 => Require(ScalarValueKind.UInt64).Bits;

    /// <summary>The binary16 payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.F16"/>.</exception>
    public Half AsF16 => BitConverter.UInt16BitsToHalf((ushort)Require(ScalarValueKind.F16).Bits);

    /// <summary>The raw 16 bits of a binary16 payload, exactly as <c>f16_value</c> carries them.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.F16"/>.</exception>
    public ushort F16Bits => (ushort)Require(ScalarValueKind.F16).Bits;

    /// <summary>The binary32 payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.F32"/>.</exception>
    public float AsF32 => BitConverter.UInt32BitsToSingle((uint)Require(ScalarValueKind.F32).Bits);

    /// <summary>The binary64 payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.F64"/>.</exception>
    public double AsF64 => BitConverter.UInt64BitsToDouble(Require(ScalarValueKind.F64).Bits);

    /// <summary>
    /// The bytes of a <see cref="ScalarValueKind.String"/> (UTF-8) or
    /// <see cref="ScalarValueKind.Bytes"/> payload. The span points into store-owned memory.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is neither String nor Bytes.</exception>
    public ReadOnlySpan<byte> AsBytes
    {
        get
        {
            ref readonly ScalarNode n = ref NodeRef();
            if (n.Kind is not (ScalarValueKind.String or ScalarValueKind.Bytes))
            {
                ThrowKind(n.Kind, "String or Bytes");
            }

            return _store!.BlobSpan(in n);
        }
    }

    /// <summary>Number of elements in a list value.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.List"/>.</exception>
    public int ListCount => Require(ScalarValueKind.List).Length;

    /// <summary>
    /// Element <paramref name="index"/> of a list value. An element that was itself absent on the
    /// wire comes back as <see cref="ScalarValueKind.Absent"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.List"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <c>[0, ListCount)</c>.</exception>
    public ScalarValue GetListElement(int index)
    {
        ref readonly ScalarNode n = ref NodeRef();
        if (n.Kind != ScalarValueKind.List)
        {
            ThrowKind(n.Kind, "List");
        }

        if ((uint)index >= (uint)n.Length)
        {
            ThrowIndex(index, n.Length);
        }

        return _store!.Handle(_store.ChildAt(in n, index));
    }

    /// <summary>The nested typed scalar of a variant value.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.Variant"/>.</exception>
    public Scalar AsVariant
    {
        get
        {
            ref readonly ScalarNode n = ref NodeRef();
            if (n.Kind != ScalarValueKind.Variant)
            {
                ThrowKind(n.Kind, "Variant");
            }

            return new Scalar(_store!.VariantType(in n), _store.Handle(n.Child));
        }
    }

    /// <summary>The type id of a union value.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.Union"/>.</exception>
    public uint UnionTypeId => (uint)Require(ScalarValueKind.Union).Bits;

    /// <summary>The payload of a union value; absent when the wire carried no inner value.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScalarValueKind.Union"/>.</exception>
    public ScalarValue UnionValue
    {
        get
        {
            ref readonly ScalarNode n = ref NodeRef();
            if (n.Kind != ScalarValueKind.Union)
            {
                ThrowKind(n.Kind, "Union");
            }

            return _store!.Handle(n.Child);
        }
    }

    /// <summary>Structural, cross-store equality. Absent equals only Absent.</summary>
    public bool Equals(ScalarValue other)
    {
        int a = _indexPlusOne;
        int b = other._indexPlusOne;
        if (a == 0 || b == 0)
        {
            return a == 0 && b == 0;
        }

        // Checked here too, not only in NodeRef: the walk below reads the node arrays directly, and
        // an equality that quietly compared a dead handle would disagree with GetHashCode.
        _store!.CheckGeneration(_generation);
        other._store!.CheckGeneration(other._generation);
        return ScalarStore.StructurallyEqual(_store, a - 1, other._store, b - 1);
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ScalarValue other && Equals(other);

    /// <summary>
    /// Structural hash matching <see cref="Equals(ScalarValue)"/> across stores. Cached on the
    /// node at construction, so this is a single array read and allocates nothing.
    /// </summary>
    public override int GetHashCode() =>
        _indexPlusOne == 0 ? 0 : _store!.NodeRef(_indexPlusOne - 1, _generation).Hash;

    /// <summary>
    /// Culture-invariant rendering: <c>absent</c>, <c>null</c>, <c>true</c>, <c>-3</c>,
    /// <c>1.5</c>, <c>"text"</c>, <c>0x00ff</c>, <c>[1, 2]</c>, <c>variant(i32 = 7)</c>,
    /// <c>union(3, 7)</c>.
    /// </summary>
    public override string ToString()
    {
        Span<char> stack = stackalloc char[128];
        ValueStringWriter writer = new(stack, DTypeFormatter.MaxRenderedLength);
        try
        {
            Append(ref writer, this);
            return writer.Build();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static void Append(ref ValueStringWriter w, ScalarValue v)
    {
        if (w.IsFull)
        {
            return;
        }

        if (v._indexPlusOne == 0)
        {
            w.Append("absent");
            return;
        }

        ScalarStore store = v._store!;
        ref readonly ScalarNode n = ref store.NodeRef(v._indexPlusOne - 1, v._generation);
        switch (n.Kind)
        {
            case ScalarValueKind.Null:
                w.Append("null");
                break;

            case ScalarValueKind.Bool:
                w.Append(n.Bits != 0 ? "true" : "false");
                break;

            case ScalarValueKind.Int64:
                w.AppendInt64(unchecked((long)n.Bits));
                break;

            case ScalarValueKind.UInt64:
                w.AppendUInt64(n.Bits);
                break;

            case ScalarValueKind.F16:
                w.AppendHalf(BitConverter.UInt16BitsToHalf((ushort)n.Bits));
                break;

            case ScalarValueKind.F32:
                w.AppendSingle(BitConverter.UInt32BitsToSingle((uint)n.Bits));
                break;

            case ScalarValueKind.F64:
                w.AppendDouble(BitConverter.UInt64BitsToDouble(n.Bits));
                break;

            case ScalarValueKind.String:
                w.Append('"');
                w.AppendUtf8(store.BlobSpan(in n));
                w.Append('"');
                break;

            case ScalarValueKind.Bytes:
                AppendHex(ref w, store.BlobSpan(in n));
                break;

            case ScalarValueKind.List:
                w.Append('[');
                for (int i = 0; i < n.Length; i++)
                {
                    if (i != 0)
                    {
                        w.Append(", ");
                    }

                    Append(ref w, store.Handle(store.ChildAt(in n, i)));
                    if (w.IsFull)
                    {
                        break;
                    }
                }

                w.Append(']');
                break;

            case ScalarValueKind.Variant:
                w.Append("variant(");
                Scalar.Append(ref w, store.VariantType(in n), store.Handle(n.Child));
                w.Append(')');
                break;

            case ScalarValueKind.Union:
                w.Append("union(");
                w.AppendUInt64(n.Bits);
                w.Append(", ");
                Append(ref w, store.Handle(n.Child));
                w.Append(')');
                break;

            default:
                w.Append("absent");
                break;
        }
    }

    private static void AppendHex(ref ValueStringWriter w, ReadOnlySpan<byte> bytes)
    {
        w.Append("0x");
        Span<char> pair = stackalloc char[2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (w.IsFull)
            {
                break;
            }

            byte b = bytes[i];
            pair[0] = Nibble(b >> 4);
            pair[1] = Nibble(b & 0xF);
            w.Append(pair);
        }
    }

    private static char Nibble(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));

    private ref readonly ScalarNode NodeRef()
    {
        int biased = _indexPlusOne;
        if (biased == 0)
        {
            ThrowAbsent();
        }

        return ref _store!.NodeRef(biased - 1, _generation);
    }

    private ref readonly ScalarNode Require(ScalarValueKind expected)
    {
        ref readonly ScalarNode n = ref NodeRef();
        if (n.Kind != expected)
        {
            ThrowKind(n.Kind, expected.ToString());
        }

        return ref n;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAbsent() =>
        throw new InvalidOperationException("The ScalarValue is absent: it carries no value.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowKind(ScalarValueKind actual, string expected) =>
        throw new InvalidOperationException(
            $"ScalarValue kind is {actual}; this member requires {expected}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex(int index, int count) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"Valid range is [0, {count}).");
}
