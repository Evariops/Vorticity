// The time slices as a key source: a file whose rows are sorted slice by slice, by a status then by
// their instant, walked by its slice table and by a sorted run on the instant, which must give the
// same entries in the same order, land on the same entry for every seek and rank alike. Three rows
// share each instant, and slices of about 2 500 rows start and end between them, so a seek lands on
// a slice's edges, between slices and on ties.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vorticity.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed partial class SlicedKeySourceTests
{
    private const int Rows = 20_000;

    private static readonly long Noon = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSlicesServeTheInstantWhenNoRunDoes()
    {
        Written written = await WriteAsync(withRun: true, shifted: false);
        await using (VortexFile file = await OpenAsync(written))
        {
            KeyPlan both = await file.Keys(nameof(Hit.At)).ExplainAsync(Ct);
            Assert.Equal(KeySourceKind.SortedRuns, both.Source);
            Assert.Contains(both.Rejected, rejection => rejection.Source == KeySourceKind.Slices);
        }

        written = await WriteAsync(withRun: false, shifted: false);
        await using (VortexFile file = await OpenAsync(written))
        {
            KeyPlan plan = await file.Keys(nameof(Hit.At)).ExplainAsync(Ct);
            Assert.Equal(KeySourceKind.Slices, plan.Source);
            Assert.Equal(written.Table.Slices.Count, plan.Runs);
            Assert.Equal(Rows, plan.EntryCount);
            Assert.True(plan.HasRows);

            // Another column has no slice table.
            KeyPlan status = await file.Keys(nameof(Hit.Status)).ExplainAsync(Ct);
            Assert.Equal(KeySourceKind.None, status.Source);
        }
    }

    [Fact]
    public async Task AWalkIsTheRunsForwardAndBackward()
    {
        Written written = await WriteAsync(withRun: true, shifted: false);
        await using VortexFile file = await OpenAsync(written);
        await using KeyCursor slices = await file.Keys(nameof(Hit.At)).WithSource(KeySourceKind.Slices).OpenAsync(Ct);

        List<(long Key, long Row)> oracle = written.Oracle();
        int index = 0;
        for (bool ok = await slices.SeekFirstAsync(Ct); ok; ok = await slices.NextAsync(Ct))
        {
            Assert.Equal(oracle[index++], (slices.Key.SignedValue, slices.Row));
        }

        Assert.Equal(oracle.Count, index);
        for (bool ok = await slices.SeekLastAsync(Ct); ok; ok = await slices.PreviousAsync(Ct))
        {
            Assert.Equal(oracle[--index], (slices.Key.SignedValue, slices.Row));
        }

        Assert.Equal(0, index);

        // A walk of keys lands on each key's first entry forward, and its last backward.
        List<(long, long)> firsts = [];
        Dictionary<long, int> counts = oracle.CountBy(entry => entry.Key).ToDictionary();
        for (bool ok = await slices.SeekFirstAsync(Ct); ok; ok = await slices.NextKeyAsync(Ct))
        {
            firsts.Add((slices.Key.SignedValue, slices.Row));
            Assert.Equal(counts[slices.Key.SignedValue], await slices.CountAtKeyAsync(Ct));
        }

        Assert.Equal([.. oracle.Where((entry, i) => i == 0 || oracle[i - 1].Key != entry.Key)], firsts);
        List<(long, long)> lasts = [];
        for (bool ok = await slices.SeekLastAsync(Ct); ok; ok = await slices.PreviousKeyAsync(Ct))
        {
            lasts.Add((slices.Key.SignedValue, slices.Row));
        }

        lasts.Reverse();
        Assert.Equal([.. oracle.Where((entry, i) => i == oracle.Count - 1 || oracle[i + 1].Key != entry.Key)], lasts);
    }

    [Fact]
    public async Task EverySeekAndRankLandsWhereTheRunsDo()
    {
        Written written = await WriteAsync(withRun: true, shifted: false);
        await using VortexFile file = await OpenAsync(written);
        await using KeyCursor slices = await file.Keys(nameof(Hit.At)).WithSource(KeySourceKind.Slices).OpenAsync(Ct);
        await using KeyCursor runs = await file.Keys(nameof(Hit.At)).WithSource(KeySourceKind.SortedRuns).OpenAsync(Ct);

        // Each slice's edges and their neighbours, keys held and keys between, and both ends.
        List<long> probes = [long.MinValue + 1, long.MaxValue - 1];
        foreach (TimeSlice slice in written.Table.Slices)
        {
            probes.AddRange([slice.Start - 1, slice.Start, slice.Start + 1, slice.End - 1, slice.End, slice.End + 1]);
        }

        Random random = new Random(3);
        List<(long Key, long Row)> oracle = written.Oracle();
        for (int i = 0; i < 300; i++)
        {
            long held = oracle[random.Next(oracle.Count)].Key;
            probes.AddRange([held, held + 1, held - 1]);
        }

        foreach (long probe in probes)
        {
            FilterLiteral key = FilterLiteral.From(probe);
            foreach (SeekMode mode in new[] { SeekMode.AtOrAfter, SeekMode.After, SeekMode.AtOrBefore, SeekMode.Before, SeekMode.Exact })
            {
                bool found = await slices.SeekAsync(key, mode, Ct);
                Assert.Equal(await runs.SeekAsync(key, mode, Ct), found);
                if (found)
                {
                    Assert.Equal((runs.Key.SignedValue, runs.Row), (slices.Key.SignedValue, slices.Row));

                    // A rank never moves the position, whichever slice it reads.
                    long elsewhere = oracle[random.Next(oracle.Count)].Key;
                    Assert.Equal(await runs.RankAsync(FilterLiteral.From(elsewhere), Ct), await slices.RankAsync(FilterLiteral.From(elsewhere), Ct));
                    Assert.Equal((runs.Key.SignedValue, runs.Row), (slices.Key.SignedValue, slices.Row));
                    Assert.Equal(await runs.NextAsync(Ct), await slices.NextAsync(Ct));
                    if (runs.IsValid)
                    {
                        Assert.Equal((runs.Key.SignedValue, runs.Row), (slices.Key.SignedValue, slices.Row));
                    }
                }
            }

            Assert.Equal(await runs.RankAsync(key, Ct), await slices.RankAsync(key, Ct));
            Assert.Equal(await runs.UpperRankAsync(key, Ct), await slices.UpperRankAsync(key, Ct));
        }

        for (int i = 0; i < 500; i++)
        {
            long rank = random.Next(-2, Rows + 2);
            bool found = await slices.SeekRankAsync(rank, Ct);
            Assert.Equal(await runs.SeekRankAsync(rank, Ct), found);
            if (found)
            {
                Assert.Equal(oracle[(int)rank], (slices.Key.SignedValue, slices.Row));
                Assert.Equal(await runs.PreviousAsync(Ct), await slices.PreviousAsync(Ct));
            }
        }
    }

    /// <summary>
    /// A read in key order over the slices, in windows of 256 rows, ten to a slice: the first window of
    /// a slice reads its own rows, the others gather from the slice decoded whole, and either way the
    /// rows come in <c>(key, row)</c> order, backwards too, under a filter and inside a range of keys.
    /// </summary>
    [Fact]
    public async Task AReadInKeyOrderGivesTheRowsInKeyOrder()
    {
        Written written = await WriteAsync(withRun: false, shifted: false);
        await using VortexFile file = await OpenAsync(written);
        List<(long Key, long Row)> oracle = written.Oracle();
        long from = oracle[3_001].Key;
        long to = oracle[14_999].Key;
        VortexExpr status = Expr.Eq(Expr.Field(nameof(Hit.Status)), Expr.Literal(FilterLiteral.From(2L)));
        VortexExpr range = Expr.And(
            Expr.Ge(Expr.Field(nameof(Hit.At)), Expr.Literal(FilterLiteral.From(from))),
            Expr.Lt(Expr.Field(nameof(Hit.At)), Expr.Literal(FilterLiteral.From(to))));
        foreach (bool descending in new[] { false, true })
        {
            foreach ((VortexExpr? filter, Func<Hit, long, bool> keeps) in new (VortexExpr?, Func<Hit, long, bool>)[]
            {
                (null, (_, _) => true),
                (status, (hit, _) => hit.Status == 2),
                (range, (_, key) => key >= from && key < to),
            })
            {
                List<(long Key, long Row)> expected = [.. oracle.Where(entry => keeps(written.Rows[entry.Row], entry.Key))];
                if (descending)
                {
                    expected.Reverse();
                }

                ScanBuilder scan = file.ScanBuilder().InKeyOrder(nameof(Hit.At), descending).WithMaxBatchRows(256);
                List<(long, long)> read = [];
                await foreach (RecordBatch batch in (filter is null ? scan : scan.Where(filter)).ExecuteAsync().WithCancellation(Ct))
                {
                    Columns<Hit> columns = batch.As<Hit>();
                    DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
                    long[] statuses = new long[batch.RowCount];
                    columns.Column<DateTimeOffset>(0).CopyTo(at);
                    columns.Column<long>(1).CopyTo(statuses);
                    read.AddRange(at.Zip(statuses, (a, s) => (Micros(a), s)));
                    batch.Dispose();
                }

                Assert.Equal([.. expected.Select(entry => (entry.Key, written.Rows[entry.Row].Status))], read);
            }
        }
    }

    /// <summary>A table whose bounds do not hold the rows is a file that lies, refused as a walk reads it.</summary>
    [Fact]
    public async Task ASliceWhoseRowsLieOutsideItsBoundsIsRefused()
    {
        Written written = await WriteAsync(withRun: false, shifted: true);
        await using VortexFile file = await OpenAsync(written);
        await using KeyCursor slices = await file.Keys(nameof(Hit.At)).OpenAsync(Ct);
        await Assert.ThrowsAsync<VortexFormatException>(async () => await slices.SeekFirstAsync(Ct));
    }

    [Fact]
    public void TheRadixKeepsTheRowsOfAKeyInTheirOrder()
    {
        Random random = new Random(11);
        foreach (int count in new[] { 0, 1, 63, 64, 1_000, 70_000 })
        {
            foreach (long spread in new[] { 1L, 3L, 2_048L, 60_000_000L, 86_400_000_000L })
            {
                long low = 1_760_000_000_000_000;
                long[] keys = [.. Enumerable.Range(0, count).Select(_ => low + random.NextInt64(spread))];
                int[] rows = [.. Enumerable.Range(0, count)];
                (long, int)[] expected = [.. keys.Select((key, row) => (key, row)).OrderBy(entry => entry.key).ThenBy(entry => entry.row)];
                SlicedKeySource.SortByKey(keys, rows, count, low);
                Assert.Equal(expected, keys.Zip(rows));
            }
        }
    }

    private sealed record Written(Hit[] Rows, SliceTable Table, byte[] Bytes)
    {
        /// <summary>Every row's instant and row, in <c>(key, row)</c> order.</summary>
        internal List<(long Key, long Row)> Oracle() =>
            [.. Rows.Select((hit, row) => (Micros(hit.At), (long)row)).OrderBy(entry => entry.Item1).ThenBy(entry => entry.Item2)];
    }

    private static long Micros(DateTimeOffset at) => at.ToUnixTimeMilliseconds() * 1000;

    /// <summary>
    /// 20 000 rows, three to an instant 90 ms apart, cut in slices of about 2 500 rows and each sorted
    /// by its status, then its instant: the file carries its slice table, a sorted run on the instant
    /// when asked, and with <paramref name="shifted"/> a table whose bounds lie an hour late.
    /// </summary>
    private static async Task<Written> WriteAsync(bool withRun, bool shifted)
    {
        Random random = new Random(7);
        Hit[] inTime = [.. Enumerable.Range(0, Rows).Select(i => new Hit(DateTimeOffset.FromUnixTimeMilliseconds((Noon / 1000) + (i / 3 * 90L)), random.Next(5)))];
        long[] instants = [.. inTime.Select(hit => Micros(hit.At))];
        List<TimeSlice> slices = TimeSlicer.Cut(instants, TimeUnit.Microseconds, targetRows: 2_500);
        List<Hit> sorted = [];
        foreach (TimeSlice slice in slices)
        {
            sorted.AddRange(inTime.Skip((int)slice.FirstRow).Take((int)slice.Rows).OrderBy(hit => hit.Status).ThenBy(hit => hit.At));
        }

        long hour = 3_600_000_000;
        SliceTable table = new SliceTable(
            TimeUnit.Microseconds,
            shifted ? [.. slices.Select(slice => slice with { Start = slice.Start + hour, End = slice.End + hour })] : slices,
            [1]);
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-sliced-keys-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new()
            {
                Indexes = withRun ? IndexPolicy.Auto.SortedRuns(nameof(Hit.At), true) : IndexPolicy.None,
                CarriesSlices = true,
                BlockRows = 1024,
                ChunkTargetBytes = 16 * 1024,
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Hit>(path, options))
            {
                await writer.WriteAsync<Hit>([.. sorted], Ct);
                writer.DeclareSlices(nameof(Hit.At), table);
                await writer.CompleteAsync(Ct);
            }

            return new Written([.. sorted], table, await System.IO.File.ReadAllBytesAsync(path, Ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static ValueTask<VortexFile> OpenAsync(Written written) =>
        VortexFile.OpenAsync(new MemorySegmentSource(written.Bytes), VortexOpenOptions.Default, Ct);

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, long Status);
}
