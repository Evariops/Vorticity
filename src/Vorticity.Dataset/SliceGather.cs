using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// A sorted slice's columns gathered through its permutation, a column a lane: each column's buffers laid out in the slice's
/// arena first, its values moved on the lanes, and its node added after, so that the lanes touch no record of the arena. A
/// primitive's values and an extension's storage move as they lie; a column of strings' views, and, where its strings lie
/// past their views, their bytes too, in the slice's order, so that every reader of the sorted column reads them in order,
/// where the views gathered alone left them at random. A column of another form, or with nulls, is gathered after, on the
/// sorting thread, as <see cref="CanonicalFilter"/> gathers it. A column the slice is sorted by, whose codes name its values
/// exactly, a number's but a float's, whose zeros and NaNs code alike, and padded views', takes each row's value from its
/// code in the sorted order instead, out of a table of its few values by rank: a read in order and one in the caches a
/// row, where the permutation reads anywhere in the column.
/// </summary>
/// <remarks>
/// The eleven columns of the design's requests took 0.3 s a slice of three million rows one after the other, the bytes of
/// its request ids 0.08 s of it (<c>vortex-queries --micro slicesort</c>); on lanes of their own, the slice takes its
/// longest column. Under five periods loaded at once (<c>--micro load --periods 5</c>), the views of its six names
/// gathered through the permutation took 9 % of the processors, and its status 1 %.
/// </remarks>
internal sealed class SliceGather : IFanWork
{
    private const int ViewSize = 16;
    private const int MaxInline = 12;

    /// <summary>The bytes past a heap's last a block's copy may write over.</summary>
    private const int Slack = 16;

    private Item[] _items = new Item[16];
    private int _count;
    private int[] _permutation = [];
    private int _rows;

    // Each field's values by rank, kept from one slice to the next.
    private byte[][] _tables = [];

    /// <summary>
    /// A column the slice is sorted by: each row's code in the sorted order, its values' count, and the scratch its codes
    /// were counted in, whose first row of each value by rank holds the value.
    /// </summary>
    internal readonly record struct Keyed(int[] Codes, int Distinct, SliceSortScratch Scratch);

    /// <summary>What a column's gather moves, and what its node is made of after.</summary>
    private struct Item
    {
        internal int Field;
        internal int Width;
        internal bool Views;
        internal VortexBuffer Source;
        internal VortexBuffer Into;
        internal VortexBuffer Heap;
        internal VortexBuffer Room;
        internal VortexBuffer[] Data;
        internal DType DType;
        internal DType? Extension;
        internal PType PType;
        internal Validity Validity;
        internal bool Padded;

        // A column the slice is sorted by: its codes in the sorted order, and its values by rank.
        internal int[]? Codes;
        internal byte[] Table;
    }

    /// <summary>
    /// The struct <paramref name="node"/> of <paramref name="arena"/> gathered through the first <paramref name="rows"/> of
    /// <paramref name="permutation"/>, its columns on the lanes of <paramref name="fan"/>, those of the fields
    /// <paramref name="keyed"/> holds codes for through their codes.
    /// </summary>
    internal int Gather(CanonicalArena arena, int node, int[] permutation, int rows, WorkFan fan, Keyed[]? keyed = null)
    {
        CanonicalNode root = arena.GetNode(node);
        if (root.Kind != CanonicalKind.Struct || !Whole(root.Validity))
        {
            return CanonicalFilter.Apply(arena, node, permutation.AsSpan(0, rows));
        }

        int fields = root.FieldCount;
        int[] gathered = new int[fields];
        _count = 0;
        for (int f = 0; f < fields; f++)
        {
            gathered[f] = -1;
            Plan(arena, arena.GetNode(node).GetFieldIndex(f), f, rows, keyed is not null && f < keyed.Length ? keyed[f] : default);
        }

        _permutation = permutation;
        _rows = rows;
        try
        {
            fan.Run(this, _count);
        }
        finally
        {
            _permutation = [];
        }

        for (int i = 0; i < _count; i++)
        {
            ref Item item = ref _items[i];
            gathered[item.Field] = Add(arena, in item, rows);
            item = default;
        }

        // A column of another form, or with nulls, gathered on this thread, its strings, where they lie past their views,
        // copied compact in the slice's order after.
        for (int f = 0; f < fields; f++)
        {
            if (gathered[f] < 0)
            {
                int column = CanonicalFilter.Apply(arena, arena.GetNode(node).GetFieldIndex(f), permutation.AsSpan(0, rows));
                gathered[f] = HoldsBytes(arena, column) ? arena.CopyFrom(arena, column) : column;
            }
        }

        CanonicalNode again = arena.GetNode(node);
        return arena.AddStruct(again.DType, rows, again.Validity, gathered);
    }

    public void Run(WorkFan fan, int index)
    {
        ref Item item = ref _items[index];

        // Each row's source: its row in the column through the permutation, or its value's rank in the table.
        bool keyed = item.Codes is not null;
        ReadOnlySpan<int> order = item.Codes is { } codes ? codes.AsSpan(0, _rows) : _permutation.AsSpan(0, _rows);
        ReadOnlySpan<byte> values = keyed ? item.Table : item.Source.Span;
        bool ahead = !keyed && Sse.IsSupported;
        Span<byte> into = Writable(item.Into);
        if (!item.Views || item.Heap.IsEmpty)
        {
            switch (item.Width)
            {
                case 2:
                    Gather(order, MemoryMarshal.Cast<byte, ushort>(values), MemoryMarshal.Cast<byte, ushort>(into), ahead);
                    break;
                case 4:
                    Gather(order, MemoryMarshal.Cast<byte, uint>(values), MemoryMarshal.Cast<byte, uint>(into), ahead);
                    break;
                case 8:
                    Gather(order, MemoryMarshal.Cast<byte, ulong>(values), MemoryMarshal.Cast<byte, ulong>(into), ahead);
                    break;
                case 16:
                    Gather(order, MemoryMarshal.Cast<byte, Int128>(values), MemoryMarshal.Cast<byte, Int128>(into), ahead);
                    break;
                default:
                    RowKernels.Gather(MemoryMarshal.AsBytes(order), PType.I32, values, item.Width, values.Length / item.Width, into, _rows);
                    break;
            }

            return;
        }

        // A view's string past its twelve bytes goes to the heap, in the order of the rows, and the view names it there.
        ReadOnlySpan<Int128> views = MemoryMarshal.Cast<byte, Int128>(values);
        Span<Int128> target = MemoryMarshal.Cast<byte, Int128>(into);
        Span<byte> heap = Writable(item.Room);
        VortexBuffer[] data = item.Data;
        int at = 0;
        for (int i = 0; i < order.Length; i++)
        {
            // The view of the row a stride ahead, and the bytes of the row half a stride ahead, whose view is in by now.
            if (ahead && i + Ahead < order.Length)
            {
                Prefetch(views, order[i + Ahead]);
                Int128 coming = views[order[i + (Ahead / 2)]];
                if ((uint)(ulong)coming > MaxInline)
                {
                    PrefetchBytes(data, coming);
                }
            }

            Int128 view = views[order[i]];
            uint size = (uint)(ulong)view;
            if (size <= MaxInline)
            {
                target[i] = view;
                continue;
            }

            ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(new ReadOnlySpan<Int128>(in view));
            int buffer = BinaryPrimitives.ReadInt32LittleEndian(raw[8..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(raw[12..]);
            // A value whose block of sixteen bytes lies in its buffer moves a block at a time, the heap's slack past its last;
            // one near its buffer's end takes the exact copy, which checks its bounds.
            ReadOnlySpan<byte> from = data[buffer].Span;
            int blocks = ((int)size + 15) & ~15;
            if ((uint)offset + (uint)blocks <= (uint)from.Length)
            {
                ref byte source = ref Unsafe.Add(ref MemoryMarshal.GetReference(from), offset);
                ref byte destination = ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), at);
                for (int b = 0; b < blocks; b += 16)
                {
                    Vector128.LoadUnsafe(ref source, (nuint)b).StoreUnsafe(ref destination, (nuint)b);
                }
            }
            else
            {
                from.Slice(offset, (int)size).CopyTo(heap[at..]);
            }
            target[i] = ((Int128)(ulong)at << 96) | ((Int128)(ulong)(uint)(ulong)(view >> 32) << 32) | size;
            at += (int)size;
        }
    }

    /// <summary>
    /// Lays out the gather of <paramref name="column"/>, field <paramref name="field"/>, when its form is one the lanes move:
    /// through its codes <paramref name="key"/>, when it is a column the slice is sorted by whose codes name its values.
    /// </summary>
    private void Plan(CanonicalArena arena, int column, int field, int rows, Keyed key)
    {
        CanonicalNode node = arena.GetNode(column);
        DType dtype = node.DType;
        DType? extension = null;
        int source = column;
        if (node.Kind == CanonicalKind.Extension)
        {
            extension = dtype;
            source = node.StorageIndex;
            dtype = arena.GetNode(source).DType;
        }

        CanonicalNode values = arena.GetNode(source);
        if (!Whole(values.Validity))
        {
            return;
        }

        Item item = new Item { Field = field, DType = dtype, Extension = extension, Validity = values.Validity, Data = [], Table = [] };
        if (values.Kind == CanonicalKind.Primitive)
        {
            ref readonly CanonicalRecord record = ref arena.RecordRef(source);
            item.PType = record.PType;
            item.Width = record.PType.ByteWidth();
            item.Source = record.BufferA;
            if (key.Codes is not null && !record.PType.IsFloat() && item.Width is 1 or 2 or 4 or 8)
            {
                item.Codes = key.Codes;
                item.Table = Table(field, key, item.Source.Span, item.Width);
            }

            item.Into = Into(arena, in item, rows);
        }
        else if (values.Kind == CanonicalKind.VarBinView)
        {
            ref readonly CanonicalRecord record = ref arena.RecordRef(source);
            item.Views = true;
            item.Width = ViewSize;
            item.Source = record.BufferA;
            item.Padded = record.PaddedViews;

            // Padded views of equal values are equal, past their bytes too; a long value's view the bytes it names.
            if (key.Codes is not null && record.PaddedViews)
            {
                item.Codes = key.Codes;
                item.Table = Table(field, key, item.Source.Span, ViewSize);
            }

            item.Into = Into(arena, in item, rows);
            long bytes = 0;
            for (int b = 0; b < values.DataBufferCount; b++)
            {
                bytes += values.GetDataBuffer(b).Length;
            }

            if (bytes > 0)
            {
                // The bytes the views name past their own, which a permutation keeps whole.
                bytes = Referenced(item.Source.Span, rows);
                item.Data = new VortexBuffer[values.DataBufferCount];
                for (int b = 0; b < item.Data.Length; b++)
                {
                    item.Data[b] = values.GetDataBuffer(b);
                }

                if (bytes > 0)
                {
                    item.Room = arena.AllocateUninitialized(SliceFill.LaidBytes(bytes + Slack), 1, out _);
                    item.Heap = item.Room.Slice(0, (int)bytes);
                }
            }
        }
        else
        {
            return;
        }

        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _count * 2);
        }

        _items[_count++] = item;
    }

    /// <summary>
    /// Where the gather of <paramref name="item"/> puts its <paramref name="rows"/> rows: a buffer of their own, or, for a
    /// column whose values come from its table, its own buffer, which nothing reads once its codes are counted, its table
    /// made and the bytes its views name counted: the slice holds the column once, where it held it unsorted and sorted.
    /// </summary>
    private static VortexBuffer Into(CanonicalArena arena, in Item item, int rows) =>
        rows == 0 ? VortexBuffer.Empty
        : item.Codes is not null ? item.Source.Slice(0, rows * item.Width)
        : arena.AllocateUninitialized(checked(SliceFill.Laid(rows) * item.Width), Math.Max(item.Width, 1), out _).Slice(0, rows * item.Width);

    /// <summary>
    /// The values of field <paramref name="field"/>, rows of <paramref name="width"/> bytes of <paramref name="source"/>, by
    /// the rank <paramref name="key"/>'s codes give them: the row of each rank's value is the first that holds it.
    /// </summary>
    private byte[] Table(int field, in Keyed key, ReadOnlySpan<byte> source, int width)
    {
        if (_tables.Length <= field)
        {
            Array.Resize(ref _tables, field + 1);
        }

        int bytes = key.Distinct * width;
        byte[] table = _tables[field] is { } kept && kept.Length >= bytes ? kept : _tables[field] = new byte[Math.Max(bytes, 256)];
        int[] firsts = key.Scratch.Firsts;
        int[] byValue = key.Scratch.ByValue;
        for (int rank = 0; rank < key.Distinct; rank++)
        {
            source.Slice(firsts[byValue[rank]] * width, width).CopyTo(table.AsSpan(rank * width));
        }

        return table;
    }

    /// <summary>The node of a column gathered.</summary>
    private static int Add(CanonicalArena arena, in Item item, int rows)
    {
        int added = !item.Views
            ? arena.AddPrimitive(item.DType, rows, item.Validity, item.PType, item.Into)
            : arena.AddVarBinView(item.DType, rows, item.Validity, item.Into, item.Heap.IsEmpty ? [] : [item.Heap], item.Padded);
        return item.Extension is { } extension ? arena.AddExtension(extension, rows, added) : added;
    }

    /// <summary>
    /// The rows a gather reads ahead of the one it moves: the source of each row lies anywhere in the column, a miss of every
    /// cache, and the processor keeps more misses in flight told of them early.
    /// </summary>
    private const int Ahead = 32;

    /// <summary>
    /// <paramref name="target"/>'s rows, those of <paramref name="source"/> <paramref name="from"/> names; when
    /// <paramref name="ahead"/>, each source <see cref="Ahead"/> rows ahead fetched into the caches as the row before it moves.
    /// </summary>
    private static void Gather<T>(ReadOnlySpan<int> from, ReadOnlySpan<T> source, Span<T> target, bool ahead)
        where T : unmanaged
    {
        int i = 0;
        if (ahead)
        {
            for (; i + Ahead < from.Length; i++)
            {
                Prefetch(source, from[i + Ahead]);
                target[i] = source[from[i]];
            }
        }

        for (; i < from.Length; i++)
        {
            target[i] = source[from[i]];
        }
    }

    /// <summary>Fetches row <paramref name="row"/> of <paramref name="source"/> into the caches, if it lies in it.</summary>
    private static unsafe void Prefetch<T>(ReadOnlySpan<T> source, int row)
        where T : unmanaged
    {
        if ((uint)row < (uint)source.Length)
        {
            Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), row)));
        }
    }

    /// <summary>Fetches the bytes <paramref name="view"/> names past it into the caches, if its buffer holds them.</summary>
    private static unsafe void PrefetchBytes(VortexBuffer[] data, Int128 view)
    {
        ulong high = (ulong)(view >> 64);
        uint buffer = (uint)high;
        uint offset = (uint)(high >> 32);
        if (buffer < (uint)data.Length && offset < (uint)data[buffer].Length)
        {
            Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(data[buffer].Span), (int)offset)));
        }
    }

    /// <summary>The bytes the first <paramref name="rows"/> views name past their own.</summary>
    private static long Referenced(ReadOnlySpan<byte> views, int rows)
    {
        ReadOnlySpan<Int128> all = MemoryMarshal.Cast<byte, Int128>(views)[..rows];
        long bytes = 0;
        foreach (Int128 view in all)
        {
            uint size = (uint)(ulong)view;
            bytes += size > MaxInline ? size : 0;
        }

        return bytes;
    }

    /// <summary>Whether <paramref name="column"/> is a column of strings some of which lie past their views, in its data buffers.</summary>
    private static bool HoldsBytes(CanonicalArena arena, int column)
    {
        CanonicalNode node = arena.GetNode(column);
        if (node.Kind != CanonicalKind.VarBinView)
        {
            return false;
        }

        for (int b = 0; b < node.DataBufferCount; b++)
        {
            if (node.GetDataBuffer(b).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="validity"/> holds no null a gather would have to move.</summary>
    private static bool Whole(Validity validity) => validity.Kind is ValidityKind.NonNullable or ValidityKind.AllValid;

    private static Span<byte> Writable(VortexBuffer buffer) =>
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(buffer.Span), buffer.Length);
}
