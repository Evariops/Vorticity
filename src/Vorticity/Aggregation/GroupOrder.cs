using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
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

    protected ColumnOrder(ReadOnlySpan<ulong> validity, bool descending)
    {
        _validity = validity.ToArray();
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

    /// <summary>The order of the positions themselves: the last of a chain, which makes a sort stable.</summary>
    internal static ColumnOrder Positions { get; } = new PositionOrder();

    /// <summary>The order of <paramref name="node"/>, a canonical column of <paramref name="type"/>, held over its buffers while the arena keeps them.</summary>
    /// <exception cref="ArgumentException">The column's type has no order.</exception>
    internal static ColumnOrder For(CanonicalArena arena, int node, VortexType type, bool descending)
    {
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        node = EncodedForms.Canonical(arena, node);
        ReadOnlySpan<ulong> validity = ArenaWords.Validity(arena, node);
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

    private sealed class PositionOrder() : ColumnOrder(default, descending: false)
    {
        protected override int CompareValues(int a, int b) => a.CompareTo(b);
    }

    private sealed class IntegerOrder<T>(Buffers.VortexBuffer values, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
        where T : unmanaged, IComparable<T>
    {
        protected override int CompareValues(int a, int b)
        {
            ReadOnlySpan<T> span = MemoryMarshal.Cast<byte, T>(values.Span);
            return span[a].CompareTo(span[b]);
        }
    }

    private sealed class FloatOrder<T>(Buffers.VortexBuffer values, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
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

    private sealed class DecimalOrder(Buffers.VortexBuffer values, int width, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
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

    private sealed class BoolOrder(Buffers.VortexBuffer bits, int offset, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b) => Bit(a).CompareTo(Bit(b));

        private bool Bit(int row)
        {
            int bit = offset + row;
            return ((bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
        }
    }

    private sealed class FixedBytesOrder(Buffers.VortexBuffer values, int size, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b) =>
            values.Span.Slice(a * size, size).SequenceCompareTo(values.Span.Slice(b * size, size));
    }

    private sealed class BytesOrder(CanonicalArena arena, int node, ReadOnlySpan<ulong> validity, bool descending) : ColumnOrder(validity, descending)
    {
        protected override int CompareValues(int a, int b)
        {
            BytesBlock block = BytesBlock.Canonical(arena, node, out _);
            return block[a].SequenceCompareTo(block[b]);
        }
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

    /// <summary>Orders <paramref name="positions"/>, of which the first <paramref name="keep"/> at most are wanted; returns how many it holds in order.</summary>
    internal static int Sort(int[] positions, int count, ChainOrder order, long keep, CancellationToken cancellationToken)
    {
        if (keep < count)
        {
            return TopK(positions, count, order, (int)keep, cancellationToken);
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

        int[] merged = new int[count];
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
    private static int TopK(int[] positions, int count, ChainOrder order, int keep, CancellationToken cancellationToken)
    {
        if (keep <= 0)
        {
            return 0;
        }

        // A heap of the kept positions whose top is the last of them in order.
        int size = 0;
        int[] heap = new int[keep];
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

    private static void Up(int[] heap, int at, ChainOrder order)
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

    private static void Down(int[] heap, int size, ChainOrder order)
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
