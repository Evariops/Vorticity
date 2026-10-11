using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A Parquet file read on several lanes: its rows cut at its row groups, each lane reading whole row
/// groups of its own, and the answers the ones one lane gives; a range of rows read from where it
/// starts in its row group to where it ends.
/// </summary>
public sealed partial class ParallelScanTests : IDisposable
{
    private const int GroupRows = 16_384;
    private const int Groups = 8;
    private const int BatchRows = 8_192;

    private static readonly string[] Desks = ["fx", "rates", "credit", "equity", "commodities"];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vorticity-parallel-{Guid.NewGuid():N}.parquet");

    [VortexRecord]
    public partial record struct Trade(long Id, string Desk, double Price);

    [VortexRecord]
    public partial record struct DeskSum(string Desk, long Count, double Sum);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public async Task CutsTheRowsAtTheRowGroupsAndThePagesEveryColumnStarts()
    {
        await WriteAsync();
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        RowRange[] pieces = Assert.IsType<RowRange[]>(await file.Source.PiecesAsync(new ScanSpec(), 4, Ct));
        Assert.True(pieces.Length > 1);
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal((long)GroupRows * Groups, pieces[^1].End);
        Assert.All(pieces, piece => Assert.Equal(0, piece.Start % BatchRows));
        Assert.All(pieces.Zip(pieces.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));

        // One lane is one range; a single group is cut where its pages start.
        Assert.Null(await file.Source.PiecesAsync(new ScanSpec(), 1, Ct));
        RowRange[] one = Assert.IsType<RowRange[]>(await file.Source.PiecesAsync(new ScanSpec { Rows = new RowRange(GroupRows, 2 * GroupRows) }, 4, Ct));
        Assert.Equal([new RowRange(GroupRows, GroupRows + BatchRows), new RowRange(GroupRows + BatchRows, 2 * GroupRows)], one);

        // The row groups a filter's statistics rule out are dead rows: the live ones are shared.
        ScanSpec filtered = new() { Filter = Expressions.Expr.Ge(Expressions.Expr.Field("Id"), Expressions.Expr.Literal(Expressions.FilterLiteral.From(6L * GroupRows))) };
        RowRange[] live = Assert.IsType<RowRange[]>(await file.Source.PiecesAsync(filtered, 4, Ct));
        Assert.True(live.Count(piece => piece.End > 6L * GroupRows) >= 2, string.Join(", ", live));
    }

    [Fact]
    public async Task ReadsARangeFromWhereItStartsInItsRowGroup()
    {
        Trade[] rows = await WriteAsync();
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        Scan<Trade> scan = file.Scan<Trade>().Rows(new RowRange(GroupRows + 10_000, GroupRows + 12_000)).With(new ScanOptions { BatchRows = 1_024 });
        List<Trade> read = [];
        await foreach (Trade trade in scan.ToRecordsAsync(Ct))
        {
            read.Add(trade);
        }

        Assert.Equal(rows[(GroupRows + 10_000)..(GroupRows + 12_000)], read);

        // The batches of 1 024 rows that hold the range, and none before or past it.
        Assert.Equal(3, scan.Metrics.BlocksDecoded);
    }

    [Fact]
    public async Task AggregatesOnSeveralLanesAsOnOne()
    {
        Trade[] rows = await WriteAsync();
        await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
        List<DeskSum>[] answers = new List<DeskSum>[2];
        foreach ((int at, int degree) in ((int, int)[])[(0, 1), (1, 4)])
        {
            Vorticity.Aggregation desks = file.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = degree })
                .GroupBy(r => r.Desk).Select(g => (g.Key, g.Count(), g.Sum(x => x.Price)));
            List<DeskSum> read = [];
            await foreach (DeskSum sum in desks.As<DeskSum>().ToRecordsAsync(Ct))
            {
                read.Add(sum);
            }

            answers[at] = [.. read.OrderBy(d => d.Desk, StringComparer.Ordinal)];
            AggregationRun run = desks.Plan.LastRun!;
            if (degree > 1)
            {
                Assert.True(run.Lanes.Length > 1, $"{run.Lanes.Length} lanes");
            }
        }

        Assert.Equal(
            rows.GroupBy(r => r.Desk).Select(g => (g.Key, (long)g.Count())).OrderBy(g => g.Key, StringComparer.Ordinal),
            answers[1].Select(d => (d.Desk, d.Count)));
        Assert.Equal(answers[0].Select(d => (d.Desk, d.Count)), answers[1].Select(d => (d.Desk, d.Count)));
        Assert.All(answers[0].Zip(answers[1]), pair => Assert.Equal(pair.First.Sum, pair.Second.Sum, 6));
    }

    [Fact]
    public async Task CutsARowGroupOnlyWhereEveryColumnStartsAPage()
    {
        // One row group, its pages on the batches: it spreads over the lanes.
        Trade[] rows = await WriteAsync(new ParquetWriteOptions { RowGroupRows = GroupRows * Groups });
        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            RowRange[] pieces = Assert.IsType<RowRange[]>(await file.Source.PiecesAsync(new ScanSpec(), 4, Ct));
            Assert.True(pieces.Length >= 4, string.Join(", ", pieces));
            Assert.All(pieces, piece => Assert.Equal(0, piece.Start % BatchRows));
            Assert.Equal(rows.Length, await CountAsync(file, 4));
        }

        // Pages of a thousand rows start on no batch but every 1 024 000th row: the group is one range.
        await WriteAsync(new ParquetWriteOptions { RowGroupRows = 64_000, BlockRows = 1_000 });
        await using (ParquetFile file = await ParquetFile.OpenAsync(_path, Ct))
        {
            RowRange[]? pieces = await file.Source.PiecesAsync(new ScanSpec(), 4, Ct);
            Assert.NotNull(pieces);
            Assert.All(pieces, piece => Assert.Equal(0, piece.Start % 64_000));
            Assert.Equal(rows.Length, await CountAsync(file, 4));
        }
    }

    /// <summary>The rows a grouped count over <paramref name="degree"/> lanes adds up to.</summary>
    private static async Task<long> CountAsync(ParquetFile file, int degree)
    {
        Vorticity.Aggregation desks = file.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = degree })
            .GroupBy(r => r.Desk).Select(g => (g.Key, g.Count(), g.Sum(x => x.Price)));
        long count = 0;
        await foreach (DeskSum sum in desks.As<DeskSum>().ToRecordsAsync(Ct))
        {
            count += sum.Count;
        }

        return count;
    }

    private Task<Trade[]> WriteAsync() => WriteAsync(new ParquetWriteOptions { RowGroupRows = GroupRows });

    private async Task<Trade[]> WriteAsync(ParquetWriteOptions options)
    {
        Random random = new(17);
        Trade[] rows = new Trade[GroupRows * Groups];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Trade(i, Desks[random.Next(Desks.Length)], Math.Round(random.NextDouble() * 100, 2));
        }

        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Trade>(_path, options);
        await writer.WriteAsync<Trade>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return rows;
    }
}
