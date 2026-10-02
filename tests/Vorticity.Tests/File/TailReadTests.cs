using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.File;

/// <summary>
/// What an open reads of a file's tail: the window the format sizes, or first what its source asks
/// for, and then the window when that missed the postscript, or the rest of the footer.
/// </summary>
public sealed class TailReadTests
{
    // 68 KiB: longer than the window, so that a source's preference applies.
    private const string Entry = "distributions/high_cardinality_i64_r8193";

    [Theory]
    [InlineData(4, 2)]
    [InlineData(16, 2)]
    [InlineData(200, 2)]
    [InlineData(16 * 1024, 1)]
    [InlineData(65_535, 1)]
    public async Task AnOpenReadsAgainWhatItsFirstReadMissed(int tailReadSize, int reads)
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] bytes = global::System.IO.File.ReadAllBytes(Corpus.Path(Entry));
        Assert.True(bytes.Length > VortexOpenOptions.DefaultInitialReadSize);

        TailSized source = new TailSized(new MemorySegmentSource(bytes), tailReadSize);
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default, ct);
        Assert.Equal(reads, source.RangeReads);
        Assert.Equal(8_193, file.RowCount);

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(ct))
        {
            rows += batch.RowCount;
        }

        Assert.Equal(8_193, rows);
    }

    [Fact]
    public async Task ACallerThatRaisedTheWindowReadsItWhateverTheSourcePrefers()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] bytes = global::System.IO.File.ReadAllBytes(Corpus.Path(Entry));
        TailSized source = new TailSized(new MemorySegmentSource(bytes), 16);

        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default with { InitialReadSize = 1 << 20 }, ct);
        Assert.Equal(1, source.RangeReads);
        Assert.Equal(bytes.Length, source.RangeBytes);
    }

    /// <summary>A source that asks an open to read <paramref name="tailReadSize"/> bytes first, and counts its range reads.</summary>
    private sealed class TailSized(ISegmentReader inner, int tailReadSize) : ISegmentReader
    {
        internal int RangeReads { get; private set; }

        internal long RangeBytes { get; private set; }

        public int TailReadSize => tailReadSize;

        public bool ReadsInPlace => inner.ReadsInPlace;

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) => inner.ReadAsync(spec, cancellationToken);

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken) => inner.ReadManyAsync(requests, cancellationToken);

        public async ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
        {
            SegmentOwner owner = await inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
            RangeReads++;
            RangeBytes += owner.Length;
            return owner;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
