using System;
using System.Buffers;
using System.Linq;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>Frame headers, block types, checksums and the statuses of the API.</summary>
public sealed class FrameTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();

    [Theory]
    [InlineData(true, 1, 200)]       // single segment, 1-byte size
    [InlineData(true, 2, 256)]       // 2-byte field: stores size - 256
    [InlineData(true, 2, 300)]
    [InlineData(true, 4, 300)]
    [InlineData(true, 8, 300)]
    [InlineData(false, 2, 300)]
    [InlineData(false, 4, 300)]
    [InlineData(false, 8, 300)]
    public void Content_size_fields(bool singleSegment, int fieldSize, int size)
    {
        byte[] content = Content.AsSpan(0, size).ToArray();
        byte[] frame = new FrameBuilder { ContentSize = (ulong)size, SingleSegment = singleSegment, ContentSizeFieldSize = fieldSize }
            .Raw(content, last: true).Build();
        FrameAssert.DecodesTo(frame, content);
        Assert.True(ZstdDecompressor.TryGetFrameContentSize(frame, out ulong declared));
        Assert.Equal((ulong)size, declared);
    }

    [Fact]
    public void A_frame_without_content_size()
    {
        byte[] frame = new FrameBuilder().Raw(Content, last: true).Build();
        FrameAssert.DecodesTo(frame, Content);
        Assert.False(ZstdDecompressor.TryGetFrameContentSize(frame, out _));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, 7)]
    [InlineData(17, 3)]
    [InlineData(27, 0)]
    [InlineData(31, 7)]
    public void Window_descriptors(int windowLog, int mantissa)
    {
        byte[] frame = new FrameBuilder { WindowLog = windowLog, WindowMantissa = mantissa, ContentSize = (ulong)Content.Length }
            .Raw(Content, last: true).Build();
        FrameAssert.DecodesTo(frame, Content);
    }

    [Fact]
    public void A_window_log_above_31_is_refused()
    {
        byte[] frame = new FrameBuilder { WindowLog = 32, ContentSize = (ulong)Content.Length }.Raw(Content, last: true).Build();
        FrameAssert.Refused(frame, ZstdError.WindowTooLarge);
    }

    [Fact]
    public void The_reserved_bit_is_refused()
    {
        byte[] frame = new FrameBuilder { ReservedBit = true, ContentSize = (ulong)Content.Length }.Raw(Content, last: true).Build();
        FrameAssert.Refused(frame, ZstdError.ReservedBitSet);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_dictionary_id_must_match(int fieldSize)
    {
        byte[] frame = new FrameBuilder { DictionaryId = 7, DictionaryIdSize = fieldSize, ContentSize = (ulong)Content.Length }
            .Raw(Content, last: true).Build();
        FrameAssert.Refused(frame, ZstdError.DictionaryMismatch);
    }

    [Fact]
    public void A_dictionary_id_of_zero_is_no_requirement()
    {
        byte[] frame = new FrameBuilder { DictionaryId = 0, DictionaryIdSize = 4, ContentSize = (ulong)Content.Length }
            .Raw(Content, last: true).Build();
        FrameAssert.DecodesTo(frame, Content);
    }

    [Fact]
    public void Rle_and_raw_blocks_and_an_empty_last_block()
    {
        byte[] expected = [.. Content, .. Enumerable.Repeat((byte)0xAB, 1000), .. Content];
        byte[] frame = new FrameBuilder { ContentSize = (ulong)expected.Length }
            .Raw(Content).Rle(0xAB, 1000).Raw(Content).Raw([], last: true).Build();
        FrameAssert.DecodesTo(frame, expected);
    }

    [Fact]
    public void The_reserved_block_type_is_refused()
    {
        byte[] frame = new FrameBuilder { ContentSize = 4 }.Block(3, 4, [1, 2, 3, 4], last: true).Build();
        FrameAssert.Refused(frame, ZstdError.ReservedBlockType);
    }

    [Fact]
    public void A_content_size_the_blocks_do_not_produce_is_refused()
    {
        byte[] frame = new FrameBuilder { ContentSize = (ulong)Content.Length + 1 }.Raw(Content, last: true).Build();
        FrameAssert.Refused(frame, ZstdError.ContentSizeMismatch);
    }

    [Fact]
    public void A_compressed_block_larger_than_the_window_is_refused()
    {
        // Window 1 KiB: a compressed block may take at most 1 KiB.
        byte[] literals = Enumerable.Repeat((byte)1, 1100).ToArray();
        byte[] frame = new FrameBuilder { WindowLog = 10 }
            .Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.NoSequences()], last: true).Build();
        FrameAssert.Refused(frame, ZstdError.BlockTooLarge);
    }

    [Fact]
    public void Checksums_are_verified()
    {
        byte[] frame = new FrameBuilder { ContentSize = (ulong)Content.Length, ChecksumOf = Content }.Raw(Content, last: true).Build();
        FrameAssert.DecodesTo(frame, Content);

        frame[^1] ^= 1;
        FrameAssert.Refused(frame, ZstdError.ChecksumMismatch);
    }

    [Fact]
    public void Skippable_frames_decode_to_nothing()
    {
        foreach (uint magic in new uint[] { 0x184D2A50, 0x184D2A5F })
        {
            byte[] frame = [(byte)magic, (byte)(magic >> 8), (byte)(magic >> 16), (byte)(magic >> 24), 3, 0, 0, 0, 1, 2, 3, 99];
            var decoder = new ZstdDecompressor();
            Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, new byte[10], out int consumed, out int written));
            Assert.Equal(11, consumed);
            Assert.Equal(0, written);
            Assert.True(ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size));
            Assert.Equal(0UL, size);

            Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(frame.AsSpan(0, 10), new byte[10], out _, out _));
            Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress(frame.AsSpan(0, 7), new byte[10], out _, out _));
        }
    }

    [Fact]
    public void Concatenated_frames_decode_one_per_call()
    {
        byte[] first = new FrameBuilder { ContentSize = 300 }.Raw(Content, last: true).Build();
        byte[] skip = [0x5A, 0x2A, 0x4D, 0x18, 2, 0, 0, 0, 7, 7];
        byte[] second = new FrameBuilder().Rle(5, 10, last: true).Build();
        byte[] stream = [.. first, .. skip, .. second];

        var decoder = new ZstdDecompressor();
        byte[] output = new byte[400];
        int read = 0, total = 0;
        while (read < stream.Length)
        {
            Assert.Equal(OperationStatus.Done, decoder.Decompress(stream.AsSpan(read), output.AsSpan(total), out int consumed, out int written));
            read += consumed;
            total += written;
        }

        Assert.Equal([.. Content, .. Enumerable.Repeat((byte)5, 10)], output.AsSpan(0, total).ToArray());
    }

    [Fact]
    public void Statuses_for_every_truncation()
    {
        byte[] frame = new FrameBuilder { ContentSize = (ulong)Content.Length, ChecksumOf = Content }
            .Rle(1, 10).Raw(Content.AsSpan(10)).Raw([], last: true).Build();
        var decoder = new ZstdDecompressor();
        byte[] output = new byte[Content.Length];
        for (int length = 0; length < frame.Length; length++)
        {
            OperationStatus status = decoder.Decompress(frame.AsSpan(0, length), output, out int consumed, out int written);
            Assert.True(status == OperationStatus.NeedMoreData, $"{length} bytes: {status}");
            Assert.Equal(0, consumed);
            Assert.Equal(0, written);
        }
    }

    [Fact]
    public void Unknown_magic_is_invalid_data()
    {
        var decoder = new ZstdDecompressor();
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress([1, 2, 3, 4, 5, 6, 7, 8, 9], new byte[10], out _, out _));
        Assert.Equal(OperationStatus.InvalidData, decoder.Decompress([0x28, 0xB5, 0x00], new byte[10], out _, out _));
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress([0x28, 0xB5, 0x2F], new byte[10], out _, out _));
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Decompress([], new byte[10], out _, out _));
        Assert.False(ZstdDecompressor.TryGetFrameContentSize([1, 2, 3, 4, 5, 6, 7, 8, 9], out _));
        Assert.False(ZstdDecompressor.TryGetFrameContentSize([0x28, 0xB5, 0x2F, 0xFD], out _));
    }

    [Fact]
    public void An_empty_destination_holds_an_empty_frame()
    {
        byte[] frame = new FrameBuilder { ContentSize = 0, SingleSegment = true }.Raw([], last: true).Build();
        var decoder = new ZstdDecompressor();
        Assert.Equal(OperationStatus.Done, decoder.Decompress(frame, [], out int consumed, out int written));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(0, written);
    }

    [Fact]
    public void A_zstd_dictionary_with_corrupted_tables_is_refused()
    {
        byte[] dictionary = [0x37, 0xA4, 0x30, 0xEC, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF];
        Assert.Throws<System.IO.InvalidDataException>(() => new ZstdDecompressor(dictionary));
    }
}
