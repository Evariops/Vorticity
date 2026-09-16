// The terminals - docs/12-index-reads.md §5.1 and §5.2: AnyAsync and CountAsync, computed without a
// RecordBatch. "A terminal is the scan with a different output, never a different scan", so every
// number here is held against the materialized scan -- with pruning on and off, and with the
// full-block proof forced off -- because the property that makes a count testable is pruning's
// own: a wrong proof is a wrong count, and a proof that is never taken is never wrong.
//
// The last test is about the batch enumerator, not the terminals: the pipelined path (a degree
// above 1) built its batch straight from the decoded split, without the take and without the
// filter. Found when the split's execution was moved to the one place both now share.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanTerminalTests
{
    /// <summary>65536 rows in 64 zones of 1024; see <c>ZoneVerdictTests</c> for the columns.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    private const long Rows = 65_536;
    private const long Block = 1024;

    /// <summary>Pruning on and off, the full-block proof on and off.</summary>
    private static readonly (bool Prune, CountTiers Tiers)[] Configurations =
    [
        (true, CountTiers.All),
        (true, CountTiers.All & ~CountTiers.FullBlock),
        (false, CountTiers.All),
        (false, CountTiers.All & ~CountTiers.FullBlock),
    ];

    /// <summary>
    /// Proven whole, mixed, open in one zone, NaN under NOT, never proven, the statistic itself,
    /// a null candidate, empty, and the shape the algebra does not see.
    /// </summary>
    public static TheoryData<string> Filters => new TheoryData<string>
    {
        "banded = 5",
        "monotone band",
        "nulls > 30000",
        "NOT (nans > 1000)",
        "strs Contains 5",
        "nulls IS NULL",
        "banded IN (3, NULL)",
        "monotone < 0",
        "nulls > 30000 OR nulls IS NULL",
    };

    public static TheoryData<string, string> Selections => new TheoryData<string, string>
    {
        { "banded = 5", "rows" },
        { "banded = 5", "short" },
        { "banded = 5", "take" },
        { "monotone band", "rows" },
        { "monotone band", "short" },
        { "monotone band", "take" },
        { "nulls > 30000", "rows" },
        { "nulls > 30000", "short" },
        { "nulls > 30000", "take" },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task CountAndAnyAgreeWithTheMaterializedScanWhateverIsSwitchedOff(string name)
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter(name);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        long expected = await Materialized(Build(file, filter, "none"));

        foreach ((bool prune, CountTiers tiers) in Configurations)
        {
            Assert.Equal(expected, await Build(file, filter, "none").WithPruning(prune).WithCountTiers(tiers).CountAsync());
            Assert.Equal(expected > 0, await Build(file, filter, "none").WithPruning(prune).WithCountTiers(tiers).AnyAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task ARangeOrATakeIsHonouredExactlyAsTheScanHonoursIt(string name, string selection)
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter(name);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        long expected = await Materialized(Build(file, filter, selection));
        Assert.True(expected > 0, "the selection should keep some matching rows, or the test proves nothing");

        foreach ((bool prune, CountTiers tiers) in Configurations)
        {
            Assert.Equal(expected, await Build(file, filter, selection).WithPruning(prune).WithCountTiers(tiers).CountAsync());
            Assert.True(await Build(file, filter, selection).WithPruning(prune).WithCountTiers(tiers).AnyAsync());
        }
    }

    [Fact]
    public async Task WithoutAFilterTheCountIsArithmeticAndReadsNothing()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);

        ScanMetrics metrics = new ScanMetrics();
        Assert.Equal(Rows, await file.Scan().WithMetrics(metrics).CountAsync());
        Assert.True(await file.Scan().WithMetrics(metrics).AnyAsync());
        Assert.Equal(5_000, await file.Scan().Rows(RowRange.FromLength(1_000, 5_000)).WithMetrics(metrics).CountAsync());
        Assert.Equal(0, await file.Scan().Rows(RowRange.FromLength(Rows + 10, 5)).WithMetrics(metrics).CountAsync());
        Assert.False(await file.Scan().Rows(RowRange.FromLength(Rows + 10, 5)).WithMetrics(metrics).AnyAsync());
        Assert.Equal(2, await file.Scan().Take([1L, 1L, 7L]).WithMetrics(metrics).CountAsync());
        Assert.True(await file.Scan().Take([7L]).WithMetrics(metrics).AnyAsync());

        Assert.Equal(0, metrics.SegmentRequests);
        Assert.Equal(0, metrics.ValuesDecoded);
    }

    [Fact]
    public async Task TheFullBlockProofCountsWithoutReadingTheBlock()
    {
        // banded = 5: the mask leaves block 5 alone alive, and its zone says min = max = 5, Exact:
        // the whole block is the answer, from the zone map the pruning pass already read.
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("banded = 5");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanPlan plan = await file.Scan().Where(filter).ExplainAsync();
        Assert.Equal(1, plan.LiveBlocks);
        long zoneMaps = plan.Pruning[0].SegmentsRead;

        ScanMetrics proven = new ScanMetrics();
        ScanMetrics decoded = new ScanMetrics();
        Assert.Equal(Block, await file.Scan().Where(filter).WithMetrics(proven).CountAsync());
        Assert.Equal(Block, await file.Scan().Where(filter).WithMetrics(decoded)
            .WithCountTiers(CountTiers.All & ~CountTiers.FullBlock).CountAsync());

        // The proof asked the source for the zone maps and nothing else; the decode asked for the
        // block's data on top, and materialized it.
        Assert.Equal(zoneMaps, proven.SegmentRequests);
        Assert.True(decoded.SegmentRequests > zoneMaps);
        Assert.True(decoded.ValuesDecoded > proven.ValuesDecoded);

        // Under a take the same proof serves, whole: every taken row of a block proven whole.
        ScanMetrics taken = new ScanMetrics();
        Assert.Equal(2, await file.Scan().Where(filter).Take([5_200L, 5_300L, 20_000L]).WithMetrics(taken).CountAsync());
        Assert.Equal(zoneMaps, taken.SegmentRequests);
    }

    [Fact]
    public async Task AnEmptyMaskAnswersWithoutADataRead()
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("monotone < 0");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanPlan plan = await file.Scan().Where(filter).ExplainAsync();
        Assert.Equal(0, plan.LiveBlocks);

        ScanMetrics metrics = new ScanMetrics();
        Assert.False(await file.Scan().Where(filter).WithMetrics(metrics).AnyAsync());
        Assert.Equal(0, await file.Scan().Where(filter).WithMetrics(metrics).CountAsync());

        // Two terminals, the zone maps each time, nothing else.
        Assert.Equal(2 * plan.Pruning[0].SegmentsRead, metrics.SegmentRequests);
        Assert.Equal(0, metrics.Batches);
    }

    [Fact]
    public async Task AnyStopsAtTheFirstSplitThatCounts()
    {
        // nulls > 30000, the proof off: blocks 29 to 62 are live and every one of them holds
        // matches, so Any decodes the first and stops where Count decodes them all.
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("nulls > 30000");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanMetrics any = new ScanMetrics();
        ScanMetrics count = new ScanMetrics();
        CountTiers decodeOnly = CountTiers.All & ~CountTiers.FullBlock;
        Assert.True(await file.Scan().Where(filter).WithMetrics(any).WithCountTiers(decodeOnly).AnyAsync());
        Assert.True(await file.Scan().Where(filter).WithMetrics(count).WithCountTiers(decodeOnly).CountAsync() > 0);

        Assert.True(any.ValuesDecoded > 0, "Any had to decode the one open block");
        Assert.True(any.ValuesDecoded * 4 < count.ValuesDecoded, "Any should stop long before Count is done");
    }

    [Fact]
    public async Task APipelinedScanAppliesTheFilterAndTheTake()
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("monotone band");
        long[] take = TakenRows();

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        List<long> filtered = await Selected(file.Scan().Where(filter));
        Assert.True(filtered.Count > 0 && filtered.Count < Rows);
        Assert.Equal(filtered, await Selected(file.Scan().Where(filter).WithDegreeOfParallelism(3)));

        List<long> taken = await Selected(file.Scan().Take(take));
        Assert.Equal(take.Length, taken.Count);
        Assert.Equal(taken, await Selected(file.Scan().Take(take).WithDegreeOfParallelism(3)));

        List<long> both = await Selected(file.Scan().Where(filter).Take(take));
        Assert.True(both.Count > 0 && both.Count < taken.Count);
        Assert.Equal(both, await Selected(file.Scan().Where(filter).Take(take).WithDegreeOfParallelism(3)));
    }

    /// <summary>
    /// The scan with the filter and one of the selections; nothing else set. "rows" is a wide
    /// range over every filter's matches, "short" a half-block inside them -- zone 5 for
    /// <c>banded = 5</c>, the band, the rows above 30 000 -- so that a range cutting through a
    /// proven block is what is counted.
    /// </summary>
    private static ScanBuilder Build(VortexFile file, VortexExpr filter, string selection)
    {
        ScanBuilder scan = file.Scan().Where(filter);
        return selection switch
        {
            "none" => scan,
            "rows" => scan.Rows(new RowRange(5_000, 40_000)),
            "short" => scan.Rows(RowRange.FromLength(ShortStart(filter), 500)),
            "take" => scan.Take(TakenRows()),
            _ => throw new ArgumentException("unknown selection " + selection, nameof(selection)),
        };
    }

    private static long ShortStart(VortexExpr filter) => filter switch
    {
        ComparisonExpr { Field.Path: "banded" } => 5_500,
        ComparisonExpr { Field.Path: "nulls" } => 31_000,
        _ => 15_000,
    };

    /// <summary>Every 997th row, plus two in zone 5 and one in zone 19: scattered, and inside the bands.</summary>
    private static long[] TakenRows()
    {
        List<long> rows = [];
        for (long row = 0; row < Rows; row += 997)
        {
            rows.Add(row);
        }

        rows.Add(5_200);
        rows.Add(5_300);
        rows.Add(20_000);
        return [.. rows];
    }

    /// <summary>The scan's row count the long way: every batch, pruning off.</summary>
    private static async Task<long> Materialized(ScanBuilder scan)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan.Project("monotone").WithPruning(false).ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>The rows the scan returns, by row index, through <c>monotone = 1 000 000 + 3·row</c>.</summary>
    private static async Task<List<long>> Selected(ScanBuilder scan)
    {
        List<long> rows = [];
        await foreach (RecordBatch batch in scan.Project("monotone").ExecuteAsync())
        {
            Collect(batch, rows);
        }

        return rows;
    }

    private static void Collect(RecordBatch batch, List<long> rows)
    {
        VortexColumn view = batch.Column(Encoding.UTF8.GetBytes("monotone"));
        ReadOnlySpan<long> values = view.AsPrimitive<long>().Values;
        for (int i = 0; i < batch.RowCount; i++)
        {
            rows.Add((values[i] - 1_000_000) / 3);
        }
    }

    private static VortexExpr Filter(string name)
    {
        FieldExpr monotone = Expr.Field("monotone");
        FieldExpr banded = Expr.Field("banded");
        FieldExpr nulls = Expr.Field("nulls");
        FieldExpr nans = Expr.Field("nans");
        FieldExpr strs = Expr.Field("strs");
        LiteralExpr thirty = Expr.Literal(FilterLiteral.From(30_000L));

        return name switch
        {
            "banded = 5" => Expr.Eq(banded, Expr.Literal(FilterLiteral.From(5L))),
            "monotone band" => Expr.And(
                Expr.Ge(monotone, Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 10_240L)))),
                Expr.Lt(monotone, Expr.Literal(FilterLiteral.From(1_000_000L + (3 * 20_480L))))),
            "nulls > 30000" => Expr.Gt(nulls, thirty),
            "NOT (nans > 1000)" => Expr.Not(Expr.Gt(nans, Expr.Literal(FilterLiteral.From(1000.0)))),
            "strs Contains 5" => Expr.Contains(strs, FilterLiteral.From("5")),
            "nulls IS NULL" => Expr.IsNull(nulls),
            "banded IN (3, NULL)" => Expr.In(banded, FilterLiteral.From(3L), FilterLiteral.Null),
            "monotone < 0" => Expr.Lt(monotone, Expr.Literal(FilterLiteral.From(0L))),
            "nulls > 30000 OR nulls IS NULL" => Expr.Or(Expr.Gt(nulls, thirty), Expr.IsNull(nulls)),
            _ => throw new ArgumentException("unknown filter " + name, nameof(name)),
        };
    }
}
