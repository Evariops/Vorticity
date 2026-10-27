using System;
using System.Threading;
using Vorticity.Aggregating;
using Vorticity.Expressions;
using Vorticity.Serialization;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// One column's statistics over the granules of a file whose rows follow a key (<see cref="IndexKinds.GranuleStats"/>,
/// spec D, 6): each granule's rows and nulls; the values it holds by their numbers, with each one's rows, while it holds at
/// most <see cref="ListedValues"/> of them, and their count past that, each number's value in the statistics'
/// dictionary; and for a measure, its count of values, exact sum, least and greatest, in <see cref="MeasureLayout"/>'s
/// words. A granule whose columns of a question hold one value each is that question's answer for its rows, read from these
/// alone.
/// </summary>
/// <remarks>
/// A granule is the writer's block, the zone of the zone maps and the place of the sparse index. Encoded as its version,
/// its count of granules and the parts it holds, then a section a part: each granule's rows and nulls; each granule's
/// values, their count then, for a granule of at most <see cref="ListedValues"/>, their numbers ascending as the gaps
/// between them and, past one value, each one's rows; each granule's measure, its count of values then, for a granule of
/// one or more, its sum's words and its extremes; and with the values, their dictionary: each number's value, its length
/// then its bytes. Sections rather than granules, so that a writer that gives a part up midway drops its section whole.
/// </remarks>
internal sealed class GranuleStats
{
    /// <summary>The values a granule lists at most, each with its rows; past them it gives their count alone.</summary>
    internal const int ListedValues = 256;

    private const byte Version = 2;

    // The parts a file's statistics hold, and a measure's kind past them.
    private const int HoldsValues = 1;
    private const int HoldsMeasure = 2;
    private const int KindShift = 2;

    private readonly long[] _starts;

    private GranuleStats(long[] rows, long[] nulls)
    {
        Rows = rows;
        Nulls = nulls;
        _starts = new long[rows.Length + 1];
        for (int b = 0; b < rows.Length; b++)
        {
            _starts[b + 1] = _starts[b] + rows[b];
        }
    }

    /// <summary>The granules.</summary>
    internal int Count => Rows.Length;

    /// <summary>Each granule's rows.</summary>
    internal long[] Rows { get; }

    /// <summary>Each granule's nulls.</summary>
    internal long[] Nulls { get; }

    /// <summary>The first row of granule <paramref name="granule"/> in the file; <see cref="Count"/> gives the file's rows.</summary>
    internal long FirstRow(int granule) => _starts[granule];

    /// <summary>The rows the granules cover.</summary>
    internal long TotalRows => _starts[^1];

    /// <summary>Whether the granules list their values (<see cref="Distinct"/>, <see cref="ValuesOf"/>).</summary>
    internal bool HoldsValueLists => Distinct is not null;

    /// <summary>Each granule's distinct values; null when the statistics hold no values.</summary>
    internal int[]? Distinct { get; private init; }

    // Where each granule's values lie in the lists, a granule past ListedValues listing none; the lists, numbers and rows.
    private int[] _listStarts = [];
    private int[] _ids = [];
    private long[] _valueRows = [];

    // Each number's value, as the column stores it, while the granules list their values.
    private byte[][] _dictionary = [];

    /// <summary>The values the dictionary names, numbered from 0.</summary>
    internal int Named => _dictionary.Length;

    /// <summary>The value numbered <paramref name="id"/>, as the column stores it; null for a number the statistics do not name.</summary>
    internal byte[]? ValueOf(int id) => (uint)id < (uint)_dictionary.Length ? _dictionary[id] : null;

    /// <summary>
    /// The numbers of the values granule <paramref name="granule"/> holds, ascending, and each one's rows; empty for a granule of
    /// no value or of more than <see cref="ListedValues"/>, whose <see cref="Distinct"/> says which.
    /// </summary>
    internal (ReadOnlyMemory<int> Ids, ReadOnlyMemory<long> Rows) ValuesOf(int granule)
    {
        int from = _listStarts[granule];
        int to = _listStarts[granule + 1];
        return (_ids.AsMemory(from, to - from), _valueRows.AsMemory(from, to - from));
    }

    /// <summary>The measure's kind; null when the statistics hold no measure.</summary>
    internal MeasureKind? Measure { get; private init; }

    // Each granule's measure, MeasureLayout's words a granule.
    private long[] _words = [];

    /// <summary>Granule <paramref name="granule"/>'s measure: its count of values, sum, least and greatest in <see cref="MeasureLayout"/>'s words.</summary>
    internal ReadOnlySpan<long> MeasureOf(int granule)
    {
        int width = MeasureLayout.Words(Measure == MeasureKind.Float);
        return _words.AsSpan(granule * width, width);
    }

    /// <summary>The least and the greatest value of granule <paramref name="granule"/>'s measure; null for none, or a float's NaN.</summary>
    internal (FilterLiteral? Least, FilterLiteral? Greatest) MeasureBounds(int granule)
    {
        if (Measure is not { } kind)
        {
            return (null, null);
        }

        bool floats = kind == MeasureKind.Float;
        ReadOnlySpan<long> words = MeasureOf(granule);
        if (words[MeasureLayout.Values] <= 0)
        {
            return (null, null);
        }

        long least = words[MeasureLayout.Least(floats)];
        long greatest = words[MeasureLayout.Greatest(floats)];
        return kind switch
        {
            MeasureKind.Float when !double.IsNaN(BitConverter.Int64BitsToDouble(least)) =>
                (FilterLiteral.From(BitConverter.Int64BitsToDouble(least)), FilterLiteral.From(BitConverter.Int64BitsToDouble(greatest))),
            MeasureKind.Float => (null, null),
            MeasureKind.Signed => (FilterLiteral.From(least), FilterLiteral.From(greatest)),
            _ => (FilterLiteral.From((ulong)least), FilterLiteral.From((ulong)greatest)),
        };
    }

    // What the readers of the statistics decode once and keep with them, the first to ask for every one after it: each
    // named value as a literal, each granule's least value of its measure as the column stores it; null before.
    private FilterLiteral?[]? _literals;
    private byte[]?[]? _measureLeast;

    /// <summary>Each named value as the literal <paramref name="layout"/>, the column's, makes of it, by its number.</summary>
    internal FilterLiteral?[] Literals(in KeyLayout layout)
    {
        if (Volatile.Read(ref _literals) is { } decoded)
        {
            return decoded;
        }

        FilterLiteral?[] literals = new FilterLiteral?[_dictionary.Length];
        for (int id = 0; id < literals.Length; id++)
        {
            literals[id] = layout.ToLiteral(_dictionary[id]);
        }

        return Interlocked.CompareExchange(ref _literals, literals, null) ?? literals;
    }

    /// <summary>
    /// Granule <paramref name="granule"/>'s least value of its measure as the column stores it, through <paramref name="layout"/>,
    /// the column's; null for none.
    /// </summary>
    internal byte[]? MeasureLeast(int granule, in KeyLayout layout) => (Volatile.Read(ref _measureLeast) ?? EncodeMeasureLeast(layout))[granule];

    private byte[]?[] EncodeMeasureLeast(in KeyLayout layout)
    {
        byte[]?[] least = new byte[]?[Count];
        Span<byte> scratch = stackalloc byte[sizeof(ulong)];
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        for (int granule = 0; granule < least.Length; granule++)
        {
            if (MeasureBounds(granule).Least is { } value && layout.TryEncode(value, scratch, out ReadOnlySpan<byte> key, zero, out _))
            {
                least[granule] = key.ToArray();
            }
        }

        return Interlocked.CompareExchange(ref _measureLeast, least, null) ?? least;
    }

    /// <summary>The bytes the statistics hold in memory.</summary>
    internal long Bytes =>
        (Rows.Length * (2L * sizeof(long) + 2 * sizeof(int))) + (_ids.Length * (long)(sizeof(int) + sizeof(long))) + (_words.Length * (long)sizeof(long))
        + DictionaryBytes();

    private long DictionaryBytes()
    {
        long bytes = 0;
        foreach (byte[] value in _dictionary)
        {
            bytes += value.Length + 24;
        }

        return bytes;
    }

    /// <summary>Writes the header of statistics over <paramref name="granules"/> granules holding the parts named, its sections following.</summary>
    internal static void WriteHeader(ref ProtoWriter writer, int granules, bool values, MeasureKind? measure)
    {
        writer.WriteVarint(Version);
        writer.WriteVarint((ulong)granules);
        int parts = (values ? HoldsValues : 0) | (measure is { } kind ? HoldsMeasure | ((int)kind << KindShift) : 0);
        writer.WriteVarint((ulong)parts);
    }

    /// <summary>Writes a granule's rows and nulls, in the first section.</summary>
    internal static void WriteRows(ref ProtoWriter writer, long rows, long nulls)
    {
        writer.WriteVarint((ulong)rows);
        writer.WriteVarint((ulong)nulls);
    }

    /// <summary>
    /// Writes a granule's values, in the values' section: <paramref name="distinct"/> of them, and while they are at most
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

    /// <summary>Writes each number's value, the values' dictionary, the last section, after the measure's.</summary>
    internal static void WriteDictionary(ref ProtoWriter writer, byte[]?[] values)
    {
        writer.WriteVarint((ulong)values.Length);
        foreach (byte[]? value in values)
        {
            writer.WriteVarint((ulong)value!.Length);
            writer.WriteRawBytes(value);
        }
    }

    /// <summary>Writes a granule's measure of <paramref name="kind"/>, its words in <see cref="MeasureLayout"/>'s layout, in the measure's section.</summary>
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
    internal static GranuleStats? From(ReadOnlySpan<byte> bytes)
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

            int granules = (int)count;
            long[] rows = new long[granules];
            long[] nulls = new long[granules];
            for (int b = 0; b < granules; b++)
            {
                rows[b] = checked((long)reader.ReadVarint());
                nulls[b] = checked((long)reader.ReadVarint());
                if (rows[b] <= 0 || nulls[b] > rows[b])
                {
                    return null;
                }
            }

            int[]? distinct = null;
            int[] listStarts = new int[granules + 1];
            int[] ids = [];
            long[] valueRows = [];
            if ((parts & HoldsValues) != 0)
            {
                distinct = new int[granules];
                System.Collections.Generic.List<int> listed = [];
                System.Collections.Generic.List<long> listedRows = [];
                for (int b = 0; b < granules; b++)
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

                listStarts[granules] = listed.Count;
                ids = [.. listed];
                valueRows = [.. listedRows];
            }

            MeasureKind? measure = (parts & HoldsMeasure) != 0 ? (MeasureKind)kind : null;
            long[] words = [];
            if (measure is { } measured)
            {
                bool floats = measured == MeasureKind.Float;
                int width = MeasureLayout.Words(floats);
                words = new long[granules * width];
                for (int b = 0; b < granules; b++)
                {
                    Span<long> granule = words.AsSpan(b * width, width);
                    long values = checked((long)reader.ReadVarint());
                    if (values > rows[b] - nulls[b])
                    {
                        return null;
                    }

                    granule[MeasureLayout.Values] = values;
                    if (values == 0)
                    {
                        if (floats)
                        {
                            granule[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(double.NaN);
                            granule[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(double.NaN);
                        }

                        continue;
                    }

                    for (int w = 0; w < MeasureLayout.SumLength(floats); w++)
                    {
                        granule[MeasureLayout.Sum + w] = Varint.ZigZagDecode64(reader.ReadVarint());
                    }

                    (granule[MeasureLayout.Least(floats)], granule[MeasureLayout.Greatest(floats)]) = measured switch
                    {
                        MeasureKind.Float => ((long)reader.ReadFixed64(), (long)reader.ReadFixed64()),
                        MeasureKind.Signed => (Varint.ZigZagDecode64(reader.ReadVarint()), Varint.ZigZagDecode64(reader.ReadVarint())),
                        _ => ((long)reader.ReadVarint(), (long)reader.ReadVarint()),
                    };
                }
            }

            // The values' dictionary, each number the granules list among it.
            byte[][] dictionary = [];
            if (distinct is not null)
            {
                ulong named = reader.ReadVarint();
                if (named > (ulong)bytes.Length)
                {
                    return null;
                }

                dictionary = new byte[(int)named][];
                for (int id = 0; id < dictionary.Length; id++)
                {
                    dictionary[id] = reader.ReadLengthDelimited().ToArray();
                }

                foreach (int id in ids)
                {
                    if (id >= dictionary.Length)
                    {
                        return null;
                    }
                }
            }

            return reader.End
                ? new GranuleStats(rows, nulls) { Distinct = distinct, _listStarts = listStarts, _ids = ids, _valueRows = valueRows, Measure = measure, _words = words, _dictionary = dictionary }
                : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
