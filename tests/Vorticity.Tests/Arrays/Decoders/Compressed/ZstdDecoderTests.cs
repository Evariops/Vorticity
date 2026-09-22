// vortex.zstd. The corpus has 5 files and every one of them is utf8, so the PRIMITIVE path -- and
// with it the whole compact-to-dense scatter -- is untested by the corpus and is the reason this
// file is not redundant. Multi-frame arrays and dictionaries are likewise absent from the corpus:
// the default writer emits neither, both are legal, and `validate` upstream has explicit rules for
// them.
//
// Fixtures are compressed here rather than checked in, because .NET's one-shot TryCompress writes
// the frame content size into the header -- which is exactly what the decoder's
// validate_frame_content_size check reads back.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class ZstdDecoderTests
{
    [Fact]
    public void DecodesUtf8FromTheLengthPrefixedStream()
    {
        // Both sides of the 12-byte inline/reference boundary, plus an empty value.
        string[] expected = ["", "short", "exactly12chr", "a value that is definitely out of line"];
        byte[] stream = ValueStream(expected);
        byte[] frame = Compress(stream);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 4)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 4));

        Assert.Equal(CanonicalKind.VarBinView, node.Kind);
        Assert.Equal(expected, ReadStrings(node, 4));
    }

    [Fact]
    public void NullRowsAreNotStoredAndAreScatteredBackOnDecode()
    {
        // The defining property of this encoding: three rows, two values in the stream.
        byte[] stream = ValueStream(["first", "third"]);
        byte[] frame = Compress(stream);
        byte[] validity = TestBuffers.Bitmap(true, false, true);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 2)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, frame, validity);
        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 3));

        List<string?> values = ReadNullableStrings(harness, node, 3);
        Assert.Equal(["first", null, "third"], values);
    }

    [Fact]
    public void DecodesPrimitiveValues()
    {
        // Not reachable from the corpus: all five of its zstd files are utf8.
        byte[] raw = TestBuffers.Int32(1, -2, 3, -4);
        byte[] frame = Compress(raw);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)raw.Length, 4)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));

        Assert.Equal(CanonicalKind.Primitive, node.Kind);
        Assert.Equal([1, -2, 3, -4], ReadInts(node, 4));
    }

    [Fact]
    public void APrimitiveArrayScattersItsValuesOverTheNullSlots()
    {
        // The compact stream holds 2 values for 4 rows, and the null slots must come back zeroed -
        // upstream's from_values_byte_buffer zeroes them rather than leaving them arbitrary.
        byte[] raw = TestBuffers.Int32(7, 9);
        byte[] frame = Compress(raw);
        byte[] validity = TestBuffers.Bitmap(false, true, false, true);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)raw.Length, 2)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, frame, validity);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(i32, 4));

        Assert.Equal([0, 7, 0, 9], ReadInts(node, 4));
    }

    [Fact]
    public void MultipleFramesConcatenateInOrder()
    {
        byte[] first = ValueStream(["alpha", "beta"]);
        byte[] second = ValueStream(["gamma"]);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(
                0,
                new ZstdFrameMetadata((ulong)first.Length, 2),
                new ZstdFrameMetadata((ulong)second.Length, 1)))
            .WithBuffer(0)
            .WithBuffer(1);

        using DecodeHarness harness = DecodeHarness.Load(root, Compress(first), Compress(second));
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 3));

        Assert.Equal(["alpha", "beta", "gamma"], ReadStrings(node, 3));
    }

    [Fact]
    public void ADictionaryBufferIsUsedForEveryFrame()
    {
        // Buffer 0 is the dictionary when dictionary_size is non-zero; the frames start at 1.
        byte[] dictionaryBytes = Encoding.UTF8.GetBytes(
            "repeated repeated repeated repeated repeated repeated repeated repeated");
        byte[] stream = ValueStream(["repeated repeated", "repeated"]);

        using ZstandardDictionary dictionary = ZstandardDictionary.Create(dictionaryBytes);
        byte[] frame = CompressWith(stream, dictionary);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(
                (uint)dictionaryBytes.Length, new ZstdFrameMetadata((ulong)stream.Length, 2)))
            .WithBuffer(0)
            .WithBuffer(1);

        using DecodeHarness harness = DecodeHarness.Load(root, dictionaryBytes, frame);
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 2));

        Assert.Equal(["repeated repeated", "repeated"], ReadStrings(node, 2));
    }

    [Fact]
    public void AFrameWhoseHeaderDisagreesWithItsMetadataIsRejected()
    {
        // validate_frame_content_size. Without it the decode short-reads and the mismatch surfaces
        // as a garbled value rather than as an error.
        byte[] stream = ValueStream(["value"]);
        byte[] frame = Compress(stream);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length + 8, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(utf8, 1));
    }

    [Fact]
    public void AFrameCountThatDisagreesWithTheBufferCountIsRejected()
    {
        byte[] stream = ValueStream(["value"]);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(
                0,
                new ZstdFrameMetadata((ulong)stream.Length, 1),
                new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, Compress(stream));
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(utf8, 2));
    }

    [Fact]
    public void ADictionarySizeThatDisagreesWithItsBufferIsRejected()
    {
        byte[] stream = ValueStream(["value"]);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(99, new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0)
            .WithBuffer(1);

        using DecodeHarness harness = DecodeHarness.Load(root, [1, 2, 3], Compress(stream));
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(utf8, 1));
    }

    [Fact]
    public void AValueLengthRunningPastTheStreamIsRejected()
    {
        // A length prefix is a class I hint: believed unchecked, it is a read past the end of the
        // decompressed buffer.
        byte[] stream = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(stream, 1000);
        byte[] frame = Compress(stream);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType binary = harness.Types.Binary(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(binary, 1));
    }

    [Fact]
    public void AStreamHoldingFewerValuesThanValidRowsIsRejected()
    {
        byte[] stream = ValueStream(["only one"]);
        byte[] frame = Compress(stream);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(utf8, 3));
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        byte[] stream = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(stream, 2);
        stream[4] = 0xFF;
        stream[5] = 0xFE;
        byte[] frame = Compress(stream);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, frame);
        DType utf8 = harness.Types.Utf8(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(utf8, 1));

        // The same bytes are a perfectly good Binary value.
        using DecodeHarness binaryHarness = DecodeHarness.Load(root, frame);
        DType binary = binaryHarness.Types.Binary(Nullability.NonNullable);
        Assert.Equal(
            CanonicalKind.VarBinView,
            binaryHarness.Node(binaryHarness.DecodeRoot(binary, 1)).Kind);
    }

    [Fact]
    public void AnUnsupportedDTypeIsRejected()
    {
        byte[] stream = ValueStream(["value"]);
        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata((ulong)stream.Length, 1)))
            .WithBuffer(0);

        using DecodeHarness harness = DecodeHarness.Load(root, Compress(stream));
        DType boolean = harness.Types.Bool(Nullability.NonNullable);
        Assert.Throws<VortexFormatException>(() => harness.DecodeRoot(boolean, 1));
    }

    [Fact]
    public void AnAllNullArrayStoresNothingAtAll()
    {
        byte[] empty = Compress([]);
        byte[] validity = TestBuffers.Bitmap(false, false);

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, new ZstdFrameMetadata(0, 0)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.bool").WithBuffer(1));

        using DecodeHarness harness = DecodeHarness.Load(root, empty, validity);
        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        CanonicalNode node = harness.Node(harness.DecodeRoot(utf8, 2));

        Assert.Equal(CanonicalKind.VarBinView, node.Kind);
        Assert.Equal([null, null], ReadNullableStrings(harness, node, 2));
    }

    [Fact]
    public void ARangeOfTextDecodesAsTheWholeArraySliced()
    {
        // Frames of four values over rows with nulls -- one in three, and a run of four -- so a
        // range starts inside a frame, past nulls, or holds no value at all.
        const int Rows = 40;
        bool[] valid = new bool[Rows];
        List<string> stored = [];
        for (int row = 0; row < Rows; row++)
        {
            valid[row] = row % 3 != 1 && row is not (>= 24 and < 28);
            if (valid[row])
            {
                stored.Add(row % 5 == 0 ? $"a value long enough to go out of line {row}" : $"v{row}");
            }
        }

        (TestNode root, byte[][] buffers) = Framed(stored, valuesPerFrame: 4, validity: valid);
        using DecodeHarness harness = DecodeHarness.Load(root, buffers);
        DType utf8 = harness.Types.Utf8(Nullability.Nullable);
        ArrayNode node = harness.Scan.Nodes.Root;
        Assert.True(harness.Scan.Decode.DecodesRange(in node));

        List<string?> whole = ReadNullableStrings(harness, harness.Node(harness.DecodeRoot(utf8, Rows)), Rows);
        foreach ((int start, int count) in new[] { (0, Rows), (0, 1), (1, 5), (7, 9), (13, 1), (24, 4), (20, 20), (39, 1) })
        {
            CanonicalNode range = harness.Node(
                harness.Scan.Decode.DecodeRootRange(in node, utf8, Rows, start, count, keepEncoding: false));
            Assert.Equal(whole.GetRange(start, count), ReadNullableStrings(harness, range, count));
        }
    }

    [Fact]
    public void ARangeOfPrimitivesDecodesAsTheWholeArraySliced()
    {
        const int Rows = 30;
        bool[] valid = new bool[Rows];
        List<int> stored = [];
        for (int row = 0; row < Rows; row++)
        {
            valid[row] = row % 4 != 2;
            if (valid[row])
            {
                stored.Add(row * 1_000 + 7);
            }
        }

        byte[][] frames = new byte[(stored.Count + 2) / 3][];
        List<ZstdFrameMetadata> metadata = [];
        for (int f = 0; f < frames.Length; f++)
        {
            int take = Math.Min(3, stored.Count - (f * 3));
            byte[] raw = new byte[take * sizeof(int)];
            for (int i = 0; i < take; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * sizeof(int)), stored[(f * 3) + i]);
            }

            frames[f] = Compress(raw);
            metadata.Add(new ZstdFrameMetadata((ulong)raw.Length, (ulong)take));
        }

        TestNode root = new TestNode("vortex.zstd").WithMetadata(TestMetadata.Zstd(0, [.. metadata]));
        for (int f = 0; f < frames.Length; f++)
        {
            root = root.WithBuffer(f);
        }

        root = root.WithChild(new TestNode("vortex.bool").WithBuffer(frames.Length));
        using DecodeHarness harness = DecodeHarness.Load(root, [.. frames, TestBuffers.Bitmap(valid)]);
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        ArrayNode node = harness.Scan.Nodes.Root;

        CanonicalNode whole = harness.Node(harness.DecodeRoot(i32, Rows));
        foreach ((int start, int count) in new[] { (0, Rows), (2, 1), (3, 7), (11, 13), (29, 1) })
        {
            CanonicalNode range = harness.Node(
                harness.Scan.Decode.DecodeRootRange(in node, i32, Rows, start, count, keepEncoding: false));
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(harness.IsValid(whole, start + i), harness.IsValid(range, i));
                if (harness.IsValid(range, i))
                {
                    Assert.Equal(ReadInts(whole, Rows)[start + i], ReadInts(range, count)[i]);
                }
            }
        }
    }

    [Fact]
    public void ValuesSpreadOverWholeWordsOfValidEmptyAndMixedRows()
    {
        // A word of valid rows, a word of nulls, then rows mixed, over a length that ends inside a
        // word -- whole, and from an offset that puts every word astride two bytes of the bitmap.
        const int Rows = 300;
        bool[] valid = new bool[Rows];
        List<int> stored = [];
        for (int row = 0; row < Rows; row++)
        {
            valid[row] = row < 64 || (row >= 128 && row % 3 != 0);
            if (valid[row])
            {
                stored.Add((row * 31) + 1);
            }
        }

        // Three frames of a third of the values each, so that a range decodes from some of them.
        byte[][] frames = new byte[3][];
        ZstdFrameMetadata[] metadata = new ZstdFrameMetadata[3];
        int third = (stored.Count + 2) / 3;
        for (int f = 0; f < 3; f++)
        {
            int first = f * third;
            int take = Math.Min(third, stored.Count - first);
            byte[] raw = new byte[take * sizeof(int)];
            for (int i = 0; i < take; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * sizeof(int)), stored[first + i]);
            }

            frames[f] = Compress(raw);
            metadata[f] = new ZstdFrameMetadata((ulong)raw.Length, (ulong)take);
        }

        TestNode root = new TestNode("vortex.zstd")
            .WithMetadata(TestMetadata.Zstd(0, metadata))
            .WithBuffer(0)
            .WithBuffer(1)
            .WithBuffer(2)
            .WithChild(new TestNode("vortex.bool").WithBuffer(3));
        using DecodeHarness harness = DecodeHarness.Load(
            root, frames[0], frames[1], frames[2], TestBuffers.Bitmap(valid));
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.Nullable);
        ArrayNode node = harness.Scan.Nodes.Root;

        foreach ((int start, int count) in new[] { (0, Rows), (5, Rows - 5), (61, 140) })
        {
            CanonicalNode decoded = harness.Node(start == 0
                ? harness.DecodeRoot(i32, Rows)
                : harness.Scan.Decode.DecodeRootRange(in node, i32, Rows, start, count, keepEncoding: false));
            int[] values = ReadInts(decoded, count);
            for (int i = 0; i < count; i++)
            {
                int row = start + i;
                Assert.Equal(valid[row], harness.IsValid(decoded, i));
                Assert.Equal(valid[row] ? (row * 31) + 1 : 0, values[i]);
            }
        }
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>
    /// A zstd node over <paramref name="stored"/> -- the valid rows' values -- cut into frames of
    /// <paramref name="valuesPerFrame"/>, with the validity child last.
    /// </summary>
    private static (TestNode Root, byte[][] Buffers) Framed(
        List<string> stored, int valuesPerFrame, bool[] validity)
    {
        List<byte[]> buffers = [];
        List<ZstdFrameMetadata> metadata = [];
        for (int first = 0; first < stored.Count; first += valuesPerFrame)
        {
            string[] values = stored.GetRange(first, Math.Min(valuesPerFrame, stored.Count - first)).ToArray();
            byte[] stream = ValueStream(values);
            buffers.Add(Compress(stream));
            metadata.Add(new ZstdFrameMetadata((ulong)stream.Length, (ulong)values.Length));
        }

        TestNode root = new TestNode("vortex.zstd").WithMetadata(TestMetadata.Zstd(0, [.. metadata]));
        for (int b = 0; b < buffers.Count; b++)
        {
            root = root.WithBuffer(b);
        }

        root = root.WithChild(new TestNode("vortex.bool").WithBuffer(buffers.Count));
        buffers.Add(TestBuffers.Bitmap(validity));
        return (root, [.. buffers]);
    }

    /// <summary>The `[u32 little-endian length][bytes]` stream the frames hold, uncompressed.</summary>
    private static byte[] ValueStream(params string[] values)
    {
        int total = 0;
        foreach (string value in values)
        {
            total += sizeof(uint) + Encoding.UTF8.GetByteCount(value);
        }

        byte[] stream = new byte[total];
        int offset = 0;
        foreach (string value in values)
        {
            int written = Encoding.UTF8.GetBytes(value, stream.AsSpan(offset + sizeof(uint)));
            BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset), (uint)written);
            offset += sizeof(uint) + written;
        }

        return stream;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        byte[] destination = new byte[(int)ZstandardEncoder.GetMaxCompressedLength(data.Length) + 16];
        Assert.True(ZstandardEncoder.TryCompress(data, destination, out int written));
        return destination.AsSpan(0, written).ToArray();
    }

    private static byte[] CompressWith(ReadOnlySpan<byte> data, ZstandardDictionary dictionary)
    {
        // The trailing int is windowLog2, NOT a compression level, and it must be at least 10.
        const int WindowLog2 = 21;
        byte[] destination = new byte[(int)ZstandardEncoder.GetMaxCompressedLength(data.Length) + 64];
        Assert.True(
            ZstandardEncoder.TryCompress(data, destination, out int written, dictionary, WindowLog2));
        return destination.AsSpan(0, written).ToArray();
    }

    private static string[] ReadStrings(CanonicalNode node, int length)
    {
        string[] values = new string[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = Encoding.UTF8.GetString(ViewValue(node, i));
        }

        return values;
    }

    private static List<string?> ReadNullableStrings(DecodeHarness harness, CanonicalNode node, int length)
    {
        List<string?> values = new List<string?>(length);
        for (int i = 0; i < length; i++)
        {
            values.Add(harness.IsValid(node, i) ? Encoding.UTF8.GetString(ViewValue(node, i)) : null);
        }

        return values;
    }

    /// <summary>Resolves one 16-byte view, inline or by reference, against the node's buffers.</summary>
    private static ReadOnlySpan<byte> ViewValue(CanonicalNode node, int index)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(index * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer(buffer).Span.Slice(offset, size);
    }

    private static int[] ReadInts(CanonicalNode node, int length)
    {
        int[] values = new int[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(node.Values.Span.Slice(i * 4, 4));
        }

        return values;
    }
}
