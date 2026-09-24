using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// How a take is cut into batches: a run of zones holding its rows is one batch, a zone holding
/// none of them is not read, and the rows come out the same at any degree.
/// </summary>
public sealed class TakeSplitTests
{
    /// <summary>Rows of the written file: one chunk of eight zones of 8 192.</summary>
    private const int Rows = 65_536;

    /// <summary>
    /// Rows in the first three splits of the take, the sixth and the eighth: three runs, the
    /// splits between them holding none.
    /// </summary>
    private static readonly long[] Wanted = [3, 9_000, 20_000, 45_000, Rows - 1];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ARunOfSplitsHoldingTakenRowsIsOneBatch(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);

            // A split a zone from the first taken row on, five of the eight holding one.
            ScanExplanation plan = await file.ScanBuilder().Take(Wanted).ExplainAsync(ct);
            Assert.Equal(8, plan.Splits);
            Assert.Equal(5, plan.LiveSplits);

            List<string> all = [];
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(ct))
            {
                Values.DescribeRows(batch, all);
            }

            List<string> taken = [];
            int batches = 0;
            await foreach (RecordBatch batch in file.ScanBuilder().Take(Wanted).WithDegreeOfParallelism(degree)
                               .ExecuteAsync().WithCancellation(ct))
            {
                batches++;
                Values.DescribeRows(batch, taken);
            }

            Assert.Equal(3, batches);
            Assert.Equal(Wanted.Length, taken.Count);
            for (int i = 0; i < Wanted.Length; i++)
            {
                Assert.Equal(all[(int)Wanted[i]], taken[i]);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Eight rows in each zone of the chunk: one batch on one lane, and on several a batch every
    /// sixteen rows, so that a small take still gives every lane a split.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public async Task ARunOnSeveralLanesEndsAtSixteenRows(int degree, int expected)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            long[] wanted = new long[64];
            for (int i = 0; i < wanted.Length; i++)
            {
                wanted[i] = ((i / 8) * 8_192L) + ((i % 8) * 1_000L);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            int batches = 0;
            long rows = 0;
            await foreach (RecordBatch batch in file.ScanBuilder().Take(wanted).WithDegreeOfParallelism(degree)
                               .ExecuteAsync().WithCancellation(ct))
            {
                batches++;
                rows += batch.RowCount;
            }

            Assert.Equal(expected, batches);
            Assert.Equal(wanted.Length, rows);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Rows far apart in a column whose encoding decodes a range and cannot select come out of the
    /// ranges around them, joined in order, as a scan reads them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RowsFarApartInADeltaColumnComeOutOfTheirOwnRanges(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("encodings/fastlanes_delta"), ct);
        Assert.Equal(4_096, file.RowCount);

        List<string> all = [];
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(ct))
        {
            Values.DescribeRows(batch, all);
        }

        // Two clusters: a row alone, then two rows closer to each other than to it.
        long[] wanted = [1, 2_560, 4_094];
        List<string> taken = [];
        await foreach (RecordBatch batch in file.ScanBuilder().Take(wanted).WithDegreeOfParallelism(degree)
                           .ExecuteAsync().WithCancellation(ct))
        {
            Values.DescribeRows(batch, taken);
        }

        Assert.Equal([all[1], all[2_560], all[4_094]], taken);
    }

    private static async Task<string> WriteAsync(CancellationToken cancellationToken)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < Rows; i++)
        {
            longs[i] = i * 7_919L % 100_003;
        }

        int column = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, values);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-take-{Guid.NewGuid():N}.vortex");
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema);
        await writer.WriteAsync(batch, cancellationToken);
        await writer.CompleteAsync(cancellationToken);
        return path;
    }
}
