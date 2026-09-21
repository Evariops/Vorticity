// The two adapters of an object store, end to end: a Vortex file written through the store's sink
// and read back through the store's source.
//
// WHAT THIS PROVES that a unit test of either half cannot: the seam holds. The writer above the
// sink is the ordinary `VortexFileWriter` and the reader above the source is the ordinary
// `VortexFile` — neither knows an object store exists — so a file that round-trips here is a file
// an S3 library's users would get. The counting store then says what the read COST, which is what
// matters against an object store, where each request pays a round trip: a scan that asked for
// forty segments and paid three requests is coalescing working, and the same scan asking forty
// times would be the bug this test exists to catch.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectAdapterTests
{
    private const int Rows = 40_000;
    private const string Key = "data/adapter.vortex";

    [Fact]
    public async Task AFileWrittenThroughTheSinkReadsBackThroughTheSource()
    {
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        long written = await WriteAsync(store, Key);

        Assert.Equal(written, (await store.HeadAsync(Key, default))!.Value.Length);

        await using ObjectSegmentSource source = new ObjectSegmentSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);
        Assert.Equal(Rows, file.RowCount);

        long sum = 0;
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
        {
            ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                sum += keys[row];
            }

            rows += batch.RowCount;
        }

        Assert.Equal(Rows, rows);
        Assert.Equal(Expected(), sum);
    }

    [Fact]
    public async Task AScanCoalescesItsSegmentsIntoFewerRequests()
    {
        Decoders.EnsureRegistered();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteAsync(inner, Key);
        await using CountingObjectStore store = new CountingObjectStore(inner);

        await using ObjectSegmentSource source = new ObjectSegmentSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);
        store.Reset();

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(Rows, rows);

        // The file holds two columns over several chunks, so a source without coalescing would ask
        // for every segment of every chunk on its own. The bound is deliberately loose: what must
        // not happen is a request per segment, and a request per chunk is already the good case.
        Assert.InRange(store.CountOf(ObjectOperation.GetRange), 1, 24);
        Assert.Equal(0, store.CountOf(ObjectOperation.PutIfAbsent));
        Assert.True(store.BytesRead > 0);
    }

    [Fact]
    public async Task AnUncommittedSinkWritesNothing()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/abandoned"))
        {
            await sink.WriteAsync(new byte[128], default);
            await sink.FlushAsync(default);
            Assert.Equal(128, sink.Position);
            Assert.False(sink.IsCommitted);
        }

        Assert.Null(await store.HeadAsync("data/abandoned", default));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ASinkOverATakenKeyReportsItRatherThanOverwriting()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("data/taken", new byte[] { 1, 2, 3 }, default);

        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/taken");
        await sink.WriteAsync(new byte[] { 9, 9 }, default);
        Assert.Equal(PutOutcome.Exists, await sink.CommitAsync(default));

        ObjectHead head = Assert.NotNull(await store.HeadAsync("data/taken", default));
        Assert.Equal(3, head.Length);
    }

    [Fact]
    public async Task ASinkRefusesToBufferMoreThanItWasGiven()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/huge", maxBytes: 1024);
        await sink.WriteAsync(new byte[1000], default);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await sink.WriteAsync(new byte[100], default));
        Assert.Contains("multipart", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceRefusesAnObjectThatChangedUnderIt()
    {
        // A read bound to one object, at the seam: an object is immutable, so a token that changes
        // mid-read means the key was deleted and created again. Carrying on would mix two objects'
        // bytes.
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        await WriteAsync(store, Key);

        await using ObjectSegmentSource source = new ObjectSegmentSource(store, Key);
        Assert.True(await source.GetLengthAsync(default) > 0);
        string? first = source.Token;
        Assert.NotNull(first);

        // The same bytes under the same key, created again: a new object, so a new token.
        using (ObjectRange range = await store.GetRangeAsync(Key, 0, int.MaxValue / 2, default))
        {
            await store.DeleteAsync(Key, default);
            await store.PutIfAbsentAsync(Key, range.Bytes, default);
        }

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await source.ReadRangeAsync(0, 64, 1, default));
    }

    /// <summary>Writes a two-column file into <paramref name="key"/> and returns its bytes.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    private static async Task<long> WriteAsync(IObjectStore store, string key)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key", "measure"], [i64, f64], Nullability.NonNullable);

        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, key);
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            sink, schema, new VortexWriteOptions { RowBlockSize = 2_048, DataBlockTargetBytes = 64 << 10 }))
        {
            const int batchRows = 4_000;
            for (int start = 0; start < Rows; start += batchRows)
            {
                CanonicalArena arena = new CanonicalArena();
                VortexBuffer keys = arena.Allocate(batchRows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
                VortexBuffer measures = arena.Allocate(batchRows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
                Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
                Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
                for (int row = 0; row < batchRows; row++)
                {
                    keyValues[row] = start + row;
                    measureValues[row] = (start + row) / 8.0;
                }

                int keyNode = arena.AddPrimitive(i64, batchRows, Validity.NonNullable, PType.I64, keys);
                int measureNode = arena.AddPrimitive(f64, batchRows, Validity.NonNullable, PType.F64, measures);
                int root = arena.AddStruct(schema, batchRows, Validity.NonNullable, [keyNode, measureNode]);
                using RecordBatch batch = new RecordBatch(arena, root, start);
                await writer.WriteAsync(batch, default);
            }

            await writer.CompleteAsync(default);
        }

        Assert.Equal(PutOutcome.Created, await sink.CommitAsync(default));
        return sink.Position;
    }

    private static long Expected()
    {
        long sum = 0;
        for (long row = 0; row < Rows; row++)
        {
            sum += row;
        }

        return sum;
    }
}
