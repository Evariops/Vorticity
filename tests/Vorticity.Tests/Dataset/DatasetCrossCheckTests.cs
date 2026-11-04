// The .NET half of Rust reading the objects a dataset's jobs write (`bench/crosscheck.sh`, `verify_objects`), and a
// partial of a question over them (`scan_files`).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetCrossCheckTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each object above level 0 of a dataset keyed by its service then its instant, cut by the hour, written out as it is
    /// stored, beside its rows written plainly, for `cargo run --example verify_objects` to compare as multisets: its text
    /// columns written against the dictionaries the merges carry, a code a row; each column in chunks of its own; its
    /// synopses beside it, which its index directory holds and the reference does not read (sheets, counts, statistics,
    /// histograms and locates over its granules, an identifier's postings); and a float column holding nulls and NaNs,
    /// which its zone maps count. Beside them, under <c>partials/</c>, the partial of a question over the dataset, the
    /// states of its groups another node reads, for `cargo run --example scan_files` to read whole. It skips rather than
    /// passes without its directory.
    /// </summary>
    [Fact]
    public async Task WritesDatasetObjectsOutForTheRustCrossCheck()
    {
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_OBJECTS");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set VORTICITY_WRITE_OBJECTS to a directory to produce a dataset's objects for the Rust cross-check.");
        System.IO.Directory.CreateDirectory(root!);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                Organization = LayoutOrganization.Leveled,
            },
            Identifiers = [nameof(Call.RequestId)],
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,

            // Chunks of 16 KiB a column and granules of 1 024 rows, so that each column runs over a dozen chunks or more and
            // its synopses over hundreds of granules.
            ChunkBytes = 16 << 10,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);

        // Two hours of calls, appended in parts that mix every service, then merged: an object an hour. A part's routes
        // take past the megabyte the writer prices a chunk of a column at, so that their 240 values pay a dictionary once
        // an object rather than a chunk, from the appends on: each declares its own, and a merge their union.
        Random random = new Random(0xC4055);
        for (int part = 0; part < 4; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 100_000).Select(i =>
            {
                int service = random.Next(6);
                return new Call(
                    $"svc-{service}",
                    Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                    $"/svc{service}/r{random.Next(40)}",
                    $"r-{part:D2}-{i:D5}",
                    (short)(random.Next(10) switch { < 7 => 200, < 9 => 404, _ => 500 }),
                    random.Next(60) switch { 0 => null, 1 => double.NaN, _ => random.Next(400_000) / 4.0 },
                    random.Next(1_000_000));
            })];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
        {
        }

        int written = 0;
        int dictionaries = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Level == 0)
            {
                continue;
            }

            dictionaries += held.Entry.Contents!.Dictionaries.Count(dictionary => dictionary is not null);
            string name = System.IO.Path.Combine(root!, $"object-{written++}");
            byte[] bytes = new byte[held.Entry.Bytes];
            await store.ReadAsync(held.Entry.Key, 0, bytes, null, Ct);
            await System.IO.File.WriteAllBytesAsync(name + ".stored.vortex", bytes, Ct);

            List<Call> stored = [];
            await using (ObjectLease lease = await dataset.RentAsync(held.Entry, Ct))
            {
                await foreach (Call row in lease.File.Scan<Call>().ToRecordsAsync(Ct))
                {
                    stored.Add(row);
                }
            }

            Assert.Equal(held.Entry.Rows, stored.Count);
            await using VortexFileWriter plain = VortexSession.Default.CreateWriter<Call>(name + ".plain.vortex");
            await plain.WriteAsync<Call>([.. stored], Ct);
            await plain.CompleteAsync(Ct);
        }

        Assert.True(written >= 2 && dictionaries > 0, $"{written} objects, {dictionaries} columns written against a dictionary");

        // The states of each service's hours: a count, integer and float sums exact, extremes, a sketch of the latencies
        // and one of the routes.
        AggregationPartial partial = await dataset.Scan<Call>()
            .GroupBy(c => (c.Service, c.At.Truncate(CalendarUnit.Hour)))
            .Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(c => c.Size), g.Sum(c => c.Latency), g.Min(c => c.Status),
                g.Max(c => c.Latency), g.Quantile(c => c.Latency, 0.9), g.ApproximateCountDistinct(c => c.Route)))
            .PartialAsync(Ct);
        string partials = System.IO.Path.Combine(root!, "partials");
        System.IO.Directory.CreateDirectory(partials);
        await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(partials, "partial-0.vortex"), partial.Bytes.ToArray(), Ct);
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Route, string RequestId, short Status, double? Latency, long Size);
}
