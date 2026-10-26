using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The reader of a file opened from a path: positional reads until a scan's plan says it will read
/// data, a mapping of the whole file after, and the same bytes either way.
/// </summary>
public sealed class LocalFileSourceTests
{
    private const int FileLength = 40_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryReadGivesTheFilesBytesPositionallyAndMapped(bool mapped)
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);

        // A reader maps the file for a scan where the session keeps its mappings; without, on
        // Windows, it reads positionally throughout.
        MappedFileCache? kept = mapped ? new MappedFileCache(4) : null;
        await using LocalFileSource source = LocalFileSource.Open(file.Path_, kept);
        if (mapped)
        {
            source.AnticipateData();
        }

        Assert.Equal(mapped, source.IsMapped);
        ISegmentReader reader = source;
        Assert.Equal(FileLength, await reader.GetLengthAsync(CancellationToken.None));

        // One segment at a time, at offsets and alignments a file declares.
        foreach ((ulong offset, uint length, byte exponent) in new (ulong, uint, byte)[] { (0, 1, 0), (64, 4096, 6), (4097, 1023, 0), (32_768, 7_232, 3), (39_999, 1, 0), (1_000, 0, 0) })
        {
            using SegmentOwner owner = await reader.ReadAsync(Spec(offset, length, exponent), CancellationToken.None);
            Assert.True(owner.Buffer.Span.SequenceEqual(content.AsSpan((int)offset, (int)length)));
        }

        // A batch, with a segment of no bytes among them.
        using (SegmentRequestSet set = new SegmentRequestSet())
        {
            int a = set.Add(Spec(128, 256, 6));
            int b = set.Add(Spec(10_000, 20_000, 0));
            int c = set.Add(Spec(500, 0, 0));
            await reader.ReadManyAsync(set, CancellationToken.None);
            Assert.True(set.IsPopulated);
            Assert.True(set.GetBuffer(a).Span.SequenceEqual(content.AsSpan(128, 256)));
            Assert.True(set.GetBuffer(b).Span.SequenceEqual(content.AsSpan(10_000, 20_000)));
            Assert.Equal(0, set.GetBuffer(c).Length);
        }

        // The tail, at an offset its alignment does not hold, and a range past the end, clamped.
        using SegmentOwner tail = await reader.ReadRangeAsync(FileLength - 1_000, 1_000, 64, CancellationToken.None);
        Assert.True(tail.Buffer.Span.SequenceEqual(content.AsSpan(FileLength - 1_000)));
        using SegmentOwner clamped = await reader.ReadRangeAsync(FileLength - 10, 100, 1, CancellationToken.None);
        Assert.True(clamped.Buffer.Span.SequenceEqual(content.AsSpan(FileLength - 10)));

        await source.DisposeAsync();
        kept?.Clear();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABatchOfManySegmentsBeforeAMappingGivesEachItsBytes(bool kept)
    {
        // Enough segments that the positional reads run on several threads at once where the
        // session keeps its mappings, or as coalesced runs where on Windows it keeps none: each slot
        // still gets its own bytes, segments of no bytes and repeats among them, and nothing maps
        // the file.
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache? mappings = kept ? new MappedFileCache(4) : null;
        await using LocalFileSource source = LocalFileSource.Open(file.Path_, mappings);
        ISegmentReader reader = source;
        using (SegmentRequestSet set = new SegmentRequestSet())
        {
            (int Slot, int Offset, int Length)[] expected = new (int, int, int)[100];
            for (int i = 0; i < expected.Length; i++)
            {
                int offset = i * 397 % (FileLength - 600);
                int length = i % 10 == 0 ? 0 : 1 + (i * 37 % 500);
                expected[i] = (set.Add(Spec((ulong)offset, (uint)length, (byte)(i % 4))), offset, length);
            }

            await reader.ReadManyAsync(set, CancellationToken.None);
            Assert.True(set.IsPopulated);
            foreach ((int slot, int offset, int length) in expected)
            {
                Assert.True(set.GetBuffer(slot).Span.SequenceEqual(content.AsSpan(offset, length)), $"{offset}+{length}");
            }
        }

        Assert.False(source.IsMapped);

        // One segment past the end refuses the whole batch, which stays unread.
        using (SegmentRequestSet set = new SegmentRequestSet())
        {
            for (int i = 0; i < 40; i++)
            {
                set.Add(Spec((ulong)(i * 100), 50));
            }

            set.Add(Spec(FileLength - 10, 20));
            await Assert.ThrowsAnyAsync<VortexFormatException>(async () => await reader.ReadManyAsync(set, CancellationToken.None));
            Assert.False(set.IsPopulated);
        }
    }

    [Fact]
    public async Task AReadPastTheFileIsRefusedPositionallyAsMapped()
    {
        using TempFile file = new TempFile(Pattern(FileLength));
        MappedFileCache kept = new MappedFileCache(4);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_, kept);
        ISegmentReader reader = source;
        await Assert.ThrowsAnyAsync<VortexFormatException>(async () => await reader.ReadAsync(Spec(FileLength - 10, 20), CancellationToken.None));
        source.AnticipateData();
        Assert.True(source.IsMapped);
        await Assert.ThrowsAnyAsync<VortexFormatException>(async () => await reader.ReadAsync(Spec(FileLength - 10, 20), CancellationToken.None));
        await source.DisposeAsync();
        kept.Clear();
    }

    [Fact]
    public async Task AnOpenMapsNothingAndAScanMapsTheFile()
    {
        Decoders.EnsureRegistered();

        // A session of its own: the default one may keep this file mapped from another test, and
        // where the platform reads a file's identity from its name an open takes that over.
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexFile file = await session.OpenAsync(Corpus.Path("distributions/high_cardinality_i64_r8193"), cancellationToken: CancellationToken.None);
        LocalFileSource source = Assert.IsType<LocalFileSource>(file.Segments);

        // What the tail and the statistics answer reads nothing more.
        Assert.True(file.RowCount > 0);
        Assert.False(source.IsMapped);

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        Assert.True(source.IsMapped);
    }

    [Fact]
    public async Task AFileTheOpenReadWholeIsScannedFromThatReadAndNeverMapped()
    {
        Decoders.EnsureRegistered();
        string path = Corpus.Path("encodings/bool");
        Assert.True(new System.IO.FileInfo(path).Length <= VortexOpenOptions.DefaultInitialReadBytes);

        // A session of its own, for the reason the test above gives.
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: CancellationToken.None);
        LocalFileSource source = Assert.IsType<LocalFileSource>(file.Source);
        Assert.Same(file, file.Segments);

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        Assert.False(source.IsMapped);
    }

    [Fact]
    public async Task AScanItsZoneMapsRuleOutMapsNothing()
    {
        Decoders.EnsureRegistered();

        // A session of its own, for the reason the test above gives.
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexFile file = await session.OpenAsync(Corpus.Path("containers/zoned_many_zones_nulls"), cancellationToken: CancellationToken.None);
        LocalFileSource source = Assert.IsType<LocalFileSource>(file.Segments);

        VortexExpr none = Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(long.MinValue)));
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(none).ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(0, rows);
        Assert.False(source.IsMapped);
    }

    [Fact]
    public async Task ALeaseOutlivesItsReader()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache kept = new MappedFileCache(4);
        LocalFileSource source = LocalFileSource.Open(file.Path_, kept);
        source.AnticipateData();
        Assert.True(source.IsMapped);
        SegmentOwner lease = await ((ISegmentReader)source).ReadAsync(Spec(1_024, 2_048), CancellationToken.None);

        await source.DisposeAsync();
        kept.Clear();

        using (lease)
        {
            Assert.True(lease.Buffer.Span.SequenceEqual(content.AsSpan(1_024, 2_048)));
        }
    }

    [Fact]
    public async Task ScansThatAskAtOnceShareOneMapping()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache kept = new MappedFileCache(4);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_, kept);

        Parallel.For(0, 16, _ => source.AnticipateData());

        Assert.True(source.IsMapped);
        using (SegmentOwner owner = await ((ISegmentReader)source).ReadAsync(Spec(7, 900), CancellationToken.None))
        {
            Assert.True(owner.Buffer.Span.SequenceEqual(content.AsSpan(7, 900)));
        }

        await source.DisposeAsync();
        kept.Clear();
    }

    [Fact]
    public async Task WithoutAKeptMappingAScanReadsCoalescedRunsOnWindows()
    {
        // Six MiB: a run of small segments a few KiB apart, mostly gaps, which is copied out; a run
        // of adjacent segments, which its segments are views of; and a segment past the 4 MiB a run
        // grows to, read whole.
        const int length = 6 << 20;
        byte[] content = Pattern(length);
        using TempFile file = new TempFile(content);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
        source.AnticipateData();
        Assert.Equal(!OperatingSystem.IsWindows(), source.IsMapped);
        Assert.Equal(source.IsMapped, ((ISegmentReader)source).ReadsInPlace);

        using SegmentRequestSet set = new SegmentRequestSet();
        List<(int Slot, int Offset, int Length)> expected = [];
        for (int i = 0; i < 100; i++)
        {
            int offset = 64 * 1024 + (i * 10 * 1024);
            expected.Add((set.Add(Spec((ulong)offset, 100)), offset, 100));
        }

        for (int i = 0; i < 8; i++)
        {
            int offset = (5 << 18) + (i * 4_096);
            expected.Add((set.Add(Spec((ulong)offset, 4_096, 6)), offset, 4_096));
        }

        int large = 3 << 19;
        Assert.True(length - large > 4 << 20);
        expected.Add((set.Add(Spec((ulong)large, (uint)(length - large))), large, length - large));

        await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);
        Assert.True(set.IsPopulated);
        foreach ((int slot, int offset, int bytes) in expected)
        {
            Assert.True(set.GetBuffer(slot).Span.SequenceEqual(content.AsSpan(offset, bytes)), $"{offset}+{bytes}");
        }
    }

    [Fact]
    public async Task LanesReadingAtOnceReadBesideEachOtherOnWindows()
    {
        // Every word its own offset, so that a read handed another's bytes shows.
        const int length = 32 << 20;
        const int range = 1 << 20;
        byte[] content = new byte[length];
        for (int at = 0; at < length; at += sizeof(long))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(content.AsSpan(at), at);
        }

        using TempFile file = new TempFile(content);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
        source.AnticipateData();
        ISegmentReader reader = source;

        // Rounds of lanes reading a megabyte each at once, until reads that waited on another opened
        // the twin, and some more through it.
        int beside = 0;
        for (int round = 0; round < 40 && beside < 4; round++)
        {
            await Parallel.ForAsync(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (lane, ct) =>
            {
                long offset = ((round * 16L) + lane) * range % length;
                using SegmentOwner owner = await reader.ReadRangeAsync(offset, range, 64, ct);
                Assert.True(owner.Buffer.Span.SequenceEqual(content.AsSpan((int)offset, range)), $"{offset}");
            });

            beside += source.ReadsBeside ? 1 : 0;
        }

        Assert.Equal(OperatingSystem.IsWindows() && Environment.ProcessorCount > 1, source.ReadsBeside);
    }

    [Fact]
    public async Task APositionalReadServesALargeSegmentItTouchesLittleOfFromAMapping()
    {
        // Two MiB: a large segment the decode touches half of and a small one it touches a page of,
        // both copied; then a large one it touches a page of, served from a mapping on Windows,
        // which outlives the reader as a copy does.
        const int length = 2 << 20;
        byte[] content = Pattern(length);
        using TempFile file = new TempFile(content);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
        source.AnticipateData();

        using (SegmentRequestSet copied = new SegmentRequestSet())
        {
            int dense = copied.AddTouched(Spec(0, 512 * 1024), 256 * 1024);
            int small = copied.AddTouched(Spec(1 << 20, 128 * 1024), 4096);
            await ((ISegmentReader)source).ReadManyAsync(copied, CancellationToken.None);

            Assert.True(copied.GetBuffer(dense).Span.SequenceEqual(content.AsSpan(0, 512 * 1024)));
            Assert.True(copied.GetBuffer(small).Span.SequenceEqual(content.AsSpan(1 << 20, 128 * 1024)));
            Assert.False(source.MapsSparseSegments);
        }

        using SegmentRequestSet sparse = new SegmentRequestSet();
        int slot = sparse.AddTouched(Spec(1 << 20, 768 * 1024), 4096);
        await ((ISegmentReader)source).ReadManyAsync(sparse, CancellationToken.None);
        Assert.Equal(OperatingSystem.IsWindows(), source.MapsSparseSegments);

        await source.DisposeAsync();
        Assert.True(sparse.GetBuffer(slot).Span.SequenceEqual(content.AsSpan(1 << 20, 768 * 1024)));
    }

    [Fact]
    public async Task ASessionThatKeepsNoMappingScansWithoutOneOnWindows()
    {
        Decoders.EnsureRegistered();
        await using VortexSession session = VortexSession.Create(options => options.MappedFileCacheCount = 0);
        await using VortexFile file = await session.OpenAsync(Corpus.Path("distributions/high_cardinality_i64_r8193"), cancellationToken: CancellationToken.None);
        LocalFileSource source = Assert.IsType<LocalFileSource>(file.Source);

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        Assert.Equal(!OperatingSystem.IsWindows(), source.IsMapped);
    }

    [Fact]
    public async Task AScanOfManySegmentsSmallerThanAPageMapsTheFileEverywhere()
    {
        // 325 segments of 2 KiB on average: a read per run of them costs more than the pages they
        // share, once the scan reads enough of them to repay the mapping. A narrow filter reads a
        // few, and on Windows reads them positionally.
        Decoders.EnsureRegistered();
        await using VortexSession session = VortexSession.Create(options => options.MappedFileCacheCount = 0);
        string path = Corpus.Path("containers/zoned_many_zones_nulls");

        await using (VortexFile narrow = await session.OpenAsync(path, cancellationToken: CancellationToken.None))
        {
            LocalFileSource source = Assert.IsType<LocalFileSource>(narrow.Source);
            VortexExpr band = Expr.And(
                Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_000_300L))),
                Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_001_800L))));
            long kept = 0;
            await foreach (RecordBatch batch in narrow.ScanBuilder().Where(band).ExecuteAsync())
            {
                kept += batch.RowCount;
            }

            Assert.True(kept > 0);
            Assert.Equal(!OperatingSystem.IsWindows(), source.IsMapped);
        }

        await using VortexFile file = await session.OpenAsync(path, cancellationToken: CancellationToken.None);
        LocalFileSource whole = Assert.IsType<LocalFileSource>(file.Source);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        Assert.True(whole.IsMapped);
        Assert.True(((ISegmentReader)whole).ReadsInPlace);
    }

    [Fact]
    public async Task AnEmptyFileIsNeverMapped()
    {
        using TempFile file = new TempFile([]);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
        source.AnticipateData();

        Assert.False(source.IsMapped);
        Assert.Equal(0, await ((ISegmentReader)source).GetLengthAsync(CancellationToken.None));
    }
}
