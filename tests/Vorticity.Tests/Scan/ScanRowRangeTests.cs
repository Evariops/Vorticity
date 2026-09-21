// Rows() at the boundaries the corpus was built around: 0, 1, 1023, 1024 (the FastLanes block),
// 1025, 8191, 8192 (the default row block), 8193.
//
// THE ORACLE IS THE SCAN ITSELF, sliced. A ranged scan must return exactly the sub-sequence a full
// scan returns for those rows - same values, same nulls, same order. That catches every off-by-one
// a row count alone would miss: a chunk selected one too early, a flat array sliced from the wrong
// offset, a batch whose StartRow lies. Comparing against the Rust sidecar is the conformance
// component's job, and it would not catch a consistent shift anyway.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanRowRangeTests
{
    private const string Wide = "distributions/high_cardinality_i64_r8193";
    private const string Narrow = "types/struct_field_names";

    public static TheoryData<long, long> Boundaries =>
    [
        (0L, 0L),
        (0L, 1L),
        (0L, 1023L),
        (0L, 1024L),
        (0L, 1025L),
        (1023L, 1025L),
        (1024L, 1024L),
        (1024L, 2048L),
        (8190L, 8191L),
        (8191L, 8192L),
        (8191L, 8193L),
        (8192L, 8193L),
        (0L, 8193L),
        (100L, 8100L),
        (4000L, 4100L),
    ];

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task ARangedScanIsTheFullScanSliced(long start, long end)
    {
        Decoders.EnsureRegistered();
        long[] all = await ReadAll(Wide);
        Assert.Equal(8193, all.Length);

        long[] ranged = await ReadRange(Wide, new RowRange(start, end));

        Assert.Equal(end - start, ranged.Length);
        for (int i = 0; i < ranged.Length; i++)
        {
            Assert.Equal(all[start + i], ranged[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task ARangedScanRespectsTheCapAndStaysContiguous(long start, long end)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Wide), CancellationToken.None);

        long expected = start;
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Rows(new RowRange(start, end))
            .WithMaxBatchRows(700)
            .ExecuteAsync())
        {
            Assert.Equal(expected, batch.StartRow);
            Assert.True(
                batch.RowCount <= 700,
                string.Create(CultureInfo.InvariantCulture, $"{batch.RowCount} rows exceeds the 700-row cap"));
            expected += batch.RowCount;
            rows += batch.RowCount;
        }

        Assert.Equal(end - start, rows);
        Assert.Equal(end, expected);
    }

    [Fact]
    public async Task ARangeSpanningThreeBatchesIsContiguous()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Wide), CancellationToken.None);

        List<long> starts = new List<long>();
        await foreach (RecordBatch batch in file.Scan()
            .Rows(new RowRange(500, 3500))
            .WithMaxBatchRows(1024)
            .ExecuteAsync())
        {
            starts.Add(batch.StartRow);
        }

        Assert.True(starts.Count >= 3, "3000 rows capped at 1024 needs at least three batches");
        Assert.Equal(500, starts[0]);
    }

    [Fact]
    public async Task AnEmptyRangeProducesNoBatches()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Wide), CancellationToken.None);

        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(17, 17)).ExecuteAsync())
        {
            batches++;
        }

        Assert.Equal(0, batches);
    }

    [Fact]
    public async Task ARangePastTheEndIsClampedNotRejected()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Wide), CancellationToken.None);

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Rows(RowRange.FromLength(0, 1_000_000))
            .ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);

        int batches = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Rows(new RowRange(file.RowCount + 10, file.RowCount + 20))
            .ExecuteAsync())
        {
            batches++;
        }

        Assert.Equal(0, batches);
    }

    [Fact]
    public void ANegativeOrInvertedRangeIsACallerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(5, 4));
    }

    [Fact]
    public async Task TheCapMustBePositive()
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Wide), CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().WithMaxBatchRows(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().WithMaxBatchRows(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().WithDegreeOfParallelism(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().WithDegreeOfParallelism(-4));
    }

    [Fact]
    public async Task ACapOfOneRowStillCoversEveryRow()
    {
        // The pathological cap: one batch per row. It is legal, it must terminate, and it must not
        // materialize a boundary per row up front (SplitPlan sub-divides lazily for exactly this).
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Narrow), CancellationToken.None);

        long rows = 0;
        long expected = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project("A")
            .Rows(new RowRange(0, 40))
            .WithMaxBatchRows(1)
            .ExecuteAsync())
        {
            Assert.Equal(1, batch.RowCount);
            Assert.Equal(expected, batch.StartRow);
            expected++;
            rows++;
        }

        Assert.Equal(40, rows);
    }

    private static async Task<long[]> ReadAll(string entry) => await ReadRange(entry, null);

    private static async Task<long[]> ReadRange(string entry, RowRange? range)
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);
        ScanBuilder builder = file.Scan();
        if (range is RowRange rows)
        {
            builder = builder.Rows(rows);
        }

        List<long> values = new List<long>();
        await foreach (RecordBatch batch in builder.ExecuteAsync())
        {
            ReadOnlySpan<long> span = batch.Column(0).AsPrimitive<long>().Values;
            for (int i = 0; i < span.Length; i++)
            {
                values.Add(span[i]);
            }
        }

        return values.ToArray();
    }
}
