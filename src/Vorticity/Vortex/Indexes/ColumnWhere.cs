using System;
using System.Collections.Generic;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// Where each value of a column lives in a file whose rows follow a key (<see cref="IndexKinds.ColumnWhere"/>): the
/// column's values in the order of their bytes, each with the number the column's block statistics name it by
/// (<see cref="ColumnBlocks"/>) and the ranges of rows of the blocks it appears in, or none for a value that lives
/// <em>everywhere</em>, in so many blocks that its ranges would spare a reader nothing. A filter on a value reads its
/// ranges alone, whatever column it is on: one the key starts with, or one that depends on it.
/// </summary>
/// <remarks>
/// Encoded as its version, the file's rows and its count of values, then each value: its bytes as those it shares with
/// the value before it, a count, and the rest, its length first; its number; its count of ranges, then each range as the
/// rows from the end of the one before it and its rows.
/// </remarks>
internal sealed class ColumnWhere
{
    private const byte Version = 1;

    private readonly int[] _rangeStarts;
    private readonly long[] _starts;
    private readonly long[] _ends;
    private int[]? _byId;

    private ColumnWhere(long rows, byte[][] values, int[] ids, int[] rangeStarts, long[] starts, long[] ends)
    {
        Rows = rows;
        Values = values;
        Ids = ids;
        _rangeStarts = rangeStarts;
        _starts = starts;
        _ends = ends;
    }

    /// <summary>The file's rows.</summary>
    internal long Rows { get; }

    /// <summary>The values, in the order of their bytes, as the column stores them.</summary>
    internal byte[][] Values { get; }

    /// <summary>Each value's number, by its place in <see cref="Values"/>.</summary>
    internal int[] Ids { get; }

    /// <summary>The place in <see cref="Values"/> of <paramref name="value"/>; -1 when the column holds no such value.</summary>
    internal int Find(ReadOnlySpan<byte> value)
    {
        int low = 0;
        int high = Values.Length - 1;
        while (low <= high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            int order = Values[middle].AsSpan().SequenceCompareTo(value);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>The place in <see cref="Values"/> of the value numbered <paramref name="id"/>; -1 for a number no value has.</summary>
    internal int PlaceOf(int id)
    {
        if (_byId is null)
        {
            int[] byId = new int[Ids.Length == 0 ? 0 : MaxOf(Ids) + 1];
            byId.AsSpan().Fill(-1);
            for (int place = 0; place < Ids.Length; place++)
            {
                byId[Ids[place]] = place;
            }

            _byId = byId;
        }

        return (uint)id < (uint)_byId.Length ? _byId[id] : -1;
    }

    private static int MaxOf(int[] ids)
    {
        int max = 0;
        foreach (int id in ids)
        {
            max = Math.Max(max, id);
        }

        return max;
    }

    /// <summary>Whether the value at <paramref name="place"/> lives everywhere: its ranges are not listed.</summary>
    internal bool Everywhere(int place) => _rangeStarts[place] == _rangeStarts[place + 1];

    /// <summary>The ranges of rows of the value at <paramref name="place"/>, ascending and apart; empty for one that lives everywhere.</summary>
    internal IEnumerable<(long Start, long End)> RangesOf(int place)
    {
        for (int r = _rangeStarts[place]; r < _rangeStarts[place + 1]; r++)
        {
            yield return (_starts[r], _ends[r]);
        }
    }

    /// <summary>The bytes the index holds in memory.</summary>
    internal long Bytes
    {
        get
        {
            long bytes = (_starts.Length * 2L * sizeof(long)) + (Ids.Length * 2L * sizeof(int));
            foreach (byte[] value in Values)
            {
                bytes += value.Length + 24;
            }

            return bytes;
        }
    }

    /// <summary>
    /// The bytes of the index of a file of <paramref name="rows"/> rows: <paramref name="values"/> in the order of their
    /// bytes, each with its number of <paramref name="ids"/> and its ranges of rows, those of value <c>v</c> from
    /// <c>rangeStarts[v]</c> to <c>rangeStarts[v + 1]</c> of <paramref name="starts"/> and <paramref name="ends"/>, none for
    /// a value that lives everywhere.
    /// </summary>
    internal static byte[] ToBytes(long rows, IReadOnlyList<byte[]> values, ReadOnlySpan<int> ids, ReadOnlySpan<int> rangeStarts, ReadOnlySpan<long> starts, ReadOnlySpan<long> ends)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteVarint(Version);
            writer.WriteVarint((ulong)rows);
            writer.WriteVarint((ulong)values.Count);
            ReadOnlySpan<byte> before = [];
            for (int v = 0; v < values.Count; v++)
            {
                ReadOnlySpan<byte> value = values[v];
                int shared = value.CommonPrefixLength(before);
                writer.WriteVarint((ulong)shared);
                writer.WriteVarint((ulong)(value.Length - shared));
                writer.WriteRawBytes(value[shared..]);
                before = value;
                writer.WriteVarint((ulong)ids[v]);
                writer.WriteVarint((ulong)(rangeStarts[v + 1] - rangeStarts[v]));
                long end = 0;
                for (int r = rangeStarts[v]; r < rangeStarts[v + 1]; r++)
                {
                    writer.WriteVarint((ulong)(starts[r] - end));
                    writer.WriteVarint((ulong)(ends[r] - starts[r]));
                    end = ends[r];
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The index <paramref name="bytes"/> encode, or null when they do not decode as one of this version.</summary>
    internal static ColumnWhere? From(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            long rows = checked((long)reader.ReadVarint());
            ulong count = reader.ReadVarint();
            if (count > (ulong)bytes.Length)
            {
                return null;
            }

            byte[][] values = new byte[(int)count][];
            int[] ids = new int[values.Length];
            int[] rangeStarts = new int[values.Length + 1];
            List<long> starts = [];
            List<long> ends = [];
            byte[] before = [];
            for (int v = 0; v < values.Length; v++)
            {
                ulong shared = reader.ReadVarint();
                if (shared > (ulong)before.Length)
                {
                    return null;
                }

                ReadOnlySpan<byte> tail = reader.ReadLengthDelimited();
                byte[] value = new byte[(int)shared + tail.Length];
                before.AsSpan(0, (int)shared).CopyTo(value);
                tail.CopyTo(value.AsSpan((int)shared));
                if (v > 0 && value.AsSpan().SequenceCompareTo(before) <= 0)
                {
                    return null;
                }

                values[v] = before = value;
                ids[v] = checked((int)reader.ReadVarint());
                rangeStarts[v] = starts.Count;
                ulong ranges = reader.ReadVarint();
                if (ranges > (ulong)bytes.Length)
                {
                    return null;
                }

                long end = 0;
                for (ulong r = 0; r < ranges; r++)
                {
                    long start = checked(end + (long)reader.ReadVarint());
                    end = checked(start + (long)reader.ReadVarint());
                    if (end <= start || end > rows || (starts.Count > rangeStarts[v] && start <= ends[^1]))
                    {
                        return null;
                    }

                    starts.Add(start);
                    ends.Add(end);
                }
            }

            rangeStarts[values.Length] = starts.Count;
            return reader.End ? new ColumnWhere(rows, values, ids, rangeStarts, [.. starts], [.. ends]) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
