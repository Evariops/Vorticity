// The read budget on one file: a point lookup costs at most a fixed number of requests and bytes,
// the same whatever the file's length.
//
// WHAT IS HELD: the counting source counts rounds, ranges and bytes as a reader asks; a file's
// indexes are described without a request once its directory is read, and preloading reads it at
// the open, from the tail it already holds; and a point lookup -- through a sorted run in fence
// pages, a filter tree, a count, a key cursor, a fragment -- costs the same requests and the same
// bytes on a file of one gibibyte and on the same file of ten, both sparse: nothing on the read path
// depends on the file's length. The SHA-256 the retired sidecar bound by until step 26 read the
// whole file; this is the test that would have said so.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ReadBudgetTests
{
    private const int Rows = 200_000;
    private const int Batch = 8_192;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["k", "s"],
        [Types.Primitive(PType.I64, Nullability.NonNullable), Types.Utf8(Nullability.NonNullable)],
        Nullability.NonNullable);

    /// <summary>Unique and scattered: no zone map prunes a key, the index does.</summary>
    private static long K(int row) => (row * 2_654_435_761L) % 4_294_967_311L;

    private static string S(int row) => "tenant-" + ((row * 31) % 4_099).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private static WritePolicy Policy => WritePolicy.None
        .For("k", IndexPolicy.SortedRuns.WithSegmentEntries(1_024))
        .For("s", IndexPolicy.Bloom());

    [Fact]
    public async Task TheCountingSourceCountsRoundsRangesAndBytes()
    {
        byte[] bytes = new byte[10_000];
        CountingSegmentSource source = new CountingSegmentSource(new MemorySegmentSource(bytes));
        using (SegmentOwner one = await source.ReadAsync(new SegmentSpec(0, 100, 0, 0, 0), default))
        using (SegmentOwner two = await source.ReadRangeAsync(100, 50, 1, default))
        using (SegmentRequestSet set = new SegmentRequestSet(3))
        {
            set.Add(new SegmentSpec(200, 10, 0, 0, 0));
            set.Add(new SegmentSpec(300, 20, 0, 0, 0));
            set.Add(new SegmentSpec(400, 30, 0, 0, 0));
            await source.ReadManyAsync(set, default);
        }

        Assert.Equal((3L, 5L, 210L), (source.Requests, source.Ranges, source.Bytes));
        source.Reset();
        Assert.Equal((0L, 0L, 0L), (source.Requests, source.Ranges, source.Bytes));
        await source.DisposeAsync();
    }

    [Fact]
    public async Task IndexesAreDescribedWithoutARequestAndPreloadedFromTheTail()
    {
        Decoders.EnsureRegistered();
        using Sparse file = await Sparse.WriteAsync(gapBytes: 0);

        // Before the directory is read, there is nothing to describe; after, a list, and no request.
        CountingSegmentSource lazySource = new CountingSegmentSource(MemoryMappedSegmentSource.Open(file.Path));
        await using (VortexFile lazy = await VortexFile.OpenAsync(lazySource, VortexOpenOptions.Default))
        {
            long atOpen = lazySource.Requests;
            Assert.Null(lazy.Indexes);
            IReadOnlyList<VortexIndexInfo> read = await lazy.ReadIndexesAsync();
            Assert.Equal(atOpen, lazySource.Requests);
            Assert.Equal(read, lazy.Indexes);

            // The merged run in fence pages, and the short last block's own run.
            VortexIndexInfo runs = Assert.Single(read, i => i.Kind == IndexKinds.SortedRuns);
            Assert.Equal(("k", VortexIndexLayout.FencePages, (long)Rows, 2), (runs.Column, runs.Layout, runs.Entries, runs.Runs));
            VortexIndexInfo bloom = Assert.Single(read, i => i.Kind == IndexKinds.BloomSbbf);
            Assert.Equal(("s", VortexIndexLayout.FilterTree, 1), (bloom.Column, bloom.Layout, bloom.Runs));
            Assert.All(read, i => Assert.True(i.ListedBytes >= 0 && i.Blocks > 0));
        }

        // Preloaded: the list is there at the open, and the open cost no more.
        CountingSegmentSource preloadedSource = new CountingSegmentSource(MemoryMappedSegmentSource.Open(file.Path));
        await using VortexFile preloaded = await VortexFile.OpenAsync(
            preloadedSource, new VortexOpenOptions { PreloadIndexes = true });
        Assert.NotNull(preloaded.Indexes);
        Assert.Equal(lazySource.Requests, preloadedSource.Requests);

        // A file without indexes describes an empty list.
        await using VortexFile bare = await VortexFile.OpenAsync(Corpus.Path("encodings/primitive"), new VortexOpenOptions { PreloadIndexes = true });
        Assert.NotNull(bare.Indexes);
        Assert.Empty(bare.Indexes!);
    }

    [Fact]
    public async Task APointLookupCostsTheSameOnASparseFileOfOneAndOfTenGibibytes()
    {
        // THE GAPS ARE CHOSEN SO THAT ONLY THE LENGTH DIFFERS. A fence page writes its regions'
        // offsets as varints, so a page is the same size in both files only when those offsets take
        // as many bytes: the index regions come after every gap, between 2^28 and 2^35 in both --
        // five bytes each -- and are small enough to get no gap of their own.
        Decoders.EnsureRegistered();
        using Sparse small = await Sparse.WriteAsync(gapBytes: 48L << 20);
        using Sparse large = await Sparse.WriteAsync(gapBytes: 480L << 20);
        Assert.InRange(new FileInfo(small.Path).Length, 1L << 30, 3L << 30);
        Assert.InRange(new FileInfo(large.Path).Length, 10L << 30, 30L << 30);

        foreach (int row in (int[])[3, 77_777, Rows - 1])
        {
            (long Requests, long Bytes)[] costs = new (long, long)[2];
            for (int at = 0; at < 2; at++)
            {
                Sparse sparse = at == 0 ? small : large;
                costs[at] = await LookupAsync(sparse.Path, row);
            }

            Assert.Equal(costs[0], costs[1]);

            // And it is a lookup: a few chunks' worth, nowhere near the file.
            Assert.InRange(costs[0].Bytes, 1, 4L << 20);
        }

        // The same with the indexes in a fragment, bound by identity: the binding reads nothing.
        (long Requests, long Bytes)[] fragmented = new (long, long)[2];
        for (int at = 0; at < 2; at++)
        {
            string plain = (at == 0 ? small : large).Plain;
            IndexFragment fragment;
            await using (VortexFile unindexed = await VortexFile.OpenAsync(plain))
            {
                fragment = await VortexFileIndexer.BuildFragmentAsync(unindexed, Policy, new RowRange(0, unindexed.RowCount));
            }

            fragmented[at] = await LookupAsync(plain, 77_777, fragment.Bytes);
        }

        Assert.Equal(fragmented[0], fragmented[1]);
        Assert.InRange(fragmented[0].Bytes, 1, 4L << 20);
    }

    /// <summary>
    /// What one lookup of <paramref name="row"/>'s keys costs on the file: the open, a count through
    /// the sorted run, a scan through the filter tree, a key cursor's seek, and the roots' answer.
    /// </summary>
    private static async Task<(long Requests, long Bytes)> LookupAsync(string path, int row, ReadOnlyMemory<byte>? fragment = null)
    {
        CountingSegmentSource source = new CountingSegmentSource(MemoryMappedSegmentSource.Open(path));
        VortexOpenOptions options = new VortexOpenOptions
        {
            Read = fragment is { } bytes ? new VortexReadOptions { IndexFragments = [bytes] } : VortexReadOptions.Default,
        };
        await using VortexFile file = await VortexFile.OpenAsync(source, options);
        if (fragment is not null)
        {
            Assert.True(await file.ReadIndexDirectoryAsync() is not null, string.Join("; ", file.IndexFragmentRefusals));
        }

        VortexExpr key = Expr.Eq(Expr.Field("k"), Expr.Literal(FilterLiteral.From(K(row))));
        Assert.Equal(1, await file.Scan().Where(key).CountAsync());

        VortexExpr tenant = Expr.Eq(Expr.Field("s"), Expr.Literal(FilterLiteral.From(S(row))));
        long matches = 0;
        await foreach (RecordBatch batch in file.Scan().Where(Expr.And(tenant, key)).ExecuteAsync())
        {
            matches += batch.RowCount;
        }

        Assert.Equal(1, matches);
        await using (KeyCursor cursor = await file.Keys("k").WithSource(KeySourceKind.SortedRuns).OpenAsync())
        {
            Assert.True(await cursor.SeekAsync(FilterLiteral.From(K(row)), SeekOp.Exact));
            Assert.Equal(row, cursor.Row);
        }

        Assert.True(await file.MayMatchAsync(tenant));
        return (source.Requests, source.Bytes);
    }

    // ------------------------------------------------------------------------------ the files

    /// <summary>
    /// The same rows written twice: once with a hole after every large write -- a sparse file of the
    /// size asked for -- and once without, for the fragment, which only an unindexed file takes.
    /// </summary>
    private sealed class Sparse : IDisposable
    {
        private Sparse(string path, string plain)
        {
            Path = path;
            Plain = plain;
        }

        internal string Path { get; }

        internal string Plain { get; }

        internal static async Task<Sparse> WriteAsync(long gapBytes)
        {
            string stem = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-budget-{Guid.NewGuid():N}");
            Sparse sparse = new Sparse(stem + ".vortex", stem + "-plain.vortex");
            await WriteOneAsync(sparse.Path, gapBytes, Policy);
            await WriteOneAsync(sparse.Plain, gapBytes, WritePolicy.None);
            return sparse;
        }

        public void Dispose()
        {
            foreach (string path in (string[])[Path, Plain])
            {
                System.IO.File.Delete(path);
            }
        }

        private static async Task WriteOneAsync(string path, long gapBytes, WritePolicy policy)
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Batch,
                DataBlockTargetBytes = null,
                Indexes = policy,
                IndexBudgetPerMille = 1_000_000,
                Identity = Guid.NewGuid(),
            };
            FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using GapSink sink = new GapSink(stream, gapBytes);
            await using (VortexFileWriter writer = VortexFileWriter.Create(sink, Schema, options))
            {
                for (int from = 0; from < Rows; from += Batch)
                {
                    int count = Math.Min(Batch, Rows - from);
                    CanonicalArena arena = new CanonicalArena();
                    try
                    {
                        using RecordBatch batch = new RecordBatch(
                            arena, arena.AddStruct(Schema, count, Validity.NonNullable, [Longs(arena, from, count), Strings(arena, from, count)]), from);
                        await writer.WriteAsync(batch);
                    }
                    finally
                    {
                        arena.Reset();
                    }
                }

                await writer.CompleteAsync();
            }
        }
    }

    /// <summary>
    /// A sink that leaves a hole after every write of 32 KiB or more -- a data chunk, an index
    /// segment -- and none after the small writes of a file's tail, which the open reads in one piece.
    /// The hole is in <see cref="Position"/> before it is on disk, because the writer asks for the
    /// position before it pads and writes.
    /// </summary>
    private sealed class GapSink(FileStream stream, long gap) : ISegmentSink, IAsyncDisposable
    {
        private const int Large = 32 << 10;
        private long _position;
        private long _pending;

        public long Position => _position + _pending;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (_pending > 0)
            {
                stream.Seek(_pending, SeekOrigin.Current);
                _position += _pending;
                _pending = 0;
            }

            await stream.WriteAsync(data, cancellationToken);
            _position += data.Length;
            if (data.Length >= Large)
            {
                _pending = gap;
            }
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => new ValueTask(stream.FlushAsync(cancellationToken));

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private static int Longs(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = K(start + i);
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Strings(CanonicalArena arena, int start, int count)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(S(start + i));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(Schema.GetField(1), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }
}
