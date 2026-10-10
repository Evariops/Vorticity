// The terminals: AnyAsync, CountAsync, MinAsync and MaxAsync,
// computed without a RecordBatch. "A terminal is the scan with a different output, never a
// different scan", so every number here is held against the materialized scan -- with pruning on
// and off, and with each tier forced off in turn -- because the property that makes a terminal
// testable is pruning's own: a wrong proof is a wrong answer, and a proof that is never taken is
// never wrong.
//
// One test is about the batch enumerator, not the terminals: the pipelined path (a degree above
// 1) must apply the take and the filter exactly as the sequential one does, rather than build its
// batch straight from the decoded split.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanTerminalTests
{
    /// <summary>65536 rows in 64 zones of 1024; see <c>ZoneVerdictTests</c> for the columns.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    private const long Rows = 65_536;
    private const long Block = 1024;

    /// <summary>Pruning on and off, the full-block proof on and off.</summary>
    private static readonly (bool Prune, TerminalTiers Tiers)[] Configurations =
    [
        (true, TerminalTiers.All),
        (true, TerminalTiers.All & ~TerminalTiers.FullBlock),
        (false, TerminalTiers.All),
        (false, TerminalTiers.All & ~TerminalTiers.FullBlock),
    ];

    /// <summary>Every resolution of an extreme, forced off one after the other, down to the decode.</summary>
    private static readonly TerminalTiers[] ExtremeTiers =
    [
        TerminalTiers.All,
        TerminalTiers.All & ~TerminalTiers.FileStatistic,
        TerminalTiers.All & ~(TerminalTiers.FileStatistic | TerminalTiers.ZoneBounds),
        TerminalTiers.All & ~TerminalTiers.FullBlock,
        TerminalTiers.None,
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

    /// <summary>
    /// Column and filter, for the extremes: exact bounds, Inexact bounds (<c>strs</c>), nulls,
    /// NaN, a filter proven whole on some splits and open on others, an empty result.
    /// </summary>
    public static TheoryData<string, string> Extremes => new TheoryData<string, string>
    {
        { "monotone", "none" },
        { "banded", "none" },
        { "strs", "none" },
        { "nulls", "none" },
        { "nans", "none" },
        { "nulls", "nulls > 30000" },
        { "nans", "NOT (nans > 1000)" },
        { "monotone", "monotone band" },
        { "strs", "banded = 5" },
        { "monotone", "monotone < 0" },
        { "nans", "banded IN (3, NULL)" },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task CountAndAnyAgreeWithTheMaterializedScanWhateverIsSwitchedOff(string name)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter(name);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        long expected = await Materialized(Build(file, filter, "none"));

        foreach ((bool prune, TerminalTiers tiers) in Configurations)
        {
            Assert.Equal(expected, await Build(file, filter, "none").WithPruning(prune).WithTiers(tiers).CountAsync(ct));
            Assert.Equal(expected > 0, await Build(file, filter, "none").WithPruning(prune).WithTiers(tiers).AnyAsync(ct));
        }
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task ARangeOrATakeIsHonouredExactlyAsTheScanHonoursIt(string name, string selection)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter(name);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        long expected = await Materialized(Build(file, filter, selection));
        Assert.True(expected > 0, "the selection should keep some matching rows, or the test proves nothing");

        foreach ((bool prune, TerminalTiers tiers) in Configurations)
        {
            Assert.Equal(expected, await Build(file, filter, selection).WithPruning(prune).WithTiers(tiers).CountAsync(ct));
            Assert.True(await Build(file, filter, selection).WithPruning(prune).WithTiers(tiers).AnyAsync(ct));
        }
    }

    [Theory]
    [MemberData(nameof(Extremes))]
    public async Task MinAndMaxAgreeWithTheMaterializedScanWhateverIsSwitchedOff(string column, string filterName)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        VortexExpr? filter = filterName == "none" ? null : Filter(filterName);

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        (FilterLiteral expectedMin, FilterLiteral expectedMax) = await MaterializedExtremes(file, column, filter);

        foreach (bool prune in new[] { true, false })
        {
            foreach (TerminalTiers tiers in ExtremeTiers)
            {
                string how = column + " under " + filterName + ", pruning " + prune + ", tiers " + tiers;
                AssertLiteral(expectedMin, await Scan(file, filter).WithPruning(prune).WithTiers(tiers).MinAsync(column, ct), "min of " + how);
                AssertLiteral(expectedMax, await Scan(file, filter).WithPruning(prune).WithTiers(tiers).MaxAsync(column, ct), "max of " + how);
            }
        }
    }

    [Fact]
    public async Task WithoutAFilterTheCountIsArithmeticAndReadsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);

        ScanCounters metrics = new ScanCounters();
        Assert.Equal(Rows, await file.ScanBuilder().WithMetrics(metrics).CountAsync(ct));
        Assert.True(await file.ScanBuilder().WithMetrics(metrics).AnyAsync(ct));
        Assert.Equal(5_000, await file.ScanBuilder().Rows(RowRange.FromLength(1_000, 5_000)).WithMetrics(metrics).CountAsync(ct));
        Assert.Equal(0, await file.ScanBuilder().Rows(RowRange.FromLength(Rows + 10, 5)).WithMetrics(metrics).CountAsync(ct));
        Assert.False(await file.ScanBuilder().Rows(RowRange.FromLength(Rows + 10, 5)).WithMetrics(metrics).AnyAsync(ct));
        Assert.Equal(2, await file.ScanBuilder().Take([1L, 1L, 7L]).WithMetrics(metrics).CountAsync(ct));
        Assert.True(await file.ScanBuilder().Take([7L]).WithMetrics(metrics).AnyAsync(ct));

        Assert.Equal(0, metrics.SegmentRequests);
        Assert.Equal(0, metrics.ValuesDecoded);
    }

    [Fact]
    public async Task TheFullBlockProofCountsWithoutReadingTheBlock()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // banded = 5: the mask leaves block 5 alone alive, and its zone says min = max = 5, Exact:
        // the whole block is the answer, from the zone map the pruning pass already read.
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("banded = 5");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanExplanation plan = await file.ScanBuilder().Where(filter).ExplainAsync(ct);
        Assert.Equal(1, plan.LiveBlocks);
        long zoneMaps = plan.Pruning[0].SegmentsRead;

        ScanCounters proven = new ScanCounters();
        ScanCounters decoded = new ScanCounters();
        Assert.Equal(Block, await file.ScanBuilder().Where(filter).WithMetrics(proven).CountAsync(ct));
        Assert.Equal(Block, await file.ScanBuilder().Where(filter).WithMetrics(decoded)
            .WithTiers(TerminalTiers.All & ~TerminalTiers.FullBlock).CountAsync(ct));

        // The plan read the zone maps, and the open file keeps them: the proof asked the source for
        // nothing at all, where the decode asked for the block's data, and materialized it.
        Assert.True(zoneMaps > 0);
        Assert.Equal(0, proven.SegmentRequests);
        Assert.True(decoded.SegmentRequests > 0);
        Assert.True(decoded.ValuesDecoded > proven.ValuesDecoded);

        // Under a take the same proof serves, whole: every taken row of a block proven whole.
        ScanCounters taken = new ScanCounters();
        Assert.Equal(2, await file.ScanBuilder().Where(filter).Take([5_200L, 5_300L, 20_000L]).WithMetrics(taken).CountAsync(ct));
        Assert.Equal(0, taken.SegmentRequests);
    }

    [Fact]
    public async Task AnEmptyMaskAnswersWithoutADataRead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("monotone < 0");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanExplanation plan = await file.ScanBuilder().Where(filter).ExplainAsync(ct);
        Assert.Equal(0, plan.LiveBlocks);

        ScanCounters metrics = new ScanCounters();
        Assert.False(await file.ScanBuilder().Where(filter).WithMetrics(metrics).AnyAsync(ct));
        Assert.Equal(0, await file.ScanBuilder().Where(filter).WithMetrics(metrics).CountAsync(ct));

        // Two terminals and nothing read: the zone maps the plan read stay with the open file.
        Assert.True(plan.Pruning[0].SegmentsRead > 0);
        Assert.Equal(0, metrics.SegmentRequests);
        Assert.Equal(0, metrics.Batches);
    }

    [Fact]
    public async Task AnyStopsAtTheFirstSplitThatCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // nulls > 30000, the proof off: blocks 29 to 62 are live and every one of them holds
        // matches, so Any decodes the first and stops where Count decodes them all.
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("nulls > 30000");

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanCounters any = new ScanCounters();
        ScanCounters count = new ScanCounters();
        TerminalTiers decodeOnly = TerminalTiers.All & ~TerminalTiers.FullBlock;
        Assert.True(await file.ScanBuilder().Where(filter).WithMetrics(any).WithTiers(decodeOnly).AnyAsync(ct));
        Assert.True(await file.ScanBuilder().Where(filter).WithMetrics(count).WithTiers(decodeOnly).CountAsync(ct) > 0);

        Assert.True(any.ValuesDecoded > 0, "Any had to decode the one open block");
        Assert.True(any.ValuesDecoded * 4 < count.ValuesDecoded, "Any should stop long before Count is done");
    }

    [Fact]
    public async Task TheFileStatisticAnswersTheWholeFileWithoutARead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The corpus file carries Exact file statistics: the whole-file extreme is one of them,
        // and no segment is asked for.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        Assert.True(file.HasFileStatistics);

        ScanCounters metrics = new ScanCounters();
        FilterLiteral min = await file.ScanBuilder().WithMetrics(metrics).MinAsync("monotone", ct);
        FilterLiteral max = await file.ScanBuilder().WithMetrics(metrics).MaxAsync("monotone", ct);
        FilterLiteral last = await file.ScanBuilder().WithMetrics(metrics).MaxAsync("strs", ct);

        Assert.Equal(1_000_000L, min.SignedValue);
        Assert.Equal(1_000_000L + (3 * (Rows - 1)), max.SignedValue);
        Assert.Equal("z0063-1023", Encoding.UTF8.GetString(last.BytesValue));
        Assert.Equal(0, metrics.SegmentRequests);
        Assert.Equal(0, metrics.ValuesDecoded);
    }

    [Fact]
    public async Task TheZoneBoundsAnswerWholeZonesWithoutADecode()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A range is not the whole file, so the statistic is out and the zone map is in: every
        // split is a whole zone with Exact bounds, and nothing but the map is decoded. With the
        // bounds forced off, every block is.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        RowRange whole = new RowRange(0, Rows);

        ScanCounters bounds = new ScanCounters();
        ScanCounters decode = new ScanCounters();
        FilterLiteral fromBounds = await file.ScanBuilder().Rows(whole).WithMetrics(bounds).MinAsync("monotone", ct);
        FilterLiteral fromDecode = await file.ScanBuilder().Rows(whole).WithMetrics(decode)
            .WithTiers(TerminalTiers.All & ~TerminalTiers.ZoneBounds).MinAsync("monotone", ct);

        Assert.Equal(1_000_000L, fromBounds.SignedValue);
        Assert.Equal(1_000_000L, fromDecode.SignedValue);
        Assert.True(bounds.ValuesDecoded < Block, "only the zone map's rows should have been decoded, not " + bounds.ValuesDecoded);
        Assert.True(decode.ValuesDecoded >= Rows, "every block should have been decoded, not " + decode.ValuesDecoded);
    }

    [Fact]
    public async Task AnInexactBoundIsACandidateDecodedOnlyWhenItCouldWin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // strs carries bounded_min(64) / bounded_max(64), Inexact by declaration: every zone is a
        // candidate, the one with the best stated bound is decoded first, and its true minimum
        // rules the rest out -- plus the few zones the reference writer left without a bound.
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        RowRange whole = new RowRange(0, Rows);

        ScanCounters bounded = new ScanCounters();
        ScanCounters decoded = new ScanCounters();
        FilterLiteral min = await file.ScanBuilder().Rows(whole).WithMetrics(bounded).MinAsync("strs", ct);
        FilterLiteral max = await file.ScanBuilder().Rows(whole).WithMetrics(bounded).MaxAsync("strs", ct);
        FilterLiteral minDecoded = await file.ScanBuilder().Rows(whole).WithMetrics(decoded).WithTiers(TerminalTiers.None).MinAsync("strs", ct);
        FilterLiteral maxDecoded = await file.ScanBuilder().Rows(whole).WithMetrics(decoded).WithTiers(TerminalTiers.None).MaxAsync("strs", ct);

        Assert.Equal("z0000-0000", Encoding.UTF8.GetString(min.BytesValue));
        Assert.Equal("z0063-1023", Encoding.UTF8.GetString(max.BytesValue));
        Assert.Equal("z0000-0000", Encoding.UTF8.GetString(minDecoded.BytesValue));
        Assert.Equal("z0063-1023", Encoding.UTF8.GetString(maxDecoded.BytesValue));

        // The bounds left one candidate to read per extreme -- the zone with the best stated
        // bound, whose true extreme rules the other 63 out -- where the decode alone reads every
        // split; counted as segments asked of the source, one per split of this column.
        Assert.True(bounded.SegmentRequests > 0);
        Assert.True(
            bounded.SegmentRequests * 8 < decoded.SegmentRequests,
            "the bounds should have spared nearly all of the 64 splits: " + bounded.SegmentRequests + " requests against " + decoded.SegmentRequests);
    }

    [Fact]
    public async Task NothingIsNull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);

        // Zone 63 of `nulls` is entirely null; a filter no row satisfies; a range past the end.
        foreach (TerminalTiers tiers in ExtremeTiers)
        {
            Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().Rows(RowRange.FromLength(63 * Block, Block)).WithTiers(tiers).MinAsync("nulls", ct)).Kind);
            Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().Where(Filter("monotone < 0")).WithTiers(tiers).MaxAsync("monotone", ct)).Kind);
            Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().Rows(RowRange.FromLength(Rows + 10, 5)).WithTiers(tiers).MinAsync("monotone", ct)).Kind);
        }

        Assert.Throws<ArgumentException>(() => file.ScanBuilder().MinAsync("no_such_column", ct));
    }

    [Fact]
    public async Task NegativeZeroIsZeroAndNaNIsNeverAnExtreme()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A file written here: a float column with NaN, 0.0 and -0.0; one that is all NaN; one
        // that is all null. IEEE order for the extremes: -0.0 equals
        // 0.0, a NaN is skipped, and a column with no value is Null -- through every resolution,
        // the writer's own statistics included.
        Decoders.EnsureRegistered();
        string path = WriteFloats();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            foreach (TerminalTiers tiers in ExtremeTiers)
            {
                FilterLiteral min = await file.ScanBuilder().WithTiers(tiers).MinAsync("mixed", ct);
                FilterLiteral max = await file.ScanBuilder().WithTiers(tiers).MaxAsync("mixed", ct);
                Assert.Equal(FilterLiteralKind.Float, min.Kind);
                Assert.Equal(0.0, min.FloatValue);
                Assert.Equal(2.5, max.FloatValue);

                Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().WithTiers(tiers).MinAsync("nans", ct)).Kind);
                Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().WithTiers(tiers).MaxAsync("nans", ct)).Kind);
                Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().WithTiers(tiers).MinAsync("nulls", ct)).Kind);
            }

            // And under a filter that keeps the NaN rows only, there is no extreme at all.
            VortexExpr nanRows = Expr.Not(Expr.Ge(Expr.Field("mixed"), Expr.Literal(FilterLiteral.From(-1.0))));
            Assert.Equal(2, await file.ScanBuilder().Where(nanRows).CountAsync(ct));
            Assert.Equal(FilterLiteralKind.Null, (await file.ScanBuilder().Where(nanRows).MinAsync("mixed", ct)).Kind);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task APipelinedScanAppliesTheFilterAndTheTake()
    {
        Decoders.EnsureRegistered();
        VortexExpr filter = Filter("monotone band");
        long[] take = TakenRows();

        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        List<long> filtered = await Selected(file.ScanBuilder().Where(filter));
        Assert.True(filtered.Count > 0 && filtered.Count < Rows);
        Assert.Equal(filtered, await Selected(file.ScanBuilder().Where(filter).WithDegreeOfParallelism(3)));

        List<long> taken = await Selected(file.ScanBuilder().Take(take));
        Assert.Equal(take.Length, taken.Count);
        Assert.Equal(taken, await Selected(file.ScanBuilder().Take(take).WithDegreeOfParallelism(3)));

        List<long> both = await Selected(file.ScanBuilder().Where(filter).Take(take));
        Assert.True(both.Count > 0 && both.Count < taken.Count);
        Assert.Equal(both, await Selected(file.ScanBuilder().Where(filter).Take(take).WithDegreeOfParallelism(3)));
    }

    private static ScanBuilder Scan(VortexFile file, VortexExpr? filter) =>
        filter is null ? file.ScanBuilder() : file.ScanBuilder().Where(filter);

    /// <summary>
    /// The scan with the filter and one of the selections; nothing else set. "rows" is a wide
    /// range over every filter's matches, "short" a half-block inside them -- zone 5 for
    /// <c>banded = 5</c>, the band, the rows above 30 000 -- so that a range cutting through a
    /// proven block is what is counted.
    /// </summary>
    private static ScanBuilder Build(VortexFile file, VortexExpr filter, string selection)
    {
        ScanBuilder scan = file.ScanBuilder().Where(filter);
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

    /// <summary>The extremes the long way: every value of the column, pruning off, NaN and null skipped.</summary>
    private static async Task<(FilterLiteral Min, FilterLiteral Max)> MaterializedExtremes(
        VortexFile file, string column, VortexExpr? filter)
    {
        FilterLiteral min = FilterLiteral.Null;
        FilterLiteral max = FilterLiteral.Null;
        byte[] name = Encoding.UTF8.GetBytes(column);
        await foreach (RecordBatch batch in Scan(file, filter).Project(column).WithPruning(false).ExecuteAsync())
        {
            Fold(column, batch.Column(name), ref min, ref max);
        }

        return (min, max);
    }

    /// <summary>One value per row, read by the column's known type: i64, i32, f64 or utf8.</summary>
    private static void Fold(string column, VortexColumn view, ref FilterLiteral min, ref FilterLiteral max)
    {
        int rows = view.Length;
        for (int row = 0; row < rows; row++)
        {
            if (!view.IsValid(row))
            {
                continue;
            }

            FilterLiteral value;
            switch (column)
            {
                case "monotone":
                case "nulls":
                    value = FilterLiteral.From(view.AsPrimitive<long>()[row]);
                    break;
                case "banded":
                    value = FilterLiteral.From((long)view.AsPrimitive<int>()[row]);
                    break;
                case "nans":
                    double d = view.AsPrimitive<double>()[row];
                    if (double.IsNaN(d))
                    {
                        continue;
                    }

                    value = FilterLiteral.From(d);
                    break;
                default:
                    value = FilterLiteral.From(view.AsBinary().GetSpan(row));
                    break;
            }

            if (min.Kind == FilterLiteralKind.Null || Compare(value, min) < 0)
            {
                min = value;
            }

            if (max.Kind == FilterLiteralKind.Null || Compare(value, max) > 0)
            {
                max = value;
            }
        }
    }

    private static int Compare(FilterLiteral a, FilterLiteral b) => a.Kind switch
    {
        FilterLiteralKind.Signed => a.SignedValue.CompareTo(b.SignedValue),
        FilterLiteralKind.Float => a.FloatValue.CompareTo(b.FloatValue),
        _ => a.BytesValue.SequenceCompareTo(b.BytesValue),
    };

    private static void AssertLiteral(FilterLiteral expected, FilterLiteral actual, string what)
    {
        Assert.True(expected.Kind == actual.Kind, what + ": expected a " + expected.Kind + ", got a " + actual.Kind);
        switch (expected.Kind)
        {
            case FilterLiteralKind.Null:
                break;
            case FilterLiteralKind.Signed:
                Assert.True(expected.SignedValue == actual.SignedValue, what + ": expected " + expected.SignedValue + ", got " + actual.SignedValue);
                break;
            case FilterLiteralKind.Float:
                Assert.True(expected.FloatValue == actual.FloatValue, what + ": expected " + expected.FloatValue + ", got " + actual.FloatValue);
                break;
            default:
                Assert.True(
                    expected.BytesValue.SequenceEqual(actual.BytesValue),
                    what + ": expected " + Encoding.UTF8.GetString(expected.BytesValue) + ", got " + Encoding.UTF8.GetString(actual.BytesValue));
                break;
        }
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

    /// <summary>
    /// Six rows of three f64 columns: <c>mixed</c> = {NaN, 0.0, -0.0, 2.5, NaN, 2.5},
    /// <c>nans</c> all NaN, <c>nulls</c> all null.
    /// </summary>
    private static string WriteFloats()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType f64n = types.Primitive(PType.F64, Nullability.Nullable);
        DType schema = types.Struct(["mixed", "nans", "nulls"], [f64, f64, f64n], Nullability.NonNullable);

        const int rows = 6;
        VortexBuffer mixed = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> mixedBytes);
        Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(mixedBytes);
        values[0] = double.NaN;
        values[1] = 0.0;
        values[2] = -0.0;
        values[3] = 2.5;
        values[4] = double.NaN;
        values[5] = 2.5;

        VortexBuffer nans = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> nanBytes);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(nanBytes).Fill(double.NaN);

        VortexBuffer nulls = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> nullBytes);
        nullBytes.Clear();

        int[] columns =
        [
            arena.AddPrimitive(f64, rows, Validity.NonNullable, PType.F64, mixed),
            arena.AddPrimitive(f64, rows, Validity.NonNullable, PType.F64, nans),
            arena.AddPrimitive(f64n, rows, Validity.AllInvalid, PType.F64, nulls),
        ];
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, columns);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-extremes-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, root).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(string path, DType schema, CanonicalArena arena, int root)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions());
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
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
