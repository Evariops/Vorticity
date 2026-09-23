using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Types;

/// <summary>One node of a <see cref="ScalarStore"/>.</summary>
internal struct ScalarNode
{
    /// <summary>Union discriminant. Never <see cref="ScalarValueKind.Absent"/>: absence has no node.</summary>
    public ScalarValueKind Kind;

    /// <summary>
    /// Bool (0/1), Int64 (two's complement), UInt64, F16/F32/F64 (raw IEEE bits) and the Union
    /// type id all live here. Storing floats as bits keeps equality bit-exact and NaN-stable.
    /// </summary>
    public ulong Bits;

    /// <summary>String/Bytes: byte offset. List: first child slot. Variant: dtype slot.</summary>
    public int Start;

    /// <summary>String/Bytes: byte length. List: element count.</summary>
    public int Length;

    /// <summary>Variant/Union: the value's node index, or -1 when the value is absent.</summary>
    public int Child;

    /// <summary>1 for a leaf, <c>1 + max(child depth)</c> otherwise. Capped at construction.</summary>
    public int Depth;

    /// <summary>Cached structural hash, identical for equal values in any store.</summary>
    public int Hash;
}

/// <summary>
/// Arena backing <see cref="ScalarValue"/> handles.
/// </summary>
/// <remarks>
/// <para>
/// A file's statistics are thousands of small scalars, so a value is a (store, index) handle over
/// growable arrays rather than an object per value. Unlike <see cref="DTypeArena"/> the store does
/// not deduplicate: dtypes repeat constantly across a wide schema, scalar values do not, so a
/// dedup table would cost a hash and a comparison per value and collapse almost nothing. Equality
/// is therefore always the structural walk, never an index compare — which comparing values held
/// by two different stores demands anyway.
/// </para>
/// <para>Not thread-safe for mutation; safe for concurrent reads once fully built.</para>
/// <para>
/// <see cref="ScalarValue.AsBytes"/> returns a span into store-owned memory. It is invalidated by
/// a later append that grows the byte array, and by <see cref="Clear"/>.
/// </para>
/// </remarks>
internal sealed class ScalarStore
{
    /// <summary>
    /// Node pairs a comparison visits before it starts memoising. Same rationale, and same value,
    /// as <see cref="DTypeArena"/>'s: past this point the walk is enumerating paths through a DAG
    /// rather than comparing a tree.
    /// </summary>
    private const int EqualityVisitBudget = 1 << 16;

    private ScalarNode[] _nodes;
    private int _nodeCount;

    private int[] _children;
    private int _childCount;

    private byte[] _bytes;
    private int _byteCount;

    private DType[] _variantTypes;
    private int _variantTypeCount;

    private int _generation;

    /// <summary>Creates an empty store.</summary>
    /// <param name="initialCapacity">Hint for the initial node capacity. Must not be negative.</param>
    /// <remarks>
    /// Only the nodes are allocated here. The children, the bytes and the variant dtypes are each
    /// allocated by the first value that has some: a store of numbers holds none of them.
    /// </remarks>
    public ScalarStore(int initialCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _nodes = new ScalarNode[Math.Max(initialCapacity, 4)];
        _children = [];
        _bytes = [];
        _variantTypes = [];
    }

    /// <summary>Number of value nodes currently held.</summary>
    public int NodeCount => _nodeCount;

    /// <summary>
    /// Counts how many times this store has been cleared. Every <see cref="ScalarValue"/> carries
    /// the value it was issued under, so a handle held across a <see cref="Clear"/> is recognised
    /// instead of silently reading whatever value now occupies its slot.
    /// </summary>
    internal int Generation => _generation;

    /// <summary>
    /// Drops every value. Every previously issued <see cref="ScalarValue"/> handle is dead: reading
    /// one throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Clear()
    {
        _generation++;
        _nodeCount = 0;
        _childCount = 0;
        _byteCount = 0;
        Array.Clear(_variantTypes, 0, _variantTypeCount);
        _variantTypeCount = 0;
    }

    /// <summary>
    /// The absent value: no wire case was present at all. It carries no store, so
    /// <c>store.Absent</c> equals <c>default(ScalarValue)</c> and equals another store's
    /// <c>Absent</c>. Deliberately distinct from <see cref="Null"/>: a statistic with no value
    /// licenses nothing, where a null one asserts that the value is null.
    /// </summary>
    public ScalarValue Absent => default;

    /// <summary>The null value (<c>null_value</c>).</summary>
    public ScalarValue Null() => Leaf(ScalarValueKind.Null, 0);

    /// <summary>A boolean value.</summary>
    public ScalarValue Bool(bool value) => Leaf(ScalarValueKind.Bool, value ? 1UL : 0UL);

    /// <summary>A signed 64-bit integer value.</summary>
    public ScalarValue Int64(long value) => Leaf(ScalarValueKind.Int64, unchecked((ulong)value));

    /// <summary>An unsigned 64-bit integer value.</summary>
    public ScalarValue UInt64(ulong value) => Leaf(ScalarValueKind.UInt64, value);

    /// <summary>A binary16 value. Stored as raw bits, so NaN payloads survive a round trip.</summary>
    public ScalarValue F16(Half value) => Leaf(ScalarValueKind.F16, BitConverter.HalfToUInt16Bits(value));

    /// <summary>A binary16 value given as its raw 16 bits, exactly as <c>f16_value</c> carries it.</summary>
    public ScalarValue F16FromBits(ushort bits) => Leaf(ScalarValueKind.F16, bits);

    /// <summary>A binary32 value. Stored as raw bits.</summary>
    public ScalarValue F32(float value) => Leaf(ScalarValueKind.F32, BitConverter.SingleToUInt32Bits(value));

    /// <summary>A binary64 value. Stored as raw bits.</summary>
    public ScalarValue F64(double value) => Leaf(ScalarValueKind.F64, BitConverter.DoubleToUInt64Bits(value));

    /// <summary>A UTF-8 string value. The bytes are copied into the store.</summary>
    public ScalarValue String(ReadOnlySpan<byte> utf8) => Blob(ScalarValueKind.String, utf8);

    /// <summary>A string value, encoded to UTF-8 and copied into the store.</summary>
    public ScalarValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> buffer = new Scratch<byte>(Encoding.UTF8.GetByteCount(value), stack);
        int written = Encoding.UTF8.GetBytes(value, buffer.Span);
        return Blob(ScalarValueKind.String, buffer.Span[..written]);
    }

    /// <summary>An opaque bytes value. The bytes are copied into the store.</summary>
    public ScalarValue Bytes(ReadOnlySpan<byte> value) => Blob(ScalarValueKind.Bytes, value);

    /// <summary>
    /// A list value. Elements must belong to this store, or be <see cref="Absent"/>, which is
    /// storeless and legal as an element (an empty <c>ScalarValue</c> inside a <c>ListValue</c>).
    /// </summary>
    /// <exception cref="ArgumentException">An element belongs to a different store.</exception>
    /// <exception cref="VortexFormatException">
    /// The resulting nesting depth exceeds <see cref="VortexLimits.MaxDTypeDepth"/>. A list of
    /// lists is as capable of blowing the stack as a nested struct.
    /// </exception>
    public ScalarValue List(ReadOnlySpan<ScalarValue> elements)
    {
        int depth = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            int d = ElementDepth(elements[i]);
            if (d > depth)
            {
                depth = d;
            }
        }

        depth++;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Scalar");

        Ensure(ref _children, _childCount, elements.Length, _nodes.Length);
        int start = _childCount;
        for (int i = 0; i < elements.Length; i++)
        {
            _children[start + i] = elements[i].NodeIndexOrAbsent;
        }

        _childCount += elements.Length;

        ScalarNode n = default;
        n.Kind = ScalarValueKind.List;
        n.Start = start;
        n.Length = elements.Length;
        n.Child = -1;
        n.Depth = depth;
        return Add(ref n);
    }

    /// <summary>
    /// A variant scalar: a nested (dtype, value) pair, per <c>Scalar variant_value = 11</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The nested value belongs to a different store.</exception>
    /// <exception cref="VortexFormatException">The resulting nesting depth exceeds the cap.</exception>
    public ScalarValue Variant(in Scalar scalar)
    {
        int depth = ElementDepth(scalar.Value) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Scalar");

        Ensure(ref _variantTypes, _variantTypeCount, 1, 4);
        int slot = _variantTypeCount;
        _variantTypes[slot] = scalar.DType;
        _variantTypeCount++;

        ScalarNode n = default;
        n.Kind = ScalarValueKind.Variant;
        n.Start = slot;
        n.Child = scalar.Value.NodeIndexOrAbsent;
        n.Depth = depth;
        return Add(ref n);
    }

    /// <summary>
    /// A present union alternative. A union that is itself null uses <see cref="Null"/> instead, so
    /// the two never collide.
    /// </summary>
    /// <exception cref="ArgumentException">The value belongs to a different store.</exception>
    /// <exception cref="VortexFormatException">The resulting nesting depth exceeds the cap.</exception>
    public ScalarValue Union(uint typeId, ScalarValue value)
    {
        int depth = ElementDepth(value) + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxDTypeDepth, "Scalar");

        ScalarNode n = default;
        n.Kind = ScalarValueKind.Union;
        n.Bits = typeId;
        n.Child = value.NodeIndexOrAbsent;
        n.Depth = depth;
        return Add(ref n);
    }

    // ---------------------------------------------------------------- node access

    internal ref readonly ScalarNode NodeRef(int index, int generation)
    {
        CheckGeneration(generation);
        return ref _nodes[index];
    }

    /// <summary>
    /// Refuses a handle issued before the last <see cref="Clear"/>. The index alone cannot tell:
    /// after a clear the store refills from zero, so a stale handle stays in bounds and names a
    /// value of some unrelated kind.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckGeneration(int generation)
    {
        if (generation != _generation)
        {
            ThrowStale(generation, _generation);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStale(int held, int current) => throw new InvalidOperationException(
        $"This ScalarValue was issued before the store was cleared (handle generation {held}, " +
        $"store generation {current}); its node index no longer means anything.");

    internal ReadOnlySpan<byte> BlobSpan(in ScalarNode n) => _bytes.AsSpan(n.Start, n.Length);

    internal int ChildAt(in ScalarNode n, int i) => _children[n.Start + i];

    internal DType VariantType(in ScalarNode n) => _variantTypes[n.Start];

    internal ScalarValue Handle(int nodeIndex) =>
        nodeIndex < 0 ? default : new ScalarValue(this, nodeIndex);

    /// <summary>
    /// Structural comparison of two value nodes, possibly in different stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VortexLimits.MaxDTypeDepth"/> bounds the recursion stack and nothing else. A
    /// list stores its elements as node indices, so <c>List([v, v])</c> nested 64 deep is a
    /// 65-node DAG with 2^64 root-to-leaf paths, and this store does not deduplicate, so two
    /// structurally identical roots built in the same store also take the full walk. The hash
    /// early-out cannot help either: equal values hash equal by construction.
    /// </para>
    /// <para>
    /// The visit count is therefore bounded the same way <see cref="DTypeArena"/> bounds it: the
    /// walk runs allocation-free until it has visited <see cref="EqualityVisitBudget"/> node
    /// pairs, then memoises the pairs it has proven equal. A mismatch propagates straight out, so
    /// unequal pairs are never revisited and are not memoised. There is no dedup table here, but
    /// "node <c>ai</c> in one store equals node <c>bi</c> in the other" is still a property of the
    /// pair alone, so the memo is sound. The <see cref="ScalarValueKind.Variant"/> branch compares
    /// dtypes through <see cref="DType.Equals(DType)"/>, which carries its own budget.
    /// </para>
    /// </remarks>
    internal static bool StructurallyEqual(ScalarStore a, int ai, ScalarStore b, int bi)
    {
        int budget = EqualityVisitBudget;
        HashSet<long>? memo = null;
        return StructurallyEqual(a, ai, b, bi, ref budget, ref memo);
    }

    private static bool StructurallyEqual(
        ScalarStore a, int ai, ScalarStore b, int bi, ref int budget, ref HashSet<long>? memo)
    {
        if (ReferenceEquals(a, b) && ai == bi)
        {
            return true;
        }

        long pair = ((long)ai << 32) | (uint)bi;
        if (memo is not null)
        {
            // Two calls on purpose, as in DTypeArena and for the same reason: what is remembered
            // is a pair already proven equal, which is only known after the walk below.
            if (memo.Contains(pair))
            {
                return true;
            }
        }
        else if (--budget < 0)
        {
            memo = new HashSet<long>();
        }

        bool equal = NodesEqual(a, ai, b, bi, ref budget, ref memo);
        if (equal)
        {
            // Re-read: the memo may have been switched on deeper inside the recursion.
            memo?.Add(pair);
        }

        return equal;
    }

    private static bool NodesEqual(
        ScalarStore a, int ai, ScalarStore b, int bi, ref int budget, ref HashSet<long>? memo)
    {
        ref readonly ScalarNode x = ref a._nodes[ai];
        ref readonly ScalarNode y = ref b._nodes[bi];
        if (x.Hash != y.Hash || x.Kind != y.Kind)
        {
            return false;
        }

        switch (x.Kind)
        {
            case ScalarValueKind.Null:
                return true;

            case ScalarValueKind.Bool:
            case ScalarValueKind.Int64:
            case ScalarValueKind.UInt64:
            case ScalarValueKind.F16:
            case ScalarValueKind.F32:
            case ScalarValueKind.F64:
                // Bitwise, deliberately: this is value identity, not an IEEE comparison. NaN
                // equals itself here and +0 does not equal -0. Filter evaluation follows IEEE 754
                // instead and must not reuse this.
                return x.Bits == y.Bits;

            case ScalarValueKind.String:
            case ScalarValueKind.Bytes:
                return a.BlobSpan(in x).SequenceEqual(b.BlobSpan(in y));

            case ScalarValueKind.List:
            {
                if (x.Length != y.Length)
                {
                    return false;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (!ChildrenEqual(
                            a, a._children[x.Start + i], b, b._children[y.Start + i], ref budget, ref memo))
                    {
                        return false;
                    }
                }

                return true;
            }

            case ScalarValueKind.Variant:
                return a._variantTypes[x.Start].Equals(b._variantTypes[y.Start]) &&
                       ChildrenEqual(a, x.Child, b, y.Child, ref budget, ref memo);

            case ScalarValueKind.Union:
                return x.Bits == y.Bits && ChildrenEqual(a, x.Child, b, y.Child, ref budget, ref memo);

            default:
                return false;
        }
    }

    private static bool ChildrenEqual(
        ScalarStore a, int ai, ScalarStore b, int bi, ref int budget, ref HashSet<long>? memo)
    {
        if (ai < 0 || bi < 0)
        {
            // Absent equals only Absent; it must never collapse into Null.
            return ai < 0 && bi < 0;
        }

        return StructurallyEqual(a, ai, b, bi, ref budget, ref memo);
    }

    // ---------------------------------------------------------------- construction plumbing

    private ScalarValue Leaf(ScalarValueKind kind, ulong bits)
    {
        ScalarNode n = default;
        n.Kind = kind;
        n.Bits = bits;
        n.Child = -1;
        n.Depth = 1;
        return Add(ref n);
    }

    private ScalarValue Blob(ScalarValueKind kind, ReadOnlySpan<byte> value)
    {
        Ensure(ref _bytes, _byteCount, value.Length, _nodes.Length * 4);
        // `value` may alias _bytes; it then refers to the pre-resize array, whose contents
        // Array.Resize copied forward, so the copy is well defined either way.
        value.CopyTo(_bytes.AsSpan(_byteCount));
        ScalarNode n = default;
        n.Kind = kind;
        n.Start = _byteCount;
        n.Length = value.Length;
        n.Child = -1;
        n.Depth = 1;
        _byteCount += value.Length;
        return Add(ref n);
    }

    private ScalarValue Add(ref ScalarNode node)
    {
        node.Hash = ComputeHash(in node);
        Ensure(ref _nodes, _nodeCount, 1, _nodes.Length);
        _nodes[_nodeCount] = node;
        return new ScalarValue(this, _nodeCount++);
    }

    private int ElementDepth(ScalarValue value)
    {
        ScalarStore? store = value.Store;
        if (store is null)
        {
            // Absent: storeless, contributes no depth.
            return 0;
        }

        if (!ReferenceEquals(store, this))
        {
            ThrowForeignValue();
        }

        return _nodes[value.NodeIndexOrAbsent].Depth;
    }

    private int ComputeHash(in ScalarNode n)
    {
        HashCode hc = default;
        hc.Add((byte)n.Kind);
        switch (n.Kind)
        {
            case ScalarValueKind.Bool:
            case ScalarValueKind.Int64:
            case ScalarValueKind.UInt64:
            case ScalarValueKind.F16:
            case ScalarValueKind.F32:
            case ScalarValueKind.F64:
                hc.Add(n.Bits);
                break;

            case ScalarValueKind.String:
            case ScalarValueKind.Bytes:
                hc.AddBytes(_bytes.AsSpan(n.Start, n.Length));
                break;

            case ScalarValueKind.List:
                hc.Add(n.Length);
                for (int i = 0; i < n.Length; i++)
                {
                    int child = _children[n.Start + i];
                    hc.Add(child < 0 ? 0 : _nodes[child].Hash);
                }

                break;

            case ScalarValueKind.Variant:
                hc.Add(_variantTypes[n.Start].GetHashCode());
                hc.Add(n.Child < 0 ? 0 : _nodes[n.Child].Hash);
                break;

            case ScalarValueKind.Union:
                hc.Add(n.Bits);
                hc.Add(n.Child < 0 ? 0 : _nodes[n.Child].Hash);
                break;

            default:
                break;
        }

        return hc.ToHashCode();
    }

    /// <summary>Grows <paramref name="array"/> to hold <paramref name="extra"/> more past <paramref name="count"/>, from <paramref name="first"/> when it is still empty.</summary>
    private static void Ensure<T>(ref T[] array, int count, int extra, int first)
    {
        long need = (long)count + extra;
        if (need <= array.Length)
        {
            return;
        }

        const int Ceiling = int.MaxValue - 16;
        if (need > Ceiling)
        {
            ThrowTooLarge();
        }

        int capacity = array.Length == 0 ? Math.Max(first, 4) : array.Length;
        while (capacity < need)
        {
            capacity = capacity > Ceiling / 2 ? Ceiling : capacity * 2;
        }

        Array.Resize(ref array, capacity);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLarge() =>
        throw new VortexFormatException("Scalar store exceeded the maximum addressable size.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowForeignValue() => throw new ArgumentException(
        "Nested ScalarValues must come from the same ScalarStore as their parent.", "value");
}
