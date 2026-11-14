// The questions the objects' counts answer, held to a ratchet.
//
// WHAT IS HELD, per question: the requests and the bytes it asks of the store, cold (a session and a handle opened for
// it, an empty synopsis cache) and warm (the same handle again, its memo of synopses and the cache filled), and the
// managed bytes the process allocates for one warm ask, every thread counted, as QueryPathBudgetTests holds the paths
// read from the rows. Each ceiling sits just above what its question costs, lowered by hand in the commit that earns it.
//
// THE QUESTIONS are DatasetPlannerTests', on its dataset: an object an hour, which keeps its columns' counts from its
// first byte. The query paths' objects keep none, too small for a reader to ask their synopses first, as an object of a
// few minutes of a quiet service is; these questions are the ones the counts answer.
//
// WHY THE FLOOR OF SEVERAL WARM ASKS: the first asks pay the JIT's tiering and the pools' first rents; the least of
// several is the steady state, and anything allocated on every ask still raises it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class PlannerBudgetTests : IClassFixture<DatasetPlannerTests.Fixture>
{
    /// <summary>
    /// The ceilings of each question: requests and bytes cold, requests and bytes warm, and the bytes one warm ask
    /// allocates, 2 % above what it measured in a process of its own. Measured on 2026-10-25, the counts over each object:
    /// a request an object its counts answer, its counts and their sketches side by side, and the two appended objects
    /// read as rows; warm, the handle keeps the counts parsed and nothing is read but the rows of the objects the unaligned
    /// window cuts, which no cache keeps. The dataset cut by the hour of its instant, where it took periods of an hour:
    /// 32 bytes more cold, its header naming the value, and the unaligned window's edges read in a request less but 42 KB
    /// more, an object gathering each column in one chunk where a period cut it at each of its slices. The allocations of
    /// the questions whose filter is evaluated on rows raised on 2026-10-25 by some 0.5 KB, each in a process of its own:
    /// the filter's key, its canonical text hashed, and the lookup of its pages (PredicatePages). Those of the two
    /// questions of measures set again on 2026-10-26 at 2 % above their measure, 72 bytes more an ask: the opens and the
    /// partials a scan begins ahead of the objects it reads next. The quantiles by service lowered the same day to 2 % above
    /// 338,928 bytes, the larger of this machine's figure and CI's Linux runner's, from 1,036,385: a quantile read in place
    /// from its sketch.
    /// <para>
    /// Measured again on 2026-10-27, after the redesign, an object an hour as before (the fixture's compaction aims past an
    /// hour's bytes since a job's objects keep to their target). Cold, a question lists the learned partials of its family
    /// and of the families that serve it, reads those families' texts and the partials that answer the appended objects,
    /// and writes the partials it learns, which the fixture's store keeps for the questions after it: 6 to 17 requests,
    /// fewer bytes where a partial answers what rows did. Warm, a plan weighs each object's alternatives and serves the
    /// appended objects from their partials, each opened as a file: 2 to 24 % more than before, after what a945e8b0 to
    /// the partial files without zones took off (1.2 MB an ask for count by route and day before them). The allocations
    /// of the questions that pass keep their ceilings.
    /// </para>
    /// <para>
    /// Set again on 2026-10-31, the learned partials withdrawn, each at what it measured, its allocations 2 % above: cold, a
    /// question lists, reads and writes no partial, 6 requests, 2 for the aligned window, where it took 6 to 17; the two
    /// appended objects, which keep no counts and no longer learn a partial as they are appended, are read as rows, 31 KB
    /// more cold where a partial answered them (count by service 3,908 to 35,130 bytes, by hour of one service 4,969 to
    /// 36,149), and warm their rows from the handle's open objects: 2 to 21 % more allocated, a quantile by service 81 %
    /// less, its states no longer served from a partial's file.
    /// </para>
    /// <para>
    /// The quantiles by service raised on 2026-11-01 to 2 % above 68,040 bytes: the granules' histograms kept at the grid a
    /// quantile asks by default, the planner weighs the granules of each object for it too, where it declined them at once;
    /// the same reads cold.
    /// </para>
    /// <para>
    /// Every cold question raised on 2026-11-14 by one request and 5,602 bytes: a header carries no page an earlier version
    /// wrote any more, and the version the questions open did not write its tree's top, which a cold question now reads
    /// where it lies, one page in one request. Each commit of the dataset writes that page less.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Budget> Ceilings = new(StringComparer.Ordinal)
    {
        ["count by service"] = new(7, 40732, 0, 0, 54664),
        ["count by service and hour"] = new(7, 40988, 0, 0, 61837),
        ["measures by service"] = new(7, 41751, 0, 0, 74436),
        ["count by day"] = new(7, 41751, 0, 0, 67263),
        ["count by hour, one service"] = new(7, 41751, 0, 0, 79895),
        ["count by service, an aligned window"] = new(3, 6003, 0, 0, 46047),
        ["measures by service, an unaligned window"] = new(6, 402466, 2, 270920, 93375),
        ["quantiles by service"] = new(7, 74441, 0, 0, 69401),
        ["distinct services by hour"] = new(7, 40988, 0, 0, 63526),
        ["count by route and day"] = new(7, 54088, 0, 0, 62669),
    };

    private readonly DatasetPlannerTests.Fixture _fixture;

    public PlannerBudgetTests(DatasetPlannerTests.Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionStaysWithinItsBudget(string question)
    {
        Budget ceiling = Ceilings[question];
        string directory = Directory.CreateTempSubdirectory("vortex-planner-").FullName;
        try
        {
            await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64L << 20));
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, DatasetPlannerTests.Options() with { Session = session }, Ct);
            store.Reset();
            List<string> cold = await DatasetPlannerTests.AskAsync(dataset, question, synopses: true);
            (long coldRequests, long coldBytes) = (store.Requests, store.BytesRead);
            store.Reset();
            List<string> warm = await DatasetPlannerTests.AskAsync(dataset, question, synopses: true);
            (long warmRequests, long warmBytes) = (store.Requests, store.BytesRead);
            Assert.Equal(cold, warm);
            Assert.NotEmpty(warm);

            long allocated = long.MaxValue;
            for (int round = 0; round < 6; round++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                await DatasetPlannerTests.AskAsync(dataset, question, synopses: true, describe: false);
                allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            Console.Out.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"PLANNER BUDGET {question}: cold {coldRequests} requests {coldBytes} bytes (ceilings {Shown(ceiling.ColdRequests)}, {Shown(ceiling.ColdBytes)}), " +
                $"warm {warmRequests} requests {warmBytes} bytes (ceilings {Shown(ceiling.WarmRequests)}, {Shown(ceiling.WarmBytes)}), " +
                $"{allocated} bytes allocated warm (ceiling {Shown(ceiling.Allocated)}), {warm.Count} rows answered.\n"));

            Assert.True(coldRequests <= ceiling.ColdRequests, $"{question}: {coldRequests} requests cold, over {ceiling.ColdRequests}");
            Assert.True(coldBytes <= ceiling.ColdBytes, $"{question}: {coldBytes} bytes cold, over {ceiling.ColdBytes}");
            Assert.True(warmRequests <= ceiling.WarmRequests, $"{question}: {warmRequests} requests warm, over {ceiling.WarmRequests}");
            Assert.True(warmBytes <= ceiling.WarmBytes, $"{question}: {warmBytes} bytes warm, over {ceiling.WarmBytes}");
            Assert.True(allocated <= ceiling.Allocated, $"{question}: {allocated} bytes allocated warm, over {ceiling.Allocated}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Shown(long ceiling) => ceiling == long.MaxValue ? "none" : ceiling.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a question may cost: requests and bytes cold and warm, and the bytes a warm ask allocates.</summary>
    private readonly record struct Budget(long ColdRequests, long ColdBytes, long WarmRequests, long WarmBytes, long Allocated);
}
