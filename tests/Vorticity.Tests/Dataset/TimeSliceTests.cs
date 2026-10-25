// Time slices: the slice table a file carries in its index directory, its bytes checked and read back, the chunks a
// slice overlaps, and a slice read without the footer. An earlier library's datasets of periods wrote their objects in
// them, which a reader still reads.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.File;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class TimeSliceTests
{
    private const long Second = 1_000_000;

    private static readonly long Noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ATableIsCheckedAndReadBackFromItsBytes()
    {
        SliceTable table = new(TimeUnit.Microseconds, [new TimeSlice(-5, 10, 0, 3), new TimeSlice(20, 30, 3, 7)]);
        SliceTable read = SliceTable.FromBytes(table.ToBytes())!;
        Assert.Equal(table.Slices, read.Slices);
        Assert.Equal(TimeUnit.Microseconds, read.Unit);
        Assert.Null(SliceTable.FromBytes(table.ToBytes()[..^1]));
        Assert.Null(SliceTable.FromBytes([9, 1, 1]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 10, 0, 3), new TimeSlice(5, 30, 3, 7)]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, [new TimeSlice(0, 10, 0, 3), new TimeSlice(10, 30, 4, 7)]));
        Assert.Throws<ArgumentException>(() => new SliceTable(TimeUnit.Microseconds, []));
    }

    /// <summary>
    /// A table of version 4, which followed its slices with the order inside them and the object's synopsis paths, reads
    /// back as its slices alone, so that an object written then is still walked by them; one torn in its paths does not.
    /// </summary>
    [Fact]
    public void ATableOfVersionFourIsReadWithoutItsOrder()
    {
        List<byte> bytes = [];
        void Varint(ulong value)
        {
            for (; value >= 0x80; value >>= 7)
            {
                bytes.Add((byte)(value | 0x80));
            }

            bytes.Add((byte)value);
        }

        // Version 4, the unit, two slices as gaps, widths and rows; an order of a field and a variant's shapes, zigzagged;
        // a path in the residual, its field, its key and its class word.
        Varint(4);
        Varint((byte)TimeUnit.Microseconds);
        Varint(2);
        Varint(9);
        Varint(15);
        Varint(3);
        Varint(10);
        Varint(10);
        Varint(7);
        Varint(2);
        Varint(2);
        Varint(5);
        Varint(1);
        Varint(2);
        Varint(5);
        bytes.AddRange("route"u8.ToArray());
        Varint((byte)Vorticity.Types.Variants.VariantClass.String | 0x100u);

        SliceTable read = SliceTable.FromBytes(bytes.ToArray())!;
        Assert.Equal([new TimeSlice(-5, 10, 0, 3), new TimeSlice(20, 30, 3, 7)], read.Slices);
        Assert.Equal(TimeUnit.Microseconds, read.Unit);
        Assert.Null(SliceTable.FromBytes(bytes.ToArray()[..^4]));
    }

    /// <summary>A writer that declares its slices writes them into its index directory, and a file read back finds them.</summary>
    [Fact]
    public async Task AFileCarriesItsSlicesInItsIndexDirectory()
    {
        Reading[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Reading(DateTimeOffset.FromUnixTimeMilliseconds((Noon / 1000) + (i * 30L)), i % 7))];
        long[] instants = [.. rows.Select(r => r.At.ToUnixTimeMilliseconds() * 1000)];
        SliceTable table = new(TimeUnit.Microseconds, Slices(instants, 900));
        Assert.True(table.Slices.Count > 10);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-slices-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new() { Indexes = IndexPolicy.Auto.SortedRuns(nameof(Reading.At), true) };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, options))
            {
                await writer.WriteAsync<Reading>(rows, Ct);
                writer.DeclareSlices(nameof(Reading.At), table);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            SliceTable read = (await SliceTable.ReadAsync(file, Ct))!;
            Assert.Equal(table.Slices, read.Slices);
            Assert.Equal(20_000, (await file.Scan<Reading>().Select(r => r.Value).ToListAsync(Ct)).Count);

            // Slices that do not cover the file's rows leave it unwritten.
            await using VortexFileWriter refused = VortexSession.Default.CreateWriter<Reading>(path + ".2", options);
            await refused.WriteAsync<Reading>(rows[..100], Ct);
            refused.DeclareSlices(nameof(Reading.At), table);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await refused.CompleteAsync(Ct));
            Assert.Throws<ArgumentException>(() => refused.DeclareSlices("Missing", table));
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(path + ".2");
        }
    }

    [Fact]
    public void AChunkDirectoryNamesTheChunksASliceOverlaps()
    {
        ChunkSpan[] column = [new(64, 100, 6, 0, 2048), new(192, 100, 6, 2048, 2048), new(320, 50, 6, 4096, 904)];
        SliceChunks chunks = new(["vortex.primitive", "fastlanes.bitpacked"], [column, column]);
        Assert.Equal(chunks.Encodings, SliceChunks.EncodingsFrom(chunks.OptionBytes()));
        ChunkSpan[] read = SliceChunks.ColumnFrom(chunks.ColumnBytes(1))!;
        Assert.Equal(column, read);
        Assert.Equal(column[..1], SliceChunks.Overlapping(read, 0, 2048).ToArray());
        Assert.Equal(column[..2], SliceChunks.Overlapping(read, 2000, 100).ToArray());
        Assert.Equal(column[2..], SliceChunks.Overlapping(read, 4096, 904).ToArray());
        Assert.Null(SliceChunks.ColumnFrom(chunks.ColumnBytes(0)[..^1]));
        Assert.Null(SliceChunks.EncodingsFrom(chunks.OptionBytes()[..^1]));
        Assert.Null(SliceChunks.ColumnFrom([2]));
    }

    /// <summary>A slice's rows, read through the chunk directory, without a byte of the dtype, the layout or the footer.</summary>
    [Fact]
    public async Task ASliceIsReadWithoutTheFooter()
    {
        Sliced sliced = await WriteSlicedAsync(declare: true, Ct);
        (Reading[] rows, SliceTable table, byte[] bytes, DType schema) = sliced;

        // Chunks of 1 024 rows, so that slices start and end inside them.
        Assert.Contains(table.Slices, slice => slice.FirstRow % 1024 != 0);
        TestSegmentSource source = new(bytes) { Ranges = [] };
        SliceReader reader = (await SliceReader.OpenAsync(source, bytes.Length, schema, null, Ct))!;
        Assert.Equal(table.Slices, reader.Slices.Slices);
        for (int s = 0; s < table.Slices.Count; s++)
        {
            TimeSlice slice = table.Slices[s];
            using RecordBatch batch = await reader.ReadSliceAsync(s, null, Ct);
            Reading[] expected = rows[(int)slice.FirstRow..(int)(slice.FirstRow + slice.Rows)];
            Assert.Equal(expected, Read(batch));
        }

        // Nothing read lies between the index directory and the postscript, where the dtype, the
        // layout and the footer are: two reads of the end, the directory, the chunk directories with
        // the first slice, then one request a slice.
        SegmentSpec directory = (await VortexFile.ReadMetadataSpecAsync(new TestSegmentSource(bytes), bytes.Length, IndexDirectory.MetadataKey, Ct))!.Value;
        long footerEnd = bytes.Length - 8 - BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6, 2));
        Assert.True(footerEnd > (long)directory.End);
        Assert.All(source.Ranges, range => Assert.True(
            range.Offset + range.Length <= (long)directory.End || range.Offset >= footerEnd, $"{range} reads the footer"));
        Assert.Equal(2, source.RangeReads);
        Assert.Equal(table.Slices.Count + 1, source.BatchReads);

        // The instant alone reads its own chunk directory and chunks, none of the other column's.
        TestSegmentSource instants = new(bytes) { Ranges = [] };
        SliceReader edge = (await SliceReader.OpenAsync(instants, bytes.Length, schema, null, Ct))!;
        int opened = instants.Ranges.Count;
        using (RecordBatch batch = await edge.ReadSliceAsync(3, [0], Ct))
        {
            TimeSlice slice = table.Slices[3];
            DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
            batch.View.Column<DateTimeOffset>(0).CopyTo(at);
            Assert.Equal(rows[(int)slice.FirstRow..(int)(slice.FirstRow + slice.Rows)].Select(r => r.At), at);
            Assert.Equal(nameof(Reading.At), Assert.Single(batch.View.Schema).Name);
        }

        // Slice 3, rows 6 000 to 8 000, lies across several chunks: one region and its chunks for the
        // instant alone, against twice as many reads for both columns.
        TestSegmentSource both = new(bytes) { Ranges = [] };
        SliceReader whole = (await SliceReader.OpenAsync(both, bytes.Length, schema, null, Ct))!;
        using (await whole.ReadSliceAsync(3, null, Ct))
        {
        }

        Assert.InRange(instants.Ranges.Count - opened, 1 + 2, int.MaxValue);
        Assert.Equal(2 * (instants.Ranges.Count - opened), both.Ranges.Count - opened);
        Assert.True(
            instants.Ranges.Skip(opened).Sum(range => range.Length) < both.Ranges.Skip(opened).Sum(range => range.Length),
            "the instant alone reads less than both columns");

        // A file that declares no slices opens no slice reader.
        byte[] plain = (await WriteSlicedAsync(declare: false, Ct)).Bytes;
        Assert.Null(await SliceReader.OpenAsync(new TestSegmentSource(plain), plain.Length, schema, null, Ct));
    }

    internal readonly record struct Sliced(Reading[] Rows, SliceTable Table, byte[] Bytes, DType Schema);

    /// <summary>
    /// 20 000 rows 30 ms apart, in chunks of 1 024 rows and slices of a minute, 2 000 rows: the file's
    /// bytes and schema, its slices declared or not.
    /// </summary>
    internal static async Task<Sliced> WriteSlicedAsync(bool declare, CancellationToken ct)
    {
        Reading[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Reading(DateTimeOffset.FromUnixTimeMilliseconds((Noon / 1000) + (i * 30L)), i))];
        long[] instants = [.. rows.Select(r => r.At.ToUnixTimeMilliseconds() * 1000)];
        SliceTable table = new(TimeUnit.Microseconds, Slices(instants, 2_500));
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-slices-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new()
            {
                Indexes = IndexPolicy.Auto.SortedRuns(nameof(Reading.At), true),
                BlockRows = 1024,
                ChunkTargetBytes = 16 * 1024,
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, options))
            {
                await writer.WriteAsync<Reading>(rows, ct);
                if (declare)
                {
                    writer.DeclareSlices(nameof(Reading.At), table);
                }

                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            return new Sliced(rows, table, await System.IO.File.ReadAllBytesAsync(path, ct), file.DType);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The slices of <paramref name="instants"/>, ascending, of <paramref name="rows"/> rows each and the rows that share
    /// the last one's instant, the last slice fewer: each from its first instant to the next one's first, the last to past
    /// its last, so that every row lies in its slice's bounds.
    /// </summary>
    internal static List<TimeSlice> Slices(long[] instants, int rows)
    {
        List<TimeSlice> slices = [];
        int first = 0;
        while (first < instants.Length)
        {
            int next = Math.Min(first + rows, instants.Length);
            while (next < instants.Length && instants[next] == instants[next - 1])
            {
                next++;
            }

            long end = next < instants.Length ? instants[next] : instants[^1] + 1;
            slices.Add(new TimeSlice(instants[first], end, first, next - first));
            first = next;
        }

        return slices;
    }

    private static Reading[] Read(RecordBatch batch)
    {
        Columns<Reading> columns = batch.As<Reading>();
        DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
        long[] values = new long[batch.RowCount];
        columns.Column<DateTimeOffset>(0).CopyTo(at);
        columns.Column<long>(1).CopyTo(values);
        return [.. at.Zip(values, (a, v) => new Reading(a, v))];
    }

    [VortexRecord]
    public partial record struct Reading(DateTimeOffset At, long Value);
}
