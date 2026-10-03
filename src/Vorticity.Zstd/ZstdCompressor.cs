using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>
/// Compresses data into Zstandard frames (RFC 8878) in fully managed code.
/// </summary>
/// <remarks>
/// <para>
/// The frames are those libzstd 1.5.7 writes at the same level (<c>ZSTD_compress</c>), byte for byte:
/// the same parameters, match finders, block splits and entropy decisions. The levels whose match
/// finders are not written yet (above 4) cascade down to the strongest that is, as a libzstd built
/// without them does.
/// </para>
/// <para>
/// An instance keeps its tables and buffers from one frame to the next, so that a warm compressor
/// allocates nothing; nor does it clear its tables between frames, whose indices follow each other.
/// It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// <see cref="Compress"/> compresses its whole source into one frame, which declares its size.
/// </para>
/// </remarks>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>The fastest level: libzstd's <c>ZSTD_minCLevel()</c>.</summary>
    public const int MinLevel = CompressionParameters.MinLevel;

    /// <summary>The strongest level: libzstd's <c>ZSTD_maxCLevel()</c>.</summary>
    public const int MaxLevel = CompressionParameters.MaxLevel;

    /// <summary>libzstd's <c>ZSTD_CLEVEL_DEFAULT</c>.</summary>
    public const int DefaultLevel = CompressionParameters.DefaultLevel;

    /// <summary>libzstd's <c>ZSTD_FRAMEHEADERSIZE_MAX</c>.</summary>
    private const int FrameHeaderSizeMax = 18;

    /// <summary>
    /// libzstd's <c>ZSTD_CURRENT_MAX</c> less its <c>ZSTD_INDEXOVERFLOW_MARGIN</c>: the indices
    /// restart, and the tables are cleared, before a frame would reach it.
    /// </summary>
    private const ulong IndexLimit = (3500UL << 20) - (16UL << 20);

    /// <summary>libzstd's <c>ZSTD_WINDOW_START_INDEX</c>: the index of the first byte after a restart.</summary>
    private const uint WindowStartIndex = 2;

    /// <summary>Creates a compressor at the <see cref="DefaultLevel"/>.</summary>
    public ZstdCompressor()
        : this(DefaultLevel)
    {
    }

    /// <summary>Creates a compressor at <paramref name="level"/>.</summary>
    /// <param name="level">From <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the <see cref="DefaultLevel"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public ZstdCompressor(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
        Level = level == 0 ? DefaultLevel : level;
        _blockBuffer = GC.AllocateUninitializedArray<byte>(BlockBufferSize, pinned: true);
        _blockStart = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_blockBuffer));
        _counts = GC.AllocateArray<uint>(HuffmanTable.MaxSymbols, pinned: true);
        _countsStart = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_counts));
    }

    /// <summary>The compression level.</summary>
    public int Level { get; }

    /// <summary>Whether the frames end with a checksum of their content (the low 32 bits of its XXH64).</summary>
    public bool AppendChecksum { get; set; }

    /// <summary>
    /// The largest frame <see cref="Compress"/> writes for <paramref name="sourceLength"/> bytes:
    /// libzstd's <c>ZSTD_compressBound</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The bound would not fit an <see cref="int"/>.</exception>
    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        long bound = sourceLength + ((long)sourceLength >> 8)
            + (sourceLength < FrameFormat.MaxBlockSize ? (FrameFormat.MaxBlockSize - sourceLength) >> 11 : 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, int.MaxValue, nameof(sourceLength));
        return (int)bound;
    }

    /// <summary>Compresses <paramref name="source"/> into one frame at the start of <paramref name="destination"/>.</summary>
    /// <param name="source">The content of the frame.</param>
    /// <param name="destination">Where the frame is written; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="bytesConsumed">The size of the source once it is compressed; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame once it is written; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is written;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when it does not fit, the destination then
    /// holding an unspecified part of it.
    /// </returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        int written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = CompressFrame(src, source.Length, dst, destination.Length);
        }

        if (written < 0)
        {
            return OperationStatus.DestinationTooSmall;
        }

        bytesConsumed = source.Length;
        bytesWritten = written;
        return OperationStatus.Done;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compress</c>: the frame header, the blocks, the checksum.
    /// </summary>
    /// <returns>The size of the frame, or -1 when it does not fit.</returns>
    private int CompressFrame(byte* source, int sourceSize, byte* destination, int capacity)
    {
        CompressionParameters parameters = CompressionParameters.ForFrame(Level, sourceSize);
        BeginFrame(parameters, source, sourceSize);

        byte* header = stackalloc byte[FrameHeaderSizeMax];
        int headerSize = WriteFrameHeader(header, parameters.WindowLog, (ulong)sourceSize, AppendChecksum);
        if (headerSize > capacity)
        {
            return -1;
        }

        Unsafe.CopyBlockUnaligned(destination, header, (uint)headerSize);
        int op = headerSize;
        if (sourceSize == 0)
        {
            // libzstd's ZSTD_writeEpilogue: a last block, raw and empty.
            if (capacity - op < FrameFormat.BlockHeaderSize)
            {
                return -1;
            }

            WriteBlockHeader(destination + op, 1, 0);
            op += FrameFormat.BlockHeaderSize;
        }
        else
        {
            int blocks = CompressBlocks(source, sourceSize, destination + op, capacity - op);
            if (blocks < 0)
            {
                return -1;
            }

            op += blocks;
        }

        if (AppendChecksum)
        {
            if (capacity - op < FrameFormat.ChecksumSize)
            {
                return -1;
            }

            uint checksum = (uint)XxHash64.HashToUInt64(new ReadOnlySpan<byte>(source, sourceSize));
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(destination + op, FrameFormat.ChecksumSize), checksum);
            op += FrameFormat.ChecksumSize;
        }

        return op;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_writeFrameHeader</c> for a frame that declares its content size and names no
    /// dictionary: a single segment when the window covers the whole content.
    /// </summary>
    /// <returns>The size of the header.</returns>
    private static int WriteFrameHeader(byte* header, int windowLog, ulong contentSize, bool checksum)
    {
        ulong windowSize = 1UL << windowLog;
        bool singleSegment = windowSize >= contentSize;
        int contentSizeCode = (contentSize >= 256 ? 1 : 0) + (contentSize >= 65536 + 256 ? 1 : 0) + (contentSize >= 0xFFFFFFFFUL ? 1 : 0);
        int descriptor = ((checksum ? 1 : 0) << 2) | ((singleSegment ? 1 : 0) << 5) | (contentSizeCode << 6);
        Unsafe.WriteUnaligned(header, FrameFormat.Magic);
        header[4] = (byte)descriptor;
        int position = 5;
        if (!singleSegment)
        {
            header[position++] = (byte)((windowLog - FrameFormat.WindowLogMin) << 3);
        }

        switch (contentSizeCode)
        {
            case 0:
                if (singleSegment)
                {
                    header[position++] = (byte)contentSize;
                }

                break;
            case 1:
                Unsafe.WriteUnaligned(header + position, (ushort)(contentSize - 256));
                position += 2;
                break;
            case 2:
                Unsafe.WriteUnaligned(header + position, (uint)contentSize);
                position += 4;
                break;
            default:
                Unsafe.WriteUnaligned(header + position, contentSize);
                position += 8;
                break;
        }

        return position;
    }

    /// <summary>A block header: whether it is the last, its type, and its size.</summary>
    private static void WriteBlockHeader(byte* output, int lastBlock, int typeAndSize)
    {
        uint value = (uint)(lastBlock | typeAndSize);
        output[0] = (byte)value;
        output[1] = (byte)(value >> 8);
        output[2] = (byte)(value >> 16);
    }
}
