using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// A key of two to four columns: each row's tuple encoded into bytes, one hash per row into one
/// table. A part is a tag byte, 0 for null, then its value: the storage bytes of a fixed-width
/// column, a length and the bytes of a text column; a boolean is its tag, 1 for false and 2 for true.
/// </summary>
internal sealed class CompositeKeys : GroupKeys
{
    private readonly ColumnShape[] _parts;
    private readonly ByteKeyTable _table = new ByteKeyTable();
    private readonly Int128[][] _decimals;
    private readonly Int256[][] _wides;
    private readonly ulong[][] _bits;
    private readonly ulong[][] _validity;
    private byte[] _key = new byte[64];

    internal CompositeKeys(ColumnShape[] parts)
    {
        _parts = parts;
        _decimals = new Int128[parts.Length][];
        _wides = new Int256[parts.Length][];
        _bits = new ulong[parts.Length][];
        _validity = new ulong[parts.Length][];
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
        int count = _parts.Length;
        Part p0 = Prepare(arena, nodes[0], 0);
        Part p1 = Prepare(arena, nodes[1], 1);
        Part p2 = count > 2 ? Prepare(arena, nodes[2], 2) : default;
        Part p3 = count > 3 ? Prepare(arena, nodes[3], 3) : default;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int length = 0;
            Encode(in p0, row, ref length);
            Encode(in p1, row, ref length);
            if (count > 2)
            {
                Encode(in p2, row, ref length);
            }

            if (count > 3)
            {
                Encode(in p3, row, ref length);
            }

            rowGroups[row] = GroupOf(_key.AsSpan(0, length));
        }

        return false;
    }

    internal override GroupKeys Fresh() => new CompositeKeys(_parts);

    internal override void MergeInto(GroupKeys target, Span<int> map)
    {
        CompositeKeys into = (CompositeKeys)target;
        for (int g = 0; g < Count; g++)
        {
            map[g] = into.GroupOf(_table.KeyOf(g));
        }

        MergeSeen(target);
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

    private int GroupOf(ReadOnlySpan<byte> key)
    {
        int group = _table.GetOrAdd(key, out bool added);
        if (added)
        {
            Count = _table.Count;
        }

        return group;
    }

    private Part Prepare(CanonicalArena arena, int node, int index)
    {
        ColumnShape shape = _parts[index];
        Saw(EncodedForms.EncodingOf(arena, node));
        switch (shape.Kind)
        {
            case StorageKind.Bool:
            {
                ReadOnlySpan<ulong> bits = BoolWords.Values(arena, ref node, ref _bits[index]);
                return new Part(StorageKind.Bool, BoolWords.Validity(arena, node, ref _validity[index])) { Bits = bits };
            }

            case StorageKind.Bytes:
            {
                BytesBlock block = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);
                return new Part(StorageKind.Bytes, validity) { Bytes = block };
            }

            case StorageKind.Decimal:
            {
                ReadOnlySpan<Int128> values = FixedReader.Values(arena, node, StorageKind.Decimal, ref _decimals[index], out ReadOnlySpan<ulong> validity);
                return new Part(StorageKind.Decimal, validity) { Fixed = MemoryMarshal.AsBytes(values), Width = 16 };
            }

            case StorageKind.Decimal256:
            {
                ReadOnlySpan<Int256> values = FixedReader.Values(arena, node, StorageKind.Decimal256, ref _wides[index], out ReadOnlySpan<ulong> validity);
                return new Part(StorageKind.Decimal256, validity) { Fixed = MemoryMarshal.AsBytes(values), Width = Int256.ByteCount };
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

    private void Encode(in Part part, int row, ref int length)
    {
        bool valid = StorageValues.IsValid(part.Validity, row);
        switch (part.Kind)
        {
            case StorageKind.Bool:
            {
                Reserve(length + 1);
                _key[length++] = !valid ? (byte)0 : ((part.Bits[row >> 6] >> (row & 63)) & 1) != 0 ? (byte)2 : (byte)1;
                return;
            }

            case StorageKind.Bytes:
            {
                if (!valid)
                {
                    Reserve(length + 1);
                    _key[length++] = 0;
                    return;
                }

                ReadOnlySpan<byte> value = part.Bytes[row];
                Reserve(length + 5 + value.Length);
                _key[length] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(_key.AsSpan(length + 1), value.Length);
                value.CopyTo(_key.AsSpan(length + 5));
                length += 5 + value.Length;
                return;
            }

            default:
            {
                if (!valid)
                {
                    Reserve(length + 1);
                    _key[length++] = 0;
                    return;
                }

                Reserve(length + 1 + part.Width);
                _key[length] = 1;
                part.Fixed.Slice(row * part.Width, part.Width).CopyTo(_key.AsSpan(length + 1));
                length += 1 + part.Width;
                return;
            }
        }
    }

    private void Reserve(int length)
    {
        if (_key.Length < length)
        {
            Array.Resize(ref _key, Math.Max(length, _key.Length * 2));
        }
    }

    /// <summary>One key column of one batch, ready to encode row by row.</summary>
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
