using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLearnedPartialsTests
{
    /// <summary>
    /// On one lane, over a store that answers each request a round trip after it is sent, the objects that learn their
    /// family's partial are opened ahead, and each learns while the one before it is served: a handle's first asking waits
    /// on fewer round trips than it reads objects, where opening then reading each in turn waited on two an object. It
    /// answers as the rows do.
    /// </summary>
    [Fact]
    public async Task OneLaneLearnsItsObjectsAhead()
    {
        const int Services = 16;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using (VortexDataset created = await VortexDataset.CreateAsync(memory, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.Service), nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Column },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct))
        {
            Random random = new Random(0xA7E);
            Call[] rows = [.. Enumerable.Range(0, Services * 3_000).Select(i => new Call(
                $"svc-{i % Services:D2}",
                Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                $"h-{i % Services}-{random.Next(3)}",
                $"/svc{i % Services}/r{random.Next(6)}",
                (short)(random.Next(20) switch { < 14 => 200, < 17 => 404, < 19 => 500, _ => 503 }),
                random.Next(4_000) / 4.0,
                random.Next(1_000_000)))];
            await using (ObjectDraft draft = created.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(rows, Ct);
                await created.AppendAsync(draft, Ct);
            }

            while (await created.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
            {
            }
        }

        int objects = 0;
        List<string> rowsAnswer;
        await using (VortexDataset read = await VortexDataset.OpenAsync(memory, new DatasetOptions(), Ct))
        {
            await foreach (DataObject _ in read.ListObjectsAsync(Ct))
            {
                objects++;
            }

            rowsAnswer = await DatasetPlannerTests.AskAsync(read, ["Route"], ["Route", "count()"], "Status = 500", synopses: false);
        }

        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        memory.Latency = TimeSpan.FromMilliseconds(5);
        store.Reset();
        List<string> learned;
        try
        {
            learned = await DatasetPlannerTests.AskAsync(dataset, ["Route"], ["Route", "count()"], "Status = 500", synopses: true);
        }
        finally
        {
            memory.Latency = TimeSpan.Zero;
        }

        Console.WriteLine($"LEARNED AHEAD {objects} objects: {store.DependentSteps} round trips waited one after another, {store.Requests} requests");
        Assert.Equal(Services, objects);
        Assert.True(store.DependentSteps < objects, $"{store.DependentSteps} round trips waited one after another over {objects} objects");
        Assert.Equal(rowsAnswer, learned);
    }
}
