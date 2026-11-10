// An append whose rows span several partitions copies each partition's rows to its store in their order: run by run where
// they come in runs of a partition, gathered by partition where they are scattered (DatasetWriter.Split). With no key to
// sort them, each partition's object keeps its rows in the order they were written, whichever way they were routed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSplitRunsTests
{
    private const int Rows = 30_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Shapes => new TheoryData<string> { "runs", "scattered", "mixed" };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task EachPartitionKeepsItsRowsInTheirOrder(string shape)
    {
        // Runs of 500 rows of a day in turn, the days coming back; a day a row; runs broken now and then by a row of another.
        Logged[] rows = new Logged[Rows];
        for (int i = 0; i < Rows; i++)
        {
            int day = shape switch
            {
                "runs" => i / 500 % 3,
                "scattered" => i % 3,
                _ => i % 997 == 0 ? (i / 500 % 3) + 1 : i / 500 % 3,
            };
            rows[i] = new Logged(day, i, $"note-{i % 13}");
        }

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Logged.Day)) },
        }, Ct);
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Logged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        foreach (int day in rows.Select(static r => r.Day).Distinct())
        {
            List<Logged> read = await dataset.Scan<Logged>().Where(r => r.Day == day).ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(rows.Where(r => r.Day == day).Select(static r => r.Sequence), read.Select(static r => r.Sequence));
            Assert.All(read, r => Assert.Equal($"note-{r.Sequence % 13}", r.Note));
        }
    }

    [VortexRecord]
    public partial record struct Logged(int Day, long Sequence, string Note);
}
