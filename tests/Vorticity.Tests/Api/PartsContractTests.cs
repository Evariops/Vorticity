using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The forms that run a caller's own work on the lanes: a scan cut into parts, a fold of whole batches by an aggregator of
/// the caller's, a state a part merged in order, and a writer's parts, filled side by side and taken in the order they
/// were created. What they give does not depend on the lanes.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class PartsContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A scan cut into parts reads every row once, in order, each part starting where the columns start a chunk.</summary>
    [Fact]
    public async Task AScanSplitIntoPartsReadsEveryRowOnceInOrder()
    {
        string path = await WriteAsync(Rows(200_000), lanes: 1, chunkRows: 16_384);
        await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
        IReadOnlyList<Scan<Reading>> parts = await file.Scan<Reading>().SplitAsync(4, Ct);
        Assert.InRange(parts.Count, 2, 4);
        List<int> days = [];
        foreach (Scan<Reading> part in parts)
        {
            await foreach (Columns<Reading> batch in part.WithCancellation(Ct))
            {
                days.AddRange(batch.Column<int>(0).Values.ToArray());
            }
        }

        Assert.Equal(Enumerable.Range(0, 200_000).Select(static i => i % 365), days);
        IReadOnlyList<Scan<Reading>> one = await file.Scan<Reading>().SplitAsync(1, Ct);
        Assert.Single(one);

        // The untyped scan cuts at the same rows.
        IReadOnlyList<Vorticity.Scan> untyped = await file.Scan([]).SplitAsync(4, Ct);
        Assert.Equal(parts.Count, untyped.Count);
        long rows = 0;
        foreach (Vorticity.Scan part in untyped)
        {
            await foreach (BatchView batch in part.WithCancellation(Ct))
            {
                rows += batch.RowCount;
            }
        }

        Assert.Equal(200_000, rows);
    }

    /// <summary>
    /// A fold of whole batches by an aggregator of the caller's gives the same state, bit for bit, on one lane, on four and
    /// on fourteen: a state a chunk, merged in the chunks' order. Its counts are the loop's over the same batches.
    /// </summary>
    [Fact]
    public async Task ABatchAggregatorGivesTheSameStateAtEveryDegree()
    {
        string path = await WriteAsync(Rows(300_000), lanes: 1, chunkRows: 32_768);
        List<Totals> states = [];
        foreach (int lanes in (int[])[1, 4, 14])
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            states.Add(await file.Scan<Reading>().AggregateAsync<TotalsAggregator, Totals>(Ct));
        }

        Assert.Equal(states[0], states[1]);
        Assert.Equal(states[0], states[2]);

        Totals loop = TotalsAggregator.Seed();
        await using (VortexFile file = await VortexFile.OpenAsync(path, Ct))
        {
            await foreach (Columns<Reading> batch in file.Scan<Reading>().WithCancellation(Ct))
            {
                TotalsAggregator.Step(ref loop, batch);
            }
        }

        Assert.Equal(loop.Rows, states[0].Rows);
        Assert.Equal(loop.Nulls, states[0].Nulls);
        Assert.Equal(loop.CityBytes, states[0].CityBytes);
        Assert.Equal(loop.Celsius, states[0].Celsius, 1e-6);
    }

    /// <summary>
    /// A dataset's scan is cut at its objects: the parts read every row once, in order, and a fold of its batches gives the
    /// same state on one lane, on four and on fourteen.
    /// </summary>
    [Fact]
    public async Task ADatasetScanIsCutAtItsObjects()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions(), Ct);
        Reading[] rows = Rows(60_000);
        for (int i = 0; i < 6; i++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Reading>(rows.AsSpan(i * 10_000, 10_000), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<Scan<Reading>> parts = await dataset.Scan<Reading>().SplitAsync(3, Ct);
        Assert.True(parts.Count > 1, $"{parts.Count} part(s) of six objects");
        List<int> days = [];
        foreach (Scan<Reading> part in parts)
        {
            await foreach (Columns<Reading> batch in part.WithCancellation(Ct))
            {
                days.AddRange(batch.Column<int>(0).Values.ToArray());
            }
        }

        Assert.Equal(rows.Select(static row => row.Day), days);

        List<Totals> states = [];
        foreach (int lanes in (int[])[1, 4, 14])
        {
            states.Add(await dataset.Scan<Reading>().With(new ScanOptions { DegreeOfParallelism = lanes }).AggregateAsync<TotalsAggregator, Totals>(Ct));
        }

        Assert.Equal(states[0], states[1]);
        Assert.Equal(states[0], states[2]);
        Assert.Equal(60_000, states[0].Rows);
    }

    /// <summary>Parts written out of their order go in the file in the order they were created; a part disposed unwritten gives its place up.</summary>
    [Fact]
    public async Task PartsGoInTheOrderTheyWereCreatedWhateverOrderTheyAreWrittenIn()
    {
        Reading[] rows = Rows(30_000);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-parts-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4))
            await using (VortexFileWriter writer = session.CreateWriter<Reading>(path))
            {
                WritePart<Reading>[] parts = new WritePart<Reading>[4];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = await writer.CreatePartAsync<Reading>(Ct);
                }

                await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() => Reading.WriteRows(parts[i].Builder, rows.AsSpan(i * 10_000, 10_000)), Ct)));
                parts[3].Dispose();
                await parts[2].WriteAsync(Ct);
                await parts[0].WriteAsync(Ct);
                await parts[1].WriteAsync(Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<int> days = [];
            await foreach (Columns<Reading> batch in file.Scan<Reading>().WithCancellation(Ct))
            {
                days.AddRange(batch.Column<int>(0).Values.ToArray());
            }

            Assert.Equal(rows.Select(static row => row.Day), days);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A file is not completed while a part is neither written nor disposed.</summary>
    [Fact]
    public async Task AFileWithAPartPendingIsNotCompleted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-parts-{Guid.NewGuid():N}.vortex");
        try
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path);
            WritePart<Reading> part = await writer.CreatePartAsync<Reading>(Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Ct));
            part.Dispose();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Records written through parts filled on the lanes make the file the one builder makes of them.</summary>
    [Fact]
    public async Task RecordsWrittenOnTheLanesMakeTheFileOfOneBuilder()
    {
        Reading[] rows = Rows(120_000);
        string alone = await WriteAsync(rows, lanes: 1, chunkRows: 0);
        string across = await WriteAsync(rows, lanes: 8, chunkRows: 0, records: true);
        try
        {
            Assert.Equal(await RowsOfAsync(alone), await RowsOfAsync(across));
        }
        finally
        {
            System.IO.File.Delete(alone);
            System.IO.File.Delete(across);
        }
    }

    private static async Task<List<Reading>> RowsOfAsync(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
        return await file.Scan<Reading>().ToRecordsAsync(Ct).ToListAsync(Ct);
    }

    private static Reading[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new Reading(i % 365, i % 7 == 0 ? null : (i % 1000) / 10.0, $"city-{i % 40}"))];

    private static async Task<string> WriteAsync(Reading[] rows, int lanes, int chunkRows, bool records = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-parts-{Guid.NewGuid():N}.vortex");
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        VortexWriteOptions? options = chunkRows > 0 ? new VortexWriteOptions { ChunkTargetBytes = chunkRows * 4 } : null;
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path, options);
        if (records)
        {
            await writer.WriteRecordsAsync<Reading>(rows, Ct);
        }
        else
        {
            await writer.WriteAsync<Reading>(rows, Ct);
        }

        await writer.CompleteAsync(Ct);
        return path;
    }

    private readonly record struct Totals(long Rows, long Nulls, double Celsius, long CityBytes);

    private readonly struct TotalsAggregator : IBatchAggregator<Reading, Totals>
    {
        public static Totals Seed() => default;

        public static void Step(ref Totals state, Columns<Reading> batch)
        {
            Column<double?> celsius = batch.Column<double?>(1);
            Column<string> city = batch.Column<string>(2);
            long nulls = 0;
            double sum = 0;
            long bytes = 0;
            for (int i = 0; i < batch.RowCount; i++)
            {
                if (celsius[i] is double value)
                {
                    sum += value;
                }
                else
                {
                    nulls++;
                }

                bytes += city.GetString(i)!.Length;
            }

            state = new Totals(state.Rows + batch.RowCount, state.Nulls + nulls, state.Celsius + sum, state.CityBytes + bytes);
        }

        public static void Merge(ref Totals into, in Totals other) =>
            into = new Totals(into.Rows + other.Rows, into.Nulls + other.Nulls, into.Celsius + other.Celsius, into.CityBytes + other.CityBytes);
    }
}
