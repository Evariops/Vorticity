using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// One time slice of a file: the rows whose instant falls in <c>[Start, End)</c>, counted in the
/// instant column's unit, which lie at rows <c>[FirstRow, FirstRow + Rows)</c>. A slice of a table bounded by keys
/// states no time: its bounds are the whole of it, <c>[long.MinValue, long.MaxValue)</c>.
/// </summary>
internal readonly record struct TimeSlice(long Start, long End, long FirstRow, long Rows);

/// <summary>
/// The slices of a file: per slice its bounds, its first row and its row count, in order, covering every row once. An
/// entry of the index directory on the column the bounds are on, <see cref="IndexKinds.Slices"/>, whose options hold the
/// table. The bounds are a time's, on the file's instant column, or its keys', on the first column of the key its rows
/// lie in the order of (<see cref="ByKeys"/>).
/// </summary>
/// <remarks>
/// <para>
/// The slices follow each other: each starts at or after the end of the one before, and its rows
/// follow the one before's. A time table is encoded behind a version byte as the instant's unit, the slice count,
/// and per slice the gap from the previous end, the width and the row count, each a varint: a few
/// bytes a slice, a day of minutes in about 6 KiB. A key table, behind its own version byte, holds the slice count, per
/// slice its rows and its first key, as the bytes it shares with the first key before it and the rest, and the last key
/// of the last slice the same way.
/// </para>
/// <para>
/// Like every index it is a hint: a table that does not decode, or whose rows are not the file's,
/// is not read, and the file reads without it.
/// </para>
/// </remarks>
internal sealed class SliceTable
{
    // The slices alone. Version 4 followed them with the order inside each slice and the object's synopsis paths,
    // which a reader skips: an object written then is walked by its slices as before. The versions before it were the
    // branch's own, which no store of a release holds.
    private const byte Version = 5;
    private const byte WithOrder = 4;

    // A table bounded by keys.
    private const byte Keyed = 6;

    // The first key of each slice, and the last key of the last one, row-encoded; null for a time table.
    private readonly byte[][]? _lows;
    private readonly byte[]? _high;

    /// <summary>A table of <paramref name="slices"/> over instants counted in <paramref name="unit"/>.</summary>
    /// <exception cref="ArgumentException">The slices are empty, do not follow each other, or one is empty or inverted.</exception>
    internal SliceTable(TimeUnit unit, IReadOnlyList<TimeSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        if (Problem(slices) is { } problem)
        {
            throw new ArgumentException(problem, nameof(slices));
        }

        Unit = unit;
        Slices = slices;
    }

    private SliceTable(TimeSlice[] slices, byte[][] lows, byte[] high)
    {
        Slices = slices;
        _lows = lows;
        _high = high;
    }

    /// <summary>
    /// A table of the slices of a file whose rows lie in the order of a key: each slice's <paramref name="rows"/>, its first
    /// key in <paramref name="lows"/>, and the last key of the last one, <paramref name="high"/>, row-encoded as the key
    /// orders them, so that a slice's keys lie from its first to the next slice's, or to <paramref name="high"/>.
    /// </summary>
    /// <exception cref="ArgumentException">There is no slice, a slice holds no row, or the keys are not in order.</exception>
    internal static SliceTable ByKeys(IReadOnlyList<long> rows, IReadOnlyList<byte[]> lows, byte[] high)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(high);
        if (rows.Count == 0 || rows.Count != lows.Count)
        {
            throw new ArgumentException($"{rows.Count} slice(s) of rows and {lows.Count} first key(s): a key table holds a first key a slice, and a slice at least.", nameof(rows));
        }

        TimeSlice[] slices = new TimeSlice[rows.Count];
        long row = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            slices[i] = new TimeSlice(long.MinValue, long.MaxValue, row, rows[i]);
            row += rows[i];
        }

        byte[][] held = [.. lows];
        return KeyProblem(slices, held, high) is { } problem
            ? throw new ArgumentException(problem, nameof(lows))
            : new SliceTable(slices, held, high);
    }

    /// <summary>The unit the slices' bounds are counted in: the instant column's, for a time table.</summary>
    internal TimeUnit Unit { get; }

    /// <summary>The slices, in order; those of a key table state no time.</summary>
    internal IReadOnlyList<TimeSlice> Slices { get; }

    /// <summary>The rows the slices cover.</summary>
    internal long Rows => Slices[^1].FirstRow + Slices[^1].Rows;

    /// <summary>Whether the slices are bounded by keys rather than by a time.</summary>
    internal bool ByKey => _lows is not null;

    /// <summary>The first key of slice <paramref name="slice"/> of a key table, row-encoded: no key of the slice is below it.</summary>
    internal ReadOnlySpan<byte> LowOf(int slice) => _lows![slice];

    /// <summary>
    /// A key at or above every key of slice <paramref name="slice"/> of a key table, row-encoded: the next slice's first,
    /// or the last key of the last slice.
    /// </summary>
    internal ReadOnlySpan<byte> CeilingOf(int slice) => slice + 1 < _lows!.Length ? _lows[slice + 1] : _high!;

    /// <summary>Why <paramref name="slices"/> are not a table, or null when they are.</summary>
    private static string? Problem(IReadOnlyList<TimeSlice> slices)
    {
        if (slices.Count == 0)
        {
            return "A slice table holds at least one slice.";
        }

        long row = 0;
        long end = long.MinValue;
        for (int i = 0; i < slices.Count; i++)
        {
            TimeSlice slice = slices[i];
            if (slice.End <= slice.Start || slice.Rows <= 0)
            {
                return $"Slice {i} spans [{slice.Start}, {slice.End}) with {slice.Rows} rows: a slice is a span of time that holds rows.";
            }

            if (slice.Start < end || slice.FirstRow != row)
            {
                return $"Slice {i} starts at {slice.Start}, row {slice.FirstRow}, where the slices before it end at {end}, row {row}: the slices follow each other.";
            }

            row += slice.Rows;
            end = slice.End;
        }

        return null;
    }

    /// <summary>Why a key table's slices and keys are not one, or null when they are.</summary>
    private static string? KeyProblem(TimeSlice[] slices, byte[][] lows, byte[] high)
    {
        for (int i = 0; i < slices.Length; i++)
        {
            if (slices[i].Rows <= 0)
            {
                return $"Slice {i} holds {slices[i].Rows} rows: a slice holds rows.";
            }

            if (i > 0 && lows[i].AsSpan().SequenceCompareTo(lows[i - 1]) < 0)
            {
                return $"Slice {i} starts below the slice before it: the slices follow the key.";
            }
        }

        return high.AsSpan().SequenceCompareTo(lows[^1]) < 0 ? "The last key lies below the last slice's first: the slices follow the key." : null;
    }

    /// <summary>The table's bytes, the options of its directory entry.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            if (_lows is { } lows)
            {
                writer.WriteVarint(Keyed);
                writer.WriteVarint((ulong)Slices.Count);
                byte[] before = [];
                for (int i = 0; i < Slices.Count; i++)
                {
                    writer.WriteVarint((ulong)Slices[i].Rows);
                    WriteKey(ref writer, before, lows[i]);
                    before = lows[i];
                }

                WriteKey(ref writer, before, _high!);
                return writer.WrittenSpan.ToArray();
            }

            writer.WriteVarint(Version);
            writer.WriteVarint((byte)Unit);
            writer.WriteVarint((ulong)Slices.Count);
            long end = 0;
            for (int i = 0; i < Slices.Count; i++)
            {
                TimeSlice slice = Slices[i];

                // The first slice's start is absolute, zigzagged; each later one's a gap, never negative.
                writer.WriteVarint(i == 0 ? ZigZag(slice.Start) : (ulong)(slice.Start - end));
                writer.WriteVarint((ulong)(slice.End - slice.Start));
                writer.WriteVarint((ulong)slice.Rows);
                end = slice.End;
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>A key as the bytes it shares with <paramref name="before"/>, a count, and the rest, its length first.</summary>
    private static void WriteKey(ref ProtoWriter writer, ReadOnlySpan<byte> before, ReadOnlySpan<byte> key)
    {
        int shared = key.CommonPrefixLength(before);
        writer.WriteVarint((ulong)shared);
        writer.WriteVarint((ulong)(key.Length - shared));
        writer.WriteRawBytes(key[shared..]);
    }

    /// <summary>The table <paramref name="bytes"/> encode, or null when they do not decode as one.</summary>
    internal static SliceTable? FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            ulong version = reader.ReadVarint();
            if (version == Keyed)
            {
                return KeysFrom(ref reader, bytes.Length);
            }

            if (version is not (Version or WithOrder))
            {
                return null;
            }

            ulong code = reader.ReadVarint();
            TimeUnit unit = code <= byte.MaxValue ? (TimeUnit)code : (TimeUnit)byte.MaxValue;
            ulong count = reader.ReadVarint();
            if (!Enum.IsDefined(unit) || count == 0 || count > (ulong)bytes.Length)
            {
                return null;
            }

            TimeSlice[] slices = new TimeSlice[(int)count];
            long end = 0;
            long row = 0;
            for (int i = 0; i < slices.Length; i++)
            {
                ulong gap = reader.ReadVarint();
                long start = i == 0 ? UnZigZag(gap) : checked(end + (long)gap);
                long stop = checked(start + (long)reader.ReadVarint());
                long rows = (long)reader.ReadVarint();
                slices[i] = new TimeSlice(start, stop, row, rows);
                row = checked(row + rows);
                end = stop;
            }

            if (version == WithOrder && !SkipOrder(ref reader, bytes.Length))
            {
                return null;
            }

            return reader.End && Problem(slices) is null ? new SliceTable(unit, slices) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A key table's slices and keys, past its version byte; null when they do not decode as one.</summary>
    private static SliceTable? KeysFrom(ref ProtoReader reader, int bytes)
    {
        ulong count = reader.ReadVarint();
        if (count == 0 || count > (ulong)bytes)
        {
            return null;
        }

        TimeSlice[] slices = new TimeSlice[(int)count];
        byte[][] lows = new byte[slices.Length][];
        byte[] before = [];
        long row = 0;
        for (int i = 0; i < slices.Length; i++)
        {
            long rows = (long)reader.ReadVarint();
            slices[i] = new TimeSlice(long.MinValue, long.MaxValue, row, rows);
            row = checked(row + rows);
            if (ReadKey(ref reader, before) is not { } low)
            {
                return null;
            }

            lows[i] = before = low;
        }

        return ReadKey(ref reader, before) is { } high && reader.End && KeyProblem(slices, lows, high) is null
            ? new SliceTable(slices, lows, high)
            : null;
    }

    /// <summary>A key written by <see cref="WriteKey"/> after <paramref name="before"/>; null when its counts do not fit.</summary>
    private static byte[]? ReadKey(ref ProtoReader reader, byte[] before)
    {
        ulong shared = reader.ReadVarint();
        if (shared > (ulong)before.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> tail = reader.ReadLengthDelimited();
        byte[] key = new byte[(int)shared + tail.Length];
        before.AsSpan(0, (int)shared).CopyTo(key);
        tail.CopyTo(key.AsSpan((int)shared));
        return key;
    }

    /// <summary>
    /// Reads past the order and the synopsis paths a table of version 4 holds after its slices: an entry a varint each,
    /// and a path its field, its key and its class word. False when their counts run past the bytes.
    /// </summary>
    private static bool SkipOrder(ref ProtoReader reader, int bytes)
    {
        ulong entries = reader.ReadVarint();
        if (entries > (ulong)bytes)
        {
            return false;
        }

        for (ulong i = 0; i < entries; i++)
        {
            reader.ReadVarint();
        }

        ulong paths = reader.ReadVarint();
        if (paths > (ulong)bytes)
        {
            return false;
        }

        for (ulong i = 0; i < paths; i++)
        {
            reader.ReadVarint();
            reader.ReadLengthDelimited();
            reader.ReadVarint();
        }

        return true;
    }

    /// <summary>
    /// The slice table of <paramref name="file"/>, read from its index directory: null when it has
    /// none, or one that does not decode or does not cover the file's rows.
    /// </summary>
    internal static async ValueTask<SliceTable?> ReadAsync(VortexFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind == IndexKinds.Slices && FromBytes(entry.Options) is { } table && table.Rows == file.RowCount)
            {
                return table;
            }
        }

        return null;
    }

    private static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
