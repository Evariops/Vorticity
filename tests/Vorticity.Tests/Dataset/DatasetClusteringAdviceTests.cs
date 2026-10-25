// What a clustering of each column the questions' filters read would have spared them, ranked: the column the questions
// lean on most, a diagnostic the library never acts on alone.
//
// THE ORACLE IS THE LOG: on a log of questions drawn, a selective column asked often ranks first, a column of few values
// kept by most rows after it, and the key's first column, which the dataset is clustered by already, never.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetClusteringAdviceTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheColumnTheQuestionsLeanOnRanksFirst()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
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

        Assert.Empty(dataset.RecommendClustering());

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

        IReadOnlyList<ClusteringAdvice> ranked = dataset.RecommendClustering();
        Console.WriteLine($"CLUSTERING ADVICE: {string.Join("; ", ranked.Select(a => $"{a.Column} {a.BytesAvoided:N0} bytes over {a.Objects} objects"))}");
        Assert.Equal(["Tenant", "Status"], ranked.Select(a => a.Column));
        Assert.True(ranked[0].BytesAvoided * 2 > ranked[1].BytesAvoided * 3, "a tenant leaves 98 % of the rows out, a status half of them on the whole");
        Assert.All(ranked, advice => Assert.Equal(8 * 4, advice.Objects));
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Tenant, short Status, long Size);
}
