// Slices in flight: a slice sorts on a thread of its own as soon as its rows are in, beside the writes of the slices before
// it, and the writes go one at a time in the slices' order; a write that fails stops every slice after it, and the drain
// throws what it threw once every slice handed over is done with its buffer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortedSliceTests
{
    /// <summary>
    /// Twelve slices of 200 spans, each write slower than a sort: the writes come one at a time and in the slices' order,
    /// each slice's rows sorted by its order; a write that throws at the fourth slice is the last one called, and what it
    /// threw is what the slices throw.
    /// </summary>
    [Fact]
    public async Task SlicesInFlightAreWrittenInTheirOrderAndAFailureStopsThem()
    {
        const int Slices = 12;
        const int SliceRows = 200;
        Random random = new Random(5);
        Span[] spans = [.. Enumerable.Range(0, Slices * SliceRows).Select(i => new Span(
            Noon.AddSeconds(i * 1.25), 200 + (random.Next(5) * 100), $"region-{random.Next(3)}", $"host-{random.Next(1_000)}"))];
        byte[] file;
        using (MemoryStream stream = new MemoryStream())
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(Span.Schema, new DTypeArena()), new VortexWriteOptions()))
            {
                await writer.WriteAsync<Span>(spans, Ct);
                await writer.CompleteAsync(Ct);
            }

            file = stream.ToArray();
        }

        await using VortexFile open = await VortexFile.OpenAsync(new MemorySegmentSource(file), VortexOpenOptions.Default, Ct);
        DType schema = open.DType;
        long micros = Noon.ToUnixTimeMilliseconds() * 1000;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, Slices).Select(s => new TimeSlice(micros + (s * 250_000_000L), micros + ((s + 1) * 250_000_000L), (long)s * SliceRows, SliceRows))],
            [schema.IndexOfField(nameof(Span.Region)), schema.IndexOfField(nameof(Span.Status))]);

        // Every slice written, in its order, one at a time, its rows sorted by its codes.
        List<int> written = await WriteSlicesAsync(open, table, schema, failAt: -1);
        Assert.Equal(Enumerable.Range(0, Slices), written);

        // A write that fails at the fourth slice is the last one called.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => WriteSlicesAsync(open, table, schema, failAt: 3));
        Assert.Equal("slice 3", failure.Message);
    }

    /// <summary>
    /// A slice whose rows come in batches that its ends cut holds the rows it was handed, its columns filled as they came:
    /// names within their views and past them, a count that is now and then null, copied a batch at a time and joined, an
    /// instant and a size; each slice its own rows, sorted by name, ties in the order they came.
    /// </summary>
    [Fact]
    public async Task ASliceFilledBatchByBatchHoldsTheRowsItWasHanded()
    {
        const int Slices = 5;
        const int SliceRows = 333;
        Random random = new Random(3);
        Filled[] filled = [.. Enumerable.Range(0, Slices * SliceRows).Select(i => new Filled(
            Noon.AddSeconds(i),
            random.Next(3) == 0 ? $"a-name-past-a-view-{random.Next(50)}" : $"n-{random.Next(20)}",
            random.Next(4) == 0 ? null : random.Next(100),
            random.NextInt64(1_000_000)))];
        byte[] file;
        using (MemoryStream stream = new MemoryStream())
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), VortexTypes.ToDType(Filled.Schema, new DTypeArena()), new VortexWriteOptions()))
            {
                await writer.WriteAsync<Filled>(filled, Ct);
                await writer.CompleteAsync(Ct);
            }

            file = stream.ToArray();
        }

        await using VortexFile open = await VortexFile.OpenAsync(new MemorySegmentSource(file), VortexOpenOptions.Default, Ct);
        DType schema = open.DType;
        long micros = Noon.ToUnixTimeMilliseconds() * 1000;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            [.. Enumerable.Range(0, Slices).Select(s => new TimeSlice(micros + (s * SliceRows * 1_000_000L), micros + ((s + 1) * SliceRows * 1_000_000L), (long)s * SliceRows, SliceRows))],
            [schema.IndexOfField(nameof(Filled.Name))]);
        List<Filled[]> written = [];
        using (SortedSlices slices = new SortedSlices(table, schema.IndexOfField(nameof(Filled.At)), schema))
        {
            await foreach (RecordBatch batch in open.Scan<Filled>().With(new ScanOptions { BatchRows = 70 }).ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await slices.AppendAsync(batch, (slice, token) =>
                    {
                        Filled[] rows = new Filled[slice.Batch.RowCount];
                        Filled.ReadRows(slice.Batch.As<Filled>(), rows);
                        written.Add(rows);
                        return ValueTask.CompletedTask;
                    }, Ct);
                }
            }

            await slices.DrainAsync();
        }

        Assert.Equal(Slices, written.Count);
        for (int s = 0; s < Slices; s++)
        {
            Filled[] expected = [.. filled.Skip(s * SliceRows).Take(SliceRows).Select((row, i) => (row, i))
                .OrderBy(p => p.row.Name, StringComparer.Ordinal).ThenBy(p => p.i).Select(p => p.row)];
            Assert.Equal(expected, written[s]);
        }
    }

    [VortexRecord]
    public partial record struct Filled(DateTimeOffset At, string Name, int? Count, long Size);

    /// <summary>
    /// The slices of <paramref name="table"/> over the rows of <paramref name="file"/>, each write 5 ms long, the write of
    /// slice <paramref name="failAt"/> throwing: the slices written, in the order they were.
    /// </summary>
    private static async Task<List<int>> WriteSlicesAsync(VortexFile file, SliceTable table, DType schema, int failAt)
    {
        List<int> written = [];
        int writing = 0;
        using SortedSlices slices = new SortedSlices(table, schema.IndexOfField(nameof(Span.At)), schema);
        try
        {
            await foreach (RecordBatch batch in file.Scan<Span>().With(new ScanOptions { BatchRows = 64 }).ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await slices.AppendAsync(batch, async (slice, token) =>
                    {
                        Assert.Equal(1, Interlocked.Increment(ref writing));
                        try
                        {
                            int index = written.Count;
                            written.Add(index);
                            Assert.Equal(table.Slices[index].Rows, slice.Batch.RowCount);
                            for (int row = 1; row < slice.Batch.RowCount; row++)
                            {
                                int compared = 0;
                                for (int k = 0; k < slice.Codes.Count && compared == 0; k++)
                                {
                                    compared = slice.Codes[k][row - 1].CompareTo(slice.Codes[k][row]);
                                }

                                Assert.True(compared <= 0, $"slice {index} is not sorted at row {row}");
                            }

                            await Task.Delay(5, token);
                            if (index == failAt)
                            {
                                throw new InvalidOperationException($"slice {index}");
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref writing);
                        }
                    }, Ct);
                }
            }

            await slices.DrainAsync();
        }
        finally
        {
            if (failAt >= 0)
            {
                Assert.Equal(Enumerable.Range(0, failAt + 1), written);
            }
        }

        return written;
    }
}
