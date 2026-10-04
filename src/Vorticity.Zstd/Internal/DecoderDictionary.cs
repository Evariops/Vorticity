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
    /// 32 bytes at a time, and the last copy of one that ends near its end reads past it.
    /// </summary>
    public const int Margin = 32;

    private DecoderDictionary(ReadOnlySpan<byte> content)
    {
        Buffer = new byte[content.Length + Margin];
        content.CopyTo(Buffer);
        Length = content.Length;
    }

    /// <summary>The content, then <see cref="Margin"/> bytes nothing reads as content.</summary>
    public byte[] Buffer { get; }

    /// <summary>The length of the content.</summary>
    public int Length { get; }

    /// <summary>The bytes before the first byte of every frame, <see cref="Margin"/> readable bytes after them.</summary>
    public ReadOnlySpan<byte> Content => Buffer.AsSpan(0, Length);

    /// <summary>The identifier a frame names it by; 0 for raw content.</summary>
    public uint Id { get; private set; }

    /// <summary>The tables and repeat offsets of the zstd format; null for raw content.</summary>
    public DictionaryEntropy? Entropy { get; private set; }

    /// <summary>
    /// libzstd's <c>ZSTD_createDDict</c> in automatic mode: the zstd format when the buffer starts with
    /// its magic number, raw content otherwise.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    public static DecoderDictionary Load(ReadOnlySpan<byte> buffer)
    {
        var dictionary = new DecoderDictionary(buffer);
        if (!DictionaryEntropy.IsZstdFormat(buffer))
        {
            return dictionary;
        }

        var entropy = new DictionaryEntropy();
        try
        {
            entropy.Load(buffer);
        }
        catch (ZstdException)
        {
            Throw.Dictionary("its entropy tables are corrupted");
        }

        dictionary.Id = entropy.Id;
        dictionary.Entropy = entropy;
        return dictionary;
    }
}

/// <summary>
/// A zstd-format dictionary's identifier, entropy tables and repeat offsets, libzstd's
/// <c>ZSTD_entropyDTables_t</c>: the state every frame decoded with it starts from.
/// </summary>
/// <remarks>
/// The tables are built in place, in storage sized for the widest, so that a decoder given one
/// dictionary after another rebuilds them without allocating; the sets that refer to them forget them
/// first (<see cref="SequenceTableSet.Forget"/>).
/// </remarks>
internal sealed class DictionaryEntropy
{
    public readonly HuffmanTable Huffman = new HuffmanTable();
    public readonly SeqTable LiteralLengths = new SeqTable(SequenceCode.LiteralLength);
    public readonly SeqTable Offsets = new SeqTable(SequenceCode.Offset);
    public readonly SeqTable MatchLengths = new SeqTable(SequenceCode.MatchLength);

    /// <summary>The identifier a frame names the dictionary by.</summary>
    public uint Id { get; private set; }

    public uint Rep0 { get; private set; }
    public uint Rep1 { get; private set; }
    public uint Rep2 { get; private set; }

    /// <summary>Whether <paramref name="buffer"/> is a dictionary in the zstd format rather than raw content.</summary>
    public static bool IsZstdFormat(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == FrameFormat.DictionaryMagic;

    /// <summary>
    /// libzstd's <c>ZSTD_loadDEntropy</c>: the tables of <paramref name="buffer"/>, a dictionary in
    /// the zstd format, built here.
    /// </summary>
    /// <returns>
    /// The bytes they take from the start of the buffer: the magic number, the identifier, the tables
    /// and the repeat offsets. What they are is a function of these bytes and the buffer's length.
    /// </returns>
    /// <exception cref="ZstdException">The tables are invalid; they are then unusable until loaded again.</exception>
    public int Load(ReadOnlySpan<byte> buffer)
    {
        const ZstdError error = ZstdError.FseTable;
        if (buffer.Length <= 8)
        {
            Throw.Error(error);
        }

        Id = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        int position = 8;
        position += Huffman.Read(buffer.Slice(position));

        Span<short> norm = stackalloc short[SequenceCodes.MaxMatchLength + 1];
        ReadTable(Offsets, buffer, ref position, norm);
        ReadTable(MatchLengths, buffer, ref position, norm);
        ReadTable(LiteralLengths, buffer, ref position, norm);

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
        Rep0 = reps[0];
        Rep1 = reps[1];
        Rep2 = reps[2];
        return position;
    }

    private static void ReadTable(SeqTable table, ReadOnlySpan<byte> buffer, ref int position, Span<short> norm)
    {
        SequenceCode code = table.Code;
        int maxSymbolValue = SequenceCodes.MaxSymbol(code);
        position += Fse.ReadNCount(norm, ref maxSymbolValue, out int tableLog, buffer.Slice(position), ZstdError.FseTable);
        if (tableLog > SequenceCodes.MaxLog(code))
        {
            Throw.Error(ZstdError.FseTable);
        }

        table.Build(norm.Slice(0, maxSymbolValue + 1), tableLog);
    }
}
