using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// An integer key numbered by value over a wide span turns to the core once a lane's first batch shows
/// it scattered over that span, before a row is folded: a file with zone maps or without, a dataset. A
/// key in the order of the rows does not, with sentinels or without, nor hot keys that drift, nor
/// keys a little late, nor one of too few values.
/// </summary>
public sealed partial class SpreadTurnTests
{
    private const int Rows = 900_000;

    [Theory]
    [InlineData("scattered", true)]
    [InlineData("ordered", false)]
    [InlineData("sentinels", false)]
    [InlineData("narrow", false)]
    [InlineData("drift", false)]
    [InlineData("late", false)]
    public async Task AKeyItsFirstBatchShowsScatteredTurnsWhetherTheFileHasZonesOrNot(string shape, bool turns)
    {
        // An edition with no zone maps, and the library's own, whose zones would read the key with a
        // sentinel as covering its span: one judgment for both.
        Row[] rows = Rows_(shape);
        foreach (VortexEdition edition in (VortexEdition[])[VortexEdition.Core20251000, VortexEditions.Default])
        {
            string path = await WriteAsync(rows, edition);
            try
            {
                await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
                await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
                (Dictionary<int, KeyTotal> got, CoreRun? core) = await TotalsAsync(file.Scan<Row>());
                Assert.Equal(Expected(rows), got);
                Assert.True((core?.Reason == CoreReason.Spread) == turns, $"{shape}, {edition}: core {core?.Reason.ToString() ?? "none"}");
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ADatasetTurnsAKeyItsFirstRowsSpreadOverItsSpan()
    {
        Row[] rows = Rows_("scattered");
        string path = await WriteAsync(rows, VortexEditions.Default);
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(
                store, VortexTypes.ToDType(Row.Schema, new Vorticity.Types.DTypeArena()), new DatasetOptions { Seed = 7 }, Ct);
            await using (VortexFile open = await VortexFile.OpenAsync(path, Ct))
            {
                for (int o = 0; o < 4; o++)
                {
                    RowRange slice = new RowRange((long)Rows * o / 4, (long)Rows * (o + 1) / 4);
                    await dataset.AppendAsync(open.Scan<Row>().Rows(slice).ToBatchesAsync(Ct), Ct);
                }
            }

            (Dictionary<int, KeyTotal> got, CoreRun? core) = await TotalsAsync(dataset.Scan<Row>().With(new ScanOptions { DegreeOfParallelism = 4 }));
            Assert.Equal(Expected(rows), got);
            Assert.Equal(CoreReason.Spread, core?.Reason);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Each key's rows and the sum of their values, and the core the run took, if any: four lanes, the core from four.</summary>
    private static async Task<(Dictionary<int, KeyTotal> Totals, CoreRun? Core)> TotalsAsync(Scan<Row> scan)
    {
        Vorticity.Aggregation query = scan.GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
        query.Plan.CoreLanes = 4;
        Dictionary<int, KeyTotal> totals = [];
        await foreach (KeyTotal total in query.As<KeyTotal>().ToRecordsAsync(Ct))
        {
            totals.Add(total.Key, total);
        }

        return (totals, query.Plan.LastRun?.Core);
    }

    private static Dictionary<int, KeyTotal> Expected(Row[] rows) =>
        rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => new KeyTotal(g.Key, g.Count(), g.Sum(r => r.Value)));

    /// <summary>
    /// A key over 1.5 million values in no order; twice the row in the order of the rows, a span of 1.8
    /// million; the same with one row in a hundred a sentinel, 0; 10⁵ values in no order; the bench's
    /// drift, half the rows hot keys a window that moves, half a million values in no order above
    /// them; and its late keys, four rows a value and up to 2 500 values behind.
    /// </summary>
    private static Row[] Rows_(string shape)
    {
        Row[] rows = new Row[Rows];
        int phase = Rows / 16;
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = Mix((ulong)row);
            int key = shape switch
            {
                "scattered" => (int)(mix % 1_500_000),
                "ordered" => 2 * row,
                "sentinels" => row % 100 == 0 ? 0 : 2 * row,
                "drift" => (mix & 1) == 0 ? (row / phase * 1_000) + (int)((mix >> 1) % 1_000) : 1_000_000 + (int)((mix >> 32) % 1_000_000),
                "late" => (2 * row) + (int)(mix % 2_500),
                _ => (int)(mix % 100_000),
            };
            rows[row] = new Row(key, (long)((mix >> 32) % 1_000));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Row[] rows, VortexEdition edition)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "spread-turn");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { TargetEdition = edition }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct KeyTotal(int Key, long Count, long Total);
}
