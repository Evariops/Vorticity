using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>One key of an order on groups: a result, ascending or descending.</summary>
/// <param name="Field">The result, as the column of the groups' results that holds it.</param>
/// <param name="Descending">Whether the largest comes first.</param>
internal readonly record struct OrderKey(ResultFieldExpr Field, bool Descending);

/// <summary>An <c>OrderBy</c> and its <c>ThenBy</c>s on groups.</summary>
internal sealed class GroupOrder : GroupOperator
{
    internal GroupOrder(OrderKey[] keys) => Keys = keys;

    internal OrderKey[] Keys { get; }
}

/// <summary>
/// The order of one column of a batch, by position: a number by value, NaN after +∞ and −0.0 equal
/// to +0.0, text and binary bytewise, <c>false</c> before <c>true</c>, a decimal by its unscaled
/// value, and a null last whichever the direction.
/// </summary>
internal abstract class ColumnOrder
{
    private readonly ulong[] _validity;
    private readonly bool _descending;

    /// <param name="validity">A bit per position, set where the value is present; empty when every one is.</param>
    /// <param name="descending">Whether the largest comes first.</param>
    protected ColumnOrder(ulong[] validity, bool descending)
    {
        _validity = validity;
        _descending = descending;
    }

    internal int Compare(int a, int b)
    {
        if (_validity.Length > 0)
        {
            bool left = ((_validity[a >> 6] >> (a & 63)) & 1) != 0;
            bool right = ((_validity[b >> 6] >> (b & 63)) & 1) != 0;
            if (!left || !right)
            {
                return left == right ? 0 : left ? -1 : 1;
            }
        }

        int order = CompareValues(a, b);
        return _descending ? -order : order;
    }

    protected abstract int CompareValues(int a, int b);

    /// <summary>Gives back what the order holds of the pool, once the sort is done.</summary>
    internal virtual void Release()
    {
    }

    /// <summary>The key of 64 bits the order ranks <paramref name="position"/> by, the direction folded in; null when it holds none, or the value is null.</summary>
    internal virtual long? KeyAt(int position) => null;

    /// <summary>Whether <paramref name="position"/>'s value is present: not a null the validity marks.</summary>
    protected bool Present(int position) => _validity.Length == 0 || ((_validity[position >> 6] >> (position & 63)) & 1) != 0;

    /// <summary>
    /// <see cref="GroupSort.TopTied"/> by this order alone: through <see cref="Compare"/>, or in a
    /// loop typed on its values where it holds them.
    /// </summary>
    internal virtual (int Before, int Tied) TopTied(int[] positions, int count, int keep, QueryMemory? memory, CancellationToken cancellationToken) =>
        GroupSort.TopTied(positions, count, new ChainOrder([this]), keep, memory, cancellationToken);

    /// <summary>The order of the positions themselves: the last of a chain, which makes a sort stable.</summary>
    internal static ColumnOrder Positions { get; } = new PositionOrder();

    /// <summary>
    /// The order of component <paramref name="component"/> of the keys of <paramref name="groups"/>,
    /// position <c>i</c> being group <c>groups[i]</c>, read from the groups' index rather than from a
    /// column of their keys built for it: ascending, the null group last, as the column would order.
    /// </summary>
    internal static ColumnOrder ForKeys(GroupKeys keys, int[] groups, int component) => new KeyOrder(keys, groups, component);

    private sealed class KeyOrder(GroupKeys keys, int[] groups, int component) : ColumnOrder([], descending: false)
    {
        protected override int CompareValues(int a, int b) => keys.CompareKeys(groups[a], groups[b], component);
    }

    /// <summary>The order of <paramref name="node"/>, a canonical column of <paramref name="type"/>, held over its buffers while the arena keeps them.</summary>
    /// <exception cref="ArgumentException">The column's type has no order.</exception>
    internal static ColumnOrder For(CanonicalArena arena, int node, VortexType type, bool descending)
    {
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        node = EncodedForms.Canonical(arena, node);
        ulong[] validity = ArenaWords.Validity(arena, node).ToArray();
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        switch (record.Kind)
        {
            case CanonicalKind.Primitive:
                return record.PType switch
                {
                    PType.I8 => new IntegerOrder<sbyte>(record.BufferA, validity, descending),
                    PType.I16 => new IntegerOrder<short>(record.BufferA, validity, descending),
                    PType.I32 => new IntegerOrder<int>(record.BufferA, validity, descending),
                    PType.I64 => new IntegerOrder<long>(record.BufferA, validity, descending),
                    PType.U8 => new IntegerOrder<byte>(record.BufferA, validity, descending),
                    PType.U16 => new IntegerOrder<ushort>(record.BufferA, validity, descending),
                    PType.U32 => new IntegerOrder<uint>(record.BufferA, validity, descending),
                    PType.U64 => new IntegerOrder<ulong>(record.BufferA, validity, descending),
                    PType.F16 => new FloatOrder<Half>(record.BufferA, validity, descending),
                    PType.F32 => new FloatOrder<float>(record.BufferA, validity, descending),
                    _ => new FloatOrder<double>(record.BufferA, validity, descending),
                };
            case CanonicalKind.Decimal:
                return new DecimalOrder(record.BufferA, DecimalStorage.ByteWidth(record.Storage), validity, descending);
            case CanonicalKind.Bool:
                return new BoolOrder(record.BufferA, record.BitOffset, validity, descending);
            case CanonicalKind.VarBinView:
                return new BytesOrder(arena, node, validity, descending);
            case CanonicalKind.FixedSizeList:
            {
                int elements = EncodedForms.Canonical(arena, arena.GetNode(node).ElementsIndex);
                return new FixedBytesOrder(arena.RecordRef(elements).BufferA, (int)record.FixedSize, validity, descending);
            }

            default:
                throw new ArgumentException($"A result of {type} has no order: order by a key component or an aggregate that delivers a value.");
        }
    }

    private sealed class PositionOrder() : ColumnOrder([], descending: false)
    {
        protected override int CompareValues(int a, int b) => a.CompareTo(b);
    }

    private sealed class IntegerOrder<T>(Buffers.VortexBuffer values, ulong[] validity, bool descending) : ColumnOrder(validity, descending)
        where T : unmanaged, IComparable<T>
    {
        protected override int CompareValues(int a, int b)
        {
            ReadOnlySpan<T> span = MemoryMarshal.Cast<byte, T>(values.Span);
            return span[a].CompareTo(span[b]);
        }
    }

    private sealed class FloatOrder<T>(Buffers.VortexBuffer values, ulong[] validity, bool descending) : ColumnOrder(validity, descending)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        protected override int CompareValues(int a, int b)
        {
            ReadOnlySpan<T> span = MemoryMarshal.Cast<byte, T>(values.Span);
            T left = span[a];
            T right = span[b];
            if (left == right)
            {
                // Both zeros, whatever their signs.
                return 0;
            }

            if (T.IsNaN(left))
            {
                return T.IsNaN(right) ? 0 : 1;
            }

            return T.IsNaN(right) || left < right ? -1 : 1;
        }
    }

    private sealed class DecimalOrder(Buffers.VortexBuffer values, int width, ulong[] validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b)
        {
            ReadOnlySpan<byte> span = values.Span;
            return width <= 16
                ? Narrow(span.Slice(a * width, width)).CompareTo(Narrow(span.Slice(b * width, width)))
                : Int256.FromLittleEndianBytes(span.Slice(a * width, width)).CompareTo(Int256.FromLittleEndianBytes(span.Slice(b * width, width)));
        }

        private static Int128 Narrow(ReadOnlySpan<byte> value) => value.Length switch
        {
            1 => (sbyte)value[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadInt64LittleEndian(value),
            _ => BinaryPrimitives.ReadInt128LittleEndian(value),
        };
    }

    private sealed class BoolOrder(Buffers.VortexBuffer bits, int offset, ulong[] validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b) => Bit(a).CompareTo(Bit(b));

        private bool Bit(int row)
        {
            int bit = offset + row;
            return ((bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
        }
    }

    private sealed class FixedBytesOrder(Buffers.VortexBuffer values, int size, ulong[] validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b) =>
            values.Span.Slice(a * size, size).SequenceCompareTo(values.Span.Slice(b * size, size));
    }

    /// <summary>Text and binary bytewise, the block resolved once: its views and its data buffers, read at each comparison.</summary>
    private sealed class BytesOrder : ColumnOrder
    {
        private readonly CanonicalArena _arena;
        private readonly Buffers.VortexBuffer _views;
        private readonly int _dataStart;
        private readonly int _dataCount;
        private readonly int _length;

        internal BytesOrder(CanonicalArena arena, int node, ulong[] validity, bool descending)
            : base(validity, descending)
        {
            ref readonly CanonicalRecord record = ref arena.RecordRef(node);
            _arena = arena;
            _views = record.BufferA;
            _dataStart = record.DataBufferStart;
            _dataCount = record.DataBufferCount;
            _length = record.Length;
        }

        protected override int CompareValues(int a, int b)
        {
            BytesBlock block = BytesBlock.Over(_arena, _views.Span, _dataStart, _dataCount, _length);
            return block[a].SequenceCompareTo(block[b]);
        }
    }
}

/// <summary>
/// The order of an aggregate's values read into an array of the pool, as a column of them would
/// order them: an integer by value, a float with NaN after +∞ and the zeros together, a null last
/// whichever the direction. Each value becomes, where it lies, a key of 64 bits whose integer order
/// is that one, the direction folded in, which a top-k ranks in a loop typed on it. The array goes
/// back to the pool once the order is released.
/// </summary>
internal static class ValuesOrder
{
    /// <summary>The key of a float that is NaN: after +∞, before a null.</summary>
    private const long NaN = long.MaxValue - 1;

    /// <summary>The key of a null float: after every value, whichever the direction.</summary>
    private const long Null = long.MaxValue;

    /// <summary>Whether values of <typeparamref name="T"/> are ordered here: the counts, sums, means and extremes most orders read.</summary>
    internal static bool Orders<T>() =>
        typeof(T) == typeof(long) || typeof(T) == typeof(long?) || typeof(T) == typeof(double) || typeof(T) == typeof(double?);

    /// <summary>An array of the pool that holds <paramref name="count"/> values of <typeparamref name="T"/>, then their keys, reserved under the query's <paramref name="memory"/>.</summary>
    internal static long[] Rent<T>(QueryMemory? memory, int count) =>
        QueryArrays.Rent<long>(memory, Unsafe.SizeOf<T>() / sizeof(long) * count, "order of a group by");

    /// <summary>The first <paramref name="count"/> values of <typeparamref name="T"/> that <paramref name="keys"/> holds before they become keys.</summary>
    internal static Span<T> Values<T>(long[] keys, int count)
    {
        // Numbers over numbers: a reference read from the array would be forged from its bits.
        Debug.Assert(Orders<T>(), $"{typeof(T)} is not a value an order reads into keys.");
        return MemoryMarshal.CreateSpan(ref Unsafe.As<long, T>(ref MemoryMarshal.GetArrayDataReference(keys)), count);
    }

    /// <summary>
    /// The order of the first <paramref name="count"/> values of <typeparamref name="T"/> that
    /// <paramref name="keys"/> holds (<see cref="Values{T}"/>), each made its key where it lies; the
    /// order takes the array.
    /// </summary>
    internal static ColumnOrder Over<T>(long[] keys, int count, bool descending, QueryMemory? memory)
    {
        if (typeof(T) == typeof(long))
        {
            // Its own key, complemented when descending: the complement reverses the order of every long.
            if (descending)
            {
                for (int i = 0; i < count; i++)
                {
                    keys[i] = ~keys[i];
                }
            }

            return new Keys(keys, [], memory);
        }

        if (typeof(T) == typeof(double))
        {
            for (int i = 0; i < count; i++)
            {
                keys[i] = Key(BitConverter.Int64BitsToDouble(keys[i]), descending);
            }

            return new Keys(keys, [], memory);
        }

        // A nullable value is 16 bytes: the key of the i-th, 8 bytes, is written over values read
        // before it. A float's null has a key past every value's; a long's, which has none to spare,
        // is left to the bits of the values present.
        if (typeof(T) == typeof(double?))
        {
            Span<double?> floats = Values<double?>(keys, count);
            for (int i = 0; i < count; i++)
            {
                keys[i] = floats[i] is double value ? Key(value, descending) : Null;
            }

            return new Keys(keys, [], memory);
        }

        Span<long?> integers = Values<long?>(keys, count);
        ulong[] present = new ulong[(count + 63) >> 6];
        bool nulls = false;
        for (int i = 0; i < count; i++)
        {
            long? value = integers[i];
            nulls |= !value.HasValue;
            present[i >> 6] |= value.HasValue ? 1UL << (i & 63) : 0;
            keys[i] = value is long integer ? (descending ? ~integer : integer) : 0;
        }

        return new Keys(keys, nulls ? present : [], memory);
    }

    /// <summary>
    /// A float as a key: its bits when it is positive, all but the sign flipped when it is negative,
    /// which order as it does; the zeros as one, NaN after +∞; complemented when descending.
    /// </summary>
    private static long Key(double value, bool descending)
    {
        long key = NaN;
        if (!double.IsNaN(value))
        {
            long bits = BitConverter.DoubleToInt64Bits(value == 0 ? 0.0 : value);
            key = bits < 0 ? bits ^ long.MaxValue : bits;
        }

        return descending ? ~key : key;
    }

    /// <summary>
    /// Keys ascending: the direction, and a float's NaN, zeros and null, folded into them; a long's
    /// nulls in the bits of the values present, which put them last.
    /// </summary>
    private sealed class Keys(long[] keys, ulong[] present, QueryMemory? memory) : ColumnOrder(present, descending: false)
    {
        /// <summary>Whether every value is present: the keys alone order them.</summary>
        private readonly bool _whole = present.Length == 0;

        protected override int CompareValues(int a, int b) => keys[a].CompareTo(keys[b]);

        internal override long? KeyAt(int position) => Present(position) ? keys[position] : null;

        internal override (int Before, int Tied) TopTied(int[] positions, int count, int keep, QueryMemory? memory, CancellationToken cancellationToken) =>
            _whole
                ? GroupSort.TopTied(positions, count, new KeysOrder(keys), keep, memory, cancellationToken)
                : base.TopTied(positions, count, keep, memory, cancellationToken);

        internal override void Release() => QueryArrays.Return(memory, keys);
    }

    private readonly struct KeysOrder(long[] keys) : IComparer<int>
    {
        public int Compare(int x, int y) => keys[x].CompareTo(keys[y]);
    }
}

/// <summary>
/// Positions by the keys of their groups, read from the groups' index component after component,
/// position <c>i</c> being group <c>groups[i]</c>: the tie-break of an order, a call per component.
/// </summary>
internal readonly struct IndexOrder(GroupKeys keys, int[] groups, int components) : IComparer<int>
{
    public int Compare(int x, int y)
    {
        for (int c = 0; c < components; c++)
        {
            int order = keys.CompareKeys(groups[x], groups[y], c);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}

/// <summary>Positions compared by a chain of column orders, the first that tells them apart deciding.</summary>
internal readonly struct ChainOrder(ColumnOrder[] orders) : IComparer<int>
{
    public int Compare(int x, int y)
    {
        foreach (ColumnOrder order in orders)
        {
            int c = order.Compare(x, y);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }
}

/// <summary>
/// Sorts positions by a chain of orders, in parts a cancellation is checked between: runs sorted
/// apart, then merged. The first <c>keep</c> alone, when fewer are wanted, through a heap of them.
/// </summary>
internal static class GroupSort
{
    /// <summary>The positions a part sorts at once, between two checks of the token.</summary>
    private const int Part = 65_536;

    /// <summary>
    /// Orders <paramref name="positions"/>, of which the first <paramref name="keep"/> at most are
    /// wanted; returns how many it holds in order. Its scratch is reserved under the query's
    /// <paramref name="memory"/>, when it has one.
    /// </summary>
    internal static int Sort(int[] positions, int count, ChainOrder order, long keep, QueryMemory? memory, CancellationToken cancellationToken)
    {
        if (keep < count)
        {
            return TopK(positions.AsSpan(0, count), order, (int)keep, memory, cancellationToken);
        }

        if (count <= Part)
        {
            positions.AsSpan(0, count).Sort(order);
            return count;
        }

        for (int start = 0; start < count; start += Part)
        {
            cancellationToken.ThrowIfCancellationRequested();
            positions.AsSpan(start, Math.Min(Part, count - start)).Sort(order);
        }

        int[] merged = QueryArrays.Rent<int>(memory, count, "order of a group by");
        try
        {
            int[] from = positions;
            int[] into = merged;
            for (int width = Part; width < count; width *= 2)
            {
                for (int start = 0; start < count; start += 2 * width)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int middle = Math.Min(start + width, count);
                    int end = Math.Min(start + (2 * width), count);
                    Merge(from, start, middle, end, into, order);
                }

                (from, into) = (into, from);
            }

            if (!ReferenceEquals(from, positions))
            {
                from.AsSpan(0, count).CopyTo(positions);
            }

            return count;
        }
        finally
        {
            QueryArrays.Return(memory, merged);
        }
    }

    private static void Merge(int[] from, int start, int middle, int end, int[] into, ChainOrder order)
    {
        int left = start;
        int right = middle;
        for (int at = start; at < end; at++)
        {
            into[at] = right >= end || (left < middle && order.Compare(from[left], from[right]) <= 0) ? from[left++] : from[right++];
        }
    }

    /// <summary>The <paramref name="keep"/> first positions in order, in the front of <paramref name="positions"/>: a heap of them, each other position compared with its top.</summary>
    internal static int TopK<TOrder>(Span<int> positions, TOrder order, int keep, QueryMemory? memory, CancellationToken cancellationToken)
        where TOrder : IComparer<int>
    {
        if (keep <= 0)
        {
            return 0;
        }

        // A heap of the kept positions whose top is the last of them in order.
        int size = 0;
        int capacity = Math.Min(keep, positions.Length);
        int[] heap = QueryArrays.Rent<int>(memory, capacity, "top of a group by");
        try
        {
            for (int i = 0; i < positions.Length; i++)
            {
                if ((i & (Part - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int candidate = positions[i];
                if (size < capacity)
                {
                    heap[size] = candidate;
                    Up(heap, size++, order);
                }
                else if (order.Compare(candidate, heap[0]) < 0)
                {
                    heap[0] = candidate;
                    Down(heap, size, order);
                }
            }

            heap.AsSpan(0, size).Sort(order);
            heap.AsSpan(0, size).CopyTo(positions);
            return size;
        }
        finally
        {
            QueryArrays.Return(memory, heap);
        }
    }

    /// <summary>
    /// Splits the positions by <paramref name="order"/>, an order that does not tell every pair apart,
    /// at the <paramref name="keep"/>-th: those strictly before it first, then every one it ties with,
    /// itself included, each part unordered; the size of each. What the order cannot rank is left to
    /// a tie-break, run on the ties alone.
    /// </summary>
    internal static (int Before, int Tied) TopTied<TOrder>(int[] positions, int count, TOrder order, int keep, QueryMemory? memory, CancellationToken cancellationToken)
        where TOrder : IComparer<int>
    {
        // A heap of the kept positions whose top is the last of them, and the positions left out
        // that tie with that top, written over the positions already read: when a position enters
        // and the top that leaves still ties with the new one, it joins them; when the new top is
        // before it, they are all out.
        int size = 0;
        int[] heap = QueryArrays.Rent<int>(memory, keep, "top of a group by");
        try
        {
            int ties = 0;
            for (int i = 0; i < count; i++)
            {
                if ((i & (Part - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int candidate = positions[i];
                if (size < keep)
                {
                    heap[size] = candidate;
                    Up(heap, size++, order);
                    continue;
                }

                int against = order.Compare(candidate, heap[0]);
                if (against > 0)
                {
                    continue;
                }

                if (against == 0)
                {
                    positions[ties++] = candidate;
                    continue;
                }

                int top = heap[0];
                heap[0] = candidate;
                Down(heap, size, order);
                if (order.Compare(heap[0], top) == 0)
                {
                    positions[ties++] = top;
                }
                else
                {
                    ties = 0;
                }
            }

            // The heap's positions before its top, then those that tie with it, then the others
            // tied, moved past them.
            positions.AsSpan(0, ties).CopyTo(positions.AsSpan(size));
            int before = 0;
            for (int i = 0; i < size; i++)
            {
                if (order.Compare(heap[i], heap[0]) < 0)
                {
                    positions[before++] = heap[i];
                }
            }

            for (int i = 0, tied = before; i < size; i++)
            {
                if (order.Compare(heap[i], heap[0]) == 0)
                {
                    positions[tied++] = heap[i];
                }
            }

            return (before, size - before + ties);
        }
        finally
        {
            QueryArrays.Return(memory, heap);
        }
    }

    private static void Up<TOrder>(int[] heap, int at, TOrder order)
        where TOrder : IComparer<int>
    {
        while (at > 0)
        {
            int parent = (at - 1) >> 1;
            if (order.Compare(heap[at], heap[parent]) <= 0)
            {
                return;
            }

            (heap[at], heap[parent]) = (heap[parent], heap[at]);
            at = parent;
        }
    }

    private static void Down<TOrder>(int[] heap, int size, TOrder order)
        where TOrder : IComparer<int>
    {
        int at = 0;
        while (true)
        {
            int left = (2 * at) + 1;
            if (left >= size)
            {
                return;
            }

            int largest = left + 1 < size && order.Compare(heap[left + 1], heap[left]) > 0 ? left + 1 : left;
            if (order.Compare(heap[largest], heap[at]) <= 0)
            {
                return;
            }

            (heap[at], heap[largest]) = (heap[largest], heap[at]);
            at = largest;
        }
    }
}
