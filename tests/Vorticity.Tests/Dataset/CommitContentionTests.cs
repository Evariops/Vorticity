// Two writers that commit in turn on one dataset: a handle builds its first attempt on the version it holds, and that
// attempt loses whenever the other committed since. A handle whose last commit met another writer's asks for the latest
// version first, and loses no put; one alone keeps putting outright, at one request a commit.
//
// THE ORACLE IS THE STORE: the puts of commit objects it created and refused, and the rows read back.
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class CommitContentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two handles appending in turn, twenty times each: each loses its first put once, against the other's first commit,
    /// then asks first and loses none, where every put after the first of each lost before.
    /// </summary>
    [Fact]
    public async Task WritersInTurnLoseTheirFirstPutOnceEach()
    {
        PutCountingStore store = new PutCountingStore(new MemoryObjectStore());
        await using VortexDataset first = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions(), Ct);
        await using VortexDataset second = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        List<Row> all = [];
        for (int turn = 0; turn < 20; turn++)
        {
            await AppendAsync(first, all);
            await AppendAsync(second, all);
        }

        Assert.Equal(40, store.Created - 1);
        Assert.InRange(store.Refused, 1, 2);
        await first.RefreshAsync(Ct);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await first.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>A handle alone commits outright at every append, its versions its own: it never asks first, and loses nothing.</summary>
    [Fact]
    public async Task AWriterAloneKeepsCommittingOutright()
    {
        PutCountingStore store = new PutCountingStore(new MemoryObjectStore());
        await using VortexDataset alone = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions(), Ct);
        List<Row> all = [];
        long lists = store.Lists;
        for (int turn = 0; turn < 20; turn++)
        {
            await AppendAsync(alone, all);
        }

        Assert.Equal(0, store.Refused);
        Assert.Equal(lists, store.Lists);
    }

    private static async Task AppendAsync(VortexDataset dataset, List<Row> all)
    {
        Row[] rows = [.. Enumerable.Range(0, 10).Select(i => new Row(all.Count + i, $"r{all.Count + i}"))];
        all.AddRange(rows);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    /// <summary>A store that counts the puts of commit objects it created and refused, and the listings it served.</summary>
    private sealed class PutCountingStore(IObjectStore inner) : ForwardingStore(inner)
    {
        internal int Created { get; private set; }

        internal int Refused { get; private set; }

        internal long Lists { get; private set; }

        public override async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            PutOutcome outcome = await Inner.PutAsync(key, content, length, condition, cancellationToken);
            if (key.StartsWith("commit/", StringComparison.Ordinal))
            {
                if (outcome == PutOutcome.Created)
                {
                    Created++;
                }
                else
                {
                    Refused++;
                }
            }

            return outcome;
        }

        public override IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken)
        {
            Lists++;
            return Inner.ListAsync(prefix, startAfter, cancellationToken);
        }
    }

    [VortexRecord]
    public partial record struct Row(long Sequence, string Name);
}
