using System;
using System.Buffers.Binary;

namespace Vorticity.Zstd.Internal;

/// <summary>The constants of the frame format.</summary>
internal static class FrameFormat
{
    public const uint Magic = 0xFD2FB528;
    public const uint SkippableMagic = 0x184D2A50;
    public const uint SkippableMagicMask = 0xFFFFFFF0;
    public const uint DictionaryMagic = 0xEC30A437;
    public const int SkippableHeaderSize = 8;
    public const int BlockHeaderSize = 3;
    public const int MaxBlockSize = 128 * 1024;
    public const int WindowLogMin = 10;
    public const int WindowLogMax = 31;
    public const int ChecksumSize = 4;

    /// <summary>Magic and frame header descriptor: what has to be read before the header's size is known.</summary>
    public const int StartingInputLength = 5;
}

/// <summary>What a frame header declares.</summary>
internal struct FrameHeader
{
    public const ulong UnknownContentSize = ulong.MaxValue;

    public int HeaderSize;
    public ulong ContentSize;
    public ulong WindowSize;
    public uint DictionaryId;
    public bool HasChecksum;
    public int BlockSizeMax;

    public readonly bool HasContentSize => ContentSize != UnknownContentSize;

    /// <summary>
    /// libzstd's <c>ZSTD_getFrameHeader</c> for a frame that starts with the zstd magic number.
    /// </summary>
    /// <returns>
    /// <see cref="ZstdError.None"/>, <see cref="ZstdError.Truncated"/> when the source ends inside the
    /// header, or what makes the header invalid.
    /// </returns>
    public static ZstdError Parse(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;
        if (source.Length < FrameFormat.StartingInputLength)
        {
            return ZstdError.Truncated;
        }

        byte descriptor = source[4];
        int dictionaryIdCode = descriptor & 3;
        bool hasChecksum = ((descriptor >> 2) & 1) != 0;
        bool singleSegment = ((descriptor >> 5) & 1) != 0;
        int contentSizeCode = descriptor >> 6;

        int dictionaryIdSize = dictionaryIdCode == 3 ? 4 : dictionaryIdCode;
        int contentSizeSize = contentSizeCode switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };
        int headerSize = FrameFormat.StartingInputLength + (singleSegment ? 0 : 1) + dictionaryIdSize + contentSizeSize;
        if (source.Length < headerSize)
        {
            return ZstdError.Truncated;
        }

        if ((descriptor & 0x08) != 0)
        {
            return ZstdError.ReservedBitSet;
        }

        int position = FrameFormat.StartingInputLength;
        ulong windowSize = 0;
        if (!singleSegment)
        {
            byte windowDescriptor = source[position++];
            int windowLog = (windowDescriptor >> 3) + FrameFormat.WindowLogMin;
            if (windowLog > FrameFormat.WindowLogMax)
            {
                return ZstdError.WindowTooLarge;
            }

            windowSize = 1UL << windowLog;
            windowSize += (windowSize >> 3) * (ulong)(windowDescriptor & 7);
        }

        uint dictionaryId = dictionaryIdCode switch
        {
            0 => 0,
            1 => source[position],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(position)),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(position)),
        };
        position += dictionaryIdSize;

        ulong contentSize = contentSizeCode switch
        {
            0 => singleSegment ? source[position] : UnknownContentSize,
            1 => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(position)) + 256UL,
            2 => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(position)),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(position)),
        };

        if (singleSegment)
        {
            windowSize = contentSize;
        }

        header.HeaderSize = headerSize;
        header.ContentSize = contentSize;
        header.WindowSize = windowSize;
        header.DictionaryId = dictionaryId;
        header.HasChecksum = hasChecksum;
        header.BlockSizeMax = (int)Math.Min(windowSize, FrameFormat.MaxBlockSize);
        return ZstdError.None;
    }

    /// <summary>
    /// Whether the first bytes of <paramref name="source"/>, fewer than four, can still begin a zstd or
    /// a skippable frame: libzstd refuses early what cannot.
    /// </summary>
    public static bool IsMagicPrefix(ReadOnlySpan<byte> source)
    {
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, FrameFormat.Magic);
        source.CopyTo(magic);
        if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == FrameFormat.Magic)
        {
            return true;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(magic, FrameFormat.SkippableMagic);
        source.CopyTo(magic);
        return (BinaryPrimitives.ReadUInt32LittleEndian(magic) & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic;
    }
}
