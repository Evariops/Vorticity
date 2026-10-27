// The key the questions' filters call for: what an order of the rows by each column they read would have spared them, and
// the order a key would take them in, a diagnostic the library never acts on alone.
//
// THE ORACLE IS THE LOG: on a log of questions drawn, a selective column asked often is credited the most bytes, a column
// of few values kept by most rows fewer, and the key's first column, which the dataset is ordered by already, nothing; the
// key takes the column of fewer values first, which groups the longer runs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLayoutAdviceTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheColumnTheQuestionsLeanOnRanksFirst()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour), SortKey = [LayoutValue.Column(nameof(Call.At))], Organization = LayoutOrganization.Leveled },
            SheetObjectBytes = 0,
            ContentsObjectBytes = 0,
        }, Ct);
        Random random = new Random(0xE5);
        for (int hour = 0; hour < 4; hour++)
        {
            Call[] rows = [.. Enumerable.Range(0, 5_000).Select(i => new Call(
                Noon.AddHours(hour).AddTicks(random.NextInt64(TimeSpan.FromHours(1).Ticks)),
                $"t-{random.Next(50):d2}",
                (short)(random.Next(10) < 7 ? 200 : 500),
                random.Next(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        // Compacted: a job's objects keep the sheets of their columns, whose registers count each column's values a partition.
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        Assert.Empty((await dataset.RecommendLayoutAsync(Ct)).Components);

        // A log: a tenant asked eight times, a status eight times, windows of the instant four times.
        string[] filters =
        [
            .. Enumerable.Range(0, 8).Select(i => $"Tenant = 't-{i:d2}'"),
            .. Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? "Status = 200" : "Status = 500"),
            .. Enumerable.Range(0, 4).Select(i => $"At >= '{Noon.AddHours(i):O}'"),
        ];
        foreach (string filter in filters)
        {
            Assert.NotEmpty(await DatasetPlannerTests.AskAsync(dataset, [], ["count()", "sum(Size)"], filter, synopses: false));
        }

        LayoutAdvice advice = await dataset.RecommendLayoutAsync(Ct);
        IReadOnlyList<ComponentAdvice> ranked = advice.Components;
        Console.WriteLine($"LAYOUT ADVICE: {string.Join("; ", ranked.Select(a => $"{a.Path} {a.BytesAvoided:N0} bytes over {a.Objects} objects, {a.DistinctPerPartition:F0} values a partition"))}, prefix {advice.IndexedPrefix}");
        Assert.Equal(["Status", "Tenant"], ranked.Select(a => a.Path));
        Assert.True(ranked[1].BytesAvoided * 2 > ranked[0].BytesAvoided * 3, "a tenant leaves 98 % of the rows out, a status half of them on the whole");
        Assert.All(ranked, component => Assert.Equal(8 * 4, component.Objects));
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Tenant, short Status, long Size);
}
