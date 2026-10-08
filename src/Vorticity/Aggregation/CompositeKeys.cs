using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types.Numerics;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A key of two columns or more: each row's tuple encoded into bytes, one hash per row into one
/// table. A part is a tag byte, 0 for null, then its value: the storage bytes of a fixed-width
/// column, a length and the bytes of a text column; a boolean is its tag, 1 for false and 2 for true.
/// </summary>
/// <remarks>
/// The tuples are encoded a column at a time, whatever the number of parts, over windows of
/// <see cref="Window"/> rows: each part adds its length to its rows, the lengths place every row's
/// key in one buffer, each part writes its bytes at the rows' cursors, and the keys are hashed. A
/// part's conversions run once a batch (<see cref="Load"/>); its spans are taken again for each
/// window (<see cref="View"/>), which costs a lookup. The buffers are a window's, not a batch's.
/// </remarks>
internal sealed class CompositeKeys : GroupKeys
{
    /// <summary>The rows encoded together: their lengths, cursors and keys stay in the cache.</summary>
    private const int Window = 4_096;

    private readonly ColumnShape[] _parts;
    private readonly ByteKeyTable _table;
    private readonly Int128[][] _decimals;
    private readonly Int256[][] _wides;
    private readonly ulong[][] _bits;
    private readonly ulong[][] _validity;
    private readonly bool[] _widened;
    private readonly int[] _lengthsOf;

    // The length a part adds to every row when it holds no null and has a fixed width, or 0: such a
    // part is not measured row by row.
    private readonly int[] _uniform;
    private byte[] _key = new byte[64];
    private int[] _lengths = [];
    private int[] _offsets = [];
    private int[] _cursors = [];

    /// <param name="parts">The key's columns.</param>
    /// <param name="shelf">The lane's shelf the table grows from, under its query's memory; null for a table nothing counts.</param>
    internal CompositeKeys(ColumnShape[] parts, ArrayShelf? shelf = null)
    {
        _parts = parts;
        _table = new ByteKeyTable(shelf);
        _decimals = new Int128[parts.Length][];
        _wides = new Int256[parts.Length][];
        _bits = new ulong[parts.Length][];
        _validity = new ulong[parts.Length][];
        _widened = new bool[parts.Length];
        _lengthsOf = new int[parts.Length];
        _uniform = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            _decimals[i] = [];
            _wides[i] = [];
            _bits[i] = [];
            _validity[i] = [];
        }
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int uniform = 0;
        for (int p = 0; p < _parts.Length; p++)
        {
            Load(arena, nodes[p], p);
            uniform += _uniform[p];
        }

        int window = Math.Min(rows, Window);
        Scratch.Grow(ref _lengths, window);
        Scratch.Grow(ref _offsets, window);
        Scratch.Grow(ref _cursors, window);
        for (int first = 0; first < rows; first += Window)
        {
            int end = Math.Min(rows, first + Window);
            Span<int> lengths = _lengths.AsSpan(0, end - first);
            lengths.Fill(uniform);
            for (int p = 0; p < _parts.Length; p++)
            {
                if (_uniform[p] == 0)
                {
                    Part part = View(arena, nodes[p], p);
                    Measure(in part, first, end, selection, lengths);
                }
            }

            Span<int> offsets = _offsets.AsSpan(0, end - first);
            Span<int> cursors = _cursors.AsSpan(0, end - first);
            int total = 0;
            RowCursor placed = new RowCursor(selection, first, end);
            while (placed.Next(out int row))
            {
                int at = row - first;
                offsets[at] = total;
                cursors[at] = total;
                total += lengths[at];
            }

            if (total == 0)
            {
                continue;
            }

            Scratch.Grow(ref _key, total);
            Span<byte> keys = _key.AsSpan(0, total);
            for (int p = 0; p < _parts.Length; p++)
            {
                Part part = View(arena, nodes[p], p);
                Write(in part, first, end, selection, keys, cursors);
            }

            RowCursor hashed = new RowCursor(selection, first, end);
            while (hashed.Next(out int row))
            {
                int at = row - first;
                rowGroups[row] = GroupOf(_key.AsSpan(offsets[at], lengths[at]));
            }
        }

        return false;
    }

    internal override GroupKeys Fresh() => new CompositeKeys(_parts);

    /// <summary>The table of the encoded tuples, the groups' keys.</summary>
    internal override long Footprint => _table.Footprint;

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // A group is its key's number in the table: the table keeps them, numbered again in order.
        _table.Retain(groups);
        Count = groups.Length;
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        CompositeKeys into = (CompositeKeys)target;
        for (int i = 0; i < groups.Length; i++)
        {
            int g = groups[i];
            map[i] = into.GroupOf(_table.KeyOf(g), HashOf(g));
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = (byte)((seed == MergeHash.Seed ? HashOf(g) : MergeHash.Of(_table.KeyOf(g), seed)) >> shift);
        }
    }

    /// <summary>The hash of group <paramref name="group"/>'s tuple under <see cref="MergeHash.Seed"/>: the table's, unless it took a seed of its own.</summary>
    private ulong HashOf(int group) => _table.Reseeded ? MergeHash.Of(_table.KeyOf(group), MergeHash.Seed) : _table.HashOf(group);

    internal override bool Spills => true;

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new CompositeKeys(_parts, shelf);

    internal override long GrowthFor(int more) => _table.GrowthFor(more);

    internal override void Hashes(Span<ulong> hashes)
    {
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = HashOf(g);
        }
    }

    /// <summary>Each tuple's hash and bytes, its nulls among them: read back, no tuple is hashed again.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            buffer.Write(HashOf(group));
            buffer.WriteBytes(_table.KeyOf(group));
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            ulong hash = reader.Read<ulong>();
            groups[i] = GroupOf(reader.Bytes(), hash);
        }
    }

    internal override int[] Order(bool sorted) => Identity(Count);

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _parts[component];
        return group =>
        {
            ReadOnlySpan<byte> key = _table.KeyOf(group);
            int offset = 0;
            for (int part = 0; part < component; part++)
            {
                offset = Skip(_parts[part], key, offset);
            }

            byte tag = key[offset++];
            if (tag == 0)
            {
                return default!;
            }

            return shape.Kind switch
            {
                StorageKind.Bool => StorageValues.BoolToClr<T>(tag == 2, shape),
                StorageKind.Bytes => StorageValues.BytesToClr<T>(key.Slice(offset + 4, BinaryPrimitives.ReadInt32LittleEndian(key[offset..])), shape),
                _ => StorageValues.Read<T>(key[offset..], shape),
            };
        };
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        ColumnShape shape = _parts[component];
        foreach (int group in groups)
        {
            ReadOnlySpan<byte> key = _table.KeyOf(group);
            int offset = 0;
            for (int part = 0; part < component; part++)
            {
                offset = Skip(_parts[part], key, offset);
            }

            byte tag = key[offset++];
            if (tag == 0)
            {
                KeyStores.AppendNull(store);
            }
            else if (shape.Kind == StorageKind.Bool)
            {
                ((BoolStore)store.Leaf).Append(tag == 2);
            }
            else
            {
                KeyStores.AppendPart(store, key[offset..], shape);
            }
        }
    }

    private static int Skip(ColumnShape shape, ReadOnlySpan<byte> key, int offset)
    {
        byte tag = key[offset++];
        if (tag == 0)
        {
            return offset;
        }

        return shape.Kind switch
        {
            StorageKind.Bool => offset,
            StorageKind.Bytes => offset + 4 + BinaryPrimitives.ReadInt32LittleEndian(key[offset..]),
            _ => offset + StorageValues.Width(shape),
        };
    }

    /// <summary>Adds each selected row's encoded length for one part, over [first, end).</summary>
    private static void Measure(in Part part, int first, int end, ReadOnlySpan<ulong> selection, Span<int> lengths)
    {
        RowCursor cursor = new RowCursor(selection, first, end);
        switch (part.Kind)
        {
            case StorageKind.Bool:
                while (cursor.Next(out int row))
                {
                    lengths[row - first] += 1;
                }

                return;
            case StorageKind.Bytes:
                while (cursor.Next(out int row))
                {
                    lengths[row - first] += StorageValues.IsValid(part.Validity, row) ? 5 + part.Bytes[row].Length : 1;
                }

                return;
            default:
                int width = 1 + part.Width;
                if (part.Validity.IsEmpty)
                {
                    while (cursor.Next(out int row))
                    {
                        lengths[row - first] += width;
                    }

                    return;
                }

                while (cursor.Next(out int row))
                {
                    lengths[row - first] += StorageValues.IsValid(part.Validity, row) ? width : 1;
                }

                return;
        }
    }

    /// <summary>Writes one part of each selected row's key at the row's cursor, and moves the cursor past it.</summary>
    private static void Write(in Part part, int first, int end, ReadOnlySpan<ulong> selection, Span<byte> keys, Span<int> cursors)
    {
        RowCursor cursor = new RowCursor(selection, first, end);
        switch (part.Kind)
        {
            case StorageKind.Bool:
                while (cursor.Next(out int row))
                {
                    keys[cursors[row - first]++] = !StorageValues.IsValid(part.Validity, row)
                        ? (byte)0
                        : ((part.Bits[row >> 6] >> (row & 63)) & 1) != 0 ? (byte)2 : (byte)1;
                }

                return;
            case StorageKind.Bytes:
                while (cursor.Next(out int row))
                {
                    int at = cursors[row - first];
                    if (!StorageValues.IsValid(part.Validity, row))
                    {
                        keys[at] = 0;
                        cursors[row - first] = at + 1;
                        continue;
                    }

                    ReadOnlySpan<byte> value = part.Bytes[row];
                    keys[at] = 1;
                    BinaryPrimitives.WriteInt32LittleEndian(keys[(at + 1)..], value.Length);
                    value.CopyTo(keys[(at + 5)..]);
                    cursors[row - first] = at + 5 + value.Length;
                }

                return;
            default:
                switch (part.Width)
                {
                    case 1:
                        WriteFixed<byte>(in part, first, end, selection, keys, cursors);
                        return;
                    case 2:
                        WriteFixed<ushort>(in part, first, end, selection, keys, cursors);
                        return;
                    case 4:
                        WriteFixed<uint>(in part, first, end, selection, keys, cursors);
                        return;
                    case 8:
                        WriteFixed<ulong>(in part, first, end, selection, keys, cursors);
                        return;
                    case 16:
                        WriteFixed<UInt128>(in part, first, end, selection, keys, cursors);
                        return;
                    default:
                        WriteBytes(in part, first, end, selection, keys, cursors);
                        return;
                }
        }
    }

    /// <summary>Writes a fixed-width part whose width is <typeparamref name="TWord"/>'s: one load and one store a row.</summary>
    private static void WriteFixed<TWord>(in Part part, int first, int end, ReadOnlySpan<ulong> selection, Span<byte> keys, Span<int> cursors)
        where TWord : unmanaged
    {
        ReadOnlySpan<TWord> values = MemoryMarshal.Cast<byte, TWord>(part.Fixed);
        RowCursor cursor = new RowCursor(selection, first, end);
        while (cursor.Next(out int row))
        {
            int at = cursors[row - first];
            if (!StorageValues.IsValid(part.Validity, row))
            {
                keys[at] = 0;
                cursors[row - first] = at + 1;
                continue;
            }

            keys[at] = 1;
            MemoryMarshal.Write(keys[(at + 1)..], in values[row]);
            cursors[row - first] = at + 1 + Unsafe.SizeOf<TWord>();
        }
    }

    /// <summary>Writes a fixed-width part of any other width, a copy a row.</summary>
    private static void WriteBytes(in Part part, int first, int end, ReadOnlySpan<ulong> selection, Span<byte> keys, Span<int> cursors)
    {
        int width = part.Width;
        RowCursor cursor = new RowCursor(selection, first, end);
        while (cursor.Next(out int row))
        {
            int at = cursors[row - first];
            if (!StorageValues.IsValid(part.Validity, row))
            {
                keys[at] = 0;
                cursors[row - first] = at + 1;
                continue;
            }

            keys[at] = 1;
            part.Fixed.Slice(row * width, width).CopyTo(keys[(at + 1)..]);
            cursors[row - first] = at + 1 + width;
        }
    }

    private int GroupOf(ReadOnlySpan<byte> key) => GroupOf(key, MergeHash.Of(key, MergeHash.Seed));

    /// <summary>As <see cref="GroupOf(ReadOnlySpan{byte})"/>, the tuple's hash under <see cref="MergeHash.Seed"/> known.</summary>
    private int GroupOf(ReadOnlySpan<byte> key, ulong hash)
    {
        int group = _table.GetOrAdd(key, hash, out bool added);
        if (added)
        {
            Count = _table.Count;
        }

        return group;
    }

    /// <summary>What a part costs once a batch: its encoding counted, its form materialized, a decimal widened, a boolean's words copied.</summary>
    private void Load(CanonicalArena arena, int node, int index)
    {
        ColumnShape shape = _parts[index];
        Saw(EncodedForms.EncodingOf(arena, node));
        switch (shape.Kind)
        {
            case StorageKind.Bool:
            {
                ReadOnlySpan<ulong> bits = BoolWords.Values(arena, ref node, ref _bits[index]);
                ReadOnlySpan<ulong> validity = BoolWords.Validity(arena, node, ref _validity[index]);
                _lengthsOf[index] = validity.IsEmpty ? -bits.Length : bits.Length;
                _uniform[index] = validity.IsEmpty ? 1 : 0;
                return;
            }

            case StorageKind.Decimal:
            {
                ReadOnlySpan<Int128> values = FixedReader.Values(arena, node, StorageKind.Decimal, ref _decimals[index], out ReadOnlySpan<ulong> validity);
                _widened[index] = Overlaps(MemoryMarshal.AsBytes(values), MemoryMarshal.AsBytes(_decimals[index].AsSpan()));
                _uniform[index] = validity.IsEmpty ? 17 : 0;
                return;
            }

            case StorageKind.Decimal256:
            {
                ReadOnlySpan<Int256> values = FixedReader.Values(arena, node, StorageKind.Decimal256, ref _wides[index], out ReadOnlySpan<ulong> validity);
                _widened[index] = Overlaps(MemoryMarshal.AsBytes(values), MemoryMarshal.AsBytes(_wides[index].AsSpan()));
                _uniform[index] = validity.IsEmpty ? 1 + Int256.ByteCount : 0;
                return;
            }

            case StorageKind.Bytes:
                EncodedForms.Canonical(arena, node);
                _uniform[index] = 0;
                return;

            default:
            {
                int canonical = EncodedForms.Canonical(arena, node);
                _uniform[index] = ArenaWords.Validity(arena, canonical).IsEmpty ? 1 + (shape.Kind == StorageKind.Uuid ? 16 : StorageValues.Width(shape)) : 0;
                return;
            }
        }
    }

    /// <summary>A part's spans, after <see cref="Load"/>: a lookup, not a conversion.</summary>
    private Part View(CanonicalArena arena, int node, int index)
    {
        ColumnShape shape = _parts[index];
        switch (shape.Kind)
        {
            case StorageKind.Bool:
            {
                int words = Math.Abs(_lengthsOf[index]);
                ReadOnlySpan<ulong> validity = _lengthsOf[index] < 0 ? default : _validity[index].AsSpan(0, words);
                return new Part(StorageKind.Bool, validity) { Bits = _bits[index].AsSpan(0, words) };
            }

            case StorageKind.Bytes:
            {
                BytesBlock block = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);
                return new Part(StorageKind.Bytes, validity) { Bytes = block };
            }

            case StorageKind.Decimal:
            {
                int canonical = EncodedForms.Canonical(arena, node);
                ReadOnlySpan<Int128> values = _widened[index]
                    ? _decimals[index].AsSpan(0, arena.RecordRef(canonical).Length)
                    : FixedReader.Values(arena, node, StorageKind.Decimal, ref _decimals[index], out _);
                return new Part(StorageKind.Decimal, ArenaWords.Validity(arena, canonical)) { Fixed = MemoryMarshal.AsBytes(values), Width = 16 };
            }

            case StorageKind.Decimal256:
            {
                int canonical = EncodedForms.Canonical(arena, node);
                ReadOnlySpan<Int256> values = _widened[index]
                    ? _wides[index].AsSpan(0, arena.RecordRef(canonical).Length)
                    : FixedReader.Values(arena, node, StorageKind.Decimal256, ref _wides[index], out _);
                return new Part(StorageKind.Decimal256, ArenaWords.Validity(arena, canonical)) { Fixed = MemoryMarshal.AsBytes(values), Width = Int256.ByteCount };
            }

            case StorageKind.Uuid:
            {
                int canonical = EncodedForms.Canonical(arena, node);
                return new Part(StorageKind.Uuid, ArenaWords.Validity(arena, canonical))
                {
                    Fixed = ColumnData.Values(arena, arena.GetNode(canonical).ElementsIndex),
                    Width = 16,
                };
            }

            default:
            {
                int canonical = EncodedForms.Canonical(arena, node);
                return new Part(StorageKind.Primitive, ArenaWords.Validity(arena, canonical))
                {
                    Fixed = ColumnData.Values(arena, canonical),
                    Width = StorageValues.Width(shape),
                };
            }
        }
    }

    private static bool Overlaps(ReadOnlySpan<byte> values, ReadOnlySpan<byte> scratch) =>
        !scratch.IsEmpty && !values.IsEmpty && values.Overlaps(scratch);

    /// <summary>One key column of one batch, ready to encode.</summary>
    private readonly ref struct Part
    {
        internal Part(StorageKind kind, ReadOnlySpan<ulong> validity)
        {
            Kind = kind;
            Validity = validity;
        }

        internal StorageKind Kind { get; }

        internal ReadOnlySpan<ulong> Validity { get; }

        internal ReadOnlySpan<byte> Fixed { get; init; }

        internal int Width { get; init; }

        internal BytesBlock Bytes { get; init; }

        internal ReadOnlySpan<ulong> Bits { get; init; }
    }
}
