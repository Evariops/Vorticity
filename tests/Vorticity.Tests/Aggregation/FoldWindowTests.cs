using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The slots fold a batch a window of rows at a time (PLAN-HIGH-CARDINALITY.md, H15), every slot the
/// window before the next window: windows of one row, seven and sixty-four give the answers of the
/// whole batch, for every kind of state, with nulls, under a filter's selection, on one lane and four.
/// </summary>
public sealed partial class FoldWindowTests
{
    private const int Rows = 120_000;

    public static TheoryData<int, bool> Cases => new TheoryData<int, bool>
    {
        { 1, false },
        { 1, true },
        { 4, false },
        { 4, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryWindowGivesTheAnswersOfTheBatch(int degree, bool filtered)
    {
        string path = await WriteAsync();
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Stats> whole = await RunAsync(file, filtered, window: 0);
            Assert.True(whole.Count > 4_900, $"{whole.Count} groups");
            foreach (int window in (int[])[1, 7, 64])
            {
                Assert.Equal(whole, await RunAsync(file, filtered, window));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<List<Stats>> RunAsync(VortexFile file, bool filtered, int window)
    {
        Scan<Row> scan = filtered ? file.Scan<Row>().Where(r => r.Value > 20L) : file.Scan<Row>();
        Vorticity.Aggregation grouped = scan
            .GroupBy(r => r.Key)
            .Select(g => (
                g.Key, g.Count(), g.Sum(r => r.Value), g.Average(r => r.Real), g.Min(r => r.Real), g.Max(r => r.Value),
                g.Any(r => r.Flag == true), g.Min(r => r.Text), g.CountDistinct(r => r.Small), g.First().Value, g.MaxBy(r => r.Real).Value));
        grouped.Plan.FoldWindow = window;
        List<Stats> stats = [];
        await foreach (Stats row in grouped.As<Stats>().ToRecordsAsync(Ct))
        {
            stats.Add(row);
        }

        return [.. stats.OrderBy(s => s.Key)];
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A key of five thousand values in no order, whose records outgrow the size under which a batch
    /// is folded whole; values with nulls, a float, a flag, a text and a small integer.
    /// </summary>
    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fold-windows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            rows[row] = new Row(
                (int)((mix >> 24) % 5_000),
                row % 13 == 0 ? null : (long)((mix >> 8) % 100),
                ((mix >> 16) % 1_000) / 8.0,
                (mix >> 40) % 3 == 0,
                $"t-{(mix >> 44) % 500:D3}",
                (int)((mix >> 50) % 40));
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int Key, long? Value, double Real, bool Flag, string Text, int Small);

    [VortexRecord]
    public partial record struct Stats(
        int Key, long Count, long Sum, double? Mean, double? Least, long? Largest, bool AnyFlag, string? FirstText, long Smalls, long? FirstValue, long? ValueOfMaxReal);
}
