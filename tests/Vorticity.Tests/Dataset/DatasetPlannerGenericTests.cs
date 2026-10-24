// The planner's questions on a dataset without periods: its objects cut where the hour of their instant changes, by a
// clustering value the application declares, each with its columns' counts once a job writes it.
//
// WHAT IS HELD: every question DatasetPlannerTests asks gives the same answer whether the objects' counts answer it as
// states or every object is read as rows, and the counts answer some objects of each; the same with objects cut by size
// alone, whose keys of the hour each object holds constant or not, and which a window covers whole or not.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPlannerGenericTests : IClassFixture<DatasetPlannerGenericTests.Fixture>
{
    private readonly Fixture _fixture;

    public DatasetPlannerGenericTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, bool> Questions()
    {
        TheoryData<string, bool> questions = [];
        foreach (string question in DatasetPlannerTests.Named)
        {
            questions.Add(question, true);
            questions.Add(question, false);
        }

        return questions;
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionOnOneColumnAnswersAsItsRowsDo(string question, bool byHour)
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(byHour ? _fixture.ByHour : _fixture.BySize, Options(), Ct);
        List<string> rows = await DatasetPlannerTests.AskAsync(dataset, question, synopses: false);
        List<string> states = await DatasetPlannerTests.AskAsync(dataset, question, synopses: true);
        ScanPlan plan = await DatasetPlannerTests.Ask(dataset, question, synopses: true).ExplainAsync(Ct);
        int answered = plan.States.Count(step => step.Read == "states");
        Console.WriteLine($"PLANNER {(byHour ? "BY HOUR" : "BY SIZE")} {question}: the counts of {answered} objects answered");
        Assert.NotEmpty(rows);
        Assert.Equal(rows, states);
        Assert.True(!byHour || answered > 0, $"{question}: no object's counts answered");
    }

    private static DatasetOptions Options() => new DatasetOptions { SheetObjectBytes = 0, ContentsObjectBytes = 0 };

    /// <summary>
    /// The planner's four hours of calls and ten minutes more, appended in parts of five minutes and compacted: once into
    /// objects cut at every hour, once into objects of a size alone; the last parts appended after, in objects of their own.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore ByHour { get; } = new MemoryObjectStore();

        internal MemoryObjectStore BySize { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            await WriteAsync(ByHour, new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, VortexWriteOptions.Default);

            // Blocks of a few kilobytes, so that an object's size moves as its rows are written and a cut at 96 KiB shows: an
            // object goes past the size it aims at by a block at most.
            await WriteAsync(BySize, new ClusteringOptions { ObjectBytes = 96L << 10 }, new VortexWriteOptions { RowBlockSize = 512, DataBlockTargetBytes = 8 << 10 });
        }

        private static async Task WriteAsync(MemoryObjectStore store, ClusteringOptions clustering, VortexWriteOptions write)
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, DatasetPlannerTests.Call.Schema, Options() with
            {
                ClusteringKey = [nameof(DatasetPlannerTests.Call.At)],
                Clustering = clustering,
                Write = write,
            });
            Random random = new Random(83);
            string[] services = ["api", "auth", "billing", "cart", "search", "media"];
            const int RowsPerHour = 12_000;
            int row = 0;
            for (int part = 0; part < (4 * 12) + 2; part++)
            {
                if (part == 4 * 12)
                {
                    while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
                    {
                    }
                }

                DatasetPlannerTests.Call[] rows = new DatasetPlannerTests.Call[RowsPerHour / 12];
                for (int i = 0; i < rows.Length; i++, row++)
                {
                    int service = random.Next(services.Length);
                    rows[i] = new DatasetPlannerTests.Call(
                        new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddSeconds(row * 3_600.0 / RowsPerHour),
                        services[service],
                        $"/{services[service]}/{random.Next(40)}",
                        random.NextInt64(1_000, 5_000_000),
                        Math.Round(random.NextDouble() * 100, 2));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<DatasetPlannerTests.Call>(rows);
                await dataset.AppendAsync(draft);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ByHour.DisposeAsync();
            await BySize.DisposeAsync();
        }
    }
}
