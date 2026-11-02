// The key's pruning reads each text its keys hold once a search: the bounds of its boxes, granule after granule, hold the
// same few values.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    /// <summary>
    /// A search of an object's index under a filter on the service, the key's first component, a text: its boxes' bounds hold
    /// the same three services granule after granule, and the search allocates no bytes for them, where it allocated a value's
    /// at each bound of each box judged. Floored over passes, each by a pruning of its own, so that a one-off of the runtime
    /// does not count; the granules it keeps are the same.
    /// </summary>
    [Fact]
    public async Task ASearchReadsEachTextItsKeysHoldOnce()
    {
        Random random = new Random(64);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
            },
            ContentsObjectBytes = 0,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);
        Row[] all = [.. Enumerable.Range(0, 200_000).Select(i =>
            new Row(0, $"svc-{random.Next(0, 3):D2}", Start.AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10), i, random.NextDouble()))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Row>(all, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        VortexExpr filter = Expr.And(
            Eq(nameof(Row.Service), FilterLiteral.From("svc-01")),
            Expr.And(Ge(nameof(Row.At), Micros(Start.AddHours(10))), Lt(nameof(Row.At), Micros(Start.AddHours(11)))));
        DatasetSchema schema = dataset.Snapshot.Schema;
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        GranuleKeys index = (await dataset.KeyIndexAsync(held.Entry, held.Entry.Contents!, Ct))!;
        List<(int First, int End)> first = KeyPruning.Of(schema, filter)!.Granules(index, default);
        Assert.InRange(first.Count, 1, index.Granules - 1);

        long floor = long.MaxValue;
        for (int pass = 0; pass < 5; pass++)
        {
            KeyPruning pruning = KeyPruning.Of(schema, filter)!;
            long before = GC.GetAllocatedBytesForCurrentThread();
            List<(int First, int End)> kept = pruning.Granules(index, default);
            floor = Math.Min(floor, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(first, kept);
        }

        Console.WriteLine($"KEY SEARCH: {index.Granules} granules, {first.Count} ranges kept, {floor} bytes allocated");
        Assert.True(floor < 4_096, $"a search of {index.Granules} granules allocated {floor} bytes");
    }
}
