using System;
using Vorticity.Aggregating;
using Vorticity.Serialization;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// One column's statistics over the blocks of a file whose rows follow a key (<see cref="IndexKinds.BlockStats"/>,
/// <see cref="BlockTable"/>): each block's rows and nulls; the values it holds by their numbers in the column's
/// <see cref="ColumnWhere"/>, with each one's rows, while it holds at most <see cref="ListedValues"/> of them, and their
/// count past that; and for a measure, its count of values, exact sum, least and greatest, in
/// <see cref="MeasureLayout"/>'s words. A block whose columns of a question hold one value each is that question's
/// answer for its rows, read from these alone.
/// </summary>
/// <remarks>
/// Encoded as its version, its count of blocks and the parts it holds, then a section a part: each block's rows and nulls;
/// each block's values, their count then, for a block of at most <see cref="ListedValues"/>, their numbers ascending as
/// the gaps between them and, past one value, each one's rows; each block's measure, its count of values then, for a
/// block of one or more, its sum's words and its extremes. Sections rather than blocks, so that a writer that gives a
/// part up midway drops its section whole.
/// </remarks>
internal sealed class ColumnBlocks
{
    /// <summary>The values a block lists at most, each with its rows; past them it gives their count alone.</summary>
    internal const int ListedValues = 256;

    private const byte Version = 1;

    // The parts a file's statistics hold, and a measure's kind past them.
    private const int HoldsValues = 1;
    private const int HoldsMeasure = 2;
    private const int KindShift = 2;

    private readonly long[] _starts;

    private ColumnBlocks(long[] rows, long[] nulls)
    {
        Rows = rows;
        Nulls = nulls;
        _starts = new long[rows.Length + 1];
        for (int b = 0; b < rows.Length; b++)
        {
            _starts[b + 1] = _starts[b] + rows[b];
        }
    }

    /// <summary>The blocks.</summary>
    internal int Count => Rows.Length;

    /// <summary>Each block's rows.</summary>
    internal long[] Rows { get; }

    /// <summary>Each block's nulls.</summary>
    internal long[] Nulls { get; }

    /// <summary>The first row of block <paramref name="block"/> in the file; <see cref="Count"/> gives the file's rows.</summary>
    internal long FirstRow(int block) => _starts[block];

    /// <summary>The rows the blocks cover.</summary>
    internal long TotalRows => _starts[^1];

    /// <summary>Whether the blocks list their values (<see cref="Distinct"/>, <see cref="ValuesOf"/>).</summary>
    internal bool HoldsValueLists => Distinct is not null;

    /// <summary>Each block's distinct values; null when the statistics hold no values.</summary>
    internal int[]? Distinct { get; private init; }

    // Where each block's values lie in the lists, a block past ListedValues listing none; the lists, numbers and rows.
    private int[] _listStarts = [];
    private int[] _ids = [];
    private long[] _valueRows = [];

    /// <summary>
    /// The numbers of the values block <paramref name="block"/> holds, ascending, and each one's rows; empty for a block of
    /// no value or of more than <see cref="ListedValues"/>, whose <see cref="Distinct"/> says which.
    /// </summary>
    internal (ReadOnlyMemory<int> Ids, ReadOnlyMemory<long> Rows) ValuesOf(int block)
    {
        int from = _listStarts[block];
        int to = _listStarts[block + 1];
        return (_ids.AsMemory(from, to - from), _valueRows.AsMemory(from, to - from));
    }

    /// <summary>The measure's kind; null when the statistics hold no measure.</summary>
    internal MeasureKind? Measure { get; private init; }

    // Each block's measure, MeasureLayout's words a block.
    private long[] _words = [];

    /// <summary>Block <paramref name="block"/>'s measure: its count of values, sum, least and greatest in <see cref="MeasureLayout"/>'s words.</summary>
    internal ReadOnlySpan<long> MeasureOf(int block)
    {
        int width = MeasureLayout.Words(Measure == MeasureKind.Float);
        return _words.AsSpan(block * width, width);
    }

    /// <summary>The bytes the statistics hold in memory.</summary>
    internal long Bytes =>
        (Rows.Length * (2L * sizeof(long) + 2 * sizeof(int))) + (_ids.Length * (long)(sizeof(int) + sizeof(long))) + (_words.Length * (long)sizeof(long));

    /// <summary>Writes the header of statistics over <paramref name="blocks"/> blocks holding the parts named, its sections following.</summary>
    internal static void WriteHeader(ref ProtoWriter writer, int blocks, bool values, MeasureKind? measure)
    {
        writer.WriteVarint(Version);
        writer.WriteVarint((ulong)blocks);
        int parts = (values ? HoldsValues : 0) | (measure is { } kind ? HoldsMeasure | ((int)kind << KindShift) : 0);
        writer.WriteVarint((ulong)parts);
    }

    /// <summary>Writes a block's rows and nulls, in the first section.</summary>
    internal static void WriteRows(ref ProtoWriter writer, long rows, long nulls)
    {
        writer.WriteVarint((ulong)rows);
        writer.WriteVarint((ulong)nulls);
    }

    /// <summary>
    /// Writes a block's values, in the values' section: <paramref name="distinct"/> of them, and while they are at most
    /// <see cref="ListedValues"/>, their numbers <paramref name="ids"/> ascending and, past one, each one's rows.
    /// </summary>
    internal static void WriteValues(ref ProtoWriter writer, int distinct, ReadOnlySpan<int> ids, ReadOnlySpan<long> rows)
    {
        writer.WriteVarint((ulong)distinct);
        if (distinct > ListedValues)
        {
            return;
        }

        int before = -1;
        foreach (int id in ids)
        {
            writer.WriteVarint((ulong)(id - before - 1));
            before = id;
        }

        for (int i = 0; distinct > 1 && i < rows.Length; i++)
        {
            writer.WriteVarint((ulong)rows[i]);
        }
    }

    /// <summary>Writes a block's measure of <paramref name="kind"/>, its words in <see cref="MeasureLayout"/>'s layout, in the measure's section.</summary>
    internal static void WriteMeasure(ref ProtoWriter writer, MeasureKind kind, ReadOnlySpan<long> words)
    {
        long count = words[MeasureLayout.Values];
        writer.WriteVarint((ulong)count);
        if (count == 0)
        {
            return;
        }

        bool floats = kind == MeasureKind.Float;
        for (int w = 0; w < MeasureLayout.SumLength(floats); w++)
        {
            writer.WriteVarint(Varint.ZigZagEncode64(words[MeasureLayout.Sum + w]));
        }

        long least = words[MeasureLayout.Least(floats)];
        long greatest = words[MeasureLayout.Greatest(floats)];
        switch (kind)
        {
            case MeasureKind.Float:
                Span<byte> bits = stackalloc byte[2 * sizeof(long)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bits, least);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bits[sizeof(long)..], greatest);
                writer.WriteRawBytes(bits);
                break;
            case MeasureKind.Signed:
                writer.WriteVarint(Varint.ZigZagEncode64(least));
                writer.WriteVarint(Varint.ZigZagEncode64(greatest));
                break;
            default:
                writer.WriteVarint((ulong)least);
                writer.WriteVarint((ulong)greatest);
                break;
        }
    }

    /// <summary>The statistics <paramref name="bytes"/> encode, or null when they do not decode as statistics of this version.</summary>
    internal static ColumnBlocks? From(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
            {
                return null;
            }

            ulong count = reader.ReadVarint();
            ulong parts = reader.ReadVarint();
            int kind = (int)(parts >> KindShift);
            if (count > (ulong)bytes.Length || parts >= 1UL << (KindShift + 2) || kind > (int)MeasureKind.Float
                || ((parts & HoldsMeasure) == 0 && kind != 0))
            {
                return null;
            }

            int blocks = (int)count;
            long[] rows = new long[blocks];
            long[] nulls = new long[blocks];
            for (int b = 0; b < blocks; b++)
            {
                rows[b] = checked((long)reader.ReadVarint());
                nulls[b] = checked((long)reader.ReadVarint());
                if (rows[b] <= 0 || nulls[b] > rows[b])
                {
                    return null;
                }
            }

            int[]? distinct = null;
            int[] listStarts = new int[blocks + 1];
            int[] ids = [];
            long[] valueRows = [];
            if ((parts & HoldsValues) != 0)
            {
                distinct = new int[blocks];
                System.Collections.Generic.List<int> listed = [];
                System.Collections.Generic.List<long> listedRows = [];
                for (int b = 0; b < blocks; b++)
                {
                    listStarts[b] = listed.Count;
                    ulong held = reader.ReadVarint();
                    long valid = rows[b] - nulls[b];
                    if (held > (ulong)valid || (held == 0) != (valid == 0))
                    {
                        return null;
                    }

                    distinct[b] = (int)held;
                    if (held > ListedValues)
                    {
                        continue;
                    }

                    long id = -1;
                    for (int i = 0; i < (int)held; i++)
                    {
                        id = checked(id + 1 + (long)reader.ReadVarint());
                        listed.Add(checked((int)id));
                    }

                    long sum = 0;
                    for (int i = 0; i < (int)held; i++)
                    {
                        long taken = held == 1 ? valid : checked((long)reader.ReadVarint());
                        if (taken <= 0)
                        {
                            return null;
                        }

                        sum += taken;
                        listedRows.Add(taken);
                    }

                    if (sum != valid)
                    {
                        return null;
                    }
                }

                listStarts[blocks] = listed.Count;
                ids = [.. listed];
                valueRows = [.. listedRows];
            }

            MeasureKind? measure = (parts & HoldsMeasure) != 0 ? (MeasureKind)kind : null;
            long[] words = [];
            if (measure is { } measured)
            {
                bool floats = measured == MeasureKind.Float;
                int width = MeasureLayout.Words(floats);
                words = new long[blocks * width];
                for (int b = 0; b < blocks; b++)
                {
                    Span<long> block = words.AsSpan(b * width, width);
                    long values = checked((long)reader.ReadVarint());
                    if (values > rows[b] - nulls[b])
                    {
                        return null;
                    }

                    block[MeasureLayout.Values] = values;
                    if (values == 0)
                    {
                        if (floats)
                        {
                            block[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(double.NaN);
                            block[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(double.NaN);
                        }

                        continue;
                    }

                    for (int w = 0; w < MeasureLayout.SumLength(floats); w++)
                    {
                        block[MeasureLayout.Sum + w] = Varint.ZigZagDecode64(reader.ReadVarint());
                    }

                    (block[MeasureLayout.Least(floats)], block[MeasureLayout.Greatest(floats)]) = measured switch
                    {
                        MeasureKind.Float => ((long)reader.ReadFixed64(), (long)reader.ReadFixed64()),
                        MeasureKind.Signed => (Varint.ZigZagDecode64(reader.ReadVarint()), Varint.ZigZagDecode64(reader.ReadVarint())),
                        _ => ((long)reader.ReadVarint(), (long)reader.ReadVarint()),
                    };
                }
            }

            return reader.End
                ? new ColumnBlocks(rows, nulls) { Distinct = distinct, _listStarts = listStarts, _ids = ids, _valueRows = valueRows, Measure = measure, _words = words }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
