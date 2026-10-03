using System;
using System.Buffers.Binary;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// A dictionary, digested once: its content, and for the zstd format its entropy tables and repeat
/// offsets, which every frame decoded with it starts from.
/// </summary>
/// <remarks>
/// As libzstd's <c>ZSTD_DDict</c>, the history a frame can reach back into is the whole buffer given,
/// header and tables included: the content proper is its tail, so a valid offset lands on the same
/// bytes either way.
/// </remarks>
internal sealed class DecoderDictionary
{
    /// <summary>
    /// Readable bytes after the content: the fast sequence loop copies a match out of the dictionary
    /// 16 bytes at a time, and the last copy of one that ends near its end reads past it.
    /// </summary>
    public const int Margin = 32;

    /// <summary>The content, then <see cref="Margin"/> bytes nothing reads as content.</summary>
    private readonly byte[] _buffer;

    private DecoderDictionary(ReadOnlySpan<byte> content)
    {
        _buffer = new byte[content.Length + Margin];
        content.CopyTo(_buffer);
    }

    /// <summary>The bytes before the first byte of every frame, <see cref="Margin"/> readable bytes after them.</summary>
    public ReadOnlySpan<byte> Content => _buffer.AsSpan(0, _buffer.Length - Margin);

    /// <summary>The identifier a frame names it by; 0 for raw content.</summary>
    public uint Id { get; private set; }

    /// <summary>Whether the dictionary carries entropy tables (the zstd format).</summary>
    public bool HasEntropy { get; private set; }

    public HuffmanTable? Huffman { get; private set; }
    public SeqTable? LiteralLengths { get; private set; }
    public SeqTable? Offsets { get; private set; }
    public SeqTable? MatchLengths { get; private set; }
    public uint Rep0 { get; private set; }
    public uint Rep1 { get; private set; }
    public uint Rep2 { get; private set; }

    /// <summary>
    /// libzstd's <c>ZSTD_createDDict</c> in automatic mode: the zstd format when the buffer starts with
    /// its magic number, raw content otherwise.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    public static DecoderDictionary Load(ReadOnlySpan<byte> buffer)
    {
        var dictionary = new DecoderDictionary(buffer);
        if (buffer.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != FrameFormat.DictionaryMagic)
        {
            return dictionary;
        }

        dictionary.Id = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        try
        {
            dictionary.LoadEntropy(buffer);
        }
        catch (ZstdException)
        {
            Throw.Dictionary("its entropy tables are corrupted");
        }

        return dictionary;
    }

    /// <summary>libzstd's <c>ZSTD_loadDEntropy</c>.</summary>
    private void LoadEntropy(ReadOnlySpan<byte> buffer)
    {
        const ZstdError error = ZstdError.FseTable;
        if (buffer.Length <= 8)
        {
            Throw.Error(error);
        }

        int position = 8;
        var huffman = new HuffmanTable();
        position += huffman.Read(buffer.Slice(position));

        Span<short> norm = stackalloc short[SequenceCodes.MaxMatchLength + 1];
        Offsets = ReadTable(buffer, ref position, norm, SequenceCode.Offset);
        MatchLengths = ReadTable(buffer, ref position, norm, SequenceCode.MatchLength);
        LiteralLengths = ReadTable(buffer, ref position, norm, SequenceCode.LiteralLength);

        if (position + 12 > buffer.Length)
        {
            Throw.Error(error);
        }

        int contentSize = buffer.Length - (position + 12);
        Span<uint> reps = stackalloc uint[3];
        for (int i = 0; i < 3; i++)
        {
            uint rep = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position));
            position += 4;
            if (rep == 0 || rep > (uint)contentSize)
            {
                Throw.Error(error);
            }

            reps[i] = rep;
        }

        SeqTable.Join(LiteralLengths, Offsets, MatchLengths);
        Huffman = huffman;
        Rep0 = reps[0];
        Rep1 = reps[1];
        Rep2 = reps[2];
        HasEntropy = true;
    }

    private static SeqTable ReadTable(ReadOnlySpan<byte> buffer, ref int position, Span<short> norm, SequenceCode code)
    {
        int maxSymbolValue = SequenceCodes.MaxSymbol(code);
        position += Fse.ReadNCount(norm, ref maxSymbolValue, out int tableLog, buffer.Slice(position), ZstdError.FseTable);
        if (tableLog > SequenceCodes.MaxLog(code))
        {
            Throw.Error(ZstdError.FseTable);
        }

        return new SeqTable(code, norm.Slice(0, maxSymbolValue + 1), tableLog);
    }
}
