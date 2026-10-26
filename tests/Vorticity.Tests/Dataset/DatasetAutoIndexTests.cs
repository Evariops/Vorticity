// IndexPolicy.Auto committed as a dataset's policy: every column a writer meets takes its automatic index, a column
// schema evolution adds after the policy included, and a policy that names no column and gives the others none is
// still refused.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetAutoIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AColumnAddedAfterAnAutomaticPolicyTakesItsIndex()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Before.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Before.Id)],
            Write = new VortexWriteOptions { IndexBudgetPerMille = 100_000 },
        }, Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Before>([.. Enumerable.Range(0, 4_096).Select(i => new Before(i, $"tag-{i % 7}"))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.IndexAsync(IndexPolicy.Auto, cancellationToken: Ct);
        Assert.Equal(IndexPolicyKind.Auto, dataset.Snapshot.Schema.IndexPolicy.Default.Kind);
        await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.IndexAsync(IndexPolicy.None, cancellationToken: Ct));

        await dataset.EvolveSchemaAsync(After.Schema, cancellationToken: Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<After>([.. Enumerable.Range(4_096, 4_096).Select(i => new After(i, $"tag-{i % 7}", $"region-{i % 5}"))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        // The object written after the evolution indexes the new column, which no override names: a column of five
        // values, which Auto's dictionary probe serves.
        int region = dataset.Snapshot.Schema.DType.IndexOfField(nameof(After.Region));
        bool indexed = false;
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
            if (lease.File.DType.IndexOfField(nameof(After.Region)) < 0)
            {
                continue;
            }

            IndexDirectory? directory = await lease.File.ReadIndexDirectoryAsync(Ct);
            indexed |= directory is not null && directory.Entries.Any(entry => entry.ColumnPath is [uint field] && field == (uint)region);
        }

        Assert.True(indexed, "the column added after the policy took no index");
        Assert.Equal(Enumerable.Range(4_096, 4_096).Count(i => i % 5 == 3), await dataset.Scan<After>().Where(r => r.Region == "region-3").CountAsync(Ct));
    }

    [VortexRecord]
    public partial record struct Before(long Id, string Tag);

    [VortexRecord]
    public partial record struct After(long Id, string Tag, string? Region);
}
