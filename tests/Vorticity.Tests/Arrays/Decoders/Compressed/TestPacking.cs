// A byte-level FastLanes packer for building bit-packed blobs. Written bit by bit from the layout
// definition in spec/REFERENCE.md, independently of the decoder's kernel: the value at logical
// index `index(row, lane)` occupies bits [row * W, (row + 1) * W) of lane `lane`'s stream, and
// that stream is the words packed[LANES * w + lane] concatenated LSB first.
using System;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

internal static class TestPacking
{
    /// <summary>
    /// Packs <paramref name="values"/> - a whole number of 1024-element blocks - at
    /// <paramref name="bitWidth"/> bits each, as a buffer of <paramref name="elementBits"/>-bit
    /// words.
    /// </summary>
    internal static byte[] Pack(ReadOnlySpan<ulong> values, int bitWidth, int elementBits)
    {
        if (values.Length % FastLanes.BlockSize != 0)
        {
            throw new ArgumentException("Packing works a whole block at a time.", nameof(values));
        }

        int blocks = values.Length / FastLanes.BlockSize;
        int blockBytes = FastLanes.BlockByteLength(bitWidth);
        byte[] packed = new byte[blocks * blockBytes];
        if (bitWidth == 0)
        {
            return packed;
        }

        int lanes = FastLanes.BlockSize / elementBits;
        int wordBytes = elementBits / 8;
        ReadOnlySpan<byte> order = FastLanes.Order;

        for (int block = 0; block < blocks; block++)
        {
            int blockStart = block * blockBytes;
            for (int lane = 0; lane < lanes; lane++)
            {
                for (int row = 0; row < elementBits; row++)
                {
                    int logical = (order[row / 8] * 16) + ((row % 8) * 128) + lane;
                    ulong value = values[(block * FastLanes.BlockSize) + logical];
                    for (int bit = 0; bit < bitWidth; bit++)
                    {
                        if (((value >> bit) & 1) == 0)
                        {
                            continue;
                        }

                        int position = (row * bitWidth) + bit;
                        int word = position / elementBits;
                        int bitInWord = position % elementBits;
                        int byteIndex = blockStart + (((lanes * word) + lane) * wordBytes)
                            + (bitInWord / 8);
                        packed[byteIndex] |= (byte)(1 << (bitInWord % 8));
                    }
                }
            }
        }

        return packed;
    }

    /// <summary>
    /// Values for one or more blocks: a deterministic ramp masked to <paramref name="bitWidth"/>.
    /// </summary>
    internal static ulong[] Ramp(int blocks, int bitWidth)
    {
        ulong[] values = new ulong[blocks * FastLanes.BlockSize];
        if (bitWidth == 0)
        {
            return values;
        }

        ulong mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ulong)(i * 2_654_435_761L) & mask;
        }

        return values;
    }
}
