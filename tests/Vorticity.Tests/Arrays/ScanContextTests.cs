// Arena ownership, the validity rule and lazy resolution at the
// dispatch site.
//
// End-to-end DecodeValidity coverage - decoding a real vortex.bool or vortex.constant validity
// child - is left to the decoder tests, which run over real corpus blobs. What is tested here is
// every branch that does not dispatch, the bitmap collapse itself, and the fact that a node whose
// encoding this build cannot decode fails at USE with a named component.
using System;
using Vorticity;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ScanContextTests
{
    // Slot 2 is the stand-in for "an id this build does not decode". Every real array id has a
    // decoder, so it is a FORGED one from a namespace no edition will define, which is also the
    // case a reader most needs to get right: a file from the future. The point of the slot: the
    // table resolves every id at open, and an unknown one is fatal only where it is USED.

    private static readonly string[] Ids = ["vortex.primitive", "vortex.bool", "vortex.acme.future_codec"];

    [Fact]
    public void ADetachedContextOwnsEveryArenaAndRefusesToInventAFile()
    {
        using ScanContext scan = new ScanContext(Ids);

        Assert.False(scan.HasFile);
        Assert.Throws<InvalidOperationException>(() => scan.File);
        Assert.NotNull(scan.Options);
        Assert.NotNull(scan.Types);
        Assert.NotNull(scan.Scalars);
        Assert.NotNull(scan.Nodes);
        Assert.NotNull(scan.Canonical);
        Assert.NotNull(scan.Segments);
        Assert.NotNull(scan.Decode);
        Assert.Same(scan, scan.Decode.Scan);
        Assert.Same(scan.Nodes, scan.Decode.Nodes);
        Assert.Same(scan.Canonical, scan.Decode.Canonical);
        Assert.Same(scan.Types, scan.Decode.Types);
        Assert.Same(scan.Scalars, scan.Decode.Scalars);
    }

    [Fact]
    public void TheEncodingTableIsResolvedOnceAndAddressableByTheWireIndex()
    {
        using ScanContext scan = new ScanContext(Ids);

        Assert.Equal(3, scan.ArrayEncodingCount);
        Assert.Equal(ArrayEncodingId.Primitive, scan.ArrayEncodings[0]);
        Assert.Equal(ArrayEncodingId.Bool, scan.ArrayEncodings[1]);
        Assert.Equal(ArrayEncodingId.Unknown, scan.ArrayEncodings[2]);

        Assert.Equal("vortex.primitive", scan.GetArrayEncodingIdText(0));
        Assert.Equal("vortex.acme.future_codec", scan.GetArrayEncodingIdText(2));

        // An index the footer never declared must not be an index-out-of-range on a throw path.
        Assert.Contains("99", scan.GetArrayEncodingIdText(99), StringComparison.Ordinal);
        Assert.Contains("-1", scan.GetArrayEncodingIdText(-1), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyEncodingTableIsLegal()
    {
        using ScanContext scan = new ScanContext([]);
        Assert.Equal(0, scan.ArrayEncodingCount);
        Assert.True(scan.ArrayEncodings.IsEmpty);
    }

    [Fact]
    public void ANullEncodingIdIsACallerError()
    {
        Assert.Throws<ArgumentNullException>(() => new ScanContext([null!]));
        Assert.Throws<ArgumentNullException>(() => new ScanContext((Vorticity.File.VortexFile)null!));
    }

    [Fact]
    public void ResetBatchClearsEveryArenaAndIsIdempotent()
    {
        using ScanContext scan = new ScanContext(Ids);

        scan.Canonical.AddNull(scan.Types.Null(Nullability.Nullable), 4);
        scan.Scalars.Int64(7);
        LoadOneNode(scan);

        Assert.Equal(1, scan.Canonical.NodeCount);
        Assert.Equal(1, scan.Nodes.NodeCount);
        Assert.True(scan.Scalars.NodeCount > 0);

        scan.ResetBatch();

        Assert.Equal(0, scan.Canonical.NodeCount);
        Assert.Equal(0, scan.Nodes.NodeCount);
        Assert.Equal(-1, scan.Nodes.RootIndex);
        Assert.Equal(0, scan.Scalars.NodeCount);

        scan.ResetBatch();
        Assert.Equal(0, scan.Canonical.NodeCount);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        ScanContext scan = new ScanContext(Ids);
        scan.Dispose();
        scan.Dispose();
    }

    [Fact]
    public void DecodingANodeThisBuildCannotDecodeFailsAtUseWithTheIdAndTheKind()
    {
        // Lazy resolution: the blob parsed fine (spec index 2 -> a forged id -> Unknown); the throw
        // happens here, at the dispatch site, and names the component.
        using ScanContext scan = new ScanContext(Ids);
        LoadOneNode(scan, encoding: 2);

        DType dtype = scan.Types.Primitive(PType.I64, Nullability.NonNullable);
        VortexUnsupportedException error = Assert.Throws<VortexUnsupportedException>(
            () => scan.Decode.Decode(scan.Nodes.Root, dtype, 8));

        Assert.Equal("vortex.acme.future_codec", error.ComponentId);
        Assert.Equal(VortexComponentKind.Array, error.Kind);
    }

    [Fact]
    public void DecodingRejectsANegativeLengthAndADefaultDType()
    {
        using ScanContext scan = new ScanContext(Ids);
        LoadOneNode(scan);

        DType dtype = scan.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => scan.Decode.Decode(scan.Nodes.Root, dtype, -1));
        Assert.Throws<ArgumentException>(() => scan.Decode.Decode(scan.Nodes.Root, default, 8));
    }

    [Fact]
    public void DecodeChildBoundsChecksBeforeItDispatches()
    {
        using ScanContext scan = new ScanContext(Ids);
        LoadOneNode(scan);

        DType dtype = scan.Types.Primitive(PType.I64, Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => scan.Decode.DecodeChild(scan.Nodes.Root, 0, dtype, 8));
        Assert.Throws<VortexFormatException>(() => scan.Decode.DecodeChild(scan.Nodes.Root, -1, dtype, 8));
    }

    // ----------------------------------------------------------------------------- DecodeValidity

    [Theory]
    [InlineData(Nullability.NonNullable, ValidityKind.NonNullable)]
    [InlineData(Nullability.Nullable, ValidityKind.AllValid)]
    public void AnAbsentValidityChildFollowsTheInheritedNullability(
        Nullability nullability,
        ValidityKind expected)
    {
        using ScanContext scan = new ScanContext(Ids);
        LoadOneNode(scan);

        Validity validity = scan.Decode.DecodeValidity(scan.Nodes.Root, 0, nullability, 1024);
        Assert.Equal(expected, validity.Kind);
        Assert.True(validity.IsAllValid);
        Assert.Equal(-1, validity.CanonicalNodeIndex);
    }

    [Fact]
    public void AChildCountThatIsNeitherTheBaseNorOneMoreIsMalformed()
    {
        using ScanContext scan = new ScanContext(Ids);
        LoadOneNode(scan);

        // The node has 0 children; asking for validity at index 2 admits only 2 or 3.
        Assert.Throws<VortexFormatException>(
            () => scan.Decode.DecodeValidity(scan.Nodes.Root, 2, Nullability.Nullable, 8));
    }

    [Fact]
    public void AValidityChildThisBuildCannotDecodeFailsAtUse()
    {
        using ScanContext scan = new ScanContext(Ids);

        ForgedNode root = new ForgedNode(0);
        root.With(new ForgedNode(2));
        Load(scan, BlobBuilder.Build(root, []));

        Assert.Throws<VortexUnsupportedException>(
            () => scan.Decode.DecodeValidity(scan.Nodes.Root, 0, Nullability.Nullable, 8));
    }

    [Fact]
    public void AnUndefinedNullabilityIsMalformed()
    {
        Assert.Throws<VortexFormatException>(() => Validity.FromNullability((Nullability)7));
    }

    // ------------------------------------------------------------------- the collapse itself

    [Theory]
    [InlineData(0, 8, new byte[] { 0x00 }, ValidityBitmapShape.AllClear)]
    [InlineData(0, 8, new byte[] { 0xFF }, ValidityBitmapShape.AllSet)]
    [InlineData(0, 8, new byte[] { 0x01 }, ValidityBitmapShape.Mixed)]
    [InlineData(0, 1, new byte[] { 0x01 }, ValidityBitmapShape.AllSet)]
    [InlineData(0, 1, new byte[] { 0xFE }, ValidityBitmapShape.AllClear)]
    [InlineData(3, 1, new byte[] { 0x08 }, ValidityBitmapShape.AllSet)]
    [InlineData(3, 1, new byte[] { 0xF7 }, ValidityBitmapShape.AllClear)]
    [InlineData(7, 2, new byte[] { 0x80, 0x01 }, ValidityBitmapShape.AllSet)]
    [InlineData(7, 2, new byte[] { 0x7F, 0xFE }, ValidityBitmapShape.AllClear)]
    [InlineData(7, 2, new byte[] { 0x80, 0xFE }, ValidityBitmapShape.Mixed)]
    [InlineData(5, 11, new byte[] { 0xE0, 0xFF, 0x00 }, ValidityBitmapShape.AllSet)]
    [InlineData(5, 11, new byte[] { 0xFF, 0xFF, 0xFF }, ValidityBitmapShape.AllSet)]
    [InlineData(5, 11, new byte[] { 0x1F, 0x00, 0xFF }, ValidityBitmapShape.AllClear)]
    internal void TheBitmapCollapseIgnoresBitsOutsideTheRange(
        int bitOffset,
        int length,
        byte[] bits,
        ValidityBitmapShape expected)
    {
        Assert.Equal(expected, ArrayDecodeContext.ClassifyValidityBits(bits, bitOffset, length));
    }

    [Fact]
    public void TheBitmapCollapseRejectsABufferTooShortForItsRange()
    {
        Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.ClassifyValidityBits([0xFF], 1, 8));
        Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.ClassifyValidityBits([], 0, 1));
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public void TheBitmapCollapseIsExactAtTheBlockBoundaries(int length)
    {
        for (int bitOffset = 0; bitOffset < 8; bitOffset++)
        {
            byte[] bits = new byte[(bitOffset + length + 7) / 8];

            bits.AsSpan().Clear();
            Assert.Equal(
                ValidityBitmapShape.AllClear,
                ArrayDecodeContext.ClassifyValidityBits(bits, bitOffset, length));

            bits.AsSpan().Fill(0xFF);
            Assert.Equal(
                ValidityBitmapShape.AllSet,
                ArrayDecodeContext.ClassifyValidityBits(bits, bitOffset, length));

            // Clear exactly the LAST in-range bit: the shape must become Mixed, and it must not
            // become Mixed for a bit one past the end.
            int last = bitOffset + length - 1;
            bits[last >> 3] &= (byte)~(1 << (last & 7));
            Assert.Equal(
                ValidityBitmapShape.Mixed,
                ArrayDecodeContext.ClassifyValidityBits(bits, bitOffset, length));
        }
    }

    // ------------------------------------------------------------------- validation helpers

    [Fact]
    public void RequireChildCountNamesTheEncoding()
    {
        ArrayDecodeContext.RequireChildCount(2, 2, "vortex.dict");
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.RequireChildCount(1, 2, "vortex.dict"));
        Assert.Contains("vortex.dict", error.Message, StringComparison.Ordinal);

        ArrayDecodeContext.RequireChildCount(2, 1, 3, "fastlanes.bitpacked");
        Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.RequireChildCount(4, 1, 3, "fastlanes.bitpacked"));
        Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.RequireChildCount(0, 1, 3, "fastlanes.bitpacked"));
    }

    [Fact]
    public void RequireBufferCountNamesTheEncoding()
    {
        ArrayDecodeContext.RequireBufferCount(1, 1, "vortex.constant");
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.RequireBufferCount(0, 1, "vortex.constant"));
        Assert.Contains("vortex.constant", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData((ulong)int.MaxValue)]
    public void CheckedLengthAcceptsWhatFits(ulong value)
    {
        Assert.Equal((int)value, ArrayDecodeContext.CheckedLength(value, "row count"));
    }

    [Theory]
    [InlineData((ulong)int.MaxValue + 1)]
    [InlineData(ulong.MaxValue)]
    public void CheckedLengthRejectsAWireValueThatWouldSizeAnAllocation(ulong value)
    {
        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => ArrayDecodeContext.CheckedLength(value, "row count"));
        Assert.Contains("row count", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(8192, 8, 65536)]
    [InlineData(int.MaxValue, 1, int.MaxValue)]
    public void CheckedMultiplyAcceptsWhatFits(int a, int b, int expected)
    {
        Assert.Equal(expected, ArrayDecodeContext.CheckedMultiply(a, b, "values"));
    }

    [Theory]
    [InlineData(int.MaxValue, 2)]
    [InlineData(65536, 65536)]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    public void CheckedMultiplyRejectsAnOverflowOrANegative(int a, int b)
    {
        Assert.Throws<VortexFormatException>(() => ArrayDecodeContext.CheckedMultiply(a, b, "values"));
    }

    [Fact]
    public void NewCanonicalGoesThroughTheArena()
    {
        using ScanContext scan = new ScanContext(Ids);
        int index = scan.Decode.NewCanonical(
            CanonicalKind.Null, scan.Types.Null(Nullability.Nullable), 3, Validity.AllInvalid);

        Assert.Equal(0, index);
        Assert.Equal(CanonicalKind.Null, scan.Canonical.GetNode(index).Kind);
    }

    [Fact]
    public void ValidityEqualityAndHashingAgree()
    {
        Assert.Equal(Validity.AllValid, Validity.AllValid);
        Assert.NotEqual(Validity.AllValid, Validity.NonNullable);
        Assert.NotEqual(Validity.AllValid, Validity.AllInvalid);
        Assert.True(Validity.Bitmap(3) == Validity.Bitmap(3));
        Assert.True(Validity.Bitmap(3) != Validity.Bitmap(4));
        Assert.Equal(Validity.Bitmap(3).GetHashCode(), Validity.Bitmap(3).GetHashCode());
        Assert.Equal(3, Validity.Bitmap(3).CanonicalNodeIndex);
        Assert.Equal(-1, Validity.AllInvalid.CanonicalNodeIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => Validity.Bitmap(-1));
        Assert.Equal("Bitmap(#3)", Validity.Bitmap(3).ToString());
        Assert.Equal("AllInvalid", Validity.AllInvalid.ToString());
        Assert.False(Validity.AllValid.Equals("AllValid"));
    }

    private static void LoadOneNode(ScanContext scan, ushort encoding = 0)
    {
        BufferSpec[] buffers = [new BufferSpec(0, 3, 0, 64)];
        ForgedNode root = new ForgedNode(encoding) { BufferIndices = [0] };
        Load(scan, BlobBuilder.Build(root, buffers));
    }

    private static void Load(ScanContext scan, byte[] blob)
    {
        // The owner lives as long as the test method, which outlives every use of the arena here.
        PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(blob, 64);
        scan.Decode.LoadBlob(owner.Buffer);
    }
}
