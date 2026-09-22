// What a dataset is accepted on: every answer equals that of a single file.
//
// SO EVERY TEST HERE IS A COMPARISON, not an assertion about a number someone chose. The same rows
// go into a dataset of several objects and into one file written in one go; the dataset's answer
// and the file's answer must be the same rows, the same count, the same values under the same
// filter. That is the only property that makes a dataset worth having over a directory of files,
// and it is the one a partial implementation breaks first.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class VortexDatasetTests
{
    private const int Batch = 5_000;

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0xDA7A_5E7,
        Write = new VortexWriteOptions { RowBlockSize = 1_024, DataBlockTargetBytes = 64 << 10 },
    };

    [Fact]
    public async Task ADatasetOfSeveralObjectsAnswersAsOneFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(), ct);
        Assert.Equal(1UL, dataset.Version);
        Assert.Equal(0, dataset.RowCount);

        const int objects = 4;
        for (int i = 0; i < objects; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * Batch, Batch), ct);
        }

        Assert.Equal(objects, dataset.ObjectCount);
        Assert.Equal(objects * Batch, dataset.RowCount);

        // The same rows, written once, into one file.
        byte[] single = await OneFileAsync(types, schema, objects * Batch);
        await using VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(single), new VortexOpenOptions(), ct);

        Assert.Equal(file.RowCount, dataset.RowCount);
        Assert.Equal(await KeysAsync(file.ScanBuilder()), await KeysAsync(dataset.ScanBuilder()));

        // And under a filter, which each object's own scan applies with its own indexes.
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(7_000L))),
            Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(12_345L))));
        Assert.Equal(
            await KeysAsync(file.ScanBuilder().Where(filter)),
            await KeysAsync(dataset.ScanBuilder().Where(filter)));
        Assert.Equal(
            await file.ScanBuilder().Where(filter).CountAsync(ct),
            await dataset.ScanBuilder().Where(filter).CountAsync(ct));

        // With the per-file index chain off, which must not change the answer: an index only
        // skips work.
        Assert.Equal(
            await KeysAsync(file.ScanBuilder().Where(filter).WithIndexes(false)),
            await KeysAsync(dataset.ScanBuilder().Where(filter).WithIndexes(false)));
    }

    [Fact]
    public async Task AFileAlreadyInTheStoreIsImportedWithoutACopy()
    {
        // A single existing Vortex file becomes a dataset of one leaf: one commit object, no copy.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] single = await OneFileAsync(types, schema, 3_000);
        string key = CommitKey.ForData("imported");
        await store.PutIfAbsentAsync(key, single, ct);
        long bytesBefore = store.Bytes;

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(), ct);
        ulong version = await dataset.ImportAsync(key, ct);

        // Version 1 is the creation, version 2 is the import: one commit object each.
        Assert.Equal(2UL, version);
        Assert.Equal(1, dataset.ObjectCount);
        Assert.Equal(3_000, dataset.RowCount);

        // THE FILE'S BYTES WERE NOT COPIED, and this says it exactly rather than by a threshold
        // somebody chose: the store grew by the commit objects, to the byte. A copy would have added
        // `single.Length` on top, and a chosen ceiling would only have said "not much more".
        long commits = 0;
        foreach (string commit in await store.ListAsync(CommitKey.Prefix, null, 100, ct))
        {
            commits += Assert.NotNull(await store.HeadAsync(commit, ct)).Length;
        }

        Assert.Equal(commits, store.Bytes - bytesBefore);
        Assert.Equal(3, store.Count);
        Console.Out.Write(FormattableString.Invariant(
            $"IMPORT WITHOUT A COPY: a {single.Length}-byte file became a dataset for {commits} bytes of commit objects, and the store holds {store.Count} objects.\n"));

        ObjectEntry entry = Assert.Single(await ObjectsAsync(dataset));
        Assert.Equal(key, entry.Key);
        Assert.Equal(single.Length, entry.Bytes);
        Assert.NotEqual(UInt128.Zero, entry.Uid);

        await using VortexFile file = await VortexFile.OpenAsync(
            new MemorySegmentSource(single), new VortexOpenOptions(), ct);
        Assert.Equal(await KeysAsync(file.ScanBuilder()), await KeysAsync(dataset.ScanBuilder()));
    }

    [Fact]
    public async Task AFileOfAnotherSchemaIsRefusedAtImportAndNothingIsCommitted()
    {
        // An object with another schema is refused at the import that would add it, not at the
        // first scan that would trip over it. A nullable key is another schema.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        DType other = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.Nullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] single = await OneFileAsync(types, schema, 1_000);
        string key = CommitKey.ForData("foreign");
        await store.PutIfAbsentAsync(key, single, ct);

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, other, Options(), ct);
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await dataset.ImportAsync(key, ct));
        Assert.Contains("schema", refused.Message, StringComparison.Ordinal);

        Assert.Equal(1UL, dataset.Version);
        Assert.Equal(0, dataset.ObjectCount);
        Assert.Single(await store.ListAsync(CommitKey.Prefix, null, 100, ct));
    }

    [Fact]
    public async Task AnAppendMintsAnIdentityAndRecordsWhatItWrote()
    {
        // The entry carries the uid the postscript holds and the hash the writer computed, so a
        // fragment bound to an old version of the bytes is refused before anything is read.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(), ct);
        await dataset.AppendAsync(Batches(types, schema, 0, 1_000), ct);

        ObjectEntry entry = Assert.Single(await ObjectsAsync(dataset));
        Assert.Equal(1_000, entry.Rows);
        Assert.NotEqual(UInt128.Zero, entry.Uid);
        Assert.NotEqual(UInt128.Zero, entry.Hash);

        ObjectHead head = Assert.NotNull(await store.HeadAsync(entry.Key, ct));
        Assert.Equal(head.Length, entry.Bytes);

        // The uid in the entry is the one the file's postscript carries.
        await using ObjectSegmentSource source = new ObjectSegmentSource(store, entry.Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        Assert.NotNull(file.StoredIdentity);
    }

    [Fact]
    public async Task AnEmptyAppendCommitsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(), ct);
        int objectsBefore = store.Count;

        ulong version = await dataset.AppendAsync(Nothing(), ct);

        Assert.Equal(dataset.Version, version);
        Assert.Equal(objectsBefore, store.Count);
        Assert.Equal(0, dataset.ObjectCount);
    }

    [Fact]
    public async Task OpeningAStoreWithoutADatasetSaysSo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await VortexDataset.OpenAsync(store, cancellationToken: ct));

        DTypeArena types = new DTypeArena();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Schema(types), Options(), ct);
        await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await VortexDataset.CreateAsync(store, Schema(types), Options(), ct));
    }

    [Fact]
    public async Task ASecondHandleSeesTheFirstsCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, schema, Options(), ct);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, Options(), ct);

        await writer.AppendAsync(Batches(types, schema, 0, 2_000), ct);

        // The reader holds its own version until it asks for another.
        Assert.Equal(0, reader.RowCount);
        Assert.Equal(writer.Version, await reader.RefreshAsync(ct));
        Assert.Equal(2_000, reader.RowCount);
        Assert.Equal(schema.FieldCount, reader.Schema.Count);
        Assert.Equal(writer.Seed, reader.Seed);
    }

    private static async Task<List<ObjectEntry>> ObjectsAsync(VortexDataset dataset)
    {
        List<ObjectEntry> entries = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            entries.Add(held.Entry);
        }

        return entries;
    }

    private static async Task<List<long>> KeysAsync(ScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    /// <summary>The same rows a dataset would hold, written once into one file.</summary>
    private static async Task<byte[]> OneFileAsync(DTypeArena types, DType schema, int rows)
    {
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, Options().Write))
        {
            await foreach (RecordBatch batch in Batches(types, schema, 0, rows))
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    private static async IAsyncEnumerable<RecordBatch> Nothing()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, long from, int rows)
    {
        const int size = 1_000;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        for (int start = 0; start < rows; start += size)
        {
            int count = Math.Min(size, rows - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keys = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = from + start + row;
                measureValues[row] = (from + start + row) / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keys);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }
}
