using System;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>The backward bit reader against streams written by a transcription of libzstd's writer.</summary>
public sealed class BitReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(57)]
    [InlineData(64)]
    [InlineData(1000)]
    public void Fields_read_back_in_reverse(int fieldCount)
    {
        var random = new Random(fieldCount);
        var fields = new (uint Value, int Count)[fieldCount];
        var writer = new BitWriter();
        for (int i = 0; i < fieldCount; i++)
        {
            int count = random.Next(32);
            uint value = count == 0 ? 0 : (uint)random.NextInt64(1L << count);
            fields[i] = (value, count);
            writer.Add(value, count);
        }

        byte[] stream = writer.Close();
        var reader = new BackwardBitReader(stream, ZstdError.SequenceBitstream);
        for (int i = fieldCount - 1; i >= 0; i--)
        {
            Assert.Equal(fields[i].Value, reader.ReadBits(fields[i].Count));
            reader.Reload();
        }

        Assert.True(reader.IsEndOfStream);
        Assert.Equal(BitStreamStatus.Completed, reader.Reload());
    }

    [Fact]
    public void A_missing_end_marker_is_refused()
    {
        Assert.Throws<ZstdException>(() => new BackwardBitReader(new byte[] { 0x12, 0x00 }, ZstdError.HuffmanStream));
        Assert.Throws<ZstdException>(() => new BackwardBitReader(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0 }, ZstdError.HuffmanStream));
        Assert.Throws<ZstdException>(() => new BackwardBitReader([], ZstdError.HuffmanStream));
    }

    [Fact]
    public void Reading_past_the_start_overflows()
    {
        var reader = new BackwardBitReader(new byte[] { 0xFF, 0x01 }, ZstdError.SequenceBitstream);
        Assert.Equal(0xFFu, reader.ReadBits(8));
        Assert.True(reader.IsEndOfStream);
        reader.ReadBits(1);
        Assert.False(reader.IsEndOfStream);
        Assert.Equal(BitStreamStatus.Overflow, reader.Reload());
    }

    [Fact]
    public void Bits_past_the_start_peek_as_zeros()
    {
        // One data bit (1) under the marker: peeking eight bits shows it followed by zeros.
        var reader = new BackwardBitReader(new byte[] { 0x03 }, ZstdError.HuffmanStream);
        Assert.Equal(0x80u, reader.PeekBitsFast(8));
    }
}
