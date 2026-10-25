// The ranges a question on one clustered value reads of an object store: each column it reads asked apart, the columns
// between them never bridged, under the rule a request's cost sets (SegmentReadOptions.Remote).
//
// THE ORACLE IS THE COLUMNS' BYTES: the bytes a question asks are those of the columns it reads, and its requests one a
// column and an object.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetJoinedRangesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Tenants cut into objects of their own, of some 100 000 rows each: two narrow columns apart by a wide one of some
    /// 800 KB, wider than a gap a request bridges. A count by both of one tenant reads the narrow columns of the tenant's
    /// object in requests apart, and not the wide column between them, which a bridge of a mebibyte read.
    /// </summary>
    [Fact]
    public async Task AQuestionOnOneValueAsksEachColumnOfItsObjectApart()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Tenanted.Tenant), nameof(Tenanted.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Column },
        };
        Random random = new Random(0x0C3);
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Tenanted.Schema, options, Ct))
        {
            for (int part = 0; part < 4; part++)
            {
                Tenanted[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Tenanted(
                    $"tenant-{random.Next(4)}", (part * 100_000L) + i, (short)(200 + random.Next(4)), random.NextDouble(), random.Next(10)))];
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Tenanted>(rows, Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        // A handle of its own, with no cache: every byte the question reads comes from the store.
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        long objects = 0;
        long largest = 0;
        await foreach (DataObject held in dataset.ListObjectsAsync(Ct))
        {
            objects++;
            largest = Math.Max(largest, held.Bytes);
        }

        store.Reset();
        Vorticity.Aggregation question = dataset.Scan<Tenanted>().Where(r => r.Tenant == "tenant-2")
            .GroupBy(r => (r.Status, r.Code)).Select(g => (g.Key.Status, g.Key.Code, g.Count()));
        List<Total> answers = await question.As<Total>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(40, answers.Count);

        // Each object's sheet, which proves the tenant absent from the other three; the tenant's object opened; then its data in
        // two reads, the columns before the wide one joined across gaps under 256 KiB, then the narrow column after it. The wide
        // column between, some 800 KB, is not read: a bridge of a mebibyte read the whole object in one request.
        long reads = store.CountOf(ObjectOperation.Read);
        Console.WriteLine($"JOINED: {objects} objects, {reads} reads, {store.BytesRead:N0} bytes of an object of {largest:N0}, {question.Metrics.BytesRequested:N0} requested");
        Assert.Equal(objects + 1 + 2, reads);
        Assert.True(store.BytesRead < largest / 2, $"{store.BytesRead} bytes read of an object of {largest}: the wide column was bridged");
    }

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long At, short Status, double Wide, int Code);

    [VortexRecord]
    public partial record struct Total(short Status, int Code, long Count);
}
