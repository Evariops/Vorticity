// A job's writer sends the runs its merge staged through on a lane of their own while the merge stages the next ones: the
// objects a consolidation writes on four lanes are those it writes on one, cut at the same rows, holding the same rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetCompactionLanesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two days of documents in blocks of 1 024 rows, consolidated on one lane and on four. On four, the writers send
    /// staged blocks through on a lane of their own; on one, none. The objects are cut at the same rows either way, the size
    /// a writer reads to cut them taken before a staged block goes through, whichever lane it goes through on; and they hold
    /// the same rows. Their bytes are not compared: a job's writer writes a column's chunk early past its share of the jobs'
    /// budget, and an object's bytes differ by some hundreds from one run to the next at one degree.
    /// </summary>
    [Fact]
    public async Task AConsolidationOnFourLanesCutsItsObjectsAtTheRowsItCutsThemOnOne()
    {
        (List<long> alone, List<Doc> aloneRows, long aloneFlown) = await ConsolidateAsync(lanes: 1);
        (List<long> across, List<Doc> acrossRows, long acrossFlown) = await ConsolidateAsync(lanes: 4);
        Assert.Equal(0, aloneFlown);
        Assert.True(acrossFlown > 0, "no staged block went through on a lane of its own");
        Assert.True(alone.Count >= 2, $"{alone.Count} objects");
        Assert.Equal(alone, across);
        Assert.Equal(aloneRows.Count, acrossRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, acrossRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(acrossRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    /// <summary>
    /// A consolidation on a session of four lanes, three of them held by other work: the job's census, its blocks and its
    /// chunks go through on the one left and on its own thread, and it ends with the objects it writes on one lane.
    /// </summary>
    [Fact]
    public async Task AConsolidationLeftOneLaneOfFourEndsWithTheObjectsOfOne()
    {
        (List<long> alone, List<Doc> aloneRows, _) = await ConsolidateAsync(lanes: 1);
        (List<long> left, List<Doc> leftRows, _) = await ConsolidateAsync(lanes: 4, held: 3);
        Assert.Equal(alone, left);
        Assert.Equal(aloneRows.Count, leftRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, leftRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(leftRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    private static async Task<(List<long> Objects, List<Doc> Rows, long Flown)> ConsolidateAsync(int lanes, int held = 0)
    {
        Random random = new Random(29);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        long[] taken = new long[held];
        for (int i = 0; i < held; i++)
        {
            Assert.True(session.WriteLanes.TryEnter(out taken[i]));
        }
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions
        {
            Session = session,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Doc.Day)),
                SortKey = [LayoutValue.Column(nameof(Doc.Service))],
                ObjectBytes = 1 << 18,
                CalmAfter = null,
            },
        }, Ct);
        for (int a = 0; a < 6; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        Assert.NotEmpty(results);
        List<long> objects = [];
        await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects.Add(positioned.Entry.Rows);
        }

        List<Doc> read = [.. (await dataset.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence)];
        foreach (long entered in taken)
        {
            session.WriteLanes.Exit(entered);
        }

        return (objects, read, results.Sum(static result => result.WriterBatchesFlown));
    }

    /// <summary>Attributes of a service's records: a route, a status, a duration, a user, and now and then a key of its own.</summary>
    private static Variant Attributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(20)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(500)}")),
        ];
        if (random.Next(5) == 0)
        {
            fields.Add(($"k{random.Next(40)}", Variant.From(random.Next(1_000))));
        }

        return Variant.Object([.. fields]);
    }

    [VortexRecord]
    public partial record struct Doc(int Day, string Service, long Sequence, Variant Attributes);
}
