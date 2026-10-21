using System;
using System.Buffers.Binary;
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
/// sorting thread, as <see cref="CanonicalFilter"/> gathers it.
/// </summary>
/// <remarks>
/// The eleven columns of the design's requests took 0.3 s a slice of three million rows one after the other, the bytes of
/// its request ids 0.08 s of it; on lanes of their own, the slice takes its longest column.
/// </remarks>
internal sealed class SliceGather : IFanWork
{
    private const int ViewSize = 16;
    private const int MaxInline = 12;

    private Item[] _items = new Item[16];
    private int _count;
    private int[] _permutation = [];
    private int _rows;

    /// <summary>What a column's gather moves, and what its node is made of after.</summary>
    private struct Item
    {
        internal int Field;
        internal int Width;
        internal bool Views;
        internal VortexBuffer Source;
        internal VortexBuffer Into;
        internal VortexBuffer Heap;
        internal VortexBuffer[] Data;
        internal DType DType;
        internal DType? Extension;
        internal PType PType;
        internal Validity Validity;
        internal bool Padded;
    }

    /// <summary>
    /// The struct <paramref name="node"/> of <paramref name="arena"/> gathered through the first <paramref name="rows"/> of
    /// <paramref name="permutation"/>, its columns on the lanes of <paramref name="fan"/>.
    /// </summary>
    internal int Gather(CanonicalArena arena, int node, int[] permutation, int rows, WorkFan fan)
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
            Plan(arena, arena.GetNode(node).GetFieldIndex(f), f, rows);
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
        ReadOnlySpan<int> permutation = _permutation.AsSpan(0, _rows);
        Span<byte> into = Writable(item.Into);
        if (!item.Views || item.Heap.IsEmpty)
        {
            RowKernels.Gather(
                MemoryMarshal.AsBytes(permutation), PType.I32, item.Source.Span, item.Width, item.Source.Length / item.Width, into, _rows);
            return;
        }

        // A view's string past its twelve bytes goes to the heap, in the order of the rows, and the view names it there.
        ReadOnlySpan<Int128> views = MemoryMarshal.Cast<byte, Int128>(item.Source.Span);
        Span<Int128> target = MemoryMarshal.Cast<byte, Int128>(into);
        Span<byte> heap = Writable(item.Heap);
        VortexBuffer[] data = item.Data;
        int at = 0;
        for (int i = 0; i < permutation.Length; i++)
        {
            Int128 view = views[permutation[i]];
            uint size = (uint)(ulong)view;
            if (size <= MaxInline)
            {
                target[i] = view;
                continue;
            }

            ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(new ReadOnlySpan<Int128>(in view));
            int buffer = BinaryPrimitives.ReadInt32LittleEndian(raw[8..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(raw[12..]);
            data[buffer].Span.Slice(offset, (int)size).CopyTo(heap[at..]);
            target[i] = ((Int128)(ulong)at << 96) | ((Int128)(ulong)(uint)(ulong)(view >> 32) << 32) | size;
            at += (int)size;
        }
    }

    /// <summary>Lays out the gather of <paramref name="column"/>, field <paramref name="field"/>, when its form is one the lanes move.</summary>
    private void Plan(CanonicalArena arena, int column, int field, int rows)
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

        Item item = new Item { Field = field, DType = dtype, Extension = extension, Validity = values.Validity, Data = [] };
        if (values.Kind == CanonicalKind.Primitive)
        {
            ref readonly CanonicalRecord record = ref arena.RecordRef(source);
            item.PType = record.PType;
            item.Width = record.PType.ByteWidth();
            item.Source = record.BufferA;
            item.Into = rows == 0 ? VortexBuffer.Empty : arena.AllocateUninitialized(rows * item.Width, Math.Max(item.Width, 1), out _);
        }
        else if (values.Kind == CanonicalKind.VarBinView)
        {
            ref readonly CanonicalRecord record = ref arena.RecordRef(source);
            item.Views = true;
            item.Width = ViewSize;
            item.Source = record.BufferA;
            item.Padded = record.PaddedViews;
            item.Into = rows == 0 ? VortexBuffer.Empty : arena.AllocateUninitialized(rows * ViewSize, ViewSize, out _);
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

                item.Heap = bytes == 0 ? VortexBuffer.Empty : arena.AllocateUninitialized(checked((int)bytes), 1, out _);
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

    /// <summary>The node of a column gathered.</summary>
    private static int Add(CanonicalArena arena, in Item item, int rows)
    {
        int added = !item.Views
            ? arena.AddPrimitive(item.DType, rows, item.Validity, item.PType, item.Into)
            : arena.AddVarBinView(item.DType, rows, item.Validity, item.Into, item.Heap.IsEmpty ? [] : [item.Heap], item.Padded);
        return item.Extension is { } extension ? arena.AddExtension(extension, rows, added) : added;
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
