// Count, Any, Min and Max over many objects, at one lane and at several: the lanes answer objects in
// whatever order they finish, and a minimum or a maximum visits the objects best summary first, so
// every answer is checked against one computed from the rows themselves, at every degree, with rows
// deleted from some objects so that their counts and extremes leave those rows out.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetFanOutAnswersTests
{
    private const int Objects = 12;
    private const int PerObject = 500;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task EveryDegreeAnswersWhatTheRowsSay(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<(long Key, double Measure)> rows) = await BuildAsync(store, ct);
        await using (dataset)
        {
            ScanOptions options = new ScanOptions { DegreeOfParallelism = degree };
            VortexExpr low = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)));
            VortexExpr none = Expr.And(
                Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25))),
                Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.25 + 1e-12))));
            VortexExpr late = Expr.Gt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.999)));
            List<(long Key, double Measure)> kept = rows.Where(row => row.Measure < 0.5).ToList();

            DatasetScanBuilder Scan(VortexExpr? filter)
            {
                DatasetScanBuilder scan = dataset.ScanBuilder().WithOptions(options, keepEncodings: false);
                return filter is null ? scan : scan.Where(filter);
            }

            Assert.Equal(rows.Count, await Scan(null).CountAsync(ct));
            Assert.Equal(kept.Count, await Scan(low).CountAsync(ct));
            Assert.Equal(rows.Count(row => row.Measure > 0.999), await Scan(late).CountAsync(ct));
            Assert.Equal(0, await Scan(none).CountAsync(ct));

            Assert.True(await Scan(low).AnyAsync(ct));
            Assert.Equal(rows.Any(row => row.Measure > 0.999), await Scan(late).AnyAsync(ct));
            Assert.False(await Scan(none).AnyAsync(ct));

            Assert.Equal(kept.Min(row => row.Key), (await Scan(low).MinAsync("key", ct)).SignedValue);
            Assert.Equal(kept.Max(row => row.Key), (await Scan(low).MaxAsync("key", ct)).SignedValue);
            Assert.Equal(kept.Min(row => row.Measure), (await Scan(low).MinAsync("measure", ct)).FloatValue);
            Assert.Equal(kept.Max(row => row.Measure), (await Scan(low).MaxAsync("measure", ct)).FloatValue);
            Assert.Equal(rows.Min(row => row.Key), (await Scan(null).MinAsync("key", ct)).SignedValue);
            Assert.Equal(rows.Max(row => row.Key), (await Scan(null).MaxAsync("key", ct)).SignedValue);
            Assert.Equal(FilterLiteralKind.Null, (await Scan(none).MaxAsync("key", ct)).Kind);
        }
    }

    [Fact]
    public async Task ACancelledQuestionThrowsAtEveryDegree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, _) = await BuildAsync(store, ct);
        await using (dataset)
        {
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            VortexExpr low = Expr.Lt(Expr.Field("measure"), Expr.Literal(FilterLiteral.From(0.5)));
            foreach (int degree in (int[])[1, 4])
            {
                DatasetScanBuilder scan = dataset.ScanBuilder().WithOptions(new ScanOptions { DegreeOfParallelism = degree }, keepEncodings: false).Where(low);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan.CountAsync(cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan.AnyAsync(cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan.MaxAsync("key", cancelled.Token));
            }
        }
    }

    /// <summary>Twelve objects of rising keys and pseudo-random measures, a tenth of the rows of every third object deleted.</summary>
    private static async Task<(VortexDataset Dataset, List<(long Key, double Measure)> Rows)> BuildAsync(MemoryObjectStore store, CancellationToken ct)
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0xFA_0017,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] },
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        List<(long Key, double Measure)> rows = [];
        for (int part = 0; part < Objects; part++)
        {
            await dataset.AppendAsync(Batches(types, schema, part, rows), ct);
        }

        List<long> deleted = [];
        for (int part = 0; part < Objects; part += 3)
        {
            for (int i = 0; i < PerObject; i += 10)
            {
                deleted.Add(((long)part * PerObject) + i);
            }
        }

        VortexExpr gone = Expr.In(Expr.Field("key"), [.. deleted.Select(key => FilterLiteral.From(key))]);
        await dataset.DeleteAsync(gone, ct);
        HashSet<long> removed = [.. deleted];
        return (dataset, rows.Where(row => !removed.Contains(row.Key)).ToList());
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int part, List<(long Key, double Measure)> rows)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        Random random = new Random(part);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(PerObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(PerObject * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < PerObject; i++)
        {
            keyValues[i] = ((long)part * PerObject) + i;
            measureValues[i] = random.NextDouble();
            rows.Add((keyValues[i], measureValues[i]));
        }

        int key = arena.AddPrimitive(i64, PerObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, PerObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, PerObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask;
    }
}
