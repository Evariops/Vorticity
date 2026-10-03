using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_CDict</c>, as <c>ZSTD_createCDict_byReference</c> prepares it for one level: the
/// block state every frame starts from (the zstd format's entropy tables and repeat offsets), the
/// content, and the match finder's tables over it, which small frames search in place and larger
/// ones copy.
/// </summary>
/// <remarks>
/// <para>
/// The zstd format (magic number 0xEC30A437) carries an identifier, entropy tables and three repeat
/// offsets before its content; any other buffer is raw content, of identifier 0. As in libzstd, the
/// size its parameters are chosen for, and that frames weigh against their own, is the whole
/// buffer's.
/// </para>
/// <para>
/// Immutable once built: frames only read it.
/// </para>
/// </remarks>
internal sealed unsafe class CompressionDictionary
{
    /// <summary>libzstd's <c>ZSTD_WINDOW_START_INDEX</c>: the index of the content's first byte.</summary>
    private const uint WindowStartIndex = 2;

    /// <summary>libzstd's <c>ZSTD_CURRENT_MAX</c> less the start index: the most content loaded.</summary>
    private const ulong MaxContent = (3500UL << 20) - WindowStartIndex;

    private readonly byte[] _buffer;
    private readonly MatchState[] _state = GC.AllocateArray<MatchState>(1, pinned: true);
    private uint[] _hashTable = [];
    private uint[] _chainTable = [];
    private byte[] _tagTable = [];

    private CompressionDictionary(ReadOnlySpan<byte> dictionary, int level)
    {
        _buffer = GC.AllocateArray<byte>(dictionary.Length, pinned: true);
        dictionary.CopyTo(_buffer);
        Level = level == 0 ? CompressionParameters.DefaultLevel : level;
        Parameters = CompressionParameters.ForDictionary(level, dictionary.Length);
    }

    /// <summary>The level frames are compressed at: libzstd's <c>cdict->compressionLevel</c>.</summary>
    public int Level { get; }

    /// <summary>The identifier frames name it by; 0 for raw content.</summary>
    public uint Id { get; private set; }

    /// <summary>libzstd's <c>dictContentSize</c>: the whole buffer, header and tables included.</summary>
    public int Size => _buffer.Length;

    /// <summary>The parameters of its tables, for small sources of unknown size.</summary>
    public CompressionParameters Parameters { get; }

    /// <summary>The block state every frame starts from: its entropy tables, their repeat modes, the repeat offsets.</summary>
    public BlockState Entropy { get; } = new();

    /// <summary>
    /// The window over the content, from index 2 to <see cref="MatchState.End"/> (2 when nothing was
    /// loaded), and the tables filled with it.
    /// </summary>
    public MatchState* State => (MatchState*)Unsafe.AsPointer(ref _state[0]);

    /// <summary>The bytes of content in the window.</summary>
    public uint ContentLength => State->End - State->DictLimit;

    /// <summary>The first byte of the content the window holds: its suffix, for a content too large.</summary>
    public byte* Content => State->Base + State->DictLimit;

    /// <summary>The whole content, past the zstd format's tables: what a frame that loads the dictionary loads.</summary>
    public byte* FullContent { get; private set; }

    /// <summary>The size of <see cref="FullContent"/>: 0 under 8 bytes of dictionary, which libzstd ignores.</summary>
    public nuint FullContentLength { get; private set; }

    /// <summary>Prepares <paramref name="dictionary"/> for frames at <paramref name="level"/>.</summary>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    public static CompressionDictionary Create(ReadOnlySpan<byte> dictionary, int level)
    {
        var prepared = new CompressionDictionary(dictionary, level);
        prepared.Load();
        return prepared;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_initCDict_internal</c>: the block state reset, the match state too, then the
    /// dictionary inserted (<c>ZSTD_compress_insertDictionary</c>): nothing under 8 bytes, the
    /// entropy tables of the zstd format, then the content.
    /// </summary>
    private void Load()
    {
        MatchState* state = State;
        byte* buffer = (byte*)Unsafe.AsPointer(ref _buffer[0]);
        CompressionParameters parameters = Parameters;
        bool rows = parameters.UsesRowMatchFinder;
        int hashSize = 1 << parameters.HashLog;
        int chainSize = parameters.Strategy == Strategy.Fast || rows ? 0 : 1 << parameters.ChainLog;
        *state = new MatchState
        {
            Base = buffer - WindowStartIndex,
            DictLimit = WindowStartIndex,
            LowLimit = WindowStartIndex,
            NextToUpdate = WindowStartIndex,
            End = WindowStartIndex,
            HashTable = Tables.Reserve(ref _hashTable, hashSize, clear: false),
            ChainTable = Tables.Reserve(ref _chainTable, chainSize, clear: false),
            TagTable = rows ? Tables.Reserve(ref _tagTable, hashSize, clear: false) : null,
            RowHashLog = parameters.HashLog - Math.Clamp(parameters.SearchLog, 4, 6),
            Parameters = parameters,
        };

        if (_buffer.Length < 8)
        {
            return;
        }

        byte* content = buffer;
        nuint contentSize = (nuint)_buffer.Length;
        if (BinaryPrimitives.ReadUInt32LittleEndian(_buffer) == FrameFormat.DictionaryMagic)
        {
            Id = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(4));
            int entropySize = LoadEntropy(Entropy, _buffer);
            content += entropySize;
            contentSize -= (nuint)entropySize;
        }

        FullContent = content;
        FullContentLength = contentSize;
        LoadContent(ref *state, content, contentSize, preparedDictionary: true);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_loadCEntropy</c>: the Huffman table, then the offset, match length and
    /// literal length tables, then the repeat offsets, which must each be within the content. A table
    /// some symbol of which has no code is checked before each use; a full one is used as it is.
    /// </summary>
    /// <returns>The bytes the header and tables take: where the content starts.</returns>
    /// <exception cref="System.IO.InvalidDataException">The tables are invalid.</exception>
    public static int LoadEntropy(BlockState entropy, ReadOnlySpan<byte> dictionary)
    {
        entropy.Reset();
        try
        {
            int position = 8;
            position += HuffmanEncoder.ReadCTable(entropy.Huffman, dictionary.Slice(position), out bool hasZeroWeights);
            entropy.HuffmanRepeat = !hasZeroWeights && entropy.Huffman.MaxSymbolValue == 255 ? HuffmanRepeat.Valid : HuffmanRepeat.Check;

            short* offsetCounts = stackalloc short[SequenceCodes.MaxOffset + 1];
            int offsetMax = ReadTable(dictionary, ref position, offsetCounts, SequenceCode.Offset, out int offsetLog);

            // Every offset code gets a state, those past the counts' last included.
            FseEncoder.BuildCTable(entropy.Offsets, offsetCounts, SequenceCodes.MaxOffset, (uint)offsetLog);

            short* matchLengthCounts = stackalloc short[SequenceCodes.MaxMatchLength + 1];
            int matchLengthMax = ReadTable(dictionary, ref position, matchLengthCounts, SequenceCode.MatchLength, out int matchLengthLog);
            FseEncoder.BuildCTable(entropy.MatchLengths, matchLengthCounts, (uint)matchLengthMax, (uint)matchLengthLog);
            entropy.MatchLengthRepeat = RepeatMode(matchLengthCounts, matchLengthMax, SequenceCodes.MaxMatchLength);

            short* literalLengthCounts = stackalloc short[SequenceCodes.MaxLiteralLength + 1];
            int literalLengthMax = ReadTable(dictionary, ref position, literalLengthCounts, SequenceCode.LiteralLength, out int literalLengthLog);
            FseEncoder.BuildCTable(entropy.LiteralLengths, literalLengthCounts, (uint)literalLengthMax, (uint)literalLengthLog);
            entropy.LiteralLengthRepeat = RepeatMode(literalLengthCounts, literalLengthMax, SequenceCodes.MaxLiteralLength);

            if (position + 12 > dictionary.Length)
            {
                Throw.Error(ZstdError.FseTable);
            }

            for (int i = 0; i < 3; i++)
            {
                entropy.Rep[i] = BinaryPrimitives.ReadUInt32LittleEndian(dictionary.Slice(position + (4 * i)));
            }

            position += 12;

            // Every offset the content and a block may need must have a code.
            uint contentSize = (uint)(dictionary.Length - position);
            int offsetCodeMax = SequenceCodes.MaxOffset;
            if (contentSize <= uint.MaxValue - (128u << 10))
            {
                offsetCodeMax = BitOperations.Log2(contentSize + (128u << 10));
            }

            entropy.OffsetRepeat = RepeatMode(offsetCounts, offsetMax, Math.Min(offsetCodeMax, SequenceCodes.MaxOffset));
            for (int i = 0; i < 3; i++)
            {
                if (entropy.Rep[i] == 0 || entropy.Rep[i] > contentSize)
                {
                    Throw.Error(ZstdError.FseTable);
                }
            }

            return position;
        }
        catch (ZstdException)
        {
            Throw.Dictionary("its entropy tables are corrupted");
            return 0;
        }
    }

    /// <summary>An FSE table's normalized counts, read into <paramref name="counts"/>, zeroed past the last.</summary>
    /// <returns>The largest symbol the counts describe.</returns>
    private static int ReadTable(ReadOnlySpan<byte> dictionary, ref int position, short* counts, SequenceCode code, out int tableLog)
    {
        int maxSymbolValue = SequenceCodes.MaxSymbol(code);
        var normalized = new Span<short>(counts, maxSymbolValue + 1);
        normalized.Clear();
        position += Fse.ReadNCount(normalized, ref maxSymbolValue, out tableLog, dictionary.Slice(position), ZstdError.FseTable);
        if (tableLog > SequenceCodes.MaxLog(code))
        {
            Throw.Error(ZstdError.FseTable);
        }

        return maxSymbolValue;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_dictNCountRepeat</c>: a table usable as it is when every symbol up to
    /// <paramref name="maxSymbolValue"/> has a count, to be checked otherwise.
    /// </summary>
    private static FseRepeat RepeatMode(short* counts, int dictionaryMaxSymbolValue, int maxSymbolValue)
    {
        if (dictionaryMaxSymbolValue < maxSymbolValue)
        {
            return FseRepeat.Check;
        }

        for (int s = 0; s <= maxSymbolValue; s++)
        {
            if (counts[s] == 0)
            {
                return FseRepeat.Check;
            }
        }

        return FseRepeat.Valid;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_loadDictionaryContent</c>: <paramref name="content"/> placed in the window
    /// past its end, as a segment of its own (what was there leaves the window), and its last part
    /// into the tables: all of it unless the tables cannot index it.
    /// </summary>
    /// <param name="state">A window cleared at its end (<see cref="MatchState.DictLimit"/> = <see cref="MatchState.End"/>).</param>
    /// <param name="content">The content.</param>
    /// <param name="size">Its size.</param>
    /// <param name="preparedDictionary">
    /// For a prepared dictionary (libzstd's <c>ZSTD_tfp_forCDict</c>), whose fast tables tag their
    /// indices and are filled with every position (<c>ZSTD_dtlm_full</c>); else a frame's tables
    /// (<c>ZSTD_tfp_forCCtx</c>, <c>ZSTD_dtlm_fast</c>).
    /// </param>
    public static void LoadContent(ref MatchState state, byte* content, nuint size, bool preparedDictionary)
    {
        CompressionParameters parameters = state.Parameters;
        ulong maxSize = MaxContent;
        if (preparedDictionary && parameters.Strategy <= Strategy.DoubleFast)
        {
            // The tags take the indices' top 8 bits.
            maxSize = Math.Min(maxSize, (1UL << (32 - CompressionParameters.DictionaryTagBits)) - WindowStartIndex);
        }

        if (size > maxSize)
        {
            content += size - (nuint)maxSize;
            size = (nuint)maxSize;
        }

        if (size == 0)
        {
            // libzstd leaves the window as it is, and a dictionary end the first block drops.
            return;
        }

        // ZSTD_window_update: not contiguous, the content takes the indices from the window's end.
        uint start = state.End;
        state.LowLimit = start;
        state.DictLimit = start;
        state.Base = content - start;
        state.End = start + (uint)size;

        // Only the suffix the tables can index.
        byte* end = content + size;
        nuint indexable = (nuint)1 << Math.Min(Math.Max(parameters.HashLog + 3, parameters.ChainLog + 1), 31);
        byte* first = size > indexable ? end - indexable : content;
        state.NextToUpdate = (uint)(first - state.Base);
        state.LoadedDictEnd = state.End;
        if ((nuint)(end - first) <= MatchFinder.HashReadSize)
        {
            return;
        }

        switch (parameters.Strategy)
        {
            case Strategy.DoubleFast:
                if (preparedDictionary)
                {
                    DoubleFastMatchFinder.FillTaggedHashTables(ref state, end);
                }
                else
                {
                    DoubleFastMatchFinder.FillHashTables(ref state, end);
                }

                break;
            default:
                throw new NotSupportedException($"Dictionaries are not implemented yet for the {parameters.Strategy} strategy.");
        }

        state.NextToUpdate = state.End;
    }
}
