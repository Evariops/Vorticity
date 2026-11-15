// What a question over several datasets holds of its budget: one question, whatever its shape, and no more memory than
// the same question over one dataset of the same rows.
//
// THE ORACLE IS THE ONE DATASET: the same rows, written to one dataset and to three whose rows follow one another, each
// in a session of a budget of its own, so that no other test's questions count in it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSetTests
{
    /// <summary>
    /// 🧪 What a question over the set may hold past the same question over one dataset, a share of the one dataset's peak:
    /// each member keeps a top of its own and reads its own objects ahead. Provisional, calibrated by measurement.
    /// </summary>
    private const double SetPeakShare = 0.25;

    /// <summary>
    /// A question over a set of three datasets is one question of its budget, whatever its shape: a scan of its rows, its
    /// rows in the order of the column its datasets are sorted by, its best rows of another column, an aggregation. Each
    /// holds the budget's one active question while it is read, and none once it is.
    /// </summary>
    [Fact]
    public async Task AQuestionOverASetIsOneQuestionOfItsBudgetWhateverItsShape()
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
        await using VortexSession session = VortexSession.Create(o => o.MemoryBudget = budget);
        await using Members members = await Members.CreateAsync(session, parts: 3);

        await OneQuestionAsync(budget, members.Set.Scan<Request>().ToRecordsAsync(Ct));
        await OneQuestionAsync(budget, members.Set.Scan<Request>().OrderBy(r => r.At).ToRecordsAsync(Ct));
        await OneQuestionAsync(budget, members.Set.Scan<Request>().OrderByDescending(r => r.Latency).Select(r => r.Latency).Take(40).ToValuesAsync(Ct));
        await OneQuestionAsync(budget, Hourly(members.Set.Scan<Request>()).As<HourTotal>().ToRecordsAsync(Ct));
    }

    /// <summary>
    /// A question over a set of three datasets holds no more of its budget than the same question over one dataset of the
    /// same rows, within <see cref="SetPeakShare"/>: its best rows of a column, and an aggregation.
    /// </summary>
    [Fact]
    public async Task AQuestionOverASetHoldsWhatItHoldsOverOneDataset()
    {
        long[] whole = await PeaksAsync(parts: 1);
        long[] set = await PeaksAsync(parts: 3);
        for (int q = 0; q < whole.Length; q++)
        {
            Assert.True(whole[q] > 0, $"question {q} held nothing over one dataset");
            Assert.True(set[q] <= whole[q] * (1 + SetPeakShare), $"question {q}: {set[q]:N0} bytes over the set, {whole[q]:N0} over one dataset");
        }
    }

    /// <summary>The most each question held of a budget of its own, over the rows in <paramref name="parts"/> datasets.</summary>
    private static async Task<long[]> PeaksAsync(int parts)
    {
        long[] peaks = new long[2];
        for (int q = 0; q < peaks.Length; q++)
        {
            QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
            await using VortexSession session = VortexSession.Create(o => o.MemoryBudget = budget);
            await using Members members = await Members.CreateAsync(session, parts);
            Scan<Request> scan = members.Set.Scan<Request>();
            if (q == 0)
            {
                Assert.Equal(4_000, (await scan.OrderByDescending(r => r.Latency).Select(r => r.Latency).Take(4_000).ToValuesAsync(Ct).ToListAsync(Ct)).Count);
            }
            else
            {
                Assert.NotEmpty(await Hourly(scan).As<HourTotal>().ToRecordsAsync(Ct).ToListAsync(Ct));
            }

            peaks[q] = budget.PeakBytes;
        }

        return peaks;
    }

    /// <summary>Reads <paramref name="answers"/> whole, the budget counting one active question from the first answer on, and none after.</summary>
    private static async Task OneQuestionAsync<T>(QueryMemoryBudget budget, IAsyncEnumerable<T> answers)
    {
        Assert.Equal(0, budget.ActiveQueries);
        IAsyncEnumerator<T> reading = answers.GetAsyncEnumerator(Ct);
        await using (reading.ConfigureAwait(false))
        {
            Assert.True(await reading.MoveNextAsync());
            Assert.Equal(1, budget.ActiveQueries);
            while (await reading.MoveNextAsync())
            {
            }
        }

        Assert.Equal(0, budget.ActiveQueries);
    }

    /// <summary>The fixture's rows in <paramref name="parts"/> datasets of a session, whose rows follow one another, read as a set.</summary>
    private sealed class Members : IAsyncDisposable
    {
        private readonly List<MemoryObjectStore> _stores = [];
        private readonly List<VortexDataset> _datasets = [];

        internal VortexDatasetSet Set { get; private set; } = null!;

        internal static async Task<Members> CreateAsync(VortexSession session, int parts)
        {
            Members members = new Members();
            int at = 0;
            for (int p = 0; p < parts; p++)
            {
                int count = (Objects - at) / (parts - p);
                MemoryObjectStore store = new MemoryObjectStore();
                members._stores.Add(store);
                VortexDataset dataset = await VortexDataset.CreateAsync(
                    store, Request.Schema, new DatasetOptions { Session = session, Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Request.At))] } }, Ct);
                members._datasets.Add(dataset);
                for (int o = at; o < at + count; o++)
                {
                    await AppendAsync(dataset, o);
                }

                at += count;
            }

            members.Set = new VortexDatasetSet([.. members._datasets]);
            return members;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (VortexDataset dataset in _datasets)
            {
                await dataset.DisposeAsync();
            }

            foreach (MemoryObjectStore store in _stores)
            {
                await store.DisposeAsync();
            }
        }
    }
}
