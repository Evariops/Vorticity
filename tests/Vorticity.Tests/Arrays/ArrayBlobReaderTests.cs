// Adversarial cases for the blob spine. Every one of them is a legal-looking file that a
// well-formed writer cannot produce, and every one of them must be a VortexFormatException and
// nothing else - never an out-of-bounds read, an unbounded allocation, or a hang.
using System;
using System.Buffers.Binary;
using System.Globalization;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ArrayBlobReaderTests
{
    private static readonly ArrayEncodingId[] Encodings =
    [
        ArrayEncodingId.Primitive,
        ArrayEncodingId.Bool,
        ArrayEncodingId.Struct,
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ASegmentShorterThanTheLengthTrailerIsMalformed(int length)
    {
        Assert.Throws<VortexFormatException>(() => Load(new byte[length]));
    }

    [Fact]
    public void AFourByteSegmentDeclaringAZeroLengthFlatBufferIsMalformed()
    {
        // fbLength == 0 leaves no root uoffset to read.
        byte[] blob = new byte[4];
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void AFlatBufferLongerThanTheSegmentIsMalformed()
    {
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(blob.Length - 4), (uint)blob.Length);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void AFlatBufferLengthNearUInt32MaxDoesNotWrapBackIntoRange()
    {
        // 4 + fbLength computed in 32 bits would wrap to a small positive number here.
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(blob.Length - 4), uint.MaxValue - 2);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void AWellFormedSingleNodeBlobLoads()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 3, 0, 32)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers);

        ArrayNodeArena arena = Load(blob);

        Assert.Equal(1, arena.NodeCount);
        Assert.Equal(0, arena.RootIndex);
        ArrayNode node = arena.Root;
        Assert.Equal(ArrayEncodingId.Primitive, node.Encoding);
        Assert.Equal(0, node.ChildCount);
        Assert.Equal(1, node.BufferCount);
        Assert.Equal(32, node.GetBuffer(0).Length);
        Assert.False(node.HasStats);
        Assert.True(node.Metadata.IsEmpty);
    }

    [Fact]
    public void PaddingIsAccumulatedFromTheBlobStartAndNotFromAFileOffset()
    {
        // Two buffers with padding in front of each: offsets must be 3 and 3+8+5 = 16.
        BufferSpec[] buffers = [new BufferSpec(3, 0, 0, 8), new BufferSpec(5, 0, 0, 4)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0, 1] };
        byte[] blob = BlobBuilder.Build(root, buffers);

        // Stamp recognisable bytes into the two buffer bodies.
        blob[3] = 0xAA;
        blob[16] = 0xBB;

        ArrayNodeArena arena = Load(blob);
        ArrayNode node = arena.Root;
        Assert.Equal(8, node.GetBuffer(0).Length);
        Assert.Equal(4, node.GetBuffer(1).Length);
        Assert.Equal(0xAA, node.GetBuffer(0).Span[0]);
        Assert.Equal(0xBB, node.GetBuffer(1).Span[0]);
    }

    [Fact]
    public void ABufferRunningPastTheRegionIsMalformed()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, 0, 64)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers, regionLength: 63);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void PaddingPlusLengthThatOverflowsAThirtyTwoBitSumIsMalformed()
    {
        // padding is u16 and length is u32; the sum must be formed in 64 bits or a length near
        // uint.MaxValue wraps to a small in-range value.
        BufferSpec[] buffers = [new BufferSpec(ushort.MaxValue, 0, 0, uint.MaxValue - 1)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers, regionLength: 128);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void ABufferIndexPastTheGlobalListIsMalformedAtLoadNotAtUse()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, 0, 8)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0, 1] };
        byte[] blob = BlobBuilder.Build(root, buffers);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(63)]
    [InlineData(255)]
    public void AnAlignmentExponentAboveTheCapIsMalformed(byte exponent)
    {
        BufferSpec[] buffers = [new BufferSpec(0, exponent, 0, 8)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    /// <summary>
    /// A compressed buffer is refused, and this is the whole of our LZ4 story.
    /// </summary>
    /// <remarks>
    /// There is no LZ4 block decoder, for a reason no amount of effort fixes: nothing in
    /// Vortex 0.86.1 reads or writes
    /// <c>Buffer.compression</c> - the only four files in the tree mentioning lz4 are the two
    /// schemas and their generated code - and the schema records neither a framing nor a
    /// decompressed length, so a decoder could only be written by inventing both.
    ///
    /// This test is therefore not a placeholder for a future feature. It pins a decision, and the
    /// decision is the safer one: the reference never inspects the field, so it would read these
    /// bytes AS DATA and hand back silently wrong values.
    /// </remarks>
    [Fact]
    public void ACompressedBufferIsUnsupportedRatherThanSilentlyRawBytes()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, (byte)BufferCompression.LZ4, 8)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers);

        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(() => Load(blob));
        Assert.Equal(VortexComponentKind.Compression, error.Kind);

        // The id as well as the kind: the message always names both, because that pair is what
        // the upstream troubleshooting procedure asks for.
        Assert.Equal("lz4", error.ComponentId);
    }

    /// <summary>
    /// A compression value outside the enum is refused too, and named by its number - there is no
    /// id text to report, and treating "not LZ4" as "not compressed" would read garbage as data.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(200)]
    [InlineData(255)]
    public void AnUnknownBufferCompressionIsUnsupportedAndNamedByItsValue(byte compression)
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, compression, 8)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] blob = BlobBuilder.Build(root, buffers);

        VortexUnsupportedException error =
            Assert.Throws<VortexUnsupportedException>(() => Load(blob));
        Assert.Equal(VortexComponentKind.Compression, error.Kind);
        Assert.Equal(compression.ToString(CultureInfo.InvariantCulture), error.ComponentId);
    }

    [Fact]
    public void AnEncodingSpecIndexPastTheFootersTableIsMalformed()
    {
        // Not an unknown component - there is no id text to defer, so it is a structural error.
        ForgedNode root = new ForgedNode(99);
        byte[] blob = BlobBuilder.Build(root, []);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void AnUnknownEncodingIdIsNotAnErrorAtLoadTime()
    {
        // Parsing an array blob never throws for an unknown array id.
        ForgedNode root = new ForgedNode(0);
        byte[] blob = BlobBuilder.Build(root, []);

        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(blob, 64);
        ArrayBlobReader.Load(arena, owner.Buffer, [ArrayEncodingId.Unknown]);

        Assert.Equal(ArrayEncodingId.Unknown, arena.Root.Encoding);
        Assert.Equal(0, arena.Root.EncodingSpecIndex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void ATreeAtOrBelowTheDepthCapLoads(int depth)
    {
        byte[] blob = BlobBuilder.Build(BlobBuilder.Chain(depth), []);
        ArrayNodeArena arena = Load(blob);
        Assert.Equal(depth, arena.NodeCount);

        // Children are contiguous and the chain is walkable end to end.
        ArrayNode node = arena.Root;
        for (int i = 1; i < depth; i++)
        {
            node = node.GetChild(0);
        }

        Assert.Equal(0, node.ChildCount);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(200)]
    public void ATreeDeeperThanMaxArrayDepthIsRejected(int depth)
    {
        byte[] blob = BlobBuilder.Build(BlobBuilder.Chain(depth), []);
        Assert.Throws<VortexFormatException>(() => Load(blob));
    }

    [Fact]
    public void AChildIndexOutOfRangeIsAFormatErrorAndNotAnIndexOutOfRange()
    {
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        ArrayNodeArena arena = Load(blob);
        Assert.Throws<VortexFormatException>(() => { _ = arena.Root.GetChild(0).Index; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.Root.GetChild(-1).Index; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.Root.GetBuffer(0).Length; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(1).Index; });
        Assert.Throws<VortexFormatException>(() => { _ = arena.GetNode(-1).Index; });
    }

    [Fact]
    public void ASharedChildDagIsStoppedByTheWorkBudgetRatherThanExpanded()
    {
        // Forward-only uoffsets exclude cycles but not SHARING. This buffer is a legal FlatBuffer
        // of a few hundred bytes whose node graph has 2^40 distinct root-to-leaf paths; a reader
        // that walks it as a tree never returns. Depth is 41, well inside MaxArrayDepth.
        byte[] blob = SharedChildDag(levels: 40);
        Assert.True(blob.Length < 4096, "the bomb is small; that is the point");

        VortexFormatException error = Assert.Throws<VortexFormatException>(() => Load(blob));
        Assert.Contains("DAG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedChildDagStillStopsWhenItsNodesAlsoClaimBufferReferences()
    {
        // The same bomb, but every shared node also claims buffer references, so the re-expansion
        // charges the index budget as well as the node budget. Whichever runs out first, the load
        // must stop - and the buffer-index list must not have been grown in the meantime.
        BufferSpec[] buffers = [new BufferSpec(0, 0, 0, 4), new BufferSpec(0, 0, 0, 4)];
        byte[] blob = SharedChildDag(levels: 30, bufferIndices: [0, 1, 0, 1], buffers: buffers);

        VortexFormatException error = Assert.Throws<VortexFormatException>(() => Load(blob));
        Assert.Contains("DAG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservingMoreNodesThanCanBeAddressedIsAFormatErrorAndNotAHang()
    {
        // The doubling that grows the record array must never overflow into a negative capacity:
        // `while (capacity < needed) capacity *= 2` spins forever once it does.
        ArrayNodeArena arena = new ArrayNodeArena();
        Assert.Throws<VortexFormatException>(() => arena.ReserveNodes(int.MaxValue));
        Assert.Throws<VortexFormatException>(() => arena.ReserveNodes(-1));
        Assert.Equal(0, arena.NodeCount);
    }

    [Fact]
    public void ResetThenLoadReusesTheSameArenaWithoutGrowingIt()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, 0, 16)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        root.With(new ForgedNode(1));
        byte[] blob = BlobBuilder.Build(root, buffers);

        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(blob, 64);

        // Warm up so every backing array has reached its final size.
        for (int i = 0; i < 4; i++)
        {
            ArrayBlobReader.Load(arena, owner.Buffer, Encodings);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            ArrayBlobReader.Load(arena, owner.Buffer, Encodings);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, arena.NodeCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void LoadingAfterAFailedLoadLeavesTheArenaUsable()
    {
        ArrayNodeArena arena = new ArrayNodeArena();

        byte[] bad = BlobBuilder.Build(BlobBuilder.Chain(200), []);
        using (PinnedArraySegmentOwner badOwner = PinnedArraySegmentOwner.CopyOf(bad, 64))
        {
            Assert.Throws<VortexFormatException>(
                () => ArrayBlobReader.Load(arena, badOwner.Buffer, Encodings));
        }

        byte[] good = BlobBuilder.Build(new ForgedNode(0), []);
        using PinnedArraySegmentOwner goodOwner = PinnedArraySegmentOwner.CopyOf(good, 64);
        ArrayBlobReader.Load(arena, goodOwner.Buffer, Encodings);

        Assert.Equal(1, arena.NodeCount);
        Assert.Equal(ArrayEncodingId.Primitive, arena.Root.Encoding);
    }

    [Fact]
    public void AFreshArenaHasNoRoot()
    {
        ArrayNodeArena arena = new ArrayNodeArena();
        Assert.Equal(-1, arena.RootIndex);
        Assert.Throws<VortexFormatException>(() => { _ = arena.Root.Index; });
    }

    [Fact]
    public void ResetClearsTheRoot()
    {
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        ArrayNodeArena arena = Load(blob);
        Assert.Equal(0, arena.RootIndex);

        arena.Reset();
        Assert.Equal(-1, arena.RootIndex);
        Assert.Equal(0, arena.NodeCount);
    }

    [Fact]
    public void MetadataIsExposedByteForByte()
    {
        byte[] metadata = [0x08, 0x03, 0x10, 0x2A];
        ForgedNode root = new ForgedNode(0) { Metadata = metadata };
        byte[] blob = BlobBuilder.Build(root, []);

        ArrayNodeArena arena = Load(blob);
        Assert.True(arena.Root.Metadata.SequenceEqual(metadata));
    }

    [Fact]
    public void NonContiguousBufferIndicesAreResolvedPerIndex()
    {
        // Upstream fast-paths a contiguous run and falls back otherwise; a reader that ASSUMES
        // contiguity reads the wrong bytes here and never notices.
        BufferSpec[] buffers =
        [
            new BufferSpec(0, 0, 0, 4),
            new BufferSpec(0, 0, 0, 4),
            new BufferSpec(0, 0, 0, 4),
        ];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [2, 0] };
        byte[] blob = BlobBuilder.Build(root, buffers);
        blob[0] = 1;
        blob[4] = 2;
        blob[8] = 3;

        ArrayNodeArena arena = Load(blob);
        Assert.Equal(3, arena.Root.GetBuffer(0).Span[0]);
        Assert.Equal(1, arena.Root.GetBuffer(1).Span[0]);
    }

    [Fact]
    public void TheInlinedVariantReadsTheSameBuffersFromOffsetZero()
    {
        BufferSpec[] buffers = [new BufferSpec(0, 0, 0, 4)];
        ForgedNode root = new ForgedNode(0) { BufferIndices = [0] };
        byte[] flatBuffer = BlobBuilder.BuildFlatBuffer(root, buffers);

        byte[] region = [7, 8, 9, 10];
        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(region, 64);
        ArrayBlobReader.Load(arena, flatBuffer, owner.Buffer, Encodings);

        Assert.Equal(1, arena.NodeCount);
        Assert.Equal(4, arena.Root.GetBuffer(0).Length);
        Assert.Equal(7, arena.Root.GetBuffer(0).Span[0]);
    }

    [Fact]
    public void TheInlinedVariantRejectsATreeShorterThanARootOffset()
    {
        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(new byte[16], 64);
        Assert.Throws<VortexFormatException>(
            () => ArrayBlobReader.Load(arena, new byte[3], owner.Buffer, Encodings));
    }

    [Fact]
    public void LoadRejectsANullArena()
    {
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(blob, 64);
        Assert.Throws<ArgumentNullException>(
            () => ArrayBlobReader.Load(null!, owner.Buffer, Encodings));
    }

    [Fact]
    public void StatisticsAreReadWithoutBeingDecoded()
    {
        // Every array node in every corpus file carries a stats table, so this path is hot even
        // though nothing in the reader uses a statistic for a correctness decision.
        CorpusBlobs blobs = CorpusBlobs.Load("types/i64_nonnull_r1024");
        ArrayEncodingId[] encodings = new ArrayEncodingId[blobs.ArrayEncodingIds.Length];
        for (int i = 0; i < encodings.Length; i++)
        {
            encodings[i] = EncodingRegistry.ResolveArray(
                System.Text.Encoding.UTF8.GetBytes(blobs.ArrayEncodingIds[i]));
        }

        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner =
            PinnedArraySegmentOwner.CopyOf(blobs.Leaves[0].Segment, 64);
        ArrayBlobReader.Load(arena, owner.Buffer, encodings);

        ArrayNode root = arena.Root;
        Assert.True(root.HasStats);

        ArrayStatsSet stats = root.Stats;
        Assert.False(stats.IsEmpty);

        // The stat bytes must lie inside the arena's FlatBuffer copy and parse as a ScalarValue.
        if (stats.HasMin)
        {
            Assert.NotEqual(0, stats.MinBytes.Length);
        }

        // Absent tri-states must read as absent, not as a defaulted false/0.
        bool sortedKnown = stats.TryGetIsSorted(out bool sorted);
        Assert.True(sortedKnown || !sorted);
    }

    [Fact]
    public void AnAbsentStatsTableYieldsAnEmptySet()
    {
        byte[] blob = BlobBuilder.Build(new ForgedNode(0), []);
        ArrayNodeArena arena = Load(blob);

        Assert.False(arena.Root.HasStats);
        ArrayStatsSet stats = arena.Root.Stats;
        Assert.True(stats.IsEmpty);
        Assert.False(stats.HasMin);
        Assert.True(stats.MinBytes.IsEmpty);
        Assert.False(stats.TryGetNullCount(out ulong nullCount));
        Assert.Equal(0UL, nullCount);
        Assert.Equal(StatPrecision.Inexact, stats.MinPrecision);
    }

    private static ArrayNodeArena Load(byte[] blob)
    {
        ArrayNodeArena arena = new ArrayNodeArena();
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(blob, 64);
        ArrayBlobReader.Load(arena, owner.Buffer, Encodings);
        return arena;
    }

    /// <summary>
    /// Builds an <c>Array</c> FlatBuffer whose node graph is a chain of <paramref name="levels"/>
    /// levels, each level's node pointing at the SAME next-level table twice. Every uoffset points
    /// forward, every table is in bounds, the depth is <c>levels + 1</c> - and a tree walk visits
    /// <c>2^levels</c> nodes.
    /// </summary>
    private static byte[] SharedChildDag(
        int levels,
        ReadOnlySpan<ushort> bufferIndices = default,
        ReadOnlySpan<BufferSpec> buffers = default)
    {
        using Vorticity.Serialization.FlatBuffers.FlatBufferBuilder builder =
            new Vorticity.Serialization.FlatBuffers.FlatBufferBuilder();

        int shared = ArrayWriter.WriteNode(builder, 0, default, default, bufferIndices, 0);
        for (int i = 0; i < levels; i++)
        {
            Span<int> twins = [shared, shared];
            shared = ArrayWriter.WriteNode(builder, 0, default, twins, bufferIndices, 0);
        }

        int array = ArrayWriter.Write(builder, shared, buffers);
        byte[] flatBuffer = builder.FinishToArray(array);

        int regionLength = 0;
        for (int i = 0; i < buffers.Length; i++)
        {
            regionLength += buffers[i].Padding + (int)buffers[i].Length;
        }

        byte[] blob = new byte[regionLength + flatBuffer.Length + 4];
        flatBuffer.CopyTo(blob.AsSpan(regionLength));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(blob.Length - 4), (uint)flatBuffer.Length);
        return blob;
    }
}
