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

    private const int ChunkRows = 1_024;

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

    [Fact]
    public async Task ATopKReadsTheRowsOfTheGroupsItDeliversAlone()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // The ten minutes of the highest prices, and the size of the row of each.
            int[] top = [.. rows.Select((row, position) => (row, position)).GroupBy(r => r.row.Minute)
                .Select(g => (Minute: g.Key, High: g.Where(r => r.row.Price is double p && !double.IsNaN(p)).MaxBy(r => r.row.Price!.Value)))
                .OrderByDescending(g => g.High.row.Price).ThenBy(g => g.Minute)
                .Take(10)
                .Select(g => g.High.position)];
            (List<int> sizes, ScanStatistics reads) = await HighestAsync(file, 10, row: true);
            Assert.Equal(top.Select(position => rows[position].Size), sizes);

            // What the rows cost: one chunk for one row; the chunks of the ten for ten; every
            // chunk for every group. The pass is the same with the rows or without them.
            ScanStatistics one = Fetched((await HighestAsync(file, 1, row: true)).Reads, (await HighestAsync(file, 1, row: false)).Reads);
            ScanStatistics ten = Fetched(reads, (await HighestAsync(file, 10, row: false)).Reads);
            ScanStatistics all = Fetched((await HighestAsync(file, int.MaxValue, row: true)).Reads, (await HighestAsync(file, int.MaxValue, row: false)).Reads);
            int chunks = top.Select(position => position / ChunkRows).Distinct().Count();
            Assert.True(one.Requests > 0 && one.BlocksDecoded > 0, $"{one}");
            Assert.True(ten.Requests <= chunks * one.Requests, $"{ten.Requests} requests for the rows of {chunks} chunks, {one.Requests} for one");
            Assert.True(ten.BlocksDecoded <= chunks * one.BlocksDecoded, $"{ten.BlocksDecoded} blocks for the rows of {chunks} chunks, {one.BlocksDecoded} for one");
            Assert.True(all.Requests >= (Rows / ChunkRows) * one.Requests, $"{all.Requests} requests for every group's row");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFirstAndALastShareOneTake()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);

            // Every chunk holds the first and the last rows of minutes: a take of both reads each
            // chunk once, as a take of the first alone does. Forced to block, each query reads
            // every group's rows in one fetch.
            Aggregation<long> none = file.Scan<Trade>().GroupBy(r => r.Minute).Select(g => g.Count());
            none.Plan.Blocking = true;
            await none.ToListAsync(Ct);
            Aggregation<double?> opens = file.Scan<Trade>().GroupBy(r => r.Minute).Select(g => g.First().Price);
            opens.Plan.Blocking = true;
            List<double?> open = await opens.ToListAsync(Ct);
            Vorticity.Aggregation both = file.Scan<Trade>().GroupBy(r => r.Minute).OrderBy(g => g.Key).Select(g => (g.Key, g.First().Price, g.Last().Price));
            both.Plan.Blocking = true;
            List<MinuteEndPrices> ends = await ListAsync(both.As<MinuteEndPrices>());

            Assert.Equal(
                rows.GroupBy(r => r.Minute).Select(g => new MinuteEndPrices(g.Key, g.First().Price, g.Last().Price)),
                ends);
            Assert.Equal(ends.Select(e => e.Open).Order(), open.Order());
            ScanStatistics first = Fetched(opens.Statistics, none.Statistics);
            ScanStatistics firstAndLast = Fetched(both.Statistics, none.Statistics);
            Assert.True(first.Requests > 0, $"{first}");
            Assert.Equal(first.Requests, firstAndLast.Requests);
            Assert.Equal(first.BlocksDecoded, firstAndLast.BlocksDecoded);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThousandsOfGroupsReadTheRowsOfEachOfTheirChoices(bool blocking)
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows, Chunked);
        try
        {
            // Four thousand groups and three choices: blocking, twelve thousand rows sorted by
            // their digits and taken at once; streaming, a take for each batch of groups closed.
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Vorticity.Aggregation ends = file.Scan<Trade>()
                .GroupBy(r => (r.Minute, r.Venue))
                .OrderBy(g => g.Key)
                .Select(g => (g.Key.Minute, g.Key.Venue, g.First().Price, g.Last().Size, g.MaxBy(x => x.Size).Symbol));
            ends.Plan.Blocking = blocking;
            List<VenueEnds> read = await ListAsync(ends.As<VenueEnds>());
            Assert.True(read.Count > 3_000, $"{read.Count} groups");
            Assert.Equal(
                rows.GroupBy(r => (r.Minute, r.Venue)).OrderBy(g => g.Key.Minute).ThenBy(g => g.Key.Venue, StringComparer.Ordinal)
                    .Select(g => new VenueEnds(g.Key.Minute, g.Key.Venue, g.First().Price, g.Last().Size, g.MaxBy(r => r.Size).Symbol)),
                read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFilterOnAChosenRowAfterATakeReadsTheGroupsTaken()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows, Chunked);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Aggregation<int> filtered = file.Scan<Trade>().GroupBy(r => r.Minute)
                .OrderByDescending(g => g.Count(x => x.Size > 20)).ThenBy(g => g.Key)
                .Take(10)
                .Where(g => g.Last().Size > 10)
                .Select(g => g.Key);
            List<int> minutes = await filtered.ToListAsync(Ct);
            Assert.Equal(
                rows.GroupBy(r => r.Minute).OrderByDescending(g => g.Count(x => x.Size > 20)).ThenBy(g => g.Key).Take(10).Where(g => g.Last().Size > 10).Select(g => g.Key),
                minutes);

            // The filter reads the last rows of the ten minutes taken, not of the thousand.
            Aggregation<int> unread = file.Scan<Trade>().GroupBy(r => r.Minute)
                .OrderByDescending(g => g.Count(x => x.Size > 20)).ThenBy(g => g.Key)
                .Take(10)
                .Select(g => g.Key);
            await unread.ToListAsync(Ct);
            Assert.True(Fetched(filtered.Statistics, unread.Statistics).Requests <= 20, $"{Fetched(filtered.Statistics, unread.Statistics)}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Chunks of one block of 1 024 rows: a take reads the chunks of its rows alone.</summary>
    private static VortexWriteOptions Chunked => new VortexWriteOptions { RowBlockSize = ChunkRows, ChunkTargetBytes = 1 << 12 };

    /// <summary>What the chosen rows' fetch read: a query's reads less those of the same query without them.</summary>
    private static ScanStatistics Fetched(ScanStatistics with, ScanStatistics without) =>
        new ScanStatistics(0, 0, with.Requests - without.Requests, with.BytesRequested - without.BytesRequested, with.BlocksDecoded - without.BlocksDecoded, 0, 0);

    /// <summary>The <paramref name="take"/> minutes of the highest prices: the size of the row of each, or the price alone; and what the query read.</summary>
    private static async Task<(List<int> Sizes, ScanStatistics Reads)> HighestAsync(VortexFile file, int take, bool row)
    {
        if (row)
        {
            Aggregation<int> sizes = file.Scan<Trade>().GroupBy(r => r.Minute).OrderByDescending(g => g.Max(x => x.Price)).Take(take).Select(g => g.MaxBy(x => x.Price).Size);
            return (await sizes.ToListAsync(Ct), sizes.Statistics);
        }

        Aggregation<double?> prices = file.Scan<Trade>().GroupBy(r => r.Minute).OrderByDescending(g => g.Max(x => x.Price)).Take(take).Select(g => g.Max(x => x.Price));
        await prices.ToListAsync(Ct);
        return ([], prices.Statistics);
    }

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

    private static async Task<string> WriteAsync(Trade[] rows, VortexWriteOptions? options = null)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "chosen-rows");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trades-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Trade>(path, options))
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

    [VortexRecord]
    public partial record struct MinuteEndPrices(int Minute, double? Open, double? Close);

    [VortexRecord]
    public partial record struct VenueEnds(int Minute, string Venue, double? Open, int LastSize, string? BiggestSymbol);
}
