// vortex.varbin and vortex.varbinview: the offsets invariants, the 12/13-byte inline boundary, the
// views-buffer-is-last rule, and the view bounds checks.
using System;
using System.Buffers.Binary;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class VarBinDecoderTests
{
    private static byte[] Offsets32(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }

        return bytes;
    }

    private static BlobNode VarBin(BlobBuilder b, byte[] heap, byte[] offsets, PType offsetsPType)
    {
        int bytesBuffer = b.AddBuffer(heap);
        int offsetsBuffer = b.AddBuffer(offsets);
        return new BlobNode("vortex.varbin")
            .WithMetadata(TestMetadata.VarBin(offsetsPType))
            .WithBuffers(bytesBuffer)
            .WithChildren(new BlobNode("vortex.primitive").WithBuffers(offsetsBuffer));
    }

    private static string ValueAt(ScanContext scan, int index, int row)
    {
        CanonicalNode node = scan.Canonical.GetNode(index);
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= 12)
        {
            return Encoding.UTF8.GetString(view.Slice(4, (int)size));
        }

        uint buffer = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        return Encoding.UTF8.GetString(node.GetDataBuffer((int)buffer).Span.Slice((int)offset, (int)size));
    }

    [Fact]
    public void VarBinCanonicalizesToVarBinView()
    {
        // "abcdefghijkl" is exactly 12 bytes and must inline; "abcdefghijklm" is 13 and must not.
        byte[] heap = Encoding.UTF8.GetBytes("abcdefghijkl" + "abcdefghijklm" + string.Empty + "z");
        byte[] offsets = Offsets32(0, 12, 25, 25, 26);

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = VarBin(b, heap, offsets, PType.I32);

        int index = h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 4);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(CanonicalKind.VarBinView, decoded.Kind);
        Assert.Equal(1, decoded.DataBufferCount);
        Assert.Equal("abcdefghijkl", ValueAt(h.Scan, index, 0));
        Assert.Equal("abcdefghijklm", ValueAt(h.Scan, index, 1));
        Assert.Equal(string.Empty, ValueAt(h.Scan, index, 2));
        Assert.Equal("z", ValueAt(h.Scan, index, 3));

        // 12 inlines, 13 does not.
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(decoded.Views.Span[..4]));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(decoded.Views.Span.Slice(16, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(decoded.Views.Span.Slice(16 + 8, 4)));
    }

    [Fact]
    public void VarBinRejectsANonMonotoneOffsetsArray()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = VarBin(b, Encoding.UTF8.GetBytes("abcdef"), Offsets32(0, 4, 2, 6), PType.I32);

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 3));
    }

    [Fact]
    public void VarBinRejectsAFirstOffsetThatIsNotZero()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = VarBin(b, Encoding.UTF8.GetBytes("abcdef"), Offsets32(1, 4, 6), PType.I32);

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 2));
    }

    [Fact]
    public void VarBinRejectsALastOffsetPastTheHeap()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = VarBin(b, Encoding.UTF8.GetBytes("abcdef"), Offsets32(0, 4, 7), PType.I32);

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 2));
    }

    [Fact]
    public void VarBinRejectsInvalidUtf8ButAcceptsItAsBinary()
    {
        byte[] heap = [0xC3, 0x28, 0x41];
        byte[] offsets = Offsets32(0, 2, 3);

        using (DecodeHarness utf8 = new DecodeHarness())
        {
            BlobBuilder b = new BlobBuilder();
            BlobNode node = VarBin(b, heap, offsets, PType.I32);
            Assert.Throws<VortexFormatException>(
                () => utf8.Decode(b, node, utf8.Types.Utf8(Nullability.NonNullable), 2));
        }

        using DecodeHarness binary = new DecodeHarness();
        BlobBuilder b2 = new BlobBuilder();
        BlobNode node2 = VarBin(b2, heap, offsets, PType.I32);
        int index = binary.Decode(b2, node2, binary.Types.Binary(Nullability.NonNullable), 2);
        Assert.Equal(CanonicalKind.VarBinView, binary.Node(index).Kind);
    }

    [Fact]
    public void VarBinLeavesNullRowsAsEmptyViews()
    {
        // A null row's offsets are still monotone here, but its view must be empty_view() - all
        // sixteen bytes zero - exactly as upstream's validate_and_fix leaves it.
        byte[] heap = Encoding.UTF8.GetBytes("hello world!!");
        byte[] offsets = Offsets32(0, 13, 13);

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int heapBuffer = b.AddBuffer(heap);
        int offsetsBuffer = b.AddBuffer(offsets);
        int bits = b.AddBuffer([0b0000_0001]);

        BlobNode node = new BlobNode("vortex.varbin")
            .WithMetadata(TestMetadata.VarBin(PType.I32))
            .WithBuffers(heapBuffer)
            .WithChildren(
                new BlobNode("vortex.primitive").WithBuffers(offsetsBuffer),
                new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits));

        int index = h.Decode(b, node, h.Types.Utf8(Nullability.Nullable), 2);
        CanonicalNode decoded = h.Node(index);

        Assert.Equal(ValidityKind.Bitmap, decoded.Validity.Kind);
        ReadOnlySpan<byte> nullView = decoded.Views.Span.Slice(16, 16);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0, nullView[i]);
        }
    }

    [Fact]
    public void VarBinRejectsTheWrongChildCount()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.varbin")
            .WithMetadata(TestMetadata.VarBin(PType.I32))
            .WithBuffers(b.AddBuffer(Encoding.UTF8.GetBytes("ab")));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 1));
    }

    // ------------------------------------------------------------------------ vortex.varbinview

    private static byte[] Views(params (int Size, string Inline, int Buffer, int Offset)[] rows)
    {
        byte[] views = new byte[rows.Length * 16];
        for (int i = 0; i < rows.Length; i++)
        {
            Span<byte> view = views.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)rows[i].Size);
            if (rows[i].Size <= 12)
            {
                Encoding.UTF8.GetBytes(rows[i].Inline).CopyTo(view[4..]);
            }
            else
            {
                Encoding.UTF8.GetBytes(rows[i].Inline)[..4].CopyTo(view[4..8]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], (uint)rows[i].Buffer);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)rows[i].Offset);
            }
        }

        return views;
    }

    [Fact]
    public void VarBinViewReadsTheViewsFromTheLastBuffer()
    {
        // Two data buffers then the views. A decoder that reads buffer 0 as the views gets garbage
        // that happens to parse, which is exactly the failure mode the ordering rule prevents.
        byte[] first = Encoding.UTF8.GetBytes("0123456789abcdef");
        byte[] second = Encoding.UTF8.GetBytes("the quick brown fox");
        byte[] views = Views(
            (16, "0123", 0, 0),
            (19, "the ", 1, 0),
            (3, "xyz", 0, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int a = b.AddBuffer(first);
        int c = b.AddBuffer(second);
        int v = b.AddBuffer(views, alignmentExponent: 4);

        BlobNode node = new BlobNode("vortex.varbinview").WithBuffers(a, c, v);
        int index = h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 3);

        Assert.Equal(2, h.Node(index).DataBufferCount);
        Assert.Equal("0123456789abcdef", ValueAt(h.Scan, index, 0));
        Assert.Equal("the quick brown fox", ValueAt(h.Scan, index, 1));
        Assert.Equal("xyz", ValueAt(h.Scan, index, 2));
    }

    [Fact]
    public void VarBinViewAcceptsZeroDataBuffers()
    {
        byte[] views = Views((3, "abc", 0, 0), (0, string.Empty, 0, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(b.AddBuffer(views, alignmentExponent: 4));

        int index = h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 2);
        Assert.Equal(0, h.Node(index).DataBufferCount);
        Assert.Equal("abc", ValueAt(h.Scan, index, 0));
    }

    [Fact]
    public void VarBinViewRejectsAViewNamingAMissingBuffer()
    {
        byte[] views = Views((16, "0123", 3, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, b.AddBuffer(views, alignmentExponent: 4));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 1));
    }

    [Fact]
    public void VarBinViewRejectsAViewRunningPastItsBuffer()
    {
        byte[] views = Views((32, "0123", 0, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, b.AddBuffer(views, alignmentExponent: 4));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 1));
    }

    [Fact]
    public void VarBinViewRejectsNoBuffersAtAll()
    {
        using DecodeHarness h = new DecodeHarness();
        Assert.Throws<VortexFormatException>(() => h.Decode(
            new BlobBuilder(),
            new BlobNode("vortex.varbinview"),
            h.Types.Utf8(Nullability.NonNullable),
            1));
    }

    [Fact]
    public void VarBinViewRejectsAViewsBufferOfTheWrongLength()
    {
        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(b.AddBuffer(new byte[31], alignmentExponent: 4));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 2));
    }

    // Upstream's `validate_view` checks the prefix between the bounds check and the UTF-8 check:
    // `vortex_ensure!(view.prefix == bytes[..4])`.
    // The prefix is a redundant copy of the value's own first four bytes, not a hint, and Arrow
    // consumers use it as a comparison fast path - so a view carrying the wrong four bytes is a
    // malformed file, and carrying it through into the arena is a wrong-answer hazard.
    [Fact]
    public void VarBinViewRejectsAReferenceViewWhosePrefixDoesNotMatch()
    {
        byte[] views = Views((16, "ZZZZ", 0, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, b.AddBuffer(views, alignmentExponent: 4));

        VortexFormatException error = Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 1));
        Assert.Contains("prefix", error.Message, StringComparison.Ordinal);
    }

    // Not gated on the UTF-8 rule: upstream checks the prefix for binary arrays too.
    [Fact]
    public void VarBinViewChecksThePrefixForBinaryToo()
    {
        byte[] views = Views((16, "ZZZZ", 0, 0));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, b.AddBuffer(views, alignmentExponent: 4));

        Assert.Throws<VortexFormatException>(
            () => h.Decode(b, node, h.Types.Binary(Nullability.NonNullable), 1));
    }

    // A view whose offset lands mid-buffer still has to carry the prefix of what it points AT.
    [Fact]
    public void VarBinViewAcceptsAMatchingPrefixAtANonZeroOffset()
    {
        byte[] views = Views((13, "3456", 0, 3));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, b.AddBuffer(views, alignmentExponent: 4));

        int index = h.Decode(b, node, h.Types.Utf8(Nullability.NonNullable), 1);
        Assert.Equal("3456789abcdef", ValueAt(h.Scan, index, 0));
    }

    [Fact]
    public void VarBinViewDoesNotDereferenceNullRows()
    {
        // Row 1's view points nowhere legal, but it is null, so upstream never validates it and
        // neither do we. This is the case a "validate every view" reading gets wrong.
        byte[] views = Views((16, "0123", 0, 0), (99, "junk", 7, 4000));

        using DecodeHarness h = new DecodeHarness();
        BlobBuilder b = new BlobBuilder();
        int data = b.AddBuffer(Encoding.UTF8.GetBytes("0123456789abcdef"));
        int v = b.AddBuffer(views, alignmentExponent: 4);
        int bits = b.AddBuffer([0b0000_0001]);

        BlobNode node = new BlobNode("vortex.varbinview")
            .WithBuffers(data, v)
            .WithChildren(new BlobNode("vortex.bool").WithMetadata(TestMetadata.Bool(0)).WithBuffers(bits));

        int index = h.Decode(b, node, h.Types.Utf8(Nullability.Nullable), 2);
        Assert.Equal("0123456789abcdef", ValueAt(h.Scan, index, 0));
        Assert.Equal(ValidityKind.Bitmap, h.Node(index).Validity.Kind);
    }
}
