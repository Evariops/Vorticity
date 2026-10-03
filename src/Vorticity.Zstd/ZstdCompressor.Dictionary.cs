using System;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>Frames compressed with a dictionary: libzstd's prepared dictionaries (<c>ZSTD_CDict</c>).</summary>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>
    /// libzstd's <c>ZSTD_USE_CDICT_PARAMS_SRCSIZE_CUTOFF</c>: below it, a frame uses the prepared
    /// dictionary's tables; from it, and from <see cref="LoadDictionaryMultiplier"/> times the
    /// dictionary, it loads the dictionary into tables of its own parameters.
    /// </summary>
    private const ulong PreparedTablesCutoff = 128 << 10;

    /// <summary>libzstd's <c>ZSTD_USE_CDICT_PARAMS_DICTSIZE_MULTIPLIER</c>.</summary>
    private const ulong LoadDictionaryMultiplier = 6;

    private readonly CompressionDictionary? _dictionary;

    /// <summary>
    /// The frame's parameters before a dictionary's replace them in its tables: the post-block
    /// splitter, as the header's window, follows them.
    /// </summary>
    private CompressionParameters _frameParameters;

    /// <summary>Creates a compressor at <paramref name="level"/> that compresses every frame with <paramref name="dictionary"/>.</summary>
    /// <param name="level">From <see cref="MinLevel"/> to <see cref="MaxLevel"/>; 0 is the <see cref="DefaultLevel"/>.</param>
    /// <param name="dictionary">
    /// A dictionary in the zstd format (as zstd's trainer writes them: identifier, entropy tables,
    /// content), or raw content of identifier 0; empty for none.
    /// </param>
    /// <remarks>
    /// The frames are those libzstd writes with the dictionary prepared at the same level
    /// (<c>ZSTD_createCDict</c>, then <c>ZSTD_CCtx_refCDict</c>), byte for byte: the dictionary's level
    /// is the frames' level. The dictionary is prepared once, here.
    /// <para>
    /// Work in progress: only the levels of libzstd's fast and double-fast strategies take a dictionary so far;
    /// the others throw <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="System.IO.InvalidDataException">A zstd-format dictionary whose tables are invalid.</exception>
    public ZstdCompressor(int level, ReadOnlySpan<byte> dictionary)
        : this(level)
    {
        if (!dictionary.IsEmpty)
        {
            _dictionary = CompressionDictionary.Create(dictionary, Level);
        }
    }

    /// <summary>
    /// libzstd's <c>attachDictSizeCutoffs</c>: up to these sizes, by the dictionary's strategy, a frame
    /// searches the prepared dictionary's tables in place rather than copying them.
    /// </summary>
    private static ulong AttachCutoff(Strategy strategy) => strategy switch
    {
        Strategy.DoubleFast => 16 << 10,
        Strategy.Greedy or Strategy.Lazy or Strategy.Lazy2 or Strategy.BinaryTreeLazy2 or Strategy.BinaryTreeOptimal => 32 << 10,
        _ => 8 << 10,
    };

    /// <summary>
    /// libzstd's start of a frame with a prepared dictionary (<c>ZSTD_CCtx_init_compressStream2</c>, then
    /// <c>ZSTD_compressBegin_internal</c>), in one of three ways by the source's size:
    /// <list type="bullet">
    /// <item>attached (<c>ZSTD_resetCCtx_byAttachingCDict</c>): small frames search the dictionary's
    /// tables in place, beside their own, sized for the source alone;</item>
    /// <item>copied (<c>ZSTD_resetCCtx_byCopyingCDict</c>): the dictionary's tables become the frame's,
    /// the dictionary a segment of its own below the source;</item>
    /// <item>loaded (<c>ZSTD_compress_insertDictionary</c>): from 128 KiB and six times the dictionary,
    /// the frame loads it into tables of its own parameters, again as a segment of its own.</item>
    /// </list>
    /// The frame starts from the dictionary's block state.
    /// </summary>
    /// <returns>The frame's parameters: its window.</returns>
    private CompressionParameters BeginDictionaryFrame(byte* source, int sourceSize)
    {
        CompressionDictionary dictionary = _dictionary!;
        ulong size = (ulong)sourceSize;
        CompressionParameters prepared = dictionary.Parameters;
        bool attach = size <= AttachCutoff(prepared.Strategy);
        CompressionParameters frame = CompressionParameters.ForFrame(dictionary.Level, sourceSize, dictionary.Size, attach);
        if (LongDistanceMatcher.EnabledFor(frame))
        {
            throw new NotSupportedException("Long-distance matching is not implemented yet with a dictionary.");
        }

        bool usePreparedTables = size < PreparedTablesCutoff || size < (ulong)dictionary.Size * LoadDictionaryMultiplier;
        if (usePreparedTables && attach)
        {
            CompressionParameters tables = prepared.ForAttachedSource(sourceSize, dictionary.Size, frame.WindowLog);
            uint dictionaryEnd = dictionary.State->End;
            bool hasContent = dictionary.ContentLength != 0;
            BeginFrame(tables, source, sourceSize, longDistance: false, hasContent ? dictionaryEnd : WindowStartIndex);
            if (hasContent)
            {
                _matchState.LoadedDictEnd = _matchState.DictLimit;
                _matchState.Dictionary = dictionary.State;
            }
        }
        else if (usePreparedTables)
        {
            CompressionParameters tables = prepared;
            tables.WindowLog = frame.WindowLog;
            BeginFrame(tables, source, sourceSize, longDistance: false);
            CopyDictionaryTables(dictionary);

            // The dictionary's window, then the source past its end: not contiguous.
            MatchState* state = dictionary.State;
            uint dictionaryEnd = state->End;
            _matchState.DictionaryBase = state->Base;
            _matchState.LowLimit = state->DictLimit;
            _matchState.DictLimit = dictionaryEnd;
            _matchState.Base = source - dictionaryEnd;
            _matchState.NextToUpdate = dictionaryEnd;
            _matchState.LoadedDictEnd = state->LoadedDictEnd;
            if (dictionaryEnd - _matchState.LowLimit < MatchFinder.HashReadSize)
            {
                // libzstd's ZSTD_window_update: an extDict too small to search.
                _matchState.LowLimit = dictionaryEnd;
            }

            _nextIndex = dictionaryEnd + (uint)sourceSize + (frame.Strategy == Strategy.BinaryTreeUltra2 ? (uint)FrameFormat.MaxBlockSize : 0);
        }
        else
        {
            BeginFrame(frame, source, sourceSize, longDistance: false);
            uint start = _matchState.DictLimit;
            CompressionDictionary.LoadContent(ref _matchState, dictionary.FullContent, dictionary.FullContentLength, preparedDictionary: false);
            if (_matchState.End != start)
            {
                // The source past the content: not contiguous.
                uint contentEnd = _matchState.End;
                _matchState.DictionaryBase = _matchState.Base;
                _matchState.LowLimit = start;
                _matchState.DictLimit = contentEnd;
                _matchState.Base = source - contentEnd;
                _matchState.NextToUpdate = contentEnd;
                if (contentEnd - start < MatchFinder.HashReadSize)
                {
                    _matchState.LowLimit = contentEnd;
                }

                _nextIndex = contentEnd + (uint)sourceSize + (frame.Strategy == Strategy.BinaryTreeUltra2 ? (uint)FrameFormat.MaxBlockSize : 0);
            }
        }

        _frameParameters = frame;
        _previous.CopyFrom(dictionary.Entropy);
        return frame;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_selectBlockCompressor</c> with a dictionary: the extDict search when a segment
    /// lies below the prefix, else the attached one.
    /// </summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    private nuint FindSequencesWithDictionary(byte* source, int size, uint* rep)
    {
        bool extDict = _matchState.LowLimit < _matchState.DictLimit;
        return _parameters.Strategy switch
        {
            Strategy.Fast => extDict
                ? FastMatchFinder.CompressBlockExtDict(ref _matchState, _store, rep, source, (nuint)size)
                : FastMatchFinder.CompressBlockAttached(ref _matchState, _store, rep, source, (nuint)size),
            Strategy.DoubleFast => extDict
                ? DoubleFastMatchFinder.CompressBlockExtDict(ref _matchState, _store, rep, source, (nuint)size)
                : DoubleFastMatchFinder.CompressBlockAttached(ref _matchState, _store, rep, source, (nuint)size),
            _ => throw new NotSupportedException($"Dictionaries are not implemented yet for the {_parameters.Strategy} strategy."),
        };
    }

    /// <summary>
    /// The copy of <c>ZSTD_resetCCtx_byCopyingCDict</c>: the prepared dictionary's tables into the
    /// frame's, the tags of the fast ones stripped; the 3-byte table, which a dictionary never fills,
    /// cleared.
    /// </summary>
    private void CopyDictionaryTables(CompressionDictionary dictionary)
    {
        MatchState* prepared = dictionary.State;
        CompressionParameters parameters = dictionary.Parameters;
        bool tagged = parameters.Strategy <= Strategy.DoubleFast;
        int hashSize = 1 << parameters.HashLog;
        CopyTable(_matchState.HashTable, prepared->HashTable, hashSize, tagged);
        if (parameters.Strategy != Strategy.Fast && !parameters.UsesRowMatchFinder)
        {
            CopyTable(_matchState.ChainTable, prepared->ChainTable, 1 << parameters.ChainLog, tagged);
        }

        if (parameters.UsesRowMatchFinder)
        {
            Buffer.MemoryCopy(prepared->TagTable, _matchState.TagTable, hashSize, hashSize);
        }

        if (_matchState.HashLog3 != 0)
        {
            new Span<uint>(_matchState.HashTable3, 1 << _matchState.HashLog3).Clear();
        }

        static void CopyTable(uint* destination, uint* source, int size, bool tagged)
        {
            if (!tagged)
            {
                Buffer.MemoryCopy(source, destination, (long)size * sizeof(uint), (long)size * sizeof(uint));
                return;
            }

            for (int i = 0; i < size; i++)
            {
                destination[i] = source[i] >> MatchFinder.TagBits;
            }
        }
    }
}
