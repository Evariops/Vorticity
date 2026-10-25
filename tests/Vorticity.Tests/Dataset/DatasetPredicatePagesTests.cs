// A filter evaluated on an object's rows notes the batches where it kept a row, its pages, which the filter asked again
// reads alone: the handle keeps them, and the session's synopsis cache for the handles after.
//
// THE ORACLE IS THE ROWS: the filter asked again answers as the first time did; and a selective filter no sheet estimates
// and no index answers reads a fraction of the bytes the second time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPredicatePagesTests
{
    private const int Rare = 12_345;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 100 000 events, bodies of 300 bytes in chunks of 512 rows, codes of twenty thousand values and a rare one held by
    /// thirty rows scattered over the objects: the distinct bodies of the rare code, asked as a range, which no sheet
    /// estimates, read every body the first time, and the batches that held its rows alone after, from the handle and from
    /// a handle opened later on the same cache.
    /// </summary>
    [Fact]
    public async Task ASelectiveFilterAskedAgainReadsItsPagesAlone()
    {
        Random random = new Random(0xF4);
        HashSet<int> rare = [];
        while (rare.Count < 30)
        {
            rare.Add(random.Next(100_000));
        }

        Event[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Event(
            Start.AddSeconds(i), rare.Contains(i) ? Rare : Other(random), Convert.ToBase64String(RandomBytes(random, 225))))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
            Clustering = new ClusteringOptions { ObjectBytes = 32 << 20 },
            ContentsObjectBytes = 1,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 512, ChunkTargetBytes = 64 << 10 },
        };
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Event.Schema, options, Ct))
        {
            for (int from = 0; from < rows.Length; from += 25_000)
            {
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Event>(rows.AsSpan(from, 25_000).ToArray(), Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        string directory = Directory.CreateTempSubdirectory("vortex-pages-").FullName;
        try
        {
            List<string> expected;
            await using (VortexDataset plain = await VortexDataset.OpenAsync(store, options, Ct))
            {
                expected = await AskAsync(plain);
                Assert.Equal(30, rows.Count(r => r.Code == Rare));
            }

            long[] bytes = new long[3];
            await using (VortexSession session = VortexSession.Create(o => o.SynopsisCache = new SynopsisCache(directory, 64L << 20)))
            await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct))
            {
                for (int ask = 0; ask < 2; ask++)
                {
                    store.Reset();
                    Assert.Equal(expected, await AskAsync(dataset));
                    bytes[ask] = store.BytesRead;
                }
            }

            await using (VortexSession session = VortexSession.Create(o => o.SynopsisCache = new SynopsisCache(directory, 64L << 20)))
            await using (VortexDataset later = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct))
            {
                store.Reset();
                Assert.Equal(expected, await AskAsync(later));
                bytes[2] = store.BytesRead;
            }

            Console.WriteLine($"PREDICATE PAGES: {bytes[0]:N0} bytes the first time, {bytes[1]:N0} after, {bytes[2]:N0} from a handle opened later");
            Assert.True(bytes[1] * 4 < bytes[0], $"{bytes[1]} bytes asked again, {bytes[0]} the first time");
            Assert.True(bytes[2] * 4 < bytes[0], $"{bytes[2]} bytes from a later handle, {bytes[0]} the first time");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The rows of the rare code and its distinct bodies, the code asked as a range, which no sheet estimates.</summary>
    private static Task<List<string>> AskAsync(VortexDataset dataset) =>
        DatasetPlannerTests.AskAsync(dataset, [], ["count()", "count_distinct(Body)"], $"Code >= {Rare} and Code <= {Rare}", synopses: true);

    /// <summary>A code of twenty thousand, any but the rare one, which lies among them: no zone map leaves it out.</summary>
    private static int Other(Random random) => random.Next(20_000) is var code && code != Rare ? code : Rare + 1;

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, int Code, string Body);
}
