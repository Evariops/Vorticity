// Random access by row index - F5, the claim Vortex makes over Parquet.
//
// Two properties, and as with pruning they fail for opposite reasons. The value tests assert a take
// returns exactly the rows a full scan puts at those indices -- they catch a take that returns the
// wrong rows. The I/O test asserts scattered rows read far fewer segments than the whole file --
// it catches a take that returns the right rows by reading everything and throwing most of it away,
// which every value test would pass.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class TakeTests
{
    /// <summary>65536 rows in 64 splits of 1024.</summary>
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task ATakeReturnsTheRowsAFullScanPutsAtThoseIndices()
    {
        List<long> all = await ReadAll();
        long[] wanted = [0, 1, 5, 1023, 1024, 4096, 40_000, 65_535];

        List<long> taken = await Take(wanted);

        List<long> expected = [];
        foreach (long index in wanted)
        {
            expected.Add(all[(int)index]);
        }

        Assert.Equal(expected, taken);
    }

    [Fact]
    public async Task IndicesAreSortedAndDeduplicatedRatherThanReplayed()
    {
        // Documented: batches come out in file order and a duplicate is collapsed. Reordering to
        // match the caller's list would mean buffering the whole result.
        List<long> all = await ReadAll();
        List<long> taken = await Take([9, 1, 9, 5, 1]);

        Assert.Equal([all[1], all[5], all[9]], taken);
    }

    [Fact]
    public async Task ASingleRowIsATake()
    {
        List<long> all = await ReadAll();
        Assert.Equal([all[12_345]], await Take([12_345L]));
    }

    [Fact]
    public async Task RowsThatFallInOneSplitProduceOneBatch()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        int batches = 0;
        long[] wanted = [10, 20, 30];
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Take(wanted)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            batches++;
            Assert.Equal(wanted.Length, batch.RowCount);
        }

        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task ATakeComposesWithAFilterOverTheTakenRows()
    {
        List<long> all = await ReadAll();
        long[] wanted = [100, 200, 300, 400, 500];
        long pivot = all[(int)wanted[2]];

        List<long> taken = await Take(
            wanted, Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(pivot))));

        List<long> expected = [];
        foreach (long index in wanted)
        {
            if (all[(int)index] >= pivot)
            {
                expected.Add(all[(int)index]);
            }
        }

        Assert.Equal(expected, taken);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public async Task ScatteredRowsReadFarFewerSegmentsThanTheWholeFile()
    {
        // The half of F5 that is about bytes rather than values: eight rows spread over eight
        // splits must not read the other fifty-six.
        long[] scattered = [0, 1024, 2048, 3072, 4096, 5120, 6144, 7168];

        int wholeFile = await CountSegments(take: null);
        int takeOnly = await CountSegments(scattered);

        Assert.True(
            takeOnly * 4 < wholeFile,
            $"a take of {scattered.Length} scattered rows read {takeOnly} segments where the whole " +
            $"file reads {wholeFile}");
    }

    [Fact]
    public async Task AnIndexPastTheEndOfTheFileIsRefusedWhenTheTakeIsSet()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().Take([file.RowCount]));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Scan().Take([-1L]));
    }

    [Fact]
    public async Task ARangeAndAnIndexListAreMutuallyExclusive()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        Assert.Throws<InvalidOperationException>(
            () => file.Scan().Rows(RowRange.FromLength(0, 10)).Take([1L]));
        Assert.Throws<InvalidOperationException>(
            () => file.Scan().Take([1L]).Rows(RowRange.FromLength(0, 10)));
    }

    [Fact]
    public async Task AnEmptyIndexListProducesNoBatches()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Take([])
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Assert.Fail($"an empty take produced a batch of {batch.RowCount} rows");
        }
    }

    private static async Task<List<long>> ReadAll()
    {
        Decoders.EnsureRegistered();
        byte[] name = System.Text.Encoding.UTF8.GetBytes("monotone");
        List<long> values = [];

        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            PrimitiveColumn<long> column = batch.Column(name).AsPrimitive<long>();
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(column.Values[row]);
            }
        }

        return values;
    }

    private static async Task<List<long>> Take(long[] rows, VortexExpr? filter = null)
    {
        Decoders.EnsureRegistered();
        byte[] name = System.Text.Encoding.UTF8.GetBytes("monotone");
        List<long> values = [];

        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path(Zoned), CancellationToken.None);

        ScanBuilder builder = file.Scan().Project("monotone").Take(rows);
        if (filter is not null)
        {
            builder = builder.Where(filter);
        }

        await foreach (RecordBatch batch in builder.ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            PrimitiveColumn<long> column = batch.Column(name).AsPrimitive<long>();
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(column.Values[row]);
            }
        }

        return values;
    }

    private static async Task<int> CountSegments(long[]? take)
    {
        Decoders.EnsureRegistered();
        await using MemoryMappedSegmentSource inner =
            MemoryMappedSegmentSource.Open(Corpus.Path(Zoned));
        RecordingSegmentSource counting = new RecordingSegmentSource(inner);

        await using VortexFile file = await VortexFile.OpenAsync(
            counting, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

        counting.ResetCounters();

        ScanBuilder builder = file.Scan().Project("monotone");
        if (take is not null)
        {
            builder = builder.Take(take);
        }

        await foreach (RecordBatch batch in builder.ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Assert.True(batch.RowCount > 0);
        }

        return counting.Requested.Count;
    }
}
