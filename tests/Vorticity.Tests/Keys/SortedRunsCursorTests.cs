// The `SortedRuns` source - docs/12-index-reads.md §3, §4, §9, and the tests §11 asks for over it:
// the five operators and the walks against an oracle, direction flips, rank and select, the run
// cache and its budget.
//
// THE COLUMNS ARE NOT SORTED, so the sorted-column source cannot serve and every answer comes from
// the runs. Blocks of 512 rows, one chunk per block and segments of 64 entries: a file of 6 000 rows
// is twelve runs of eight segments, so a seek excludes runs, lands inside segments and crosses
// them, and a walk merges twelve heads. The oracle is every non-null `(value, row)` of the column
// sorted by the total order of §4.4, written out here rather than borrowed from the library.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed class SortedRunsCursorTests
{
    private const int Rows = 6_000;
    private const int Block = 512;
    private const int SegmentEntries = 64;

    private static readonly string[] Names = ["i64", "u8", "f32", "text", "nullable"];

    private static readonly float[] Floats =
    [
        BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00000)),
        float.NegativeInfinity,
        -1.5f,
        -0.0f,
        0.0f,
        2.25f,
        float.PositiveInfinity,
        BitConverter.Int32BitsToSingle(0x7FC00000),
    ];

    /// <summary>A NaN with its sign bit clear: .NET's own <c>double.NaN</c> has it set.</summary>
    private static readonly double PositiveNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);

    private static readonly double NegativeNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000));

    private static long I64(int row) => ((row * 7919L) % 5003) - 2500;

    private static byte U8(int row) => (byte)((row * 31) % 256 / 16);

    private static float F32(int row) => Floats[(row * 5) % Floats.Length];

    private static string Text(int row) =>
        "k" + ((uint)row * 2654435761u % 997).ToString(CultureInfo.InvariantCulture) + (row % 3 == 0 ? "-long-enough-to-leave-the-view" : string.Empty);

    private static int? Nullable(int row) => row % 7 == 0 ? null : (row * 13) % 101;

    /// <summary>Five long strings and nulls: what a writer puts in a dictionary.</summary>
    private static string? Label(int row) =>
        row % 11 == 0 ? null : "label-" + (row % 5).ToString(CultureInfo.InvariantCulture) + "-long-enough-to-leave-the-view";

    public static TheoryData<string> Columns() => new(Names);

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task TheRunsServeTheColumnAndSayHowMany(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        KeyPlan plan = await written.File.Keys(column).ExplainAsync();

        Assert.Equal(KeySourceKind.SortedRuns, plan.Source);
        Assert.True(plan.Runs > 1, $"{plan.Runs} runs");
        Assert.Equal(Oracle(column).Count, plan.EntryCount);
        Assert.True(plan.HasRows);
        Assert.Contains(plan.Rejected, r => r.Source == KeySourceKind.SortedColumn);
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task AFullWalkIsTheOracleForwardAndBackward(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync();

        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            AssertAt(cursor, oracle, index++);
        }

        Assert.Equal(oracle.Count, index);
        Assert.False(cursor.IsValid);

        for (bool ok = await cursor.SeekLastAsync(); ok; ok = await cursor.PrevAsync())
        {
            AssertAt(cursor, oracle, --index);
        }

        Assert.Equal(0, index);
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task TheFiveOperatorsLandWhereTheOracleSays(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync();

        foreach (FilterLiteral key in Probes(column, oracle))
        {
            int lower = Lower(oracle, key);
            int upper = Upper(oracle, key);
            await Check(cursor, oracle, key, SeekOp.AtOrAfter, lower);
            await Check(cursor, oracle, key, SeekOp.After, upper);
            await Check(cursor, oracle, key, SeekOp.AtOrBefore, upper - 1);
            await Check(cursor, oracle, key, SeekOp.Before, lower - 1);
            await Check(cursor, oracle, key, SeekOp.Exact, lower < upper ? lower : -1);

            Assert.Equal(lower, await cursor.RankAsync(key));
        }
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task StepsAcrossAFlipStayOnTheOracle(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync();

        // A deterministic zigzag: runs of steps one way, then the other, each flip a re-seek.
        Random random = new Random(12345);
        int index = oracle.Count / 3;
        Assert.True(await cursor.SeekRankAsync(index));
        AssertAt(cursor, oracle, index);
        for (int leg = 0; leg < 40; leg++)
        {
            bool forward = leg % 2 == 0;
            int steps = random.Next(1, 90);
            for (int s = 0; s < steps; s++)
            {
                int next = forward ? index + 1 : index - 1;
                bool moved = forward ? await cursor.NextAsync() : await cursor.PrevAsync();
                Assert.Equal(next >= 0 && next < oracle.Count, moved);
                if (!moved)
                {
                    Assert.True(await cursor.SeekRankAsync(index));
                    break;
                }

                index = next;
                AssertAt(cursor, oracle, index);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task RankSelectAndKeyCountAgree(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync();

        for (int i = 0; i < oracle.Count; i += 97)
        {
            Assert.True(await cursor.SeekRankAsync(i));
            AssertAt(cursor, oracle, i);
            FilterLiteral key = cursor.Key;
            long count = await cursor.KeyCountAsync();
            Assert.Equal(Upper(oracle, key) - Lower(oracle, key), count);

            // The count leaves the position where it was.
            AssertAt(cursor, oracle, i);
            long rank = await cursor.RankAsync(key);
            Assert.InRange(i, rank, rank + count - 1);
        }

        Assert.False(await cursor.SeekRankAsync(oracle.Count));
        Assert.False(await cursor.SeekRankAsync(-1));
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task NextKeyVisitsEachKeyOnceAndPrevKeyReversesIt(string column)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        List<int> firsts = [];
        for (int i = 0; i < oracle.Count; i++)
        {
            if (i == 0 || Order(oracle[i - 1].Key, oracle[i].Key) != 0)
            {
                firsts.Add(i);
            }
        }

        await using KeyCursor cursor = await written.File.Keys(column).Distinct().OpenAsync();
        int group = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextKeyAsync())
        {
            AssertAt(cursor, oracle, firsts[group++]);
        }

        Assert.Equal(firsts.Count, group);

        Assert.True(await cursor.SeekLastAsync());
        for (int g = firsts.Count - 1; g > 0; g--)
        {
            Assert.True(await cursor.PrevKeyAsync());
            AssertAt(cursor, oracle, firsts[g] - 1);
        }

        Assert.False(await cursor.PrevKeyAsync());
    }

    [Fact]
    public async Task TheRunCacheKeepsWhatWasReadWithinItsBudget()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle("i64");

        await using (KeyCursor cursor = await written.File.Keys("i64").OpenAsync())
        {
            Assert.True(await cursor.SeekFirstAsync());
        }

        IndexRunCache cache = written.File.RunCache;
        Assert.True(cache.Count > 0);
        Assert.True(cache.Bytes > 0);

        // A budget of nothing keeps nothing, and the walk is the same walk.
        await using VortexFile bare = await VortexFile.OpenAsync(
            written.Path, new VortexOpenOptions { Read = new VortexReadOptions { IndexCacheBytes = 0 } });
        await using (KeyCursor cursor = await bare.Keys("i64").OpenAsync())
        {
            int index = 0;
            for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
            {
                AssertAt(cursor, oracle, index++);
            }

            Assert.Equal(oracle.Count, index);
        }

        Assert.Equal(0, bare.RunCache.Count);

        // A budget of one segment evicts as it goes and stays under its cap.
        long one = cache.Bytes / cache.Count;
        await using VortexFile tight = await VortexFile.OpenAsync(
            written.Path, new VortexOpenOptions { Read = new VortexReadOptions { IndexCacheBytes = 2 * one } });
        await using (KeyCursor cursor = await tight.Keys("i64").OpenAsync())
        {
            for (int i = 0; i < oracle.Count; i += 311)
            {
                Assert.True(await cursor.SeekRankAsync(i));
                AssertAt(cursor, oracle, i);
                Assert.True(tight.RunCache.Bytes <= 2 * one);
            }
        }
    }

    [Fact]
    public async Task AStepInsideALoadedSegmentAllocatesNothing()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        await using KeyCursor cursor = await written.File.Keys("text").OpenAsync();

        Assert.True(await cursor.SeekFirstAsync());
        for (int warm = 0; warm < 256; warm++)
        {
            await cursor.NextAsync();
        }

        // Warm, then measure from a fresh position: the steps that stay in loaded segments are
        // synchronous, and a borrowed key costs nothing.
        Assert.True(await cursor.SeekFirstAsync());
        long bytes = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int synchronous = 0;
        for (int step = 0; step < 256; step++)
        {
            ValueTask<bool> move = cursor.NextAsync();
            if (move.IsCompletedSuccessfully)
            {
                synchronous++;
            }

            Assert.True(await move);
            bytes += cursor.KeyBytes.Length;
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes > 0);
        Assert.Equal(256, synchronous);
        Assert.True(delta == 0, $"256 steps allocated {delta} bytes");
    }

    [Fact]
    public async Task ASeekOfTheWrongDomainIsRefusedAndTheZerosAreTwoKeys()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        await using KeyCursor cursor = await written.File.Keys("f32").OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () => await cursor.SeekAsync(FilterLiteral.From(1L), SeekOp.Exact));

        Assert.True(await cursor.SeekAsync(FilterLiteral.From(-0.0), SeekOp.Exact));
        Assert.True(double.IsNegative(cursor.Key.FloatValue));
        long negative = await cursor.KeyCountAsync();
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(0.0), SeekOp.Exact));
        Assert.False(double.IsNegative(cursor.Key.FloatValue));
        Assert.Equal(Rows / Floats.Length, negative);
        Assert.Equal(Rows / Floats.Length, await cursor.KeyCountAsync());

        // The NaNs are the ends: nothing after the positive one, nothing before the negative one,
        // and each is a key of its own.
        Assert.False(await cursor.SeekAsync(FilterLiteral.From(PositiveNaN), SeekOp.After));
        Assert.False(await cursor.SeekAsync(FilterLiteral.From(NegativeNaN), SeekOp.Before));
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(NegativeNaN), SeekOp.Exact));
        Assert.True(double.IsNaN(cursor.Key.FloatValue) && double.IsNegative(cursor.Key.FloatValue));
        Assert.True(await cursor.SeekLastAsync());
        Assert.True(double.IsNaN(cursor.Key.FloatValue) && !double.IsNegative(cursor.Key.FloatValue));
    }

    // ------------------------------------------------------------------------------ exact cover

    public static TheoryData<string> CoverFilters() =>
    [
        "i64 = 17", "i64 = 18", "i64 < -100", "i64 >= 2400", "i64 >= -5 and i64 < 30",
        "i64 = 17 or i64 = -2500", "i64 in", "i64 != 17",
        "u8 = 3", "u8 > 12",
        "f32 = 0", "f32 > -1.5", "f32 < nan", "f32 <= inf", "f32 = -1.5 or f32 = 2.25",
        "text starts k1", "text = k10", "text > k9", "text starts k99 and text < k995",
        "nullable >= 50", "nullable = 0",
    ];

    [Theory]
    [MemberData(nameof(CoverFilters))]
    public async Task AnExactCoverCountsReadsAndBoundsLikeTheDecode(string text)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        (string column, VortexExpr filter, Func<FilterLiteral, bool> matches) = Parse(text);
        List<Entry> oracle = Oracle(column).FindAll(e => matches(e.Key));
        VortexFile file = written.File;

        // The count and the membership, from the runs alone: no data segment is read.
        ScanMetrics metrics = new ScanMetrics();
        Assert.Equal(oracle.Count, await file.Scan().Where(filter).WithMetrics(metrics).CountAsync());
        Assert.Equal(0, metrics.ValuesDecoded);
        Assert.Equal(oracle.Count > 0, await file.Scan().Where(filter).AnyAsync());

        // The same numbers through every other tier, and without the index.
        Assert.Equal(oracle.Count, await file.Scan().Where(filter).WithTiers(TerminalTiers.All & ~TerminalTiers.ExactCover).CountAsync());
        Assert.Equal(oracle.Count, await file.Scan().Where(filter).WithIndexes(false).CountAsync());

        // Under a range, the slices' rows are walked and intersected.
        RowRange range = new RowRange(1_000, 4_500);
        long inRange = oracle.FindAll(e => e.Row >= range.Start && e.Row < range.End).Count;
        Assert.Equal(inRange, await file.Scan().Where(filter).Rows(range).CountAsync());

        // The extremes of the covered column are the ends of the slices.
        FilterLiteral min = await file.Scan().Where(filter).MinAsync(column);
        FilterLiteral max = await file.Scan().Where(filter).MaxAsync(column);
        FilterLiteral decodedMin = await file.Scan().Where(filter).WithTiers(TerminalTiers.Decode).MinAsync(column);
        FilterLiteral decodedMax = await file.Scan().Where(filter).WithTiers(TerminalTiers.Decode).MaxAsync(column);
        Assert.True(SameExtreme(decodedMin, min), $"min {Describe(min)} against {Describe(decodedMin)}");
        Assert.True(SameExtreme(decodedMax, max), $"max {Describe(max)} against {Describe(decodedMax)}");

        // The scan delivers exactly the proven rows, with the index and without; with it, a batch's
        // worth of rows or fewer is a take, which decodes no more than the pruned scan does.
        ScanMetrics indexed = new ScanMetrics();
        ScanMetrics unindexed = new ScanMetrics();
        List<long> on = await RowsOf(file.Scan().Where(filter).WithMetrics(indexed));
        List<long> off = await RowsOf(file.Scan().Where(filter).WithIndexes(false).WithMetrics(unindexed));
        Assert.True(
            indexed.ValuesDecoded <= unindexed.ValuesDecoded,
            $"{indexed.ValuesDecoded} values decoded with the index, {unindexed.ValuesDecoded} without");
        List<long> expected = oracle.ConvertAll(e => e.Row);
        expected.Sort();
        Assert.Equal(expected, off);
        Assert.Equal(expected, on);
    }

    // ------------------------------------------------------------------ explain and counters (§8.1)

    [Fact]
    public async Task ExplainSaysWhatTheIndexSelectsHowACountResolvesAndWhatAnOrderWalks()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        VortexFile file = written.File;
        // A range of about a hundred rows: fewer than a batch, so the index selects them outright.
        (_, VortexExpr filter, Func<FilterLiteral, bool> matches) = Parse("i64 >= 2400");
        List<Entry> oracle = Oracle("i64").FindAll(e => matches(e.Key));

        ScanPlan plan = await file.Scan().Where(filter).ExplainAsync();
        Assert.NotNull(plan.Count);
        Assert.True(plan.Count.ExactCover);
        Assert.Equal(oracle.Count, plan.Count.ExactCount);
        Assert.Equal(oracle.Count, plan.RowsSelectedByIndex);
        Assert.Equal(plan.LiveSplits, plan.Count.SplitsProven + plan.Count.SplitsDecoded);
        Assert.Null(plan.Order);

        // Without the index, no cover; a take narrows it off too.
        ScanPlan off = await file.Scan().Where(filter).WithIndexes(false).ExplainAsync();
        Assert.False(off.Count!.ExactCover);
        Assert.Equal(0, off.RowsSelectedByIndex);

        ScanPlan ordered = await file.Scan().Where(filter).InKeyOrder("i64", descending: true).ExplainAsync();
        OrderPlan order = Assert.IsType<OrderPlan>(ordered.Order);
        Assert.Equal(KeySourceKind.SortedRuns, order.Source);
        Assert.Equal(oracle.Count, order.EntriesInRange);
        Assert.True(order.Descending);
        Assert.True(order.Runs > 1);
        int runsWithKeys = 0;
        for (int start = 0; start < Rows; start += Block)
        {
            bool any = false;
            for (int row = start; row < Math.Min(start + Block, Rows); row++)
            {
                any |= I64(row) >= 2400;
            }

            runsWithKeys += any ? 1 : 0;
        }

        Assert.Equal(runsWithKeys, order.RunsInRange);

        ScanPlan nothing = await file.Scan().InKeyOrder("i64").WithIndexes(false).ExplainAsync();
        Assert.Equal(KeySourceKind.None, nothing.Order!.Source);
    }

    [Fact]
    public async Task TheEventSourceCountsSeeksStepsRunsAndCountTiersWhileSomeoneListens()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        VortexFile file = written.File;
        using CounterListener listener = new CounterListener();

        Vorticity.Diagnostics.VortexEventSource log = Vorticity.Diagnostics.VortexEventSource.Log;
        long seeks = log.CursorSeeks;
        long steps = log.CursorSteps;
        long runs = log.IndexRunsRead;
        long decoded = log.CountBlocksDecoded;
        long windows = log.KeyOrderWindows;

        await using (KeyCursor cursor = await file.Keys("text").OpenAsync())
        {
            for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
            {
            }
        }

        Assert.True(log.CursorSeeks > seeks);
        Assert.True(log.CursorSteps - steps >= Oracle("text").Count);
        Assert.True(log.IndexRunsRead > runs);

        VortexExpr notIndexed = Expr.Ne(Expr.Field("f32"), Expr.Literal(FilterLiteral.From(0.0)));
        await file.Scan().Where(notIndexed).CountAsync();
        Assert.True(log.CountBlocksDecoded > decoded);

        await foreach (RecordBatch batch in file.Scan().InKeyOrder("i64").WithMaxBatchRows(500).ExecuteAsync())
        {
        }

        Assert.True(log.KeyOrderWindows - windows >= Rows / 500);

        // The counters are published under the names docs/12 §8.1 gives.
        Assert.True(await listener.SawAsync("cursor-seeks"), "no cursor-seeks counter was published; saw " + listener.Describe());
    }

    /// <summary>
    /// Enables the `Vorticity` source with counters polled every second -- a whole number, because
    /// the runtime parses the interval in the current culture and "0.1" is not a number in French.
    /// </summary>
    private sealed class CounterListener : System.Diagnostics.Tracing.EventListener
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _seen = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Vorticity")
            {
                EnableEvents(
                    source,
                    System.Diagnostics.Tracing.EventLevel.Verbose,
                    System.Diagnostics.Tracing.EventKeywords.All,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _events = new();

        internal string Describe() => string.Join(", ", _events.Keys);

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
        {
            _events[data.EventName ?? "?"] = true;
            if (data.EventName != "EventCounters" || data.Payload is null)
            {
                return;
            }

            foreach (object? item in data.Payload)
            {
                if (item is IDictionary<string, object> fields && fields.TryGetValue("Name", out object? name) && name is string text)
                {
                    _seen[text] = true;
                }
            }
        }

        internal async Task<bool> SawAsync(string name)
        {
            for (int i = 0; i < 100 && !_seen.ContainsKey(name); i++)
            {
                await Task.Delay(50);
            }

            return _seen.ContainsKey(name);
        }
    }

    // ------------------------------------------------------------------ distinct keys (§5.4)

    public static TheoryData<string, KeySourceKind> DistinctWalks()
    {
        TheoryData<string, KeySourceKind> data = new();
        foreach (string column in Names)
        {
            data.Add(column, KeySourceKind.SortedRuns);
            data.Add(column, KeySourceKind.Postings);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DistinctWalks))]
    public async Task ADistinctWalkIsEveryKeyOnceBothWays(string column, KeySourceKind source)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(PolicyOf(source));
        await AssertDistinctWalk(written.File, column, source);
    }

    [Fact]
    public async Task TheDictionariesServeADistinctWalkOfTheColumnsTheWriterEncodedSo()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(IndexPolicy.Auto);
        int served = 0;
        List<string> refused = [];
        foreach (string column in (string[])[.. Names, "label"])
        {
            KeyPlan plan = await written.File.Keys(column).Distinct().WithSource(KeySourceKind.Dictionary).ExplainAsync();
            if (plan.Source != KeySourceKind.Dictionary)
            {
                KeySourceRejection why = Assert.Single(plan.Rejected, r => r.Source == KeySourceKind.Dictionary);
                Assert.False(string.IsNullOrEmpty(why.Reason));
                refused.Add($"{column}: {why.Reason}");
                continue;
            }

            served++;
            Assert.False(plan.HasRows);
            Assert.Null(plan.EntryCount);
            await AssertDistinctWalk(written.File, column, KeySourceKind.Dictionary);
        }

        // The writer picks the dictionary where it pays: eight floats with both zeros and both NaNs,
        // and five long strings with nulls. The others go to bit-packing or stay plain, and their
        // entry is simply absent.
        Assert.True(served >= 2, $"{served} columns served by their dictionaries; {string.Join("; ", refused)}");
        Assert.All(refused, r => Assert.Contains("has no vorticity.dict.probe.v1 entry", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADistinctWalkOverPostingsReadsNoDataSegment()
    {
        Decoders.EnsureRegistered();
        CountingSegmentSource? counting = null;
        await using Written written = await Written.CreateAsync(
            PolicyOf(KeySourceKind.Postings),
            inner => counting = new CountingSegmentSource(inner));
        VortexFile file = written.File;
        counting!.ResetCounters();

        await using KeyCursor cursor = await file.Keys("text").Distinct().OpenAsync();
        int keys = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            keys++;
        }

        HashSet<string> distinct = [];
        for (int row = 0; row < Rows; row++)
        {
            distinct.Add(Text(row));
        }

        Assert.Equal(distinct.Count, keys);
        Assert.True(counting.Requested.Count > 0);
        foreach (SegmentSpec spec in counting.Requested)
        {
            foreach (SegmentSpec data in file.SegmentSpecs)
            {
                Assert.False(
                    spec.Offset == data.Offset && spec.Length == data.Length,
                    $"the walk read the data segment at {spec.Offset}");
            }
        }
    }

    [Fact]
    public async Task ADistinctChoiceTakesTheCheapestKeySourceAndSaysWhy()
    {
        Decoders.EnsureRegistered();
        await using (Written postings = await Written.CreateAsync(PolicyOf(KeySourceKind.Postings)))
        {
            KeyPlan keys = await postings.File.Keys("i64").Distinct().ExplainAsync();
            Assert.Equal(KeySourceKind.Postings, keys.Source);
            Assert.False(keys.HasRows);

            // Without Distinct(), keys without rows are refused by name.
            KeyPlan rows = await postings.File.Keys("i64").ExplainAsync();
            Assert.Equal(KeySourceKind.None, rows.Source);
            Assert.Contains(rows.Rejected, r => r.Source == KeySourceKind.Postings && r.Reason.Contains("Distinct()", StringComparison.Ordinal));
        }

        await using Written runs = await Written.CreateAsync();
        KeyPlan plan = await runs.File.Keys("i64").Distinct().ExplainAsync();
        Assert.Equal(KeySourceKind.SortedRuns, plan.Source);
        Assert.True(plan.HasRows);
    }

    private static IndexPolicy PolicyOf(KeySourceKind source) => source == KeySourceKind.Postings
        ? IndexPolicy.Postings.WithSegmentEntries(SegmentEntries)
        : IndexPolicy.SortedRuns.WithSegmentEntries(SegmentEntries);

    /// <summary>
    /// A distinct cursor forced onto <paramref name="source"/> visits the oracle's keys once each,
    /// forward and backward, lands backward seeks on a key's first entry, and has rows exactly when
    /// the source does.
    /// </summary>
    private static async Task AssertDistinctWalk(VortexFile file, string column, KeySourceKind source)
    {
        List<Entry> oracle = Oracle(column);
        List<Entry> firsts = [];
        foreach (Entry entry in oracle)
        {
            if (firsts.Count == 0 || Order(firsts[^1].Key, entry.Key) != 0)
            {
                firsts.Add(entry);
            }
        }

        await using KeyCursor cursor = await file.Keys(column).Distinct().WithSource(source).OpenAsync();
        Assert.True(cursor.IsDistinct);
        bool rows = source == KeySourceKind.SortedRuns;
        Assert.Equal(rows, cursor.HasRows);

        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            AssertDistinctAt(cursor, firsts, index++, rows);
        }

        Assert.Equal(firsts.Count, index);
        for (bool ok = await cursor.SeekLastAsync(); ok; ok = await cursor.PrevAsync())
        {
            AssertDistinctAt(cursor, firsts, --index, rows);
        }

        Assert.Equal(0, index);

        // Backward seeks land on the key's first entry too.
        int middle = firsts.Count / 2;
        Assert.True(await cursor.SeekAsync(firsts[middle].Key, SeekOp.AtOrBefore));
        AssertDistinctAt(cursor, firsts, middle, rows);
        Assert.True(await cursor.SeekAsync(firsts[middle].Key, SeekOp.Before));
        AssertDistinctAt(cursor, firsts, middle - 1, rows);

        if (!rows)
        {
            Assert.Null(cursor.EntryCount);
            Assert.Throws<InvalidOperationException>(() => cursor.Row);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await cursor.KeyCountAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await cursor.RankAsync(firsts[0].Key));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await cursor.SeekRankAsync(0));
        }
    }

    private static void AssertDistinctAt(KeyCursor cursor, List<Entry> firsts, int index, bool rows)
    {
        Assert.True(cursor.IsValid, $"expected key {index}, the cursor is not positioned");
        Entry expected = firsts[index];
        Assert.True(
            Order(expected.Key, cursor.Key) == 0,
            $"key {index}: expected {Describe(expected.Key)}, got {Describe(cursor.Key)}");
        if (rows)
        {
            Assert.Equal(expected.Row, cursor.Row);
        }
    }

    // ------------------------------------------------------------------ key-ordered delivery (§6)

    public static TheoryData<string, string, bool, int, int> OrderedScans() => new()
    {
        { "i64", string.Empty, false, 1, 0 },
        { "i64", string.Empty, true, 3, 700 },
        { "i64", string.Empty, false, 1, 1 },
        { "i64", "i64 < -2000", true, 2, 1 },
        { "u8", string.Empty, true, 1, 7 },
        { "text", "text > k9", false, 3, 7 },
        { "f32", string.Empty, false, 2, 1_024 },
        { "i64", "i64 in", false, 1, 0 },
        { "i64", "i64 > 100 or i64 < -2400", true, 2, 90 },
        { "u8", string.Empty, false, 2, 100 },
        { "u8", "u8 >= 7", true, 1, 0 },
        { "f32", string.Empty, false, 1, 300 },
        { "f32", string.Empty, true, 1, 0 },
        { "f32", "f32 > -1.5", true, 4, 0 },
        { "f32", "f32 = 0", false, 1, 0 },
        { "text", string.Empty, true, 1, 0 },
        { "text", "text starts k1", false, 2, 50 },
        { "nullable", string.Empty, false, 1, 0 },
        { "nullable", "nullable >= 50", true, 2, 128 },
        { "nullable", "nullable = 1000", false, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(OrderedScans))]
    public async Task AKeyOrderedScanIsTheOracleWindowByWindow(string column, string text, bool descending, int degree, int window)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<Entry> oracle = Oracle(column);
        ScanMetrics metrics = new ScanMetrics();
        Vorticity.Scan.ScanBuilder scan = written.File.Scan()
            .InKeyOrder(column, descending)
            .WithDegreeOfParallelism(degree)
            .WithMetrics(metrics);
        if (text.Length > 0)
        {
            (_, VortexExpr filter, Func<FilterLiteral, bool> matches) = Parse(text);
            oracle = oracle.FindAll(e => matches(e.Key));
            scan.Where(filter);
        }

        if (window > 0)
        {
            scan.WithMaxBatchRows(window);
        }

        if (descending)
        {
            oracle.Reverse();
        }

        (List<long> rows, int largest, int empty) = await OrderedRowsOf(scan);
        Assert.Equal(oracle.ConvertAll(e => e.Row), rows);
        Assert.Equal(0, empty);
        Assert.True(largest <= (window > 0 ? window : Block), $"a batch of {largest} rows");
        Assert.Equal(oracle.Count, metrics.Rows);
        Assert.True(metrics.WindowSplits >= metrics.Windows, $"{metrics.WindowSplits} splits over {metrics.Windows} windows");
        Assert.Equal(oracle.Count == 0 ? 0 : 1, Math.Sign(metrics.Windows));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AKeyOrderedScanGathersEveryColumnInTheWindowsOrder(int degree)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        int batches = 0;
        await foreach (RecordBatch batch in written.File.Scan()
            .InKeyOrder("i64")
            .WithMaxBatchRows(200)
            .WithDegreeOfParallelism(degree)
            .Where(Expr.And(
                Expr.Ge(Expr.Field("i64"), Expr.Literal(FilterLiteral.From(-1_000L))),
                Expr.Eq(Expr.Field("u8"), Expr.Literal(FilterLiteral.From(3UL)))))
            .Project("text", "nullable", "row")
            .ExecuteAsync())
        {
            batches++;
            Assert.Equal(3, batch.FieldCount);
            BinaryColumn texts = batch.Column(0).AsBinary();
            VortexColumn nullables = batch.Column(1);
            ReadOnlySpan<long> rows = batch.Column(2).AsPrimitive<long>().Values;
            ReadOnlySpan<int> values = nullables.AsPrimitive<int>().Values;
            long previous = long.MinValue;
            for (int i = 0; i < rows.Length; i++)
            {
                int row = (int)rows[i];
                Assert.True(I64(row) >= -1_000 && U8(row) == 3, $"row {row} does not match");
                Assert.True(I64(row) >= previous, $"row {row} is out of key order");
                previous = I64(row);
                Assert.Equal(Text(row), texts.GetString(i));
                Assert.Equal(Nullable(row) is not null, nullables.IsValid(i));
                if (Nullable(row) is int v)
                {
                    Assert.Equal(v, values[i]);
                }
            }
        }

        Assert.True(batches > 1, $"{batches} batches");
    }

    [Fact]
    public async Task AConsumerThatStopsNeverReadsThePastWindows()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        ScanMetrics first = new ScanMetrics();
        await foreach (RecordBatch batch in written.File.Scan().InKeyOrder("text").WithMaxBatchRows(10).WithMetrics(first).ExecuteAsync())
        {
            Assert.Equal(10, batch.RowCount);
            break;
        }

        ScanMetrics all = new ScanMetrics();
        await foreach (RecordBatch batch in written.File.Scan().InKeyOrder("text").WithMaxBatchRows(10).WithMetrics(all).ExecuteAsync())
        {
            Assert.True(batch.RowCount <= 10);
        }

        Assert.Equal(1, first.Windows);
        Assert.Equal(Rows / 10, all.Windows);
        Assert.True(first.SegmentRequests * 10 < all.SegmentRequests, $"{first.SegmentRequests} against {all.SegmentRequests}");
    }

    [Fact]
    public async Task AKeyOrderIsRefusedWithoutASourceAndBesideARowSelection()
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        VortexFile file = written.File;

        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().InKeyOrder("i64").WithIndexes(false).ExecuteAsync())
            {
                Assert.Fail("a batch without a key source");
            }
        });
        Assert.Contains("SortedRuns", refused.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => file.Scan().Rows(new RowRange(0, 10)).InKeyOrder("i64"));
        Assert.Throws<InvalidOperationException>(() => file.Scan().Take([1, 2]).InKeyOrder("i64"));
        Assert.Throws<InvalidOperationException>(() => file.Scan().InKeyOrder("i64").Rows(new RowRange(0, 10)));
        Assert.Throws<InvalidOperationException>(() => file.Scan().InKeyOrder("i64").Take([1, 2]));
        Assert.Throws<ArgumentException>(() => file.Scan().InKeyOrder("missing"));
    }

    /// <summary>The rows a key-ordered scan delivers in its order, its largest batch, and how many were empty.</summary>
    private static async Task<(List<long> Rows, int Largest, int Empty)> OrderedRowsOf(Vorticity.Scan.ScanBuilder scan)
    {
        List<long> rows = [];
        int largest = 0;
        int empty = 0;
        await foreach (RecordBatch batch in scan.Project("row").ExecuteAsync())
        {
            largest = Math.Max(largest, batch.RowCount);
            empty += batch.RowCount == 0 ? 1 : 0;
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int i = 0; i < values.Length; i++)
            {
                rows.Add(values[i]);
            }
        }

        return (rows, largest, empty);
    }

    private static bool SameExtreme(FilterLiteral expected, FilterLiteral actual) =>
        expected.Kind == actual.Kind
        && (expected.Kind == FilterLiteralKind.Null
            || (expected.Kind == FilterLiteralKind.Float
                ? expected.FloatValue == actual.FloatValue
                : Order(expected, actual) == 0));

    /// <summary>The file rows a scan returns, read from the fixture's row-number column.</summary>
    private static async Task<List<long>> RowsOf(Vorticity.Scan.ScanBuilder scan)
    {
        List<long> rows = [];
        await foreach (RecordBatch batch in scan.Project("row").ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int i = 0; i < values.Length; i++)
            {
                rows.Add(values[i]);
            }
        }

        return rows;
    }

    private static (string Column, VortexExpr Filter, Func<FilterLiteral, bool> Matches) Parse(string text)
    {
        if (text == "i64 in")
        {
            long[] values = [17, -2500, 999_999, 17];
            return ("i64", Expr.In(Expr.Field("i64"), [.. Array.ConvertAll(values, FilterLiteral.From)]),
                k => Array.IndexOf(values, k.SignedValue) >= 0);
        }

        string[] or = text.Split(" or ");
        if (or.Length == 2)
        {
            (string c, VortexExpr l, Func<FilterLiteral, bool> lm) = Parse(or[0]);
            (_, VortexExpr r, Func<FilterLiteral, bool> rm) = Parse(or[1]);
            return (c, Expr.Or(l, r), k => lm(k) || rm(k));
        }

        string[] and = text.Split(" and ");
        if (and.Length == 2)
        {
            (string c, VortexExpr l, Func<FilterLiteral, bool> lm) = Parse(and[0]);
            (_, VortexExpr r, Func<FilterLiteral, bool> rm) = Parse(and[1]);
            return (c, Expr.And(l, r), k => lm(k) && rm(k));
        }

        string[] parts = text.Split(' ');
        string column = parts[0];
        FieldExpr field = Expr.Field(column);
        if (parts[1] == "starts")
        {
            byte[] prefix = Encoding.UTF8.GetBytes(parts[2]);
            return (column, Expr.StartsWith(field, FilterLiteral.From(parts[2])), k => k.BytesValue.StartsWith(prefix));
        }

        FilterLiteral literal = column switch
        {
            "u8" => FilterLiteral.From(ulong.Parse(parts[2], CultureInfo.InvariantCulture)),
            "text" => FilterLiteral.From(parts[2]),
            "f32" => FilterLiteral.From(parts[2] switch
            {
                "nan" => double.NaN,
                "inf" => double.PositiveInfinity,
                _ => double.Parse(parts[2], CultureInfo.InvariantCulture),
            }),
            _ => FilterLiteral.From(long.Parse(parts[2], CultureInfo.InvariantCulture)),
        };

        // The scan's semantics, written out: IEEE for floats, bytes for strings.
        int Compare(FilterLiteral k) => k.Kind switch
        {
            FilterLiteralKind.Float => k.FloatValue.CompareTo(literal.FloatValue),
            _ => Order(k, literal),
        };
        bool nan(FilterLiteral k) => k.Kind == FilterLiteralKind.Float && (double.IsNaN(k.FloatValue) || double.IsNaN(literal.FloatValue));
        LiteralExpr value = Expr.Literal(literal);
        return parts[1] switch
        {
            "=" => (column, Expr.Eq(field, value), k => !nan(k) && Compare(k) == 0),
            "!=" => (column, Expr.Ne(field, value), k => nan(k) || Compare(k) != 0),
            "<" => (column, Expr.Lt(field, value), k => !nan(k) && Compare(k) < 0),
            "<=" => (column, Expr.Le(field, value), k => !nan(k) && Compare(k) <= 0),
            ">" => (column, Expr.Gt(field, value), k => !nan(k) && Compare(k) > 0),
            _ => (column, Expr.Ge(field, value), k => !nan(k) && Compare(k) >= 0),
        };
    }

    // ------------------------------------------------------------------------------ the oracle

    private readonly record struct Entry(FilterLiteral Key, long Row);

    private static void AssertAt(KeyCursor cursor, List<Entry> oracle, int index)
    {
        Assert.True(cursor.IsValid, $"expected entry {index}, the cursor is not positioned");
        Entry expected = oracle[index];
        Assert.True(
            expected.Row == cursor.Row && Order(expected.Key, cursor.Key) == 0,
            $"entry {index}: expected ({Describe(expected.Key)}, {expected.Row}), got ({Describe(cursor.Key)}, {cursor.Row})");
        if (expected.Key.Kind == FilterLiteralKind.Bytes)
        {
            Assert.True(cursor.KeyBytes.SequenceEqual(expected.Key.BytesValue));
        }
    }

    private static async Task Check(KeyCursor cursor, List<Entry> oracle, FilterLiteral key, SeekOp op, int expected)
    {
        bool found = await cursor.SeekAsync(key, op);
        bool exists = expected >= 0 && expected < oracle.Count;
        Assert.True(found == exists, $"{op} {Describe(key)}: found {found}, expected entry {expected}");
        if (found)
        {
            AssertAt(cursor, oracle, expected);
        }
    }

    private static List<FilterLiteral> Probes(string column, List<Entry> oracle)
    {
        List<FilterLiteral> probes = [];
        for (int i = 0; i < oracle.Count; i += oracle.Count / 23)
        {
            probes.Add(oracle[i].Key);
        }

        probes.Add(oracle[0].Key);
        probes.Add(oracle[^1].Key);
        switch (column)
        {
            case "f32":
                probes.Add(FilterLiteral.From(-1.0));
                probes.Add(FilterLiteral.From(1e300));
                probes.Add(FilterLiteral.From(-1e300));
                probes.Add(FilterLiteral.From(0.1));
                break;
            case "text":
                probes.Add(FilterLiteral.From(string.Empty));
                probes.Add(FilterLiteral.From("k5"));
                probes.Add(FilterLiteral.From("z"));
                probes.Add(FilterLiteral.From("k500-long"));
                break;
            case "u8":
                probes.Add(FilterLiteral.From(ulong.MaxValue));
                probes.Add(FilterLiteral.From(0UL));
                probes.Add(FilterLiteral.From(300UL));
                break;
            default:
                probes.Add(FilterLiteral.From(long.MinValue));
                probes.Add(FilterLiteral.From(long.MaxValue));
                probes.Add(FilterLiteral.From(-2501L));
                probes.Add(FilterLiteral.From(1L << 40));
                break;
        }

        return probes;
    }

    private static int Lower(List<Entry> oracle, FilterLiteral key)
    {
        int i = 0;
        while (i < oracle.Count && Order(oracle[i].Key, key) < 0)
        {
            i++;
        }

        return i;
    }

    private static int Upper(List<Entry> oracle, FilterLiteral key)
    {
        int i = 0;
        while (i < oracle.Count && Order(oracle[i].Key, key) <= 0)
        {
            i++;
        }

        return i;
    }

    /// <summary>The total order of docs/12 §4.4, written out: sign, then magnitude, NaN outermost.</summary>
    private static int Order(FilterLiteral a, FilterLiteral b) => a.Kind switch
    {
        FilterLiteralKind.Signed => a.SignedValue.CompareTo(b.SignedValue),
        FilterLiteralKind.Unsigned => a.UnsignedValue.CompareTo(b.UnsignedValue),
        FilterLiteralKind.Bytes => Math.Sign(a.BytesValue.SequenceCompareTo(b.BytesValue)),
        _ => Rank(a.FloatValue).CompareTo(Rank(b.FloatValue)),
    };

    private static (int Band, double Value) Rank(double d)
    {
        bool negative = double.IsNegative(d);
        if (double.IsNaN(d))
        {
            return (negative ? -3 : 3, 0);
        }

        if (d == 0)
        {
            return (negative ? -1 : 1, 0);
        }

        return (negative ? -2 : 2, d);
    }

    private static string Describe(FilterLiteral key) => key.Kind switch
    {
        FilterLiteralKind.Bytes => Encoding.UTF8.GetString(key.BytesValue),
        FilterLiteralKind.Float => key.FloatValue.ToString("R", CultureInfo.InvariantCulture) + (double.IsNegative(key.FloatValue) ? "(-)" : string.Empty),
        FilterLiteralKind.Signed => key.SignedValue.ToString(CultureInfo.InvariantCulture),
        _ => key.UnsignedValue.ToString(CultureInfo.InvariantCulture),
    };

    private static List<Entry> Oracle(string column)
    {
        List<Entry> entries = [];
        for (int row = 0; row < Rows; row++)
        {
            FilterLiteral? key = column switch
            {
                "i64" => FilterLiteral.From(I64(row)),
                "u8" => FilterLiteral.From((ulong)U8(row)),
                "f32" => FilterLiteral.From((double)F32(row)),
                "text" => FilterLiteral.From(Text(row)),
                "label" => Label(row) is string s ? FilterLiteral.From(s) : null,
                _ => Nullable(row) is int v ? FilterLiteral.From((long)v) : null,
            };
            if (key is { } k)
            {
                entries.Add(new Entry(k, row));
            }
        }

        entries.Sort((x, y) => Order(x.Key, y.Key) is var o && o != 0 ? o : x.Row.CompareTo(y.Row));
        return entries;
    }

    // ------------------------------------------------------------------------------ the file

    private sealed class Written : IAsyncDisposable
    {
        private Written(string path, VortexFile file)
        {
            Path = path;
            File = file;
        }

        internal string Path { get; }

        internal VortexFile File { get; }

        internal static Task<Written> CreateAsync() =>
            CreateAsync(IndexPolicy.SortedRuns.WithSegmentEntries(SegmentEntries));

        /// <summary>The fixture, every column but `row` indexed with <paramref name="policy"/>.</summary>
        internal static async Task<Written> CreateAsync(IndexPolicy policy, Func<ISegmentSource, ISegmentSource>? wrap = null)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-runs-{Guid.NewGuid():N}.vortex");
            await WriteAsync(path, policy);
            if (wrap is null)
            {
                return new Written(path, await VortexFile.OpenAsync(path, CancellationToken.None));
            }

            ISegmentSource source = wrap(MemoryMappedSegmentSource.Open(path));
            return new Written(path, await VortexFile.OpenAsync(source, new VortexOpenOptions(), CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await File.DisposeAsync();
            System.IO.File.Delete(Path);
        }

        private static async Task WriteAsync(string path, IndexPolicy policy)
        {
            DTypeArena types = new DTypeArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
            DType f32 = types.Primitive(PType.F32, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType i32n = types.Primitive(PType.I32, Nullability.Nullable);
            DType utf8n = types.Utf8(Nullability.Nullable);
            DType schema = types.Struct(
                [.. Names, "row", "label"], [i64, u8, f32, utf8, i32n, i64, utf8n], Nullability.NonNullable);

            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                DataBlockTargetBytes = null,
                IndexBudgetPerMille = 1_000_000,
                Indexes = WritePolicy.None
                    .WithDefault(policy)
                    .For("row", IndexPolicy.None),
            };
            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
            for (int start = 0; start < Rows; start += Block)
            {
                CanonicalArena arena = new CanonicalArena();
                int count = Math.Min(Block, Rows - start);
                int[] columns =
                [
                    Fixed<long>(arena, i64, PType.I64, start, count, I64),
                    Fixed<byte>(arena, u8, PType.U8, start, count, U8),
                    Fixed<float>(arena, f32, PType.F32, start, count, F32),
                    Strings(arena, types, utf8, start, count, Text),
                    Nullables(arena, types, i32n, start, count),
                    Fixed<long>(arena, i64, PType.I64, start, count, row => row),
                    Strings(arena, types, utf8n, start, count, Label),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using RecordBatch record = new RecordBatch(arena, root, start);
                await writer.WriteAsync(record, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Fixed<T>(CanonicalArena arena, DType dtype, PType ptype, int start, int count, Func<int, T> value)
            where T : unmanaged
        {
            int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            VortexBuffer buffer = arena.Allocate(count * size, size, out Span<byte> bytes);
            Span<T> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, ptype, buffer);
        }

        private static int Nullables(CanonicalArena arena, DTypeArena types, DType dtype, int start, int count)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                int? v = Nullable(start + i);
                values[i] = v ?? 0;
                if (v is not null)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            int mask = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
            return arena.AddPrimitive(dtype, count, Validity.Bitmap(mask), PType.I32, buffer);
        }

        /// <summary>Views inline and out of line, the latter in one data buffer; a null is an empty invalid view.</summary>
        private static int Strings(
            CanonicalArena arena, DTypeArena types, DType dtype, int start, int count, Func<int, string?> value)
        {
            List<byte[]> values = [];
            int heap = 0;
            bool nulls = false;
            for (int i = 0; i < count; i++)
            {
                string? text = value(start + i);
                nulls |= text is null;
                byte[] utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
                values.Add(utf8);
                heap += utf8.Length > 12 ? utf8.Length : 0;
            }

            Validity validity = Validity.NonNullable;
            if (nulls)
            {
                VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
                raw.Clear();
                for (int i = 0; i < count; i++)
                {
                    if (value(start + i) is not null)
                    {
                        raw[i >> 3] |= (byte)(1 << (i & 7));
                    }
                }

                validity = Validity.Bitmap(
                    arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0));
            }
            else if (dtype.IsNullable)
            {
                validity = Validity.AllValid;
            }

            VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
            bytes.Clear();
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] utf8 = values[i];
                Span<byte> view = bytes.Slice(i * 16, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
                if (utf8.Length <= 12)
                {
                    utf8.CopyTo(view[4..]);
                    continue;
                }

                utf8.AsSpan(0, 4).CopyTo(view[4..]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)offset);
                utf8.CopyTo(dataBytes[offset..]);
                offset += utf8.Length;
            }

            return arena.AddVarBinView(dtype, count, validity, views, [data]);
        }
    }
}
