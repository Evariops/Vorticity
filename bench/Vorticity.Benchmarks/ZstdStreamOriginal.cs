using System;
using System.Buffers.Binary;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks;

/// <summary>
/// A frozen copy of the two passes <c>ZstdPlan</c> made over a string column before it compressed
/// it: the stream's length, then the stream itself, each valid value behind its <c>u32</c> length,
/// cut where a block of rows ends. Kept as the baseline of <see cref="ZstdStreamBenchmarks"/>.
/// </summary>
internal static class ZstdStreamOriginal
{
    private const int FrameFields = 3;

    internal static long StreamBytes(CanonicalArena arena, CanonicalNode node, out int valueCount)
    {
        int rows = node.Length;
        long streamBytes = 0;
        valueCount = 0;
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        ViewValues strings = new ViewValues(node);
        for (int i = 0; i < rows; i++)
        {
            if (!valid.IsValid(i))
            {
                continue;
            }

            valueCount++;
            streamBytes += sizeof(uint) + strings.At(i).Length;
            if (streamBytes > int.MaxValue)
            {
                return streamBytes;
            }
        }

        return streamBytes;
    }

    internal static void Lay(CanonicalArena arena, CanonicalNode node, byte[] stream, int[] frames, int blocks, int frameRows)
    {
        int rows = node.Length;
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        ViewValues strings = new ViewValues(node);
        int offset = 0;
        int values = 0;
        for (int block = 0; block < blocks; block++)
        {
            int end = (int)Math.Min((long)(block + 1) * frameRows, rows);
            for (int i = (int)Math.Min((long)block * frameRows, rows); i < end; i++)
            {
                if (!valid.IsValid(i))
                {
                    continue;
                }

                ReadOnlySpan<byte> value = strings.At(i);
                BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset, sizeof(uint)), (uint)value.Length);
                offset += sizeof(uint);
                value.CopyTo(stream.AsSpan(offset));
                offset += value.Length;
                values++;
            }

            frames[(block * FrameFields) + 1] = offset;
            frames[(block * FrameFields) + 2] = values;
        }
    }
}
