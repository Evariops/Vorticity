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

    // More objects than an extreme lists at a time, each answered by its exact summary: the walk is taken a list at a time,
    // the best so far carried from one list to the next, and the answer is every object's best whichever list holds it:
    // the last, for a maximum of keys rising, the first for a minimum, with the lists after it refuted by their summaries.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AnExtremeOverMoreObjectsThanAListIsEveryObjectsBest(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        DatasetOptions options = new DatasetOptions { Seed = 0xFA_0017, Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] } };
        await using (VortexDataset created = await VortexDataset.CreateAsync(store, schema, options, ct))
        {
        }

        int objects = (2 * DatasetScanBuilder.ListedObjects) + 100;
        List<DatasetOperation> added = [];
        for (int i = 0; i < objects; i++)
        {
            (ReadOnlyMemory<byte> key, ObjectEntry entry) = Summarised(i * 100L, (i * 100L) + 50);
            added.Add(new DatasetOperation.AddObject(key, entry) { Level = 1 });
        }

        await DatasetCommitter.CommitAsync(store, added, new CommitOptions { Seed = 0xFA_0017 }, ct);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, ct);
        DatasetScanBuilder scan = dataset.ScanBuilder().WithOptions(new ScanOptions { DegreeOfParallelism = degree }, keepEncodings: false);
        Assert.Equal(((objects - 1) * 100L) + 50, (await scan.MaxAsync("key", ct)).SignedValue);
        Assert.Equal(0L, (await dataset.ScanBuilder().WithOptions(new ScanOptions { DegreeOfParallelism = degree }, keepEncodings: false).MinAsync("key", ct)).SignedValue);
    }

    /// <summary>An object's entry at level 1 whose exact summary of <c>key</c> runs from <paramref name="low"/> to <paramref name="high"/>, its rows those keys.</summary>
    private static (ReadOnlyMemory<byte> Key, ObjectEntry Entry) Summarised(long low, long high)
    {
        UInt128 uid = ((UInt128)(ulong)low << 32) | (uint)high;
        ObjectSummaries summaries = ObjectSummaries.From(
            [new ColumnSummary("key", FilterLiteral.From(low), true, FilterLiteral.From(high), true, IsExact: true, 0, true)]);
        ObjectEntry entry = new ObjectEntry(CommitKey.ForData($"01{low:x12}{high:x12}"), uid, high - low + 1, 1_000, uid, [], summaries);
        byte[] key = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, (ulong)low ^ (1UL << 63));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), (ulong)(uid >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), (ulong)uid);
        return (key, entry);
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
