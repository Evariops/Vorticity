// The cursor: the five seek operators against an oracle, a full walk against the materialized
// column, the rank and select invariants, and the refusal on a column with no source.
//
// THE FIXTURE IS WRITTEN BY THIS WRITER, because no other writer produces what the source needs.
// The reference drops `is_sorted` from its file-level aggregation, so a corpus file never carries
// it; the columns below are written here, with their statistics, and the oracle is the formula
// each column is generated from.
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
using Vorticity.Keys;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed class KeyCursorTests
{
    /// <summary>20 zones of 1 024 rows, so a seek walks bounds before it decodes.</summary>
    private const int Rows = 20_480;

    private const int Block = 1_024;

    /// <summary>The 500 leading nulls of <c>nulls_i32</c>: a sorted nullable column keeps them first.</summary>
    private const int Nulls = 500;

    [Theory]
    [InlineData("strict_i64", false)]
    [InlineData("dups_u32", false)]
    [InlineData("nulls_i32", false)]
    [InlineData("floats_f64", false)]
    [InlineData("keys_utf8", false)]
    [InlineData("strict_i64", true)]
    [InlineData("dups_u32", true)]
    [InlineData("nulls_i32", true)]
    [InlineData("floats_f64", true)]
    [InlineData("keys_utf8", true)]
    public async Task TheFiveOperatorsLandWhereTheOracleSaysAndTheWalkIsExactlyTheEntries(string column, bool verify)
    {
        // Under VerifyStatistics every zone a seek decodes is checked: a truthful
        // column must land exactly where it does without the check.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(verify);
        List<FilterLiteral> oracle = Oracle(column);

        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(oracle.Count, cursor.EntryCount);

        foreach (FilterLiteral key in Probes(column, oracle))
        {
            long lower = LowerBound(oracle, key);
            long upper = UpperBound(oracle, key);
            bool present = lower < oracle.Count && Same(oracle[(int)lower], key);

            await Check(cursor, key, SeekMode.Exact, present ? lower : -1, oracle);
            await Check(cursor, key, SeekMode.AtOrAfter, lower < oracle.Count ? lower : -1, oracle);
            await Check(cursor, key, SeekMode.After, upper < oracle.Count ? upper : -1, oracle);
            await Check(cursor, key, SeekMode.AtOrBefore, upper - 1, oracle);
            await Check(cursor, key, SeekMode.Before, lower - 1, oracle);
        }
    }

    [Theory]
    [InlineData("strict_i64", false)]
    [InlineData("dups_u32", false)]
    [InlineData("nulls_i32", false)]
    [InlineData("keys_utf8", false)]
    [InlineData("strict_i64", true)]
    [InlineData("nulls_i32", true)]
    [InlineData("keys_utf8", true)]
    public async Task AFullWalkEqualsTheMaterializedColumnInOrderAndReversesExactly(string column, bool verify)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync(verify);
        List<FilterLiteral> oracle = Oracle(column);

        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync(ct);

        // Forward, from the first entry: every key in order, and every row is the entry's own.
        int at = 0;
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct), at++)
        {
            Assert.True(Same(oracle[at], cursor.Key), $"{column} entry {at}");
            Assert.Equal(FirstRow(column) + at, cursor.Row);
        }

        Assert.Equal(oracle.Count, at);
        Assert.False(cursor.IsValid);

        // Backward is the exact reverse, and a direction flip after a step needs no re-seek here.
        at = oracle.Count;
        for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PreviousAsync(ct))
        {
            at--;
            Assert.True(Same(oracle[at], cursor.Key), $"{column} reversed at {at}");
        }

        Assert.Equal(0, at);

        Assert.True(await cursor.SeekRankAsync(oracle.Count / 2, ct));
        Assert.True(await cursor.NextAsync(ct));
        Assert.True(await cursor.PreviousAsync(ct));
        Assert.True(Same(oracle[oracle.Count / 2], cursor.Key));
    }

    [Theory]
    [InlineData("dups_u32", 7)]
    [InlineData("keys_utf8", 3)]
    [InlineData("strict_i64", 1)]
    public async Task RankAndSelectAgreeWithEachOtherAndWithTheKeyCount(string column, long perKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<FilterLiteral> oracle = Oracle(column);

        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync(ct);
        foreach (long i in new long[] { 0, 1, perKey - 1, perKey, 5_000, oracle.Count - 1 })
        {
            Assert.True(await cursor.SeekRankAsync(i, ct), $"rank {i}");
            FilterLiteral key = cursor.Key;
            long rank = await cursor.RankAsync(key, ct);
            long count = await cursor.CountAtKeyAsync(ct);

            // The invariant of rank and select: select lands inside its key's own slice.
            Assert.True(rank <= i, $"{column}: rank {rank} above {i}");
            Assert.True(i < rank + count, $"{column}: {i} outside [{rank}, {rank + count})");

            // The count is the oracle's own slice, which is `perKey` everywhere but the last
            // group: neither 7 nor 3 divides 20 480, so the final key is short.
            Assert.Equal(LowerBound(oracle, key), rank);
            Assert.Equal(UpperBound(oracle, key) - LowerBound(oracle, key), count);
            Assert.True(count <= perKey, $"{column}: a key cannot hold more than {perKey} entries");

            // And select(rank(k)) is the key's first entry.
            Assert.True(await cursor.SeekRankAsync(rank, ct));
            Assert.True(Same(key, cursor.Key));
        }

        Assert.False(await cursor.SeekRankAsync(oracle.Count, ct));
        Assert.False(await cursor.SeekRankAsync(-1, ct));
    }

    [Theory]
    [InlineData("dups_u32", 7)]
    [InlineData("keys_utf8", 3)]
    public async Task NextKeyIsOneSeekPerGroupAndPrevKeyIsItsReverse(string column, int perKey)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<FilterLiteral> oracle = Oracle(column);

        await using KeyCursor cursor = await written.File.Keys(column).OpenAsync(ct);

        int groups = 0;
        long expected = 0;
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextKeyAsync(ct))
        {
            Assert.True(Same(oracle[(int)expected], cursor.Key), $"group {groups}");
            Assert.Equal(expected, cursor.Row - FirstRow(column));
            groups++;
            expected += perKey;
        }

        Assert.Equal((oracle.Count + perKey - 1) / perKey, groups);

        // Backwards, PrevKey lands on the LAST entry of the previous key -- which is the entry
        // just before the last key's first, the last group being short.
        Assert.True(await cursor.SeekLastAsync(ct));
        long lastGroupStart = LowerBound(oracle, oracle[^1]);
        Assert.True(await cursor.PreviousKeyAsync(ct));
        Assert.Equal(lastGroupStart - 1, cursor.Row - FirstRow(column));
    }

    [Fact]
    public async Task NullsAreNeverVisitedAndAreNotEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        await using KeyCursor cursor = await written.File.Keys("nulls_i32").OpenAsync(ct);
        Assert.Equal(Rows - Nulls, cursor.EntryCount);

        Assert.True(await cursor.SeekFirstAsync(ct));
        Assert.Equal(Nulls, cursor.Row);
        Assert.Equal(0L, cursor.Key.SignedValue);

        // And a seek below every key lands on the first entry, never inside the null run.
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(-1L), SeekMode.AtOrAfter, ct));
        Assert.Equal(Nulls, cursor.Row);
        Assert.False(await cursor.SeekAsync(FilterLiteral.From(-1L), SeekMode.Before, ct));
    }

    [Fact]
    public async Task ASortedColumnPutsTheTwoZerosInOneKeyAndCompareSeparatesThem()
    {
        // The cursor has a TOTAL order, in which -0.0 sorts below +0.0. A sorted
        // COLUMN is sorted in IEEE order, where they are equal, and `is_sorted` is computed that
        // way; so on this source they are one key, and `KeyCursor.Compare` still tells them apart.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        Assert.True(KeyCursor.Compare(FilterLiteral.From(-0.0), FilterLiteral.From(0.0)) < 0);
        Assert.Equal(0, KeyCursor.Compare(FilterLiteral.From(0.0), FilterLiteral.From(0.0)));

        // Row 0 is -0.0 and rows 1 and 2 are +0.0, so the key holds three entries and its first is
        // the negative zero: one key, and the walk starts where IEEE says it does.
        await using KeyCursor cursor = await written.File.Keys("floats_f64").OpenAsync(ct);
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(0.0), SeekMode.Exact, ct));
        Assert.Equal(0, cursor.Row);
        Assert.Equal(3L, await cursor.CountAtKeyAsync(ct));
        Assert.True(double.IsNegative(cursor.Key.FloatValue));

        Assert.True(await cursor.SeekAsync(FilterLiteral.From(-0.0), SeekMode.Exact, ct));
        Assert.Equal(0, cursor.Row);
    }

    [Theory]
    [InlineData("dups_u32")]
    [InlineData("nulls_i32")]
    [InlineData("floats_f64")]
    [InlineData("keys_utf8")]
    public async Task ADistinctWalkOfASortedColumnIsEachKeyAtItsFirstRow(string column)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        List<FilterLiteral> oracle = Oracle(column);
        List<(FilterLiteral Key, long Row)> firsts = [];
        for (int i = 0; i < oracle.Count; i++)
        {
            // IEEE: on a sorted column the two zeros are one key.
            if (firsts.Count == 0 || !SameIeee(firsts[^1].Key, oracle[i]))
            {
                firsts.Add((oracle[i], FirstRow(column) + i));
            }
        }

        KeyPlan plan = await written.File.Keys(column).Distinct().ExplainAsync(ct);
        Assert.Equal(KeySourceKind.SortedColumn, plan.Source);

        await using KeyCursor cursor = await written.File.Keys(column).Distinct().OpenAsync(ct);
        Assert.True(cursor.HasRows);
        int index = 0;
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
        {
            Assert.True(SameIeee(firsts[index].Key, cursor.Key), $"key {index}: {Describe(cursor.Key)}");
            Assert.Equal(firsts[index].Row, cursor.Row);
            index++;
        }

        Assert.Equal(firsts.Count, index);
        for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PreviousAsync(ct))
        {
            index--;
            Assert.True(SameIeee(firsts[index].Key, cursor.Key), $"key {index}: {Describe(cursor.Key)}");
            Assert.Equal(firsts[index].Row, cursor.Row);
        }

        Assert.Equal(0, index);
    }

    private static bool SameIeee(FilterLiteral a, FilterLiteral b) =>
        a.Kind == FilterLiteralKind.Float ? a.FloatValue == b.FloatValue : Same(a, b);

    [Fact]
    public async Task AColumnWithNoSourceIsRefusedAndTheRefusalNamesThePolicy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        VortexUnsupportedException refused = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await written.File.Keys("shuffled_i64").OpenAsync(ct));
        Assert.Contains("IndexPolicy.SortedRuns", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not sorted", refused.Message, StringComparison.Ordinal);

        // A key-only source serves a distinct walk and nothing else; a distinct walk with no source
        // names the cheapest structure that would serve it.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await written.File.Keys("strict_i64").WithSource(KeySourceKind.Postings).OpenAsync(ct));
        await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await written.File.Keys("strict_i64").Distinct().WithSource(KeySourceKind.Postings).OpenAsync(ct));
        VortexUnsupportedException keys = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () => await written.File.Keys("shuffled_i64").Distinct().OpenAsync(ct));
        Assert.Contains("IndexPolicy.Postings", keys.Message, StringComparison.Ordinal);

        KeyPlan plan = await written.File.Keys("shuffled_i64").ExplainAsync(ct);
        Assert.Equal(KeySourceKind.None, plan.Source);
        Assert.Null(plan.EntryCount);
        Assert.Contains(plan.Rejected, r => r.Source == KeySourceKind.SortedColumn);

        KeyPlan sorted = await written.File.Keys("strict_i64").ExplainAsync(ct);
        Assert.Equal(KeySourceKind.SortedColumn, sorted.Source);
        Assert.Equal(1, sorted.Runs);
        Assert.Equal(Rows, sorted.EntryCount);
        Assert.True(sorted.HasRows);
    }

    [Fact]
    public async Task ASeekOfTheWrongDomainIsRefusedAtTheCall()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        await using KeyCursor cursor = await written.File.Keys("strict_i64").OpenAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await cursor.SeekAsync(FilterLiteral.From("nope"), SeekMode.Exact, ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await cursor.SeekAsync(FilterLiteral.Null, SeekMode.Exact, ct));
        Assert.Throws<InvalidOperationException>(() => cursor.Key);
    }

    [Fact]
    public async Task AStepInsideALoadedZoneAllocatesNothing()
    {
        // A cursor step reading KeyBytes allocates nothing. The zone is decoded by the seek;
        // the steps that follow inside it read a borrowed span and must cost nothing at all.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        await using KeyCursor cursor = await written.File.Keys("keys_utf8").OpenAsync(ct);
        Assert.True(await cursor.SeekAsync(Utf8Key(200), SeekMode.AtOrAfter, ct));

        // Warm the path, then measure steps that stay inside the zone the seek loaded.
        long bytes = 0;
        for (int warm = 0; warm < 64; warm++)
        {
            await cursor.NextAsync(ct);
            bytes += cursor.KeyBytes.Length;
        }

        Assert.True(await cursor.SeekAsync(Utf8Key(200), SeekMode.AtOrAfter, ct));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < 64; step++)
        {
            Assert.True(Step(cursor));
            bytes += cursor.KeyBytes.Length;
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes > 0);
        Assert.True(delta == 0, $"64 steps allocated {delta} bytes");
    }

    /// <summary>
    /// One step, taken synchronously: a step inside a loaded zone never suspends, which is the
    /// half of the zero-allocation claim the byte count cannot see.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4)]
    public async Task ASortedColumnDeliversItsKeyOrderAsAContiguousRead(bool descending, int degree)
    {
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        ScanCounters metrics = new ScanCounters();
        List<long> values = [];
        await foreach (RecordBatch batch in written.File.ScanBuilder()
            .InKeyOrder("strict_i64", descending)
            .WithDegreeOfParallelism(degree)
            .Where(Expr.And(
                Expr.Ge(Expr.Field("strict_i64"), Expr.Literal(FilterLiteral.From(20_000L))),
                Expr.Lt(Expr.Field("strict_i64"), Expr.Literal(FilterLiteral.From(40_000L)))))
            .Project("strict_i64")
            .WithMetrics(metrics)
            .ExecuteAsync())
        {
            values.AddRange(batch.Column(0).AsPrimitive<long>().Values.ToArray());
        }

        List<long> expected = [];
        for (int i = 0; i < Rows; i++)
        {
            long v = 1_000L + (3L * i);
            if (v is >= 20_000 and < 40_000)
            {
                expected.Add(v);
            }
        }

        if (descending)
        {
            expected.Reverse();
        }

        Assert.Equal(expected, values);

        // Consecutive keys are consecutive rows: a window of a zone's rows spans two splits at most.
        Assert.True(metrics.Windows > 1);
        Assert.True(metrics.WindowSplits <= 2 * metrics.Windows, $"{metrics.WindowSplits} splits over {metrics.Windows} windows");
    }

    [Fact]
    public async Task AKeyOrderedWindowAllocatesNothing()
    {
        // An InKeyOrder window costs what a filtered batch costs plus its rented permutation: the
        // permutation and the verdicts are rented, the selection is the scan's own, and the
        // window's batch is the previous window's bound again, so a window in steady state
        // allocates nothing.
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();

        // Row 500 is key 2 500: the filtered windows start on a split boundary too.
        VortexExpr filter = Expr.Ge(Expr.Field("strict_i64"), Expr.Literal(FilterLiteral.From(2_500L)));
        for (int warm = 0; warm < 2; warm++)
        {
            await PerWindow(written.File.ScanBuilder().InKeyOrder("strict_i64").Where(filter).WithMaxBatchRows(500));
            await PerWindow(written.File.ScanBuilder().InKeyOrder("strict_i64").WithMaxBatchRows(100));
        }

        Assert.Equal(0, await PerWindow(written.File.ScanBuilder().InKeyOrder("strict_i64").WithMaxBatchRows(100)));
        Assert.Equal(0, await PerWindow(written.File.ScanBuilder().InKeyOrder("strict_i64").Where(filter).WithMaxBatchRows(500)));
    }

    /// <summary>
    /// What a window allocates in steady state: the median, which a one-off tier-up cannot move.
    /// </summary>
    /// <remarks>
    /// The windows are aligned with the splits, so each reads one split whole. A window across two
    /// splits takes each in part, and pays what the take push-down pays on this column's encoding,
    /// which is the take's figure and not the window's.
    /// </remarks>
    private static async Task<long> PerWindow(ScanBuilder builder)
    {
        IAsyncEnumerator<RecordBatch> enumerator = builder.ExecuteAsync().GetAsyncEnumerator();
        List<long> perWindow = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.True(Advance(enumerator));
            }

            while (true)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                bool more = Advance(enumerator);
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (!more)
                {
                    break;
                }

                perWindow.Add(delta);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        perWindow.Sort();
        Assert.True(perWindow.Count > 20, $"{perWindow.Count} windows");
        return perWindow[perWindow.Count / 2];
    }

    private static bool Advance(IAsyncEnumerator<RecordBatch> enumerator)
    {
        ValueTask<bool> move = enumerator.MoveNextAsync();
        Assert.True(move.IsCompletedSuccessfully, "a memory-mapped window must complete synchronously");
        return move.Result;
    }

    [Fact]
    public async Task AKeyOrderedScanOfANullableColumnDeliversItsNullsLast()
    {
        // In both directions, as the row encoding's default null sentinel sorts them: a merge across
        // files and one file's key-ordered scan must agree.
        Decoders.EnsureRegistered();
        await using Written written = await Written.CreateAsync();
        foreach (bool descending in (bool[])[true, false])
        {
            List<int?> values = [];
            await foreach (RecordBatch batch in written.File.ScanBuilder().InKeyOrder("nulls_i32", descending).Project("nulls_i32").ExecuteAsync())
            {
                VortexColumn column = batch.Column(0);
                ReadOnlySpan<int> ints = column.AsPrimitive<int>().Values;
                for (int i = 0; i < batch.RowCount; i++)
                {
                    values.Add(column.IsValid(i) ? ints[i] : null);
                }
            }

            List<int?> expected = [];
            for (int i = Nulls; i < Rows; i++)
            {
                expected.Add((i - Nulls) / 3);
            }

            if (descending)
            {
                expected.Reverse();
            }

            expected.AddRange(System.Linq.Enumerable.Repeat<int?>(null, Nulls));
            Assert.Equal(expected, values);
        }
    }

    private static bool Step(KeyCursor cursor)
    {
        ValueTask<bool> move = cursor.NextAsync();
        Assert.True(move.IsCompletedSuccessfully, "a step inside a loaded zone must not suspend");
        return move.Result;
    }

    private static async Task Check(
        KeyCursor cursor, FilterLiteral key, SeekMode op, long expected, List<FilterLiteral> oracle)
    {
        bool found = await cursor.SeekAsync(key, op);
        Assert.True(
            found == (expected >= 0 && expected < oracle.Count),
            $"{op} on {Describe(key)}: found {found}, expected entry {expected}");
        if (found)
        {
            Assert.True(
                Same(oracle[(int)expected], cursor.Key),
                $"{op} on {Describe(key)}: landed on {Describe(cursor.Key)}, expected {Describe(oracle[(int)expected])}");
        }
    }

    /// <summary>Keys at both ends, inside, absent between neighbours, and duplicated.</summary>
    private static List<FilterLiteral> Probes(string column, List<FilterLiteral> oracle)
    {
        List<FilterLiteral> probes = [oracle[0], oracle[1], oracle[^1], oracle[oracle.Count / 2]];
        switch (column)
        {
            case "strict_i64":
                probes.Add(FilterLiteral.From(0L));
                probes.Add(FilterLiteral.From(1_001L));
                probes.Add(FilterLiteral.From(long.MaxValue));
                break;
            case "dups_u32":
                probes.Add(FilterLiteral.From(0UL));
                probes.Add(FilterLiteral.From((ulong)Rows));
                break;
            case "nulls_i32":
                probes.Add(FilterLiteral.From(-1L));
                probes.Add(FilterLiteral.From((long)Rows));
                break;
            case "floats_f64":
                probes.Add(FilterLiteral.From(-1.0));
                probes.Add(FilterLiteral.From(0.25));
                probes.Add(FilterLiteral.From(1e9));
                break;
            default:
                probes.Add(FilterLiteral.From("a"));
                probes.Add(FilterLiteral.From("k000000x"));
                probes.Add(FilterLiteral.From("z"));
                break;
        }

        return probes;
    }

    private static long LowerBound(List<FilterLiteral> oracle, FilterLiteral key)
    {
        long low = 0;
        long high = oracle.Count;
        while (low < high)
        {
            long mid = low + ((high - low) >> 1);
            if (Order(oracle[(int)mid], key) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static long UpperBound(List<FilterLiteral> oracle, FilterLiteral key)
    {
        long low = 0;
        long high = oracle.Count;
        while (low < high)
        {
            long mid = low + ((high - low) >> 1);
            if (Order(oracle[(int)mid], key) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>The order the source is in: IEEE for floats, so the two zeros are one key.</summary>
    private static int Order(FilterLiteral a, FilterLiteral b) => a.Kind switch
    {
        FilterLiteralKind.Signed => a.SignedValue.CompareTo(b.SignedValue),
        FilterLiteralKind.Unsigned => a.UnsignedValue.CompareTo(b.UnsignedValue),
        FilterLiteralKind.Float => a.FloatValue.CompareTo(b.FloatValue),
        _ => Math.Sign(a.BytesValue.SequenceCompareTo(b.BytesValue)),
    };

    private static bool Same(FilterLiteral a, FilterLiteral b) => a.Kind == b.Kind && Order(a, b) == 0;

    private static string Describe(FilterLiteral key) => key.Kind switch
    {
        FilterLiteralKind.Signed => key.SignedValue.ToString(CultureInfo.InvariantCulture),
        FilterLiteralKind.Unsigned => key.UnsignedValue.ToString(CultureInfo.InvariantCulture),
        FilterLiteralKind.Float => key.FloatValue.ToString(CultureInfo.InvariantCulture),
        FilterLiteralKind.Bytes => Encoding.UTF8.GetString(key.BytesValue),
        _ => key.Kind.ToString(),
    };

    private static long FirstRow(string column) => column == "nulls_i32" ? Nulls : 0;

    private static FilterLiteral Utf8Key(int group) =>
        FilterLiteral.From("k" + group.ToString("D6", CultureInfo.InvariantCulture));

    /// <summary>The entries a column holds, in key order: the formula it is generated from.</summary>
    private static List<FilterLiteral> Oracle(string column)
    {
        List<FilterLiteral> keys = [];
        for (int i = 0; i < Rows; i++)
        {
            switch (column)
            {
                case "strict_i64":
                    keys.Add(FilterLiteral.From(1_000L + (3L * i)));
                    break;
                case "dups_u32":
                    keys.Add(FilterLiteral.From((ulong)(i / 7)));
                    break;
                case "nulls_i32":
                    if (i >= Nulls)
                    {
                        keys.Add(FilterLiteral.From((long)((i - Nulls) / 3)));
                    }

                    break;
                case "floats_f64":
                    keys.Add(FilterLiteral.From(i == 0 ? -0.0 : ((i - 1) / 2) * 0.5));
                    break;
                default:
                    keys.Add(Utf8Key(i / 3));
                    break;
            }
        }

        return keys;
    }

    /// <summary>The fixture: six columns, five of them sorted, written with their statistics.</summary>
    private sealed class Written : IAsyncDisposable
    {
        private Written(VortexFile file)
        {
            File = file;
        }

        internal VortexFile File { get; }

        internal static async Task<Written> CreateAsync(bool verify = false)
        {
            string path = await SharedFiles.GetAsync(nameof(KeyCursorTests), WriteAsync);
            VortexOpenOptions options = new VortexOpenOptions
            {
                Read = new VortexReadOptions { VerifyStatistics = verify },
            };
            return new Written(await VortexFile.OpenAsync(path, options, CancellationToken.None));
        }

        public ValueTask DisposeAsync() => File.DisposeAsync();

        private static async Task WriteAsync(string path)
        {
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            DType i32n = types.Primitive(PType.I32, Nullability.Nullable);
            DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
            DType utf8 = types.Utf8(Nullability.NonNullable);
            DType schema = types.Struct(
                ["strict_i64", "dups_u32", "nulls_i32", "floats_f64", "keys_utf8", "shuffled_i64"],
                [i64, u32, i32n, f64, utf8, i64],
                Nullability.NonNullable);

            VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = Block };
            await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);

            const int batch = 4_096;
            for (int start = 0; start < Rows; start += batch)
            {
                int count = Math.Min(batch, Rows - start);
                int[] columns =
                [
                    Longs(arena, i64, start, count, i => 1_000L + (3L * i)),
                    UInts(arena, u32, start, count, i => (uint)(i / 7)),
                    NullableInts(arena, types, i32n, start, count, i => i < Nulls ? null : (i - Nulls) / 3),
                    Doubles(arena, f64, start, count, i => i == 0 ? -0.0 : ((i - 1) / 2) * 0.5),
                    Strings(arena, utf8, start, count, i => "k" + (i / 3).ToString("D6", CultureInfo.InvariantCulture)),
                    Longs(arena, i64, start, count, i => (i * 7919L) % Rows),
                ];
                int root = arena.AddStruct(schema, count, Validity.NonNullable, columns);
                using (RecordBatch record = new RecordBatch(arena, root, start))
                {
                    await writer.WriteAsync(record, CancellationToken.None);
                }
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        private static int Longs(CanonicalArena arena, DType dtype, int start, int count, Func<int, long> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
        }

        private static int UInts(CanonicalArena arena, DType dtype, int start, int count, Func<int, uint> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(uint), sizeof(uint), out Span<byte> bytes);
            Span<uint> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.U32, buffer);
        }

        private static int Doubles(CanonicalArena arena, DType dtype, int start, int count, Func<int, double> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> bytes);
            Span<double> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = value(start + i);
            }

            return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.F64, buffer);
        }

        private static int NullableInts(
            CanonicalArena arena, DTypeArena types, DType dtype, int start, int count, Func<int, int?> value)
        {
            VortexBuffer buffer = arena.Allocate(count * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
            VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < count; i++)
            {
                int? v = value(start + i);
                values[i] = v ?? 0;
                if (v is not null)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            int mask = arena.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0);
            return arena.AddPrimitive(dtype, count, Validity.Bitmap(mask), PType.I32, buffer);
        }

        /// <summary>Short keys only, so every view is inline and there is no data buffer.</summary>
        private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
        {
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
            bytes.Clear();
            for (int i = 0; i < count; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(value(start + i));
                Span<byte> view = bytes.Slice(i * 16, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
                utf8.CopyTo(view[4..]);
            }

            return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [VortexBuffer.Empty]);
        }
    }
}
