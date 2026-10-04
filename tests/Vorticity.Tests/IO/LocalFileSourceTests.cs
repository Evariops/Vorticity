using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
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
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
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
    }

    [Fact]
    public async Task ABatchOfManySegmentsBeforeAMappingGivesEachItsBytes()
    {
        // Enough segments that the positional reads run on several threads at once: each slot still
        // gets its own bytes, segments of no bytes and repeats among them, and nothing maps the file.
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
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
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);
        ISegmentReader reader = source;
        await Assert.ThrowsAnyAsync<VortexFormatException>(async () => await reader.ReadAsync(Spec(FileLength - 10, 20), CancellationToken.None));
        source.AnticipateData();
        await Assert.ThrowsAnyAsync<VortexFormatException>(async () => await reader.ReadAsync(Spec(FileLength - 10, 20), CancellationToken.None));
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
        Assert.True(new System.IO.FileInfo(path).Length <= VortexOpenOptions.DefaultInitialReadSize);

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
        LocalFileSource source = LocalFileSource.Open(file.Path_);
        source.AnticipateData();
        SegmentOwner lease = await ((ISegmentReader)source).ReadAsync(Spec(1_024, 2_048), CancellationToken.None);

        await source.DisposeAsync();

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
        await using LocalFileSource source = LocalFileSource.Open(file.Path_);

        Parallel.For(0, 16, _ => source.AnticipateData());

        Assert.True(source.IsMapped);
        using SegmentOwner owner = await ((ISegmentReader)source).ReadAsync(Spec(7, 900), CancellationToken.None);
        Assert.True(owner.Buffer.Span.SequenceEqual(content.AsSpan(7, 900)));
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
