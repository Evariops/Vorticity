using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// One time slice of a file: the rows whose instant falls in <c>[Start, End)</c>, counted in the
/// instant column's unit, which lie at rows <c>[FirstRow, FirstRow + Rows)</c>.
/// </summary>
internal readonly record struct TimeSlice(long Start, long End, long FirstRow, long Rows);

/// <summary>
/// The time slices of a file: per slice its bounds on the instant, its first row and its row count,
/// in time order, covering every row once. An entry of the index directory on the instant column,
/// <see cref="IndexKinds.Slices"/>, whose options hold the table.
/// </summary>
/// <remarks>
/// <para>
/// The slices follow each other: each starts at or after the end of the one before, and its rows
/// follow the one before's. Encoded behind a version byte as the instant's unit, the slice count,
/// and per slice the gap from the previous end, the width and the row count, each a varint: a few
/// bytes a slice, a day of minutes in about 6 KiB.
/// </para>
/// <para>
/// Like every index it is a hint: a table that does not decode, or whose rows are not the file's,
/// is not read, and the file reads without it.
/// </para>
/// </remarks>
internal sealed class SliceTable
{
    private const byte Version = 1;

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

    /// <summary>The unit the slices' bounds are counted in: the instant column's.</summary>
    internal TimeUnit Unit { get; }

    /// <summary>The slices, in time order.</summary>
    internal IReadOnlyList<TimeSlice> Slices { get; }

    /// <summary>The rows the slices cover.</summary>
    internal long Rows => Slices[^1].FirstRow + Slices[^1].Rows;

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

    /// <summary>The table's bytes, the options of its directory entry.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
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

    /// <summary>The table <paramref name="bytes"/> encode, or null when they do not decode as one.</summary>
    internal static SliceTable? FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            if (reader.ReadVarint() != Version)
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

            return reader.End && Problem(slices) is null ? new SliceTable(unit, slices) : null;
        }
        catch (Exception malformed) when (malformed is VortexFormatException or OverflowException or ArgumentException)
        {
            return null;
        }
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
