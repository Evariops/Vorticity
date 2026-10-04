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
/// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> decodes one whole frame
/// per call, from the start of the source into the start of the destination. When the source ends
/// before the frame does, or the destination cannot hold it, it reports so without consuming or
/// writing anything: call again with the whole frame, or with a larger destination.
/// </para>
/// <para>
/// A dictionary is given to the constructor, which copies it and builds its tables once, or with
/// each frame (<see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/>):
/// one decompressor then serves frames of any dictionary, building a dictionary's tables once for
/// as long as the frames give the same.
/// </para>
/// </remarks>
public sealed partial class ZstdDecompressor
{
    private readonly DecoderDictionary? _dictionary;

    /// <summary>The tables of the dictionaries given with frames, rebuilt in place when the dictionary changes.</summary>
    private DictionaryEntropy? _callEntropy;

    /// <summary>The bytes <see cref="_callEntropy"/> was built from: magic number, identifier, tables and repeat offsets.</summary>
    private byte[] _callEntropyKey = [];

    /// <summary>How many of <see cref="_callEntropyKey"/>'s bytes are its; -1 while <see cref="_callEntropy"/> holds no usable tables.</summary>
    private int _callEntropyLength = -1;

    /// <summary>The length of the dictionary <see cref="_callEntropy"/> was built from, whose content its repeat offsets were checked against.</summary>
    private int _callDictionaryLength;

    /// <summary>
    /// The dictionary given with the frame being decoded, copied with <see cref="DecoderDictionary.Margin"/>
    /// bytes of room after it, as the constructor's is: grown, never shrunk.
    /// </summary>
    private byte[] _callHistory = [];

    /// <summary>
    /// Whether the call fills its destination with the start of the frame's content and stops there:
    /// <see cref="DecompressPrefix"/>'s, which every other call leaves false.
    /// </summary>
    private bool _prefix;

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
            int written;
            int consumed;
            DecoderDictionary? dictionary = _dictionary;
            if (dictionary is null)
            {
                written = DecompressFrame(source, destination, default, null, 0, out consumed);
            }
            else
            {
                written = DecompressFrame(source, destination, dictionary.Content, dictionary.Entropy, dictionary.Id, out consumed);
            }

            bytesConsumed = consumed;
            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e);
        }
    }

    /// <summary>
    /// Decompresses the frame that starts <paramref name="source"/> into the start of
    /// <paramref name="destination"/>, with <paramref name="dictionary"/> in place of the dictionary
    /// the decompressor was created with, if any.
    /// </summary>
    /// <param name="source">A zstd or skippable frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the frame's content is written.</param>
    /// <param name="dictionary">
    /// A dictionary in the zstd format, or any other bytes as raw content; empty for none. It is
    /// copied for the call and not kept, but the tables built from it are, for the next calls that
    /// give the same dictionary: once they are built, a frame costs a copy of its dictionary and
    /// nothing else, and another dictionary rebuilds them without allocating.
    /// </param>
    /// <param name="bytesConsumed">The size of the frame, once it is decoded; otherwise 0.</param>
    /// <param name="bytesWritten">The size of the frame's content, once it is decoded; otherwise 0.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the frame is decoded;
    /// <see cref="OperationStatus.NeedMoreData"/> when the source ends inside the frame;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when the content does not fit;
    /// <see cref="OperationStatus.InvalidData"/> when the frame is not valid zstd, names another
    /// dictionary, or when the dictionary has the zstd format but invalid tables.
    /// </returns>
    public OperationStatus Decompress(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary, out int bytesConsumed, out int bytesWritten)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        try
        {
            ReadOnlySpan<byte> history = UseDictionary(dictionary, out DictionaryEntropy? entropy, out uint id);
            int written = DecompressFrame(source, destination, history, entropy, id, out int consumed);
            bytesConsumed = consumed;
            bytesWritten = written;
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e);
        }
    }

    /// <summary>
    /// Decompresses the start of the frame that starts <paramref name="source"/>: as much of its
    /// content as <paramref name="destination"/> holds, or all of it when the content is shorter,
    /// with <paramref name="dictionary"/> as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/>
    /// takes it. A frame whose declared content fits is decoded and checked whole; otherwise nothing
    /// past the destination's end is decoded, so nothing past it is checked: the rest of its block
    /// and the blocks after it, the content size and the checksum.
    /// </summary>
    /// <remarks>
    /// A frame's sequences run in order, so its start costs what it holds: a value near the start
    /// of a frame is reached without executing the matches that fill the rest of it.
    /// </remarks>
    /// <param name="source">A zstd frame, possibly followed by other data, which is left alone.</param>
    /// <param name="destination">Where the start of the frame's content is written.</param>
    /// <param name="dictionary">A dictionary as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/> takes it; empty for none.</param>
    /// <param name="bytesWritten">The bytes written: the destination's length, unless the content is shorter.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> once the destination is full or the frame decoded; otherwise
    /// what <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, out int, out int)"/> returns.
    /// </returns>
    internal OperationStatus DecompressPrefix(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.IsEmpty)
        {
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }

        if (TryGetFrameContentSize(source, out ulong size) && size <= (ulong)destination.Length)
        {
            return Decompress(source, destination, dictionary, out _, out bytesWritten);
        }

        _prefix = true;
        try
        {
            ReadOnlySpan<byte> history = UseDictionary(dictionary, out DictionaryEntropy? entropy, out uint id);
            bytesWritten = DecompressFrame(source, destination, history, entropy, id, out _);
            LastError = ZstdError.None;
            return OperationStatus.Done;
        }
        catch (ZstdException e)
        {
            return Refuse(e);
        }
        finally
        {
            _prefix = false;
        }
    }

    /// <summary>
    /// Forgets the frame in progress. <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>
    /// decodes whole frames, so there is never one in progress: the method exists for callers written
    /// against a streaming decoder.
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

    /// <summary>The status a refusal reports, which it records.</summary>
    private OperationStatus Refuse(ZstdException e)
    {
        LastError = e.Error;
        return e.Error switch
        {
            ZstdError.Truncated => OperationStatus.NeedMoreData,
            ZstdError.DestinationTooSmall => OperationStatus.DestinationTooSmall,
            _ => OperationStatus.InvalidData,
        };
    }

    /// <summary>
    /// libzstd's <c>ZSTD_decompress_usingDict</c>, without its tables for the same dictionary again:
    /// <paramref name="dictionary"/> made the history of the next frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dictionary is copied, where the constructor's is: with <see cref="DecoderDictionary.Margin"/>
    /// bytes after it, which the fast sequence loops read past the end of a match that ends near its
    /// end, copying it 32 bytes at a time. A dictionary read in place would need those loops to check
    /// for it, and they are sensitive to the least change of their code. The copy costs what the
    /// dictionary weighs: Vortex writers train a hundredth of the data it serves, 100 KiB at most.
    /// </para>
    /// <para>
    /// The entropy tables are a function of the dictionary's first bytes, up to its repeat offsets,
    /// and of its length, which they are checked against: when both are those of the dictionary they
    /// were built from, they are taken as they are.
    /// </para>
    /// </remarks>
    /// <returns>The history: the copy of the dictionary.</returns>
    private ReadOnlySpan<byte> UseDictionary(ReadOnlySpan<byte> dictionary, out DictionaryEntropy? entropy, out uint id)
    {
        entropy = null;
        id = 0;
        if (dictionary.IsEmpty)
        {
            return default;
        }

        if (DictionaryEntropy.IsZstdFormat(dictionary))
        {
            entropy = LoadCallEntropy(dictionary);
            id = entropy.Id;
        }

        if (_callHistory.Length < dictionary.Length + DecoderDictionary.Margin)
        {
            _callHistory = new byte[dictionary.Length + DecoderDictionary.Margin];
        }

        dictionary.CopyTo(_callHistory);
        return _callHistory.AsSpan(0, dictionary.Length);
    }

    /// <summary>
    /// The tables of <paramref name="dictionary"/>, a zstd-format one: the ones built last when it is
    /// the same, else rebuilt in place.
    /// </summary>
    private DictionaryEntropy LoadCallEntropy(ReadOnlySpan<byte> dictionary)
    {
        DictionaryEntropy? entropy = _callEntropy;
        int length = _callEntropyLength;
        if (entropy is not null && length >= 0 && dictionary.Length == _callDictionaryLength
            && dictionary.StartsWith(_callEntropyKey.AsSpan(0, length)))
        {
            return entropy;
        }

        // The sets let go of the tables first: they know a table by its reference, and would take
        // the rebuilt ones for those they hold copies of.
        entropy ??= _callEntropy = new DictionaryEntropy();
        _callEntropyLength = -1;
        _sequenceTables.Forget();
        _pairTables?.Forget();
        try
        {
            length = entropy.Load(dictionary);
        }
        catch (ZstdException)
        {
            Throw.Error(ZstdError.DictionaryCorrupted);
        }

        if (_callEntropyKey.Length < length)
        {
            _callEntropyKey = new byte[length];
        }

        dictionary.Slice(0, length).CopyTo(_callEntropyKey);
        _callEntropyLength = length;
        _callDictionaryLength = dictionary.Length;
        return entropy;
    }

    /// <summary>Decodes one frame; throws <see cref="ZstdException"/> on anything but success.</summary>
    /// <param name="source">The frame.</param>
    /// <param name="destination">Where its content goes.</param>
    /// <param name="history">What precedes the frame: the dictionary's content, if any.</param>
    /// <param name="entropy">The dictionary's tables, if it has them.</param>
    /// <param name="dictionaryId">The identifier of the dictionary; 0 for none, or for raw content.</param>
    /// <param name="consumed">The size of the frame.</param>
    /// <returns>The size of the content.</returns>
    private int DecompressFrame(
        ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> history, DictionaryEntropy? entropy, uint dictionaryId,
        out int consumed)
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

        if (header.DictionaryId != 0 && header.DictionaryId != dictionaryId)
        {
            Throw.Error(ZstdError.DictionaryMismatch);
        }

        if (header.HasContentSize && header.ContentSize > (ulong)destination.Length && !_prefix)
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        BeginFrame(entropy);

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
                        if (!_prefix)
                        {
                            Throw.Error(ZstdError.DestinationTooSmall);
                        }

                        // The destination ends in this block: its start, and the call is done.
                        BulkCopy.Copy(source.Slice(ip, destination.Length - op), destination.Slice(op));
                        return destination.Length;
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
                        if (!_prefix)
                        {
                            Throw.Error(ZstdError.DestinationTooSmall);
                        }

                        destination.Slice(op).Fill(source[ip]);
                        return destination.Length;
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

                    // With the next block compressed too, both at once (see ZstdDecompressor.Pairs.cs).
                    int next = ip + blockSize;
                    if (!lastBlock && !_prefix && _lastBlockSequences >= MinPairSequences && PairsBlocks
                        && TryPeekCompressedBlock(source, next, header.BlockSizeMax, out int nextSize, out bool nextLast)
                        && WorthPairing(source, ip, blockSize, next + FrameFormat.BlockHeaderSize, nextSize))
                    {
                        op += DecodeBlockPair(
                            source, ip, blockSize, next + FrameFormat.BlockHeaderSize, nextSize, destination, op, header.BlockSizeMax, history, out bool paired);
                        if (paired)
                        {
                            next += FrameFormat.BlockHeaderSize + nextSize;
                            lastBlock = nextLast;
                        }
                    }
                    else
                    {
                        op += DecodeCompressedBlock(source, ip, blockSize, destination, op, header.BlockSizeMax, history);
                        if (_prefix && op == destination.Length)
                        {
                            return op;
                        }
                    }

                    ip = next;
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
    /// dictionary's tables and repeat offsets when it has them.
    /// </summary>
    private void BeginFrame(DictionaryEntropy? entropy)
    {
        _rep0 = 1;
        _rep1 = 4;
        _rep2 = 8;
        _literalEntropy = false;
        _sequenceEntropy = false;
        _lastBlockSequences = MinPairSequences;

        // A reference is written only when it changes: each write is a call to the GC's write
        // barrier, and frame after frame with one dictionary the references stay the same.
        HuffmanTable huffman = entropy is not null ? entropy.Huffman : _huffman;
        if (!ReferenceEquals(_currentHuffman, huffman))
        {
            _currentHuffman = huffman;
        }

        if (entropy is not null)
        {
            _literalEntropy = true;
            _sequenceEntropy = true;
            _sequenceTables.BeginFrame(entropy.LiteralLengths, entropy.Offsets, entropy.MatchLengths);
            _rep0 = entropy.Rep0;
            _rep1 = entropy.Rep1;
            _rep2 = entropy.Rep2;
        }
    }
}
