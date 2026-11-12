// A count and an existence over many windows of one file, on one lane and on several: the lanes take a
// window's splits each and finish them out of order, so every answer is held against one computed from
// the rows themselves, under a range and a take too; and an extreme under a filter on another column,
// which the column's zone bounds now order and cut short, against the rows.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class TerminalLanesTests
{
    /// <summary>Rows of the file: three windows of a chunk and a part of a fourth.</summary>
    private const int Rows = 400_000;

    [Fact]
    public async Task EveryDegreeCountsWhatTheRowsSay()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (byte[] bytes, long[] keys, double[] measures) = await WriteAsync();
        await using VortexFile file = await MemoryFiles.OpenAsync(bytes);
        VortexExpr low = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)));
        VortexExpr none = Expr.And(
            Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25))),
            Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25 + 1e-12))));
        VortexExpr rare = Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.99999)));
        long[] taken = [.. Enumerable.Range(0, 5_000).Select(i => (long)i * 79)];
        RowRange range = RowRange.FromLength(70_000, 250_000);

        foreach (int degree in (int[])[1, 2, 4, 8])
        {
            ScanBuilder Scan(VortexExpr filter) => file.ScanBuilder().Where(filter).WithDegreeOfParallelism(degree);

            Assert.Equal(measures.Count(m => m < 0.5), await Scan(low).CountAsync(ct));
            Assert.Equal(measures.Count(m => m > 0.99999), await Scan(rare).CountAsync(ct));
            Assert.Equal(0, await Scan(none).CountAsync(ct));
            Assert.True(await Scan(low).AnyAsync(ct));
            Assert.Equal(measures.Any(m => m > 0.99999), await Scan(rare).AnyAsync(ct));
            Assert.False(await Scan(none).AnyAsync(ct));

            Assert.Equal(
                Enumerable.Range((int)range.Start, (int)range.Length).Count(i => measures[i] < 0.5),
                await Scan(low).Rows(range).CountAsync(ct));
            Assert.Equal(taken.Count(i => measures[i] < 0.5), await Scan(low).Take(taken).CountAsync(ct));

            Assert.Equal(
                keys.Where((_, i) => measures[i] < 0.5).Max(),
                (await Scan(low).MaxAsync("key", ct)).SignedValue);
            Assert.Equal(
                keys.Where((_, i) => measures[i] < 0.5).Min(),
                (await Scan(low).MinAsync("key", ct)).SignedValue);
            Assert.Equal(FilterLiteralKind.Null, (await Scan(none).MaxAsync("key", ct)).Kind);
        }
    }

    [Fact]
    public async Task ACancelledCountOnLanesThrows()
    {
        (byte[] bytes, _, _) = await WriteAsync();
        await using VortexFile file = await MemoryFiles.OpenAsync(bytes);
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        VortexExpr low = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await file.ScanBuilder().Where(low).WithDegreeOfParallelism(4).CountAsync(cancelled.Token));
    }

    /// <summary>A file of rising keys and pseudo-random measures, and its columns.</summary>
    private static async Task<(byte[] Bytes, long[] Keys, double[] Measures)> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key", "measure"], [i64, f64], Nullability.NonNullable);
        VortexBuffer keyBuffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measureBuffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        Random random = new Random(0x1A9E5);
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row * 3L;
            measureValues[row] = random.NextDouble();
        }

        long[] keys = keyValues.ToArray();
        double[] measures = measureValues.ToArray();
        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keyBuffer);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measureBuffer);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [key, measure]);
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, new VortexWriteOptions()))
        {
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch);
            await writer.CompleteAsync();
        }

        return (stream.ToArray(), keys, measures);
    }
}
