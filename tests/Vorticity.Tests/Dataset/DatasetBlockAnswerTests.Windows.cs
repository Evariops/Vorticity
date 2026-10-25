// Windows on the instant, the key's second column, whose edges cut blocks: a block an edge cuts is read as rows, the
// blocks the window holds whole are settled by their statistics, and the blocks it leaves out by their extremes.
//
// THE ORACLE IS THE ROWS: every window drawn, to the second, gives the rows' answer, whether the blocks end at each hour or
// not; and a window whose edges cut most rows' blocks reads no more than its rows take, the blocks' statistics aside.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBlockAnswerTests
{
    /// <summary>Windows drawn to the second over the two hours, under keys and measures drawn too, answer as their rows do.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AWindowCuttingBlocksAnswersAsItsRowsDo(int seed)
    {
        Random random = new Random(seed);
        string[][] keys = [["Service"], ["Service", "Status"], ["Status"], ["Host"], []];
        string[] measures = ["sum(Size)", "avg(Latency)", "min(Latency)", "max(Size)"];
        await using VortexDataset dataset = await VortexDataset.OpenAsync(seed % 2 == 0 ? _fixture.Hourly : _fixture.Store, new DatasetOptions(), Ct);
        for (int question = 0; question < 10; question++)
        {
            string[] key = keys[random.Next(keys.Length)];
            List<string> results = [.. key, "count()"];
            if (random.Next(2) == 0)
            {
                results.Add(measures[random.Next(measures.Length)]);
            }

            string filter = Window(Noon.AddSeconds(random.Next(7_200)), random.Next(1, 7_200));
            if (random.Next(3) == 0)
            {
                filter += random.Next(2) == 0 ? " and Status >= 500" : " and Service = 'svc-2'";
            }

            List<string> rows = await DatasetPlannerTests.AskAsync(dataset, key, [.. results], filter, synopses: false);
            List<string> blocks = await DatasetPlannerTests.AskAsync(dataset, key, [.. results], filter, synopses: true);
            Assert.True(rows.SequenceEqual(blocks), $"seed {seed}: by {string.Join(", ", key)}, {string.Join(", ", results)}, under {filter}: {blocks.Count} groups through the blocks, {rows.Count} through the rows");
        }
    }

    /// <summary>
    /// Counts by service and status over an hour that starts mid-hour, whose edges cut most rows' blocks: those are read as
    /// rows, with the blocks between two of them that cost less read so than a part of the scan more, which would read the
    /// chunks the two share again. Never more than the rows take, the blocks' statistics aside.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWindowCuttingMostBlocksReadsNoMoreThanItsRows(bool hourly)
    {
        string[] keys = ["Service", "Status"];
        string[] results = ["Service", "Status", "count()"];
        string filter = Window(Noon.AddMinutes(37.5), 3_600);
        await using CountingObjectStore store = new CountingObjectStore(hourly ? _fixture.Hourly : _fixture.Store);
        long[] bytes = new long[2];
        foreach (bool synopses in (bool[])[false, true])
        {
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
            store.Reset();
            await DatasetPlannerTests.AskAsync(dataset, keys, results, filter, synopses);
            bytes[synopses ? 1 : 0] = store.BytesRead;
        }

        Console.WriteLine($"WINDOW EDGES counts by service and status over an hour from 12:37:30{(hourly ? ", blocks ending at each hour" : string.Empty)}: {bytes[1]:N0} bytes through the blocks, {bytes[0]:N0} through the rows");
        Assert.True(bytes[1] <= bytes[0] + (16 << 10), $"{bytes[1]} bytes through the blocks, {bytes[0]} through the rows");
    }

    private static string Window(DateTimeOffset from, int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"At >= '{from:O}' and At < '{from.AddSeconds(seconds):O}'");
}
