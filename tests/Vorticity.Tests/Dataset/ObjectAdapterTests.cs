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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectAdapterTests
{
    private const int Rows = 40_000;
    private const string Key = "data/adapter.vortex";

    [Fact]
    public async Task AFileWrittenThroughTheSinkReadsBackThroughTheSource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        long written = await WriteAsync(store, Key);

        Assert.Equal(written, (await store.HeadAsync(Key, ct))!.Value.Length);

        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        Assert.Equal(Rows, file.RowCount);

        long sum = 0;
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
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

        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), TestContext.Current.CancellationToken);
        store.Reset();

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(Rows, rows);

        // The file holds two columns over several chunks, so a source without coalescing would ask
        // for every segment of every chunk on its own. The bound is deliberately loose: what must
        // not happen is a request per segment, and a request per chunk is already the good case.
        Assert.InRange(store.CountOf(ObjectOperation.Read), 1, 24);
        Assert.Equal(0, store.CountOf(ObjectOperation.Put));
        Assert.True(store.BytesRead > 0);
    }

    /// <summary>
    /// A projection of a column whose chunks take a few dozen bytes each, 200 chunks of 1 024 rows between those of a
    /// column of noise: each split asked for its own few bytes, a request a split, where a batch now reads along the
    /// small segments of the splits ahead, a request for some 32 of them; at four lanes as at one, and every value read.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AScanReadsTheSmallSegmentsOfTheSplitsAheadAlong(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Chunks = 200;
        const int ChunkRows = 1_024;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteSmallAsync(inner, Key, Chunks, ChunkRows);
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        store.Reset();

        long rows = 0;
        long sum = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("flag").WithDegreeOfParallelism(degree).ExecuteAsync().WithCancellation(ct))
        {
            ReadOnlySpan<long> flags = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                sum += flags[row];
            }

            rows += batch.RowCount;
        }

        Assert.Equal(Chunks * ChunkRows, rows);
        Assert.Equal((long)Chunks * (Chunks - 1) / 2 * ChunkRows, sum);
        Assert.InRange(store.CountOf(ObjectOperation.Read), 1, Chunks / 10);
    }

    /// <summary>
    /// The same flags under a filter that keeps the first 60 chunks and the last 60: the walk ahead skips the dead ones
    /// as the scan does, and widens the splits the zone maps prove whole as the scan widens them, so each segment read
    /// along waits for the batch that asks for it; split by split under a cap of one chunk a batch, at four lanes as at
    /// one.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    public async Task AFilteredScanReadsAlongTheSplitsItWidensOrSkips(int degree, bool widened)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Chunks = 200;
        const int ChunkRows = 1_024;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteSmallAsync(inner, Key, Chunks, ChunkRows);
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        store.Reset();

        VortexExpr kept = Expr.Or(
            Expr.Lt(Expr.Field("flag"), Expr.Literal(FilterLiteral.From(60L))),
            Expr.Ge(Expr.Field("flag"), Expr.Literal(FilterLiteral.From(140L))));
        ScanBuilder scan = file.ScanBuilder().Where(kept).Project("flag").WithDegreeOfParallelism(degree);
        if (!widened)
        {
            scan = scan.WithMaxBatchRows(ChunkRows);
        }

        long rows = 0;
        long sum = 0;
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(ct))
        {
            ReadOnlySpan<long> flags = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                sum += flags[row];
            }

            rows += batch.RowCount;
        }

        Assert.Equal(120 * ChunkRows, rows);
        Assert.Equal(((59L * 60 / 2) + ((140L + 199) * 60 / 2)) * ChunkRows, sum);
        Assert.InRange(store.CountOf(ObjectOperation.Read), 1, Chunks / 10);
    }

    /// <summary>
    /// The flags read with the noise beside them, 8 KiB a chunk: each split reads a large segment of its own, which its
    /// flag joins in one request, so nothing is read along, and no batch waits on another's read before it makes its own.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ASplitThatReadsALargeSegmentOfItsOwnReadsItsSmallOnesWithIt(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Chunks = 200;
        const int ChunkRows = 1_024;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteSmallAsync(inner, Key, Chunks, ChunkRows);
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        store.Reset();

        long rows = 0;
        double noise = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("flag", "noise").WithDegreeOfParallelism(degree).ExecuteAsync().WithCancellation(ct))
        {
            ReadOnlySpan<double> values = batch.Column(1).AsPrimitive<double>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                noise += values[row];
            }

            rows += batch.RowCount;
        }

        // A request a split at most, the last few chunks lying in the tail the open read; and the bytes of the columns,
        // where flags read along with a batch's own read made it a run over the noise of the splits ahead, 4.3 MB for 1.6.
        Assert.Equal(Chunks * ChunkRows, rows);
        Assert.InRange(noise, 0.4 * rows, 0.6 * rows);
        Assert.InRange(store.CountOf(ObjectOperation.Read), Chunks - 8, Chunks);
        Assert.True(store.BytesRead < Chunks * ChunkRows * sizeof(double) * 5L / 4, $"{store.BytesRead} bytes read");
    }

    /// <summary>
    /// The flags and the noise in chunks of 4 096 rows, read in splits of 1 024: the first split of a chunk reads its noise
    /// and its flag, and the three after it share both. The flag the first reads is not read along for the others, which
    /// would read the noise between the flags twice and keep the first split's batch waiting on the walk's read before
    /// it made its own.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ASmallSegmentThatASplitWhichReadsAsksForIsNotReadAlong(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Chunks = 50;
        const int ChunkRows = 4_096;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteSmallAsync(inner, Key, Chunks, ChunkRows);
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        store.Reset();

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("flag", "noise").WithMaxBatchRows(1_024)
            .WithDegreeOfParallelism(degree).ExecuteAsync().WithCancellation(ct))
        {
            rows += batch.RowCount;
        }

        Assert.Equal(Chunks * ChunkRows, rows);
        Assert.InRange(store.CountOf(ObjectOperation.Read), 1, Chunks);
        Assert.True(store.BytesRead < Chunks * ChunkRows * sizeof(double) * 5L / 4, $"{store.BytesRead} bytes read");
    }

    /// <summary>
    /// The same flags between chunks of noise of 128 KiB: a segment ahead lies past what a read bridges, so each split
    /// reads its own few bytes, as before, rather than the noise between them.
    /// </summary>
    [Fact]
    public async Task AScanReadsNoSegmentAheadPastWhatAReadBridges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int Chunks = 40;
        const int ChunkRows = 16_384;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await WriteSmallAsync(inner, Key, Chunks, ChunkRows);
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);
        store.Reset();

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("flag").ExecuteAsync().WithCancellation(ct))
        {
            rows += batch.RowCount;
        }

        Assert.Equal(Chunks * ChunkRows, rows);
        Assert.True(store.BytesRead < Chunks * 4_096, $"{store.BytesRead} bytes read for {Chunks} chunks of a flag");
    }

    /// <summary>A flag of one value a chunk, its number, and a column of noise beside it, in chunks of <paramref name="chunkRows"/>.</summary>
    private static async Task WriteSmallAsync(IObjectStore store, string key, int chunks, int chunkRows)
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["flag", "noise"], [i64, f64], Nullability.NonNullable);
        Random draw = new Random(0x5A11);
        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, key);
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            sink, schema, new VortexWriteOptions { BlockRows = chunkRows, ChunkTargetBytes = chunkRows * sizeof(double) }))
        {
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                CanonicalArena arena = new CanonicalArena();
                VortexBuffer flags = arena.Allocate(chunkRows * sizeof(long), sizeof(long), out Span<byte> flagBytes);
                VortexBuffer noise = arena.Allocate(chunkRows * sizeof(double), sizeof(double), out Span<byte> noiseBytes);
                MemoryMarshal.Cast<byte, long>(flagBytes).Fill(chunk);
                Span<double> noiseValues = MemoryMarshal.Cast<byte, double>(noiseBytes);
                for (int row = 0; row < chunkRows; row++)
                {
                    noiseValues[row] = draw.NextDouble();
                }

                int flagNode = arena.AddPrimitive(i64, chunkRows, Validity.NonNullable, PType.I64, flags);
                int noiseNode = arena.AddPrimitive(f64, chunkRows, Validity.NonNullable, PType.F64, noise);
                int root = arena.AddStruct(schema, chunkRows, Validity.NonNullable, [flagNode, noiseNode]);
                using RecordBatch batch = new RecordBatch(arena, root, (long)chunk * chunkRows);
                await writer.WriteAsync(batch, default);
            }

            await writer.CompleteAsync(default);
        }

        Assert.Equal(PutOutcome.Created, await sink.CommitAsync(default));
    }

    [Theory]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, false, 2, 1)]
    [InlineData(false, true, 1, 0)]
    public async Task AnOpenAsksForAHeadOnlyWhenNothingElseSaysTheLength(bool readsSuffix, bool lengthKnown, int requests, int heads)
    {
        // A store that reads suffixes learns the length from the tail's answer; another asks a head
        // first, unless the caller knows the length, as a dataset's entry does.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        long written = await WriteAsync(inner, Key);
        await using CountingObjectStore store = new CountingObjectStore(readsSuffix ? inner : new NoSuffix(inner));
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key, lengthKnown ? written : -1);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct);

        Assert.Equal(Rows, file.RowCount);
        Assert.Equal((requests, heads), (store.Requests, store.CountOf(ObjectOperation.Head)));
        Assert.Equal(requests, store.DependentSteps);
    }

    [Fact]
    public async Task ASourceRefusesAnObjectOfAnotherLengthThanTheOneItWasTold()
    {
        // A dataset's entry records the object's length, and an object of another length under its
        // key is not the one the entry names.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        long written = await WriteAsync(store, Key);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key, written + 10);
        VortexFormatException refused = await Assert.ThrowsAsync<VortexFormatException>(
            async () => await VortexFile.OpenAsync(source, new VortexOpenOptions(), ct));
        Assert.Contains($"{written} bytes where {written + 10} were recorded", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUncommittedSinkWritesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/abandoned"))
        {
            await sink.WriteAsync(new byte[128], ct);
            await sink.FlushAsync(ct);
            Assert.Equal(128, sink.Position);
            Assert.False(sink.IsCommitted);
        }

        Assert.Null(await store.HeadAsync("data/abandoned", ct));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task ASinkHoldsItsBytesInChunksThatGrowAndPutsThemBackInOrder()
    {
        // Pieces of odd sizes across chunks of 64 KiB, 128, 256, 512 and then a mebibyte each: the
        // object is the bytes in the order they were written, read in place or put.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] expected = new byte[(3 << 20) + 12_345];
        new Random(0x5_1_4C).NextBytes(expected);
        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/chunked");
        for (int at = 0, piece = 1; at < expected.Length; at += piece, piece = (piece * 7 % 100_003) + 1)
        {
            await sink.WriteAsync(expected.AsMemory(at, Math.Min(piece, expected.Length - at)), ct);
        }

        Assert.Equal(expected.Length, sink.Position);
        await using (Vorticity.IO.ISegmentSource content = sink.Content())
        {
            using Vorticity.IO.SegmentLease lease = await content.ReadAsync(new Vorticity.IO.SegmentRange(100_000, 900_000), ct);
            Assert.False(lease.IsContiguous);
            Assert.True(System.Buffers.BuffersExtensions.ToArray(lease.Bytes).AsSpan().SequenceEqual(expected.AsSpan(100_000, 900_000)));
        }

        Assert.Equal(PutOutcome.Created, await sink.CommitAsync(ct));
        using ObjectRange stored = await store.GetRangeAsync("data/chunked", 0, expected.Length, ct);
        Assert.True(stored.Contiguous().Span.SequenceEqual(expected));
    }

    [Fact]
    public async Task ASinkPastItsThresholdStreamsTheObjectAndHoldsNothing()
    {
        // Past 64 KiB, what was held goes into the pipe to the store's put, and the rest as it is
        // written: the object is the same bytes, its hash the same, and the sink holds none of them.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] expected = new byte[(3 << 20) + 54_321];
        new Random(0x5_7_EA).NextBytes(expected);
        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/streamed", streamedFrom: 64 << 10);
        for (int at = 0, piece = 1; at < expected.Length; at += piece, piece = (piece * 7 % 100_003) + 1)
        {
            await sink.WriteAsync(expected.AsMemory(at, Math.Min(piece, expected.Length - at)), ct);
            Assert.True(sink.HeldBytes <= 64 << 10, $"{sink.HeldBytes} bytes held at {at}");
        }

        Assert.True(sink.IsStreamed);
        Assert.Throws<InvalidOperationException>(() => sink.Content());
        Assert.Equal(System.IO.Hashing.XxHash128.HashToUInt128(expected), sink.ContentHash);
        Assert.Equal(PutOutcome.Created, await sink.CommitAsync(ct));
        Assert.Equal(expected.Length, sink.StoredLength);
        using ObjectRange stored = await store.GetRangeAsync("data/streamed", 0, expected.Length, ct);
        Assert.True(stored.Contiguous().Span.SequenceEqual(expected));
    }

    [Fact]
    public async Task AStreamedSinkGivenUpLeavesNoObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/given-up", streamedFrom: 64 << 10))
        {
            await sink.WriteAsync(new byte[200_000], ct);
            Assert.True(sink.IsStreamed);
            sink.Discard();
        }

        Assert.Null(await store.HeadAsync("data/given-up", ct));
    }

    [Fact]
    public async Task ASinkOverATakenKeyReportsItRatherThanOverwriting()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync("data/taken", new byte[] { 1, 2, 3 }, ct);

        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/taken");
        await sink.WriteAsync(new byte[] { 9, 9 }, ct);
        Assert.Equal(PutOutcome.Exists, await sink.CommitAsync(ct));

        ObjectHead head = Assert.NotNull(await store.HeadAsync("data/taken", ct));
        Assert.Equal(3, head.Length);
    }

    [Fact]
    public async Task ASinkRefusesToBufferMoreThanItWasGiven()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/huge", maxBytes: 1024);
        await sink.WriteAsync(new byte[1000], ct);
        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(
            async () => await sink.WriteAsync(new byte[100], ct));
        Assert.Contains("DatasetOptions.MaxObjectBytes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceRefusesAnObjectThatChangedUnderIt()
    {
        // A read bound to one object, at the seam: an object is immutable, so a token that changes
        // mid-read means the key was deleted and created again. Carrying on would mix two objects'
        // bytes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using MemoryObjectStore store = new MemoryObjectStore();
        await WriteAsync(store, Key);

        await using ObjectStoreSource source = new ObjectStoreSource(store, Key);
        Assert.True(await source.GetLengthAsync(ct) > 0);
        string? first = source.Token;
        Assert.NotNull(first);

        // The same bytes under the same key, created again: a new object, so a new token.
        using (ObjectRange range = await store.GetRangeAsync(Key, 0, int.MaxValue / 2, ct))
        {
            await store.DeleteAsync(Key, ct);
            await store.PutIfAbsentAsync(Key, range.Memory, ct);
        }

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await source.ReadRangeAsync(0, 64, 1, ct));
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

        // Measures drawn at random, which no encoding shrinks: the file is several times the window
        // an open reads at its end, so a scan reads most of it from the store.
        Random draw = new Random(0x0B1EC7);
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
                    measureValues[row] = draw.NextDouble();
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

    /// <summary>A store that reads no suffix, as Azure's does not, so that the length takes a head.</summary>
    private sealed class NoSuffix(IObjectStore inner) : ForwardingStore(inner)
    {
        public override ObjectStoreCapabilities Capabilities => Inner.Capabilities & ~ObjectStoreCapabilities.ReadsSuffix;
    }
}
