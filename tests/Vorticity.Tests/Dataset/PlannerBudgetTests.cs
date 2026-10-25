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
    /// more, an object gathering each column in one chunk where a period cut it at each of its slices.
    /// </summary>
    private static readonly Dictionary<string, Budget> Ceilings = new(StringComparer.Ordinal)
    {
        ["count by service"] = new(6, 41647, 0, 0, 45378),
        ["count by service and hour"] = new(6, 41647, 0, 0, 51825),
        ["measures by service"] = new(6, 41647, 0, 0, 68030),
        ["count by day"] = new(6, 41647, 0, 0, 59062),
        ["count by hour, one service"] = new(6, 41647, 0, 0, 62897),
        ["count by service, an aligned window"] = new(2, 762, 0, 0, 37248),
        ["measures by service, an unaligned window"] = new(5, 402495, 2, 270920, 76704),
        ["quantiles by service"] = new(6, 74337, 0, 0, 1036385),
        ["distinct services by hour"] = new(6, 41647, 0, 0, 53513),
        ["count by route and day"] = new(6, 93472, 0, 0, 56475),
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
