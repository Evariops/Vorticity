// Opening a file costs one or two round trips; these tests count them.
//
// No golden corpus file reaches the second-read branch - the largest is 688 KB and its footer
// segments all sit inside the last 65535 bytes - so the synthetic file below is the only thing
// that exercises it. Round-trip count is the object-storage metric; nothing else measures it.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileRoundTripTests
{
    private static byte[] FileWithFooterOutsideTheTailWindow(DTypeArena arena, int filler) =>
        SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            RowCount = 8193,
            FillerAfterLayout = filler,
            Metadata = [("conformance.note", [1, 2, 3])],
        });

    [Fact]
    public async Task AFooterInsideTheTailWindowCostsOneProbeAndOneRead()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = FileWithFooterOutsideTheTailWindow(arena, filler: 0);
        Assert.True(bytes.Length < VortexFileFormat.InitialReadSize);

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        Assert.Equal(8193, file.RowCount);
        Assert.Equal(1, source.LengthProbes);
        Assert.Equal(1, source.TotalReads);
    }

    [Fact]
    public async Task AFooterOutsideTheTailWindowCostsExactlyOneExtraRead()
    {
        // 70000 bytes of filler between the layout and the footer pushes the layout and dtype
        // segments before `fileLength - 65535`, which is precisely the second-read condition
        // `R < fileLength - buffer.Length`, R being the lowest offset the open must read.
        DTypeArena arena = new DTypeArena();
        byte[] bytes = FileWithFooterOutsideTheTailWindow(arena, filler: 70_000);
        Assert.True(bytes.Length > VortexFileFormat.InitialReadSize);

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        Assert.Equal(8193, file.RowCount);
        Assert.Equal("{ints=i64, strs=utf8?}", ManifestDTypeFormatter.Format(file.DType));
        Assert.Equal(1, source.LengthProbes);
        Assert.Equal(2, source.TotalReads);
        Assert.Equal(2, source.RangeReads);

        // The prepended prefix and the tail are one contiguous buffer afterwards: the segment map
        // still reads, and it points into the combined buffer.
        Assert.Equal(2, file.SegmentSpecs.Length);
        Assert.Equal(1, file.MetadataCount);
        Assert.Equal("conformance.note", file.GetMetadataKey(0));
    }

    [Fact]
    public async Task RaisingTheInitialReadSizeRemovesTheSecondRead()
    {
        // "A caller may raise it, never lower it. Raising it can only reduce round trips."
        DTypeArena arena = new DTypeArena();
        byte[] bytes = FileWithFooterOutsideTheTailWindow(arena, filler: 70_000);

        TestSegmentSource source = new TestSegmentSource(bytes);
        VortexOpenOptions options = new VortexOpenOptions { InitialReadBytes = bytes.Length };
        await using VortexFile file = await VortexFile.OpenAsync(source, options, CancellationToken.None);

        Assert.Equal(8193, file.RowCount);
        Assert.Equal(1, source.TotalReads);
    }

    [Fact]
    public async Task LoweringTheInitialReadSizeIsIgnored()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = FileWithFooterOutsideTheTailWindow(arena, filler: 70_000);

        TestSegmentSource lowered = new TestSegmentSource(bytes);
        await using (await VortexFile.OpenAsync(
            lowered, new VortexOpenOptions { InitialReadBytes = 16 }, CancellationToken.None))
        {
        }

        // Floored at 65535, so it still behaves exactly like the default: one tail read that
        // covers the postscript by construction, plus the one extension.
        Assert.Equal(2, lowered.TotalReads);
    }

    [Fact]
    public async Task ASuppliedFileLengthRemovesTheLengthProbe()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = FileWithFooterOutsideTheTailWindow(arena, filler: 0);

        TestSegmentSource source = new TestSegmentSource(bytes);
        VortexOpenOptions options = new VortexOpenOptions { FileLength = bytes.Length };
        await using VortexFile file = await VortexFile.OpenAsync(source, options, CancellationToken.None);

        Assert.Equal(0, source.LengthProbes);
        Assert.Equal(1, source.TotalReads);
        Assert.Equal(bytes.Length, file.FileLength);
    }

    [Fact]
    public async Task ASuppliedDTypeRemovesTheDTypeSegmentFromTheSecondReadDecision()
    {
        // A supplied DType removes the dtype segment from R, the lowest offset the open must
        // read, so the same file needs one read with it and two without.
        DTypeArena arena = new DTypeArena();
        DType schema = SyntheticVortexFile.SmallSchema(arena);

        // The dtype segment lands before the filler; the layout and footer after it. Without the
        // supplied DType, R is the dtype offset and a second read follows; with it, R is the
        // layout offset, which is inside the window.
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = schema,
            RowCount = 1024,
            FillerBeforeLayout = 70_000,
        });

        TestSegmentSource without = new TestSegmentSource(bytes);
        await using (await VortexFile.OpenAsync(without, VortexOpenOptions.Default, CancellationToken.None))
        {
        }

        Assert.Equal(2, without.TotalReads);

        TestSegmentSource with = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            with, new VortexOpenOptions { DType = schema }, CancellationToken.None);

        Assert.Equal(1, with.TotalReads);
        Assert.Equal(1024, file.RowCount);
        Assert.Same(arena, file.Types);
    }

    [Fact]
    public async Task DisposeReleasesTheTailAndTheSourceAndIsIdempotent()
    {
        DTypeArena arena = new DTypeArena();
        TestSegmentSource source = new TestSegmentSource(FileWithFooterOutsideTheTailWindow(arena, 0));
        VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        Assert.False(source.Disposed);
        Assert.False(file.SegmentSpecs.IsEmpty);

        await file.DisposeAsync();
        Assert.True(source.Disposed);

        // Idempotent, and the tail-backed views are gone rather than dangling.
        await file.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = file.SegmentSpecs.Length;
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await file.ReadMetadataAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task LeaveSourceOpenKeepsTheSourceAlive()
    {
        DTypeArena arena = new DTypeArena();
        TestSegmentSource source = new TestSegmentSource(FileWithFooterOutsideTheTailWindow(arena, 0));
        VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

        await file.DisposeAsync();
        Assert.False(source.Disposed);
    }

    [Fact]
    public async Task AFailedOpenDisposesASourceItOwns()
    {
        TestSegmentSource source = new TestSegmentSource([1, 2, 3, 4]);
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await VortexFile.OpenAsync(source, VortexOpenOptions.Default, CancellationToken.None));
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task AFailedOpenLeavesABorrowedSourceOpen()
    {
        TestSegmentSource source = new TestSegmentSource([1, 2, 3, 4]);
        await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(
            source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None));
        Assert.False(source.Disposed);
    }

    [Fact]
    public async Task RootLayoutBytesParseAsALayoutAndSurviveDisposal()
    {
        DTypeArena arena = new DTypeArena();
        VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(FileWithFooterOutsideTheTailWindow(arena, 0)),
            VortexOpenOptions.Default,
            CancellationToken.None);

        ReadOnlyMemory<byte> layoutBytes = file.RootLayoutBytes;
        await file.DisposeAsync();

        // Copied at open, not borrowed from the tail: the layouts component may hold it.
        int budget = VortexLimits.MaxFlatBufferTables;
        LayoutView layout = LayoutView.Root(layoutBytes.Span, ref budget);
        Assert.Equal(8193ul, layout.RowCount);
        Assert.Equal(0, layout.ChildCount);
        Assert.Equal(1, layout.Segments.Length);
    }

    [Fact]
    public async Task ReadingAMetadataSegmentOutsideTheWindowIssuesOneTargetedRead()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            MetadataFirst = true,
            FillerAfterMetadata = 70_000,
            Metadata = [("early", [7, 7, 7, 7])],
        });

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        int before = source.TotalReads;
        SegmentOwnerScope scope = new SegmentOwnerScope(
            await file.ReadMetadataAsync(0, CancellationToken.None));
        try
        {
            Assert.Equal(4, scope.Owner.Length);
            Assert.Equal(7, scope.Owner.Buffer.Span[0]);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Equal(before + 1, source.TotalReads);
        Assert.Equal(1, source.SegmentReads);
    }

    [Fact]
    public async Task OutOfRangeMetadataIndicesAreCallerErrors()
    {
        DTypeArena arena = new DTypeArena();
        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(FileWithFooterOutsideTheTailWindow(arena, 0)),
            VortexOpenOptions.Default,
            CancellationToken.None);

        Assert.Equal(1, file.MetadataCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetMetadataKey(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetMetadataKey(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetMetadataSegment(1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await file.ReadMetadataAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeTheFirstReadPropagates()
    {
        DTypeArena arena = new DTypeArena();
        TestSegmentSource source = new TestSegmentSource(FileWithFooterOutsideTheTailWindow(arena, 0));
        using CancellationTokenSource cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await VortexFile.OpenAsync(source, VortexOpenOptions.Default, cts.Token));
        Assert.True(source.Disposed);
    }

    private readonly struct SegmentOwnerScope : IDisposable
    {
        internal SegmentOwnerScope(SegmentOwner owner) => Owner = owner;

        internal SegmentOwner Owner { get; }

        public void Dispose() => Owner.Release();
    }
}
