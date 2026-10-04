using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A group's chosen rows: its first and last in file order, and the row of its smallest or largest
/// value, the first in file order on a tie, a null or a NaN never one; their columns read after the
/// pass, the same at every degree, in a group by that streams as in one that does not, and over a
/// whole scan, against LINQ over the rows in file order.
/// </summary>
public sealed partial class ChosenRowTests
{
    private const int Rows = 50_000;

    private static readonly string[] Symbols = ["AAPL", "MSFT", "NVDA", "ORCL", "SAP", "VOD"];

    private static readonly string[] Venues = ["XNAS", "XNYS", "BATS", "IEXG"];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task OpenHighLowCloseAreTheFirstTheExtremesAndTheLast(int degree)
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            List<Candle> candles = await ListAsync(file.Scan<Trade>()
                .GroupBy(r => r.Symbol)
                .OrderBy(g => g.Key)
                .Select(g => (
                    g.Key,
                    g.First().Price,
                    g.Max(x => x.Price),
                    g.Min(x => x.Price),
                    g.Last().Price,
                    g.MaxBy(x => x.Price).Minute,
                    g.MaxBy(x => x.Price).Venue,
                    g.MinBy(x => x.Size).Minute,
                    g.Where(x => x.Venue == "IEXG").First().Minute))
                .As<Candle>());

            List<Candle> expected = [.. rows.GroupBy(r => r.Symbol).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                Trade[] of = [.. g];
                Trade high = Best(of, r => r.Price, max: true)!.Value;
                Trade low = Best(of, r => r.Size, max: false)!.Value;
                // The extremes skip a NaN, as the file statistics do.
                Trade[] numbers = [.. of.Where(r => r.Price is not double.NaN)];
                return new Candle(
                    g.Key, of[0].Price, numbers.Max(r => r.Price), numbers.Min(r => r.Price), of[^1].Price,
                    high.Minute, high.Venue, low.Minute, of.FirstOrDefault(r => r.Venue == "IEXG").Minute);
            })];
            Assert.Equal(expected, candles);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AGroupWithoutACandidateReadsNullOrTheDefault()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<NoCandidate> groups = await ListAsync(file.Scan<Trade>()
                .Where(r => r.Price == null)
                .GroupBy(r => r.Symbol)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.MaxBy(x => x.Price).Minute, g.MaxBy(x => x.Price).Venue, g.Where(x => x.Size < 0).Last().Price))
                .As<NoCandidate>());

            Assert.NotEmpty(groups);
            Assert.All(groups, g => Assert.Equal(new NoCandidate(g.Symbol, null, null, null), g));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AWholeScanTakesItsChosenRowsInFileOrder()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Ends ends = await file.Scan<Trade>()
                .Where(r => r.Minute >= 100)
                .AggAsync<Ends>(a => (a.First().Symbol, a.Last().Symbol, a.MaxBy(x => x.Size).Venue, a.MinBy(x => x.Price).Minute), Ct);

            Trade[] kept = [.. rows.Where(r => r.Minute >= 100)];
            Assert.Equal(new Ends(kept[0].Symbol, kept[^1].Symbol, Best(kept, r => r.Size, max: true)!.Value.Venue, Best(kept, r => r.Price, max: false)!.Value.Minute), ends);
            Assert.Equal(kept[^1].Venue, await file.Scan<Trade>().Where(r => r.Minute >= 100).AggAsync(a => a.Last().Venue, Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AGroupByThatStreamsReadsTheRowsOfTheGroupsItCloses()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation byMinute = file.Scan<Trade>()
                .GroupBy(r => r.Minute)
                .Where(g => g.First().Size > 10)
                .Select(g => (g.Key, g.First().Venue, g.Last().Symbol, g.MaxBy(x => x.Size).Size));
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)byMinute.Query) >= 0);
            List<MinuteEnds> minutes = await ListAsync(byMinute.As<MinuteEnds>());
            List<MinuteEnds> expected = [.. rows.GroupBy(r => r.Minute).OrderBy(g => g.Key)
                .Where(g => g.First().Size > 10)
                .Select(g => new MinuteEnds(g.Key, g.First().Venue, g.Last().Symbol, g.Max(r => r.Size)))];
            Assert.Equal(expected, minutes);
            Assert.True(((AggregationQuery)byMinute.Query).PeakGroups < expected.Count, $"the streaming group by held {((AggregationQuery)byMinute.Query).PeakGroups} groups");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AChosenColumnIsFilteredOnAndOrderedByButReadByNoAggregate()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<string> symbols = await file.Scan<Trade>()
                .GroupBy(r => r.Symbol)
                .Where(g => g.Last().Size > 5)
                .OrderByDescending(g => g.First().Minute)
                .ThenBy(g => g.Key)
                .Select(g => g.Key)
                .ToListAsync(Ct);
            Assert.Equal(
                rows.GroupBy(r => r.Symbol).Where(g => g.Last().Size > 5).OrderByDescending(g => g.First().Minute).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key),
                symbols);

            Assert.Throws<InvalidOperationException>(() => file.Scan<Trade>().GroupBy(r => r.Symbol).Select(g => g.Sum(x => g.First().Size)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The row of the largest or smallest value, the first on a tie, nulls and NaN never.</summary>
    private static Trade? Best(Trade[] rows, Func<Trade, double?> by, bool max)
    {
        Trade? best = null;
        double kept = 0;
        foreach (Trade row in rows)
        {
            if (by(row) is not double value || double.IsNaN(value))
            {
                continue;
            }

            if (best is null || (max ? value > kept : value < kept))
            {
                best = row;
                kept = value;
            }
        }

        return best;
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Trades by the minute, sorted: prices with nulls and NaN, sizes with ties.</summary>
    private static Trade[] Trades()
    {
        Random random = new Random(23);
        Trade[] rows = new Trade[Rows];
        for (int row = 0; row < Rows; row++)
        {
            double? price = row % 37 == 0 ? null : row % 101 == 0 ? double.NaN : Math.Round(100 + (random.NextDouble() * 50), 2);
            rows[row] = new Trade(Symbols[random.Next(Symbols.Length)], row / 50, price, Venues[random.Next(Venues.Length)], random.Next(1, 40));
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Trade[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "chosen-rows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trades-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Trade>(path))
        {
            await writer.WriteAsync<Trade>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Trade(string Symbol, int Minute, double? Price, string Venue, int Size);

    [VortexRecord]
    public partial record struct Candle(string Symbol, double? Open, double? High, double? Low, double? Close, int? HighAt, string? HighVenue, int? SmallestAt, int? FirstIexAt);

    [VortexRecord]
    public partial record struct NoCandidate(string Symbol, int? HighAt, string? HighVenue, double? LastNegative);

    [VortexRecord]
    public partial record struct Ends(string FirstSymbol, string LastSymbol, string? BiggestVenue, int? CheapestAt);

    [VortexRecord]
    public partial record struct MinuteEnds(int Minute, string FirstVenue, string LastSymbol, int? Biggest);
}
