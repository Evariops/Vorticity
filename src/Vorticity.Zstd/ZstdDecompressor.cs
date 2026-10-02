using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>
/// Decompresses Zstandard frames (RFC 8878) in fully managed code.
/// </summary>
/// <remarks>
/// <para>
/// An instance keeps its tables and buffers from one frame to the next, so that a warm decoder
/// allocates nothing. It is not thread-safe: use one per thread.
/// </para>
/// <para>
/// Every block must regenerate at most the frame's <c>Block_Maximum_Size</c>, as RFC 8878 states.
/// libzstd enforces this when it streams and only partly in one shot; Vorticity.Zstd always does.
/// </para>
/// <para>
/// <see cref="Decompress"/> decodes one whole frame per call, from the start of the source into the
/// start of the destination. When the source ends before the frame does, or the destination cannot
/// hold it, it reports so without consuming or writing anything: call again with the whole frame, or
/// with a larger destination.
/// </para>
/// </remarks>
public sealed partial class ZstdDecompressor
{
    private readonly DecoderDictionary? _dictionary;

    /// <summary>Creates a decoder without a dictionary.</summary>
    public ZstdDecompressor()
    {
        _literals = new byte[FrameFormat.MaxBlockSize + LiteralsMargin];
        _huffman = new HuffmanTable();
        _sequenceTables = new SequenceTableSet();
        _currentHuffman = _huffman;
    }

    /// <summary>
    /// Creates a decoder for frames compressed with <paramref name="dictionary"/>: a dictionary in the
    /// zstd format, or any other bytes as raw content. The bytes are copied.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The dictionary has the zstd format but invalid tables.</exception>
    public ZstdDecompressor(ReadOnlySpan<byte> dictionary)
        : this()
    {
        if (!dictionary.IsEmpty)
        {
            _dictionary = DecoderDictionary.Load(dictionary);
        }
    }

    /// <summary>Why the last call refused its frame; <see cref="ZstdError.None"/> after a success.</summary>
    internal ZstdError LastError { get; private set; }

    /// <summary>
    /// Decompresses the frame that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">A zstd or skippable frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the frame's content is written.</param>
    /// <param name="bytesConsumed">The size of the frame, once it is decoded; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame's content, once it is decoded; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is decoded;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source ends inside the frame;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the content does not fit;
    /// <see cref="OperationStatus.InvalidData"/> when the frame is not valid zstd.
    /// </returns>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        try
        {
            int written = DecompressFrame(source, destination, out int consumed);
            bytesConsumed = consumed;
            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            LastError = e.Error;
            return e.Error switch
            {
                ZstdError.Truncated => OperationStatus.NeedMoreData,
                ZstdError.DestinationTooSmall => OperationStatus.DestinationTooSmall,
                _ => OperationStatus.InvalidData,
            };
        }
    }

    /// <summary>
    /// Forgets the frame in progress. <see cref="Decompress"/> decodes whole frames, so there is never
    /// one in progress: the method exists for callers written against a streaming decoder.
    /// </summary>
    public void Reset() => LastError = ZstdError.None;

    /// <summary>
    /// Reads the content size the frame starting <paramref name="source"/> declares in its header.
    /// </summary>
    /// <returns>
    /// Whether the header is complete and valid and declares a size; a skippable frame declares 0.
    /// </returns>
    public static bool TryGetFrameContentSize(ReadOnlySpan<byte> source, out ulong size)
    {
        size = 0;
        if (source.Length < 4)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            return source.Length >= FrameFormat.SkippableHeaderSize;
        }

        if (magic != FrameFormat.Magic || FrameHeader.Parse(source, out FrameHeader header) != ZstdError.None
            || !header.HasContentSize)
        {
            return false;
        }

        size = header.ContentSize;
        return true;
    }

    /// <summary>Decodes one frame; throws <see cref="ZstdException"/> on anything but success.</summary>
    /// <returns>The size of the content.</returns>
    private int DecompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed)
    {
        consumed = 0;
        if (source.Length < 4)
        {
            Throw.Error(FrameHeader.IsMagicPrefix(source) ? ZstdError.Truncated : ZstdError.UnknownMagic);
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & FrameFormat.SkippableMagicMask) == FrameFormat.SkippableMagic)
        {
            consumed = SkipFrame(source);
            return 0;
        }

        if (magic != FrameFormat.Magic)
        {
            Throw.Error(ZstdError.UnknownMagic);
        }

        ZstdError headerError = FrameHeader.Parse(source, out FrameHeader header);
        if (headerError != ZstdError.None)
        {
            Throw.Error(headerError);
        }

        uint expectedDictionary = _dictionary?.Id ?? 0;
        if (header.DictionaryId != 0 && header.DictionaryId != expectedDictionary)
        {
            Throw.Error(ZstdError.DictionaryMismatch);
        }

        if (header.HasContentSize && header.ContentSize > (ulong)destination.Length)
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        BeginFrame();
        ReadOnlySpan<byte> history = _dictionary is null ? default : _dictionary.Content;

        int ip = header.HeaderSize;
        int op = 0;
        while (true)
        {
            if (source.Length - ip < FrameFormat.BlockHeaderSize)
            {
                Throw.Error(ZstdError.Truncated);
            }

            int blockHeader = source[ip] | (source[ip + 1] << 8) | (source[ip + 2] << 16);
            bool lastBlock = (blockHeader & 1) != 0;
            int blockType = (blockHeader >> 1) & 3;
            int blockSize = blockHeader >> 3;
            ip += FrameFormat.BlockHeaderSize;

            switch (blockType)
            {
                case 0: // raw
                    if (blockSize > source.Length - ip)
                    {
                        Throw.Error(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        Throw.Error(ZstdError.BlockTooLarge);
                    }

                    if (blockSize > destination.Length - op)
                    {
                        Throw.Error(ZstdError.DestinationTooSmall);
                    }

                    BulkCopy.Copy(source.Slice(ip, blockSize), destination.Slice(op));
                    op += blockSize;
                    ip += blockSize;
                    break;

                case 1: // RLE: the size is the content's, the block holds one byte
                    if (source.Length - ip < 1)
                    {
                        Throw.Error(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        Throw.Error(ZstdError.BlockTooLarge);
                    }

                    if (blockSize > destination.Length - op)
                    {
                        Throw.Error(ZstdError.DestinationTooSmall);
                    }

                    destination.Slice(op, blockSize).Fill(source[ip]);
                    op += blockSize;
                    ip += 1;
                    break;

                case 2: // compressed
                    if (blockSize > source.Length - ip)
                    {
                        Throw.Error(ZstdError.Truncated);
                    }

                    if (blockSize > header.BlockSizeMax)
                    {
                        Throw.Error(ZstdError.BlockTooLarge);
                    }

                    op += DecodeCompressedBlock(source, ip, blockSize, destination, op, header.BlockSizeMax, history);
                    ip += blockSize;
                    break;

                default:
                    Throw.Error(ZstdError.ReservedBlockType);
                    break;
            }

            if (lastBlock)
            {
                break;
            }
        }

        if (header.HasContentSize && (ulong)op != header.ContentSize)
        {
            Throw.Error(ZstdError.ContentSizeMismatch);
        }

        if (header.HasChecksum)
        {
            if (source.Length - ip < FrameFormat.ChecksumSize)
            {
                Throw.Error(ZstdError.Truncated);
            }

            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(ip));
            uint actual = (uint)XxHash64.HashToUInt64(destination.Slice(0, op));
            if (actual != expected)
            {
                Throw.Error(ZstdError.ChecksumMismatch);
            }

            ip += FrameFormat.ChecksumSize;
        }

        consumed = ip;
        return op;
    }

    /// <summary>libzstd's <c>readSkippableFrameSize</c>.</summary>
    private static int SkipFrame(ReadOnlySpan<byte> source)
    {
        if (source.Length < FrameFormat.SkippableHeaderSize)
        {
            Throw.Error(ZstdError.Truncated);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));
        if (size + (uint)FrameFormat.SkippableHeaderSize < size)
        {
            Throw.Error(ZstdError.SkippableFrameTooLarge);
        }

        ulong total = (ulong)FrameFormat.SkippableHeaderSize + size;
        if (total > (ulong)source.Length)
        {
            Throw.Error(ZstdError.Truncated);
        }

        return (int)total;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_decompressBegin_usingDDict</c>: the state every frame starts from, the
    /// dictionary's when there is one.
    /// </summary>
    private void BeginFrame()
    {
        _rep0 = 1;
        _rep1 = 4;
        _rep2 = 8;
        _literalEntropy = false;
        _sequenceEntropy = false;
        _currentHuffman = _huffman;

        DecoderDictionary? dictionary = _dictionary;
        if (dictionary is not null && dictionary.HasEntropy)
        {
            _literalEntropy = true;
            _sequenceEntropy = true;
            _currentHuffman = dictionary.Huffman!;
            _sequenceTables.BeginFrame(dictionary.LiteralLengths, dictionary.Offsets, dictionary.MatchLengths);
            _rep0 = dictionary.Rep0;
            _rep1 = dictionary.Rep1;
            _rep2 = dictionary.Rep2;
        }
    }
}
