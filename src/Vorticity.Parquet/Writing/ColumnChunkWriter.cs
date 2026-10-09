using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Thrift;
using Vorticity.Types.Numerics;
using Vorticity.Writing;
using Vorticity.Zstd;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// One column's pages for the row group being written: rows staged in their PLAIN form as they
/// arrive, a page closed at every block of rows, and the closed pages kept until the row group is.
/// </summary>
/// <remarks>
/// <para>
/// Staging is the page's own form: the values of the rows that hold one, packed as PLAIN stores
/// them, and the validity as a bitmap. A page closes at every block of the writer's block rows
/// counted from the file's first row, the same rows in every column, which is the grid a reader of
/// this file reads a batch per page on.
/// </para>
/// <para>
/// A column of values a dictionary serves — integers of four bytes or more, floats, decimals, text
/// and binary — is probed into the core's distinct table as its rows are staged, its new values
/// appended to the dictionary in their PLAIN form as they first occur. A page is then its codes,
/// RLE_DICTIONARY, while the dictionary holds: while it stays under
/// <see cref="DictionaryPageBytes"/> and the chunk's codes and dictionary take fewer bytes than the
/// values they stand for. Once it does not, that page and the chunk's later ones are PLAIN, and the
/// dictionary page, written first in the chunk when the row group closes, holds the values coded so
/// far.
/// </para>
/// <para>
/// A page is a v2 data page: its definition levels uncompressed before its values, the values
/// compressed with the chunk's codec unless that saves less than an eighth, in which case they are
/// stored as they are and the header says so.
/// </para>
/// </remarks>
internal sealed class ColumnChunkWriter : IDisposable
{
    /// <summary>The most bytes a dictionary page may hold: past them, the chunk's later pages are PLAIN.</summary>
    internal const int DictionaryPageBytes = 1 << 20;

    private readonly WriteColumn _column;
    private readonly CompressionCodec _codec;
    private readonly int _level;
    private readonly ZstdCompressor? _zstd;
    private readonly int _blockRows;
    private readonly PooledBytes _values;
    private readonly PooledBytes _levels;
    private readonly PooledBytes _compressed;
    private readonly ChunkBytes _chunk;
    private readonly byte[] _validity;
    private byte[] _levelBytes;

    /// <summary>The page's validity as the words the compressing kernel takes, a block's worth.</summary>
    private readonly ulong[] _mask;
    private readonly List<PageLocation> _pages = [];

    /// <summary>Whether the column's values are of a kind a dictionary serves.</summary>
    private readonly bool _eligible;

    /// <summary>The dictionary's values in their PLAIN form, in code order, the null left out.</summary>
    private readonly PooledBytes _entries;

    /// <summary>A page's codes, RLE/bit-packed behind their width.</summary>
    private readonly PooledBytes _codes;

    /// <summary>The dictionary page, written ahead of the data pages when the chunk closes.</summary>
    private readonly PooledBytes _dictionaryPage;

    private uint[] _pageCodes;
    private int[] _firstOccurrences = [];
    private DistinctTable? _table;

    /// <summary>Whether the chunk's pages are still dictionary-encoded.</summary>
    private bool _dictionary;

    /// <summary>Where the page's codes start in the table's, or -1 when its rows were not probed.</summary>
    private int _pageFirstCode = -1;

    /// <summary>The dictionary's entries, the null not counted.</summary>
    private int _entryCount;

    /// <summary>The entries the dictionary pages written so far reference, and their PLAIN bytes.</summary>
    private int _frozenEntries;
    private int _frozenBytes;

    /// <summary>Whether a PLAIN page may take another encoding where it pays: under Auto and Smallest.</summary>
    private readonly bool _encodings;

    /// <summary>A page encoded otherwise than PLAIN, and a byte array page's values without their lengths.</summary>
    private readonly PooledBytes _encoded;
    private readonly PooledBytes _data;
    private int[] _lengths;
    private int[] _prefixes;
    private int[] _suffixes;

    /// <summary>Per encoding, the chunk's data pages that took it.</summary>
    private readonly int[] _pagesBy = new int[16];

    /// <summary>Whether a float column's pages split their bytes into streams: decided by a trial on the chunk's first PLAIN page.</summary>
    private bool? _split;

    /// <summary>The codes the chunk's dictionary pages took, and the PLAIN bytes they stand for.</summary>
    private long _codeBytes;
    private long _plainBytes;
    private int _pageRows;
    private int _pageValues;
    private int _boolBits;
    private long _chunkRows;
    private long _chunkEntries;
    private long _chunkNulls;
    private long _chunkUncompressed;
    private Bounds _chunkBounds;

    /// <summary>Whether the staged rows carry a validity bitmap: a flat column's that may be null.</summary>
    private readonly bool _rowValidity;

    /// <summary>A nested column's shredding of its top-level field's rows into entries.</summary>
    private readonly Shredder? _shredder;

    /// <summary>A nested column's page's levels, a byte per entry.</summary>
    private readonly PooledBytes? _repetitionLevels;
    private readonly PooledBytes? _definitionLevels;

    /// <summary>A nested column's page's rows and entries; its values are what <see cref="_pageRows"/> counts.</summary>
    private int _pageNestedRows;
    private int _pageEntries;

    internal ColumnChunkWriter(WriteColumn column, CompressionCodec codec, int level, ZstdCompressor? zstd, int blockRows, CompressionProfile profile, AlignedBufferPool pool)
    {
        _rowValidity = column.Nullable && !column.Nested;
        if (column.Nested)
        {
            _shredder = new Shredder(pool);
            _repetitionLevels = new PooledBytes(pool);
            _definitionLevels = new PooledBytes(pool);
        }

        _column = column;
        _codec = codec;
        _level = level;
        _zstd = zstd;
        _blockRows = blockRows;
        _values = new PooledBytes(pool);
        _levels = new PooledBytes(pool);
        _compressed = new PooledBytes(pool);
        _chunk = new ChunkBytes(pool);
        _entries = new PooledBytes(pool);
        _codes = new PooledBytes(pool);
        _dictionaryPage = new PooledBytes(pool);
        _validity = new byte[(blockRows + 7) / 8 + 8];
        _levelBytes = new byte[blockRows];
        _mask = new ulong[(blockRows + 63) >> 6];
        _pageCodes = new uint[blockRows];
        _chunkBounds = Bounds.Empty;
        _eligible = profile != CompressionProfile.None && column.Conversion is not (ValueConversion.Bool or ValueConversion.Null) && !column.FixedElements;
        _encodings = profile is CompressionProfile.Auto or CompressionProfile.Smallest;
        _encoded = new PooledBytes(pool);
        _data = new PooledBytes(pool);
        _lengths = new int[blockRows];
        _prefixes = new int[blockRows];
        _suffixes = new int[blockRows];
        _dictionary = _eligible;
    }

    internal WriteColumn Column => _column;

    /// <summary>The bytes of the pages closed so far, waiting for the row group to close.</summary>
    internal long BufferedBytes => _chunk.Length + _values.Length + _entries.Length;

    /// <summary>Hands the closed chunk's bytes to <paramref name="sink"/>, between <see cref="Close"/> and <see cref="Reset"/>: its dictionary page first.</summary>
    internal async ValueTask WriteChunkAsync(ISegmentSink sink, CancellationToken cancellationToken)
    {
        if (_dictionaryPage.Length > 0)
        {
            await sink.WriteAsync(_dictionaryPage.Written, cancellationToken).ConfigureAwait(false);
        }

        await _chunk.WriteToAsync(sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends <paramref name="count"/> rows of <paramref name="node"/> from <paramref name="start"/>,
    /// closing pages as blocks fill: the column's own node, or a nested column's top-level field's.
    /// </summary>
    internal void Append(CanonicalArena arena, int node, int start, int count)
    {
        if (_shredder is not null)
        {
            AppendNested(arena, node, start, count);
            return;
        }

        while (count > 0)
        {
            int take = Math.Min(count, _blockRows - _pageRows);
            Stage(arena, node, start, take);
            start += take;
            count -= take;
            if (_pageRows == _blockRows)
            {
                ClosePage();
            }
        }
    }

    /// <summary>Closes the page being staged, if it holds a row: at the end of a row group.</summary>
    internal void ClosePage()
    {
        bool nested = _shredder is not null;
        int rows = nested ? _pageNestedRows : _pageRows;
        if (rows == 0)
        {
            return;
        }

        int entries = nested ? _pageEntries : rows;
        int nulls = entries - _pageValues;
        _levels.Clear();
        int repetitionLength = 0;
        if (nested)
        {
            // The repetition levels, then the definition levels, each at the width of its maximum.
            repetitionLength = Levels(_repetitionLevels!.WrittenSpan, _column.MaxRepetitionLevel);
            Levels(_definitionLevels!.WrittenSpan, _column.MaxDefinitionLevel);
        }
        else if (_column.Nullable)
        {
            Span<byte> levels = _levelBytes.AsSpan(0, rows);
            BitPacking.Unpack8(_validity, 1, levels);
            _levels.Truncate(RleHybridEncoder.Encode(levels, 1, _levels.Reserve(RleHybridEncoder.MaxSize(rows, 1))));
        }

        Fit(_pageRows);

        ReadOnlySpan<byte> body = _column.Conversion == ValueConversion.Bool
            ? _values.WrittenSpan[..((_boolBits + 7) / 8)]
            : _values.WrittenSpan;
        Bounds bounds = Bounds.Of(_column.Domain, body, _pageValues);
        _chunkBounds = _chunkBounds.Merge(bounds);

        ParquetEncoding encoding = ParquetEncoding.Plain;
        if (_dictionary && _pageFirstCode >= 0)
        {
            switch (EncodeCodes(_pageRows, body.Length))
            {
                case Coding.Codes:
                    body = _codes.WrittenSpan;
                    encoding = ParquetEncoding.RleDictionary;
                    _frozenEntries = _entryCount;
                    _frozenBytes = _entries.Length;
                    break;
                case Coding.Fallback:
                    _dictionary = false;
                    break;
            }
        }

        if (encoding == ParquetEncoding.Plain && _encodings && _pageValues > 0)
        {
            encoding = Choose(ref body);
        }

        _pagesBy[(int)encoding]++;

        bool compressed = _codec != CompressionCodec.Uncompressed;
        ReadOnlySpan<byte> stored = body;
        if (compressed)
        {
            ReadOnlySpan<byte> squeezed = Compress(body);
            if (squeezed.Length <= body.Length - body.Length / 8)
            {
                stored = squeezed;
            }
            else
            {
                compressed = false;
            }
        }

        PageHeader header = new()
        {
            Type = PageType.DataPageV2,
            UncompressedPageSize = _levels.Length + body.Length,
            CompressedPageSize = _levels.Length + stored.Length,
            ValueCount = entries,
            NullCount = nulls,
            RowCount = rows,
            Encoding = encoding,
            DefinitionLevelsLength = _levels.Length - repetitionLength,
            RepetitionLevelsLength = repetitionLength,
            IsCompressed = compressed || _codec == CompressionCodec.Uncompressed,
        };

        long pageStart = _chunk.Length;
        ThriftCompactWriter writer = new(_chunk);
        header.Write(ref writer, default);
        writer.Flush();
        int headerLength = (int)(_chunk.Length - pageStart);
        _chunk.Write(_levels.WrittenSpan);
        _chunk.Write(stored);
        _pages.Add(new PageLocation(pageStart, (int)(_chunk.Length - pageStart), _chunkRows));
        _chunkUncompressed += headerLength + header.UncompressedPageSize;
        _chunkRows += rows;
        _chunkEntries += entries;
        _chunkNulls += nulls;

        _pageRows = 0;
        _pageValues = 0;
        _boolBits = 0;
        _pageFirstCode = -1;
        _values.Clear();
        if (nested)
        {
            _pageNestedRows = 0;
            _pageEntries = 0;
            _repetitionLevels!.Clear();
            _definitionLevels!.Clear();
        }
        else
        {
            Array.Clear(_validity);
        }
    }

    /// <summary>A nested page's levels of one kind, RLE at the width of their maximum, after those already in the page's; their bytes.</summary>
    private int Levels(ReadOnlySpan<byte> levels, int max)
    {
        if (max == 0)
        {
            return 0;
        }

        // Encoded once into room for the most it can take, and cut to what it took.
        int width = 32 - BitOperations.LeadingZeroCount((uint)max);
        int before = _levels.Length;
        int size = RleHybridEncoder.Encode(levels, width, _levels.Reserve(RleHybridEncoder.MaxSize(levels.Length, width)));
        _levels.Truncate(before + size);
        return size;
    }

    /// <summary>Grows the arrays a page's values are encoded through to hold <paramref name="count"/>: a nested page holds more values than rows.</summary>
    private void Fit(int count)
    {
        if (_pageCodes.Length >= count)
        {
            return;
        }

        int size = Math.Max(count, _pageCodes.Length * 2);
        _pageCodes = new uint[size];
        _lengths = new int[size];
        _prefixes = new int[size];
        _suffixes = new int[size];
        _levelBytes = new byte[size];
    }

    /// <summary>
    /// Appends a nested column's rows: each block's worth shredded into entries, whose levels the
    /// page takes and whose values it stages densely, a range of the leaf where they lie back to
    /// back and gathered where they do not.
    /// </summary>
    private void AppendNested(CanonicalArena arena, int node, int start, int count)
    {
        Shredder shredder = _shredder!;
        while (count > 0)
        {
            int take = Math.Min(count, _blockRows - _pageNestedRows);
            shredder.Shred(arena, node, start, take, _column);
            if (_column.MaxRepetitionLevel > 0)
            {
                _repetitionLevels!.Write(shredder.Repetition);
            }

            if (_column.MaxDefinitionLevel > 0)
            {
                _definitionLevels!.Write(shredder.Definition);
            }

            _pageEntries = checked(_pageEntries + shredder.Count);
            int values = shredder.Values;
            if (values > 0)
            {
                if (shredder.Contiguous)
                {
                    StageDense(arena, shredder.Leaf, shredder.ValueRows[0], values);
                }
                else
                {
                    StageDense(arena, CanonicalFilter.Apply(arena, shredder.Leaf, shredder.ValueRows), 0, values);
                }
            }

            _pageNestedRows += take;
            start += take;
            count -= take;
            if (_pageNestedRows == _blockRows)
            {
                ClosePage();
            }
        }
    }

    /// <summary>Stages <paramref name="count"/> rows of <paramref name="index"/> from <paramref name="start"/>, every one a value: a nested page's.</summary>
    private void StageDense(CanonicalArena arena, int index, int start, int count)
    {
        if (_pageRows == 0)
        {
            _pageFirstCode = _dictionary ? _table?.Rows ?? 0 : -1;
        }

        if (_dictionary && _pageFirstCode >= 0)
        {
            Probe(arena, index, start, count);
        }

        StageValues(arena, arena.GetNode(index), start, count, count, _values);
        _pageRows += count;
        _pageValues += count;
    }

    /// <summary>
    /// The chunk's pages and what its metadata says of them, the chunk starting at
    /// <paramref name="offset"/> in the file; the caller writes it there with
    /// <see cref="WriteChunkAsync"/> and then calls <see cref="Reset"/>.
    /// </summary>
    /// <param name="offset">Where the chunk goes in the file.</param>
    /// <param name="partial">
    /// Whether the page being staged closes with the chunk: at the file's end. Otherwise its rows,
    /// short of a block, wait for the next row group, which keeps every row group whole blocks.
    /// </param>
    internal ChunkResult Close(long offset, bool partial)
    {
        if (partial)
        {
            ClosePage();
        }

        WriteDictionaryPage();
        long dictionary = _dictionaryPage.Length;
        PageLocation[] pages = new PageLocation[_pages.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            PageLocation page = _pages[i];
            pages[i] = page with { Offset = offset + dictionary + page.Offset };
        }

        uint encodings = dictionary > 0 ? 1u << (int)ParquetEncoding.Plain : 0;
        for (int encoding = 0; encoding < _pagesBy.Length; encoding++)
        {
            if (_pagesBy[encoding] > 0)
            {
                encodings |= 1u << encoding;
            }
        }

        if (_column.MaxDefinitionLevel > 0 || _column.MaxRepetitionLevel > 0)
        {
            encodings |= 1u << (int)ParquetEncoding.Rle;
        }

        return new ChunkResult(
            offset,
            offset + dictionary,
            dictionary > 0 ? offset : -1,
            _chunkRows,
            _chunkEntries,
            _chunkNulls,
            _chunkUncompressed,
            _chunk.Length + dictionary,
            encodings,
            _chunkBounds,
            pages,
            (int[])_pagesBy.Clone());
    }

    /// <summary>Forgets the closed chunk, keeping the buffers for the next row group's.</summary>
    internal void Reset()
    {
        _chunk.Clear();
        _pages.Clear();
        _chunkRows = 0;
        _chunkEntries = 0;
        _chunkNulls = 0;
        _chunkUncompressed = 0;
        _chunkBounds = Bounds.Empty;
        _table?.Reset();
        _entries.Clear();
        _dictionaryPage.Clear();
        _entryCount = 0;
        _frozenEntries = 0;
        _frozenBytes = 0;
        Array.Clear(_pagesBy);
        _split = null;
        _codeBytes = 0;
        _plainBytes = 0;
        _dictionary = _eligible;

        // A page carried into the next row group was probed into the table just reset: it is PLAIN.
        _pageFirstCode = -1;
    }

    public void Dispose()
    {
        _values.Dispose();
        _levels.Dispose();
        _compressed.Dispose();
        _chunk.Dispose();
        _entries.Dispose();
        _codes.Dispose();
        _dictionaryPage.Dispose();
        _encoded.Dispose();
        _data.Dispose();
        _repetitionLevels?.Dispose();
        _shredder?.Dispose();
        _definitionLevels?.Dispose();
        _table?.Reset();
    }

    private void Stage(CanonicalArena arena, int index, int start, int count)
    {
        if (_pageRows == 0)
        {
            _pageFirstCode = _dictionary ? _table?.Rows ?? 0 : -1;
        }

        CanonicalNode node = arena.GetNode(index);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        int values;
        if (_column.Nullable)
        {
            if (mask.AllValid)
            {
                BitmapKernels.SetRange(_validity, _pageRows, count);
            }
            else if (!mask.AllInvalid)
            {
                BitmapKernels.CopyRange(mask.Bits, mask.BitOffset + start, _validity, _pageRows, count);
            }

            values = BitmapKernels.CountSet(_validity, _pageRows, count);
        }
        else
        {
            if (!mask.AllValid && BitmapKernels.CountSet(mask.Bits, mask.BitOffset + start, count) != count)
            {
                throw new VortexSchemaException($"The column '{_column.Name}' is not nullable and is given a null.");
            }

            values = count;
        }

        if (_dictionary && _pageFirstCode >= 0)
        {
            Probe(arena, index, start, count);
        }

        if (values > 0)
        {
            StageValues(arena, node, start, count, values, _values);
        }

        _pageRows += count;
        _pageValues += values;
    }

    /// <summary>
    /// Probes the rows into the chunk's distinct table and appends the values that first occur
    /// among them to the dictionary, in code order, the null left out.
    /// </summary>
    private void Probe(CanonicalArena arena, int index, int start, int count)
    {
        CanonicalNode node = arena.GetNode(index);
        DistinctTable? table = _table ??= DistinctTable.For(node);
        if (table is null)
        {
            _dictionary = false;
            _pageFirstCode = -1;
            return;
        }

        int rowsBefore = table.Rows;
        int distinctBefore = table.Distinct;
        table.Probe(arena, node, start, count);
        if (table.Abandoned)
        {
            _dictionary = false;
            _pageFirstCode = -1;
            return;
        }

        int distinct = table.Distinct;
        if (distinct == distinctBefore)
        {
            return;
        }

        int nullCode = table.NullCode;
        ReadOnlySpan<int> first = table.FirstRows;
        if (_firstOccurrences.Length < distinct - distinctBefore)
        {
            _firstOccurrences = new int[Math.Max(distinct - distinctBefore, _firstOccurrences.Length * 2)];
        }

        int found = 0;
        for (int code = distinctBefore; code < distinct; code++)
        {
            if (code != nullCode)
            {
                _firstOccurrences[found++] = start + (first[code] - rowsBefore);
            }
        }

        if (found > 0)
        {
            int entries = CanonicalFilter.Apply(arena, index, _firstOccurrences.AsSpan(0, found));
            StageValues(arena, arena.GetNode(entries), 0, found, found, _entries);
            _entryCount += found;
        }
    }

    /// <summary>
    /// The page as codes into the dictionary, when the dictionary stays under its bound and the
    /// chunk's codes and dictionary take fewer bytes than the <paramref name="plain"/> values of its
    /// pages so far.
    /// </summary>
    private Coding EncodeCodes(int rows, int plain)
    {
        DistinctTable table = _table!;
        ReadOnlySpan<int> codes = table.Codes.Slice(_pageFirstCode, rows);
        int nullCode = table.NullCode;
        int count = 0;
        for (int row = 0; row < rows; row++)
        {
            if (_rowValidity && !CanonicalSupport.BitAt(_validity, row))
            {
                continue;
            }

            int code = codes[row];
            _pageCodes[count++] = (uint)(nullCode >= 0 && code > nullCode ? code - 1 : code);
        }

        if (count == 0)
        {
            // A page of nulls has no value for either encoding to weigh.
            return Coding.Plain;
        }

        if (_entries.Length > DictionaryPageBytes)
        {
            return Coding.Fallback;
        }

        int width = Math.Max(1, 32 - BitOperations.LeadingZeroCount((uint)(_entryCount - 1)));
        ReadOnlySpan<uint> pageCodes = _pageCodes.AsSpan(0, count);
        int size = 1 + RleHybridEncoder.Size(pageCodes, width);

        // The dictionary pays while the chunk's codes and the dictionary page together take fewer
        // bytes than the values they stand for: a column of values that seldom repeat stops here,
        // its dictionary as large as its values.
        if (_entries.Length + _codeBytes + size >= _plainBytes + plain)
        {
            return Coding.Fallback;
        }

        _codeBytes += size;
        _plainBytes += plain;

        _codes.Clear();
        Span<byte> into = _codes.Reserve(size);
        into[0] = (byte)width;
        RleHybridEncoder.Encode(pageCodes, width, into[1..]);
        return Coding.Codes;
    }

    /// <summary>
    /// The encoding a PLAIN page takes where another pays, <paramref name="body"/> then its bytes:
    /// DELTA_BINARY_PACKED for integers, priced exactly; BYTE_STREAM_SPLIT for floats under a codec,
    /// by a trial on the chunk's first page; the delta encodings of byte arrays, priced exactly; RLE
    /// for booleans. An encoding is taken when it saves an eighth of the bytes, which pays for its
    /// slower decode, but for a byte array's lengths, which always do.
    /// </summary>
    private ParquetEncoding Choose(ref ReadOnlySpan<byte> body)
    {
        switch (_column.Physical)
        {
            case PhysicalType.Int32:
            {
                ReadOnlySpan<int> values = MemoryMarshal.Cast<byte, int>(body);
                int size = DeltaBinaryPacked.Size32(values);
                if (size > body.Length - (body.Length / 8))
                {
                    return ParquetEncoding.Plain;
                }

                _encoded.Clear();
                DeltaBinaryPacked.Encode32(values, _encoded.Reserve(size));
                body = _encoded.WrittenSpan;
                return ParquetEncoding.DeltaBinaryPacked;
            }

            case PhysicalType.Int64:
            {
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(body);
                int size = DeltaBinaryPacked.Size64(values);
                if (size > body.Length - (body.Length / 8))
                {
                    return ParquetEncoding.Plain;
                }

                _encoded.Clear();
                DeltaBinaryPacked.Encode64(values, _encoded.Reserve(size));
                body = _encoded.WrittenSpan;
                return ParquetEncoding.DeltaBinaryPacked;
            }

            case PhysicalType.Float:
            case PhysicalType.Double:
            {
                if (_codec == CompressionCodec.Uncompressed || _split == false)
                {
                    return ParquetEncoding.Plain;
                }

                int width = _column.Physical == PhysicalType.Float ? sizeof(float) : sizeof(double);
                _encoded.Clear();
                ByteStreamSplit.Encode(body, width, _encoded.Reserve(body.Length));
                if (_split is null)
                {
                    // The trial: what the codec makes of either form of the chunk's first page.
                    int plain = Compress(body).Length;
                    int split = Compress(_encoded.WrittenSpan).Length;
                    _split = split <= plain - (plain / 8);
                    if (_split == false)
                    {
                        return ParquetEncoding.Plain;
                    }
                }

                body = _encoded.WrittenSpan;
                return ParquetEncoding.ByteStreamSplit;
            }

            case PhysicalType.ByteArray when _column.Conversion == ValueConversion.ByteArray:
                return ChooseBytes(ref body);

            case PhysicalType.Boolean:
            {
                Span<byte> values = _levelBytes.AsSpan(0, _pageValues);
                BitPacking.Unpack8(body, 1, values);
                int size = sizeof(int) + RleHybridEncoder.Size(values, 1);
                if (size >= body.Length)
                {
                    return ParquetEncoding.Plain;
                }

                _encoded.Clear();
                Span<byte> into = _encoded.Reserve(size);
                BinaryPrimitives.WriteInt32LittleEndian(into, size - sizeof(int));
                RleHybridEncoder.Encode(values, 1, into[sizeof(int)..]);
                body = _encoded.WrittenSpan;
                return ParquetEncoding.Rle;
            }

            default:
                return ParquetEncoding.Plain;
        }
    }

    /// <summary>
    /// A byte array page's encoding: DELTA_LENGTH_BYTE_ARRAY, its lengths delta-encoded ahead of its
    /// bytes, which the standard prefers to PLAIN, or DELTA_BYTE_ARRAY when the prefixes values share
    /// save an eighth more, its rebuild costing a copy per value on read.
    /// </summary>
    private ParquetEncoding ChooseBytes(ref ReadOnlySpan<byte> body)
    {
        int count = _pageValues;
        Span<int> lengths = _lengths.AsSpan(0, count);
        _data.Clear();
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
            lengths[i] = length;
            _data.Write(body.Slice(at + sizeof(int), length));
            at += sizeof(int) + length;
        }

        ReadOnlySpan<byte> data = _data.WrittenSpan;
        int byLength = DeltaByteArrays.SizeLengths(lengths, data.Length);
        Span<int> prefixes = _prefixes.AsSpan(0, count);
        Span<int> suffixes = _suffixes.AsSpan(0, count);
        int rest = DeltaByteArrays.Prefixes(data, lengths, prefixes, suffixes);
        int byPrefix = DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest);
        _encoded.Clear();
        if (byPrefix <= byLength - (byLength / 8))
        {
            DeltaByteArrays.EncodePrefixes(data, lengths, prefixes, suffixes, _encoded.Reserve(byPrefix));
            body = _encoded.WrittenSpan;
            return ParquetEncoding.DeltaByteArray;
        }

        if (byLength >= body.Length)
        {
            return ParquetEncoding.Plain;
        }

        DeltaByteArrays.EncodeLengths(lengths, data, _encoded.Reserve(byLength));
        body = _encoded.WrittenSpan;
        return ParquetEncoding.DeltaLengthByteArray;
    }

    /// <summary>The dictionary page, when a data page used the dictionary: the values coded until the last such page.</summary>
    private void WriteDictionaryPage()
    {
        _dictionaryPage.Clear();
        if (_pagesBy[(int)ParquetEncoding.RleDictionary] == 0)
        {
            return;
        }

        ReadOnlySpan<byte> values = _entries.WrittenSpan[.._frozenBytes];

        // A dictionary page has no flag that says it is stored as it is: under a codec it is
        // compressed whatever it saves.
        ReadOnlySpan<byte> stored = _codec == CompressionCodec.Uncompressed ? values : Compress(values);
        PageHeader header = new()
        {
            Type = PageType.DictionaryPage,
            UncompressedPageSize = values.Length,
            CompressedPageSize = stored.Length,
            ValueCount = _frozenEntries,
            Encoding = ParquetEncoding.Plain,
        };

        ThriftCompactWriter writer = new(_dictionaryPage);
        header.Write(ref writer, default);
        writer.Flush();
        _chunkUncompressed += _dictionaryPage.Length + values.Length;
        _dictionaryPage.Write(stored);
    }

    private ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> body)
    {
        _compressed.Clear();
        Span<byte> destination = _compressed.GetSpan(PageCodecs.MaxCompressedLength(_codec, body.Length));
        int size = PageCodecs.Compress(_codec, _level, body, destination, _zstd);
        return destination[..size];
    }

    /// <summary>The values of the rows that hold one, in their PLAIN form, appended to <paramref name="target"/>.</summary>
    private void StageValues(CanonicalArena arena, CanonicalNode node, int start, int count, int values, PooledBytes target)
    {
        bool dense = values == count;
        int first = _pageRows;
        switch (_column.Conversion)
        {
            case ValueConversion.Null:
                return;
            case ValueConversion.Bool:
                StageBits(node, start, count, dense, first);
                return;
            case ValueConversion.ByteArray:
                StageBytes(node, start, count, dense, first, target);
                return;
            case ValueConversion.Same:
                int width = _column.ValueWidth;

                // A fixed-size list's rows are its elements', back to back, from its own row on.
                ReadOnlySpan<byte> all = _column.FixedElements
                    ? arena.GetNode(EncodedForms.Canonical(arena, node.ElementsIndex)).Values.Span
                    : node.Values.Span;
                ReadOnlySpan<byte> source = all.Slice(start * width, count * width);
                if (dense)
                {
                    target.Write(source);
                }
                else
                {
                    Compact(source, width, first, count, target.Reserve(values * width));
                }

                return;
            default:
                StageConverted(node, start, count, dense, first, values, target);
                return;
        }
    }

    /// <summary>
    /// The values of <paramref name="source"/> whose rows the page's validity sets from
    /// <paramref name="first"/>, packed into <paramref name="destination"/>: by the core's
    /// compressing kernel at the widths it takes, a row at a time at the others.
    /// </summary>
    private void Compact(ReadOnlySpan<byte> source, int width, int first, int count, Span<byte> destination)
    {
        if (width is 1 or 2 or 4 or 8)
        {
            Span<ulong> mask = _mask.AsSpan(0, (count + 63) >> 6);
            for (int k = 0; k < mask.Length; k++)
            {
                mask[k] = BitWords.Load(_validity, first + (k << 6));
            }

            if ((count & 63) != 0)
            {
                mask[^1] &= BitWords.Mask(count & 63);
            }

            switch (width)
            {
                case 1:
                    MaskFilter.Compress(source, mask, destination);
                    return;
                case 2:
                    MaskFilter.Compress(MemoryMarshal.Cast<byte, ushort>(source), mask, MemoryMarshal.Cast<byte, ushort>(destination));
                    return;
                case 4:
                    MaskFilter.Compress(MemoryMarshal.Cast<byte, uint>(source), mask, MemoryMarshal.Cast<byte, uint>(destination));
                    return;
                default:
                    MaskFilter.Compress(MemoryMarshal.Cast<byte, ulong>(source), mask, MemoryMarshal.Cast<byte, ulong>(destination));
                    return;
            }
        }

        int at = 0;
        for (int row = 0; row < count; row++)
        {
            if (CanonicalSupport.BitAt(_validity, first + row))
            {
                source.Slice(row * width, width).CopyTo(destination.Slice(at, width));
                at += width;
            }
        }
    }

    private void StageBits(CanonicalNode node, int start, int count, bool dense, int first)
    {
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset + start;
        int needed = (_boolBits + count + 7) / 8;
        if (_values.Length < needed)
        {
            _values.Reserve(needed - _values.Length).Clear();
        }

        Span<byte> destination = _values.WrittenSpan;
        if (dense)
        {
            BitmapKernels.CopyRange(bits, offset, destination, _boolBits, count);
            _boolBits += count;
            return;
        }

        for (int row = 0; row < count; row++)
        {
            if (CanonicalSupport.BitAt(_validity, first + row))
            {
                if (CanonicalSupport.BitAt(bits, offset + row))
                {
                    destination[_boolBits >> 3] |= (byte)(1 << (_boolBits & 7));
                }
                else
                {
                    destination[_boolBits >> 3] &= (byte)~(1 << (_boolBits & 7));
                }

                _boolBits++;
            }
        }
    }

    private void StageBytes(CanonicalNode node, int start, int count, bool dense, int first, PooledBytes target)
    {
        ViewValues views = new(node);
        for (int row = 0; row < count; row++)
        {
            if (!dense && !CanonicalSupport.BitAt(_validity, first + row))
            {
                continue;
            }

            ReadOnlySpan<byte> value = views.At(start + row);
            Span<byte> destination = target.Reserve(4 + value.Length);
            BinaryPrimitives.WriteInt32LittleEndian(destination, value.Length);
            value.CopyTo(destination[4..]);
        }
    }

    private void StageConverted(CanonicalNode node, int start, int count, bool dense, int first, int values, PooledBytes target)
    {
        int width = _column.ValueWidth;
        Span<byte> destination = target.Reserve(values * width);
        int at = 0;
        switch (_column.Conversion)
        {
            case ValueConversion.WidenInt8:
            case ValueConversion.WidenInt16:
            case ValueConversion.WidenUInt8:
            case ValueConversion.WidenUInt16:
                ReadOnlySpan<byte> source = node.Values.Span;
                for (int row = 0; row < count; row++)
                {
                    if (dense || CanonicalSupport.BitAt(_validity, first + row))
                    {
                        int r = start + row;
                        int value = _column.Conversion switch
                        {
                            ValueConversion.WidenInt8 => (sbyte)source[r],
                            ValueConversion.WidenInt16 => BinaryPrimitives.ReadInt16LittleEndian(source[(r * 2)..]),
                            ValueConversion.WidenUInt8 => source[r],
                            _ => BinaryPrimitives.ReadUInt16LittleEndian(source[(r * 2)..]),
                        };
                        BinaryPrimitives.WriteInt32LittleEndian(destination[at..], value);
                        at += 4;
                    }
                }

                return;
            default:
                // Decimals: the stored width is the node's, at least the precision's, and the value
                // is read sign-extended from it.
                int stored = DecimalStorage.ByteWidth(node.Storage);
                ReadOnlySpan<byte> decimals = node.Values.Span;
                Span<byte> wide = stackalloc byte[32];
                for (int row = 0; row < count; row++)
                {
                    if (!dense && !CanonicalSupport.BitAt(_validity, first + row))
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> value = decimals.Slice((start + row) * stored, stored);
                    byte sign = (value[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0;
                    wide.Fill(sign);
                    value.CopyTo(wide);
                    Span<byte> into = destination.Slice(at, width);
                    if (_column.Conversion == ValueConversion.DecimalToBigEndian)
                    {
                        for (int b = 0; b < width; b++)
                        {
                            into[b] = wide[width - 1 - b];
                        }
                    }
                    else
                    {
                        wide[..width].CopyTo(into);
                    }

                    at += width;
                }

                return;
        }
    }

    /// <summary>What a page's codes come to.</summary>
    private enum Coding : byte
    {
        /// <summary>The page is its codes.</summary>
        Codes,

        /// <summary>This page is PLAIN, and the chunk's later pages may still be codes.</summary>
        Plain,

        /// <summary>The dictionary stops here: this page and the chunk's later ones are PLAIN.</summary>
        Fallback,
    }
}

/// <summary>A closed column chunk: where it goes, and what its metadata says of it.</summary>
/// <param name="Offset">Where the chunk starts: its dictionary page, or its first data page.</param>
/// <param name="DataPageOffset">Where its first data page starts.</param>
/// <param name="DictionaryPageOffset">Where its dictionary page starts, or -1.</param>
/// <param name="Rows">Its rows.</param>
/// <param name="Entries">Its entries, the values its levels count: its rows, unless it is nested.</param>
/// <param name="Nulls">Its entries without a value.</param>
/// <param name="UncompressedSize">Its pages' bytes, headers included, before compression.</param>
/// <param name="CompressedSize">Its bytes in the file.</param>
/// <param name="Encodings">The encodings its pages use, a bit per encoding.</param>
/// <param name="Bounds">Its least and greatest values, where its domain has them.</param>
/// <param name="Pages">Where each data page lies, for the offset index.</param>
/// <param name="PagesByEncoding">Per encoding, the data pages that took it.</param>
internal sealed record ChunkResult(
    long Offset,
    long DataPageOffset,
    long DictionaryPageOffset,
    long Rows,
    long Entries,
    long Nulls,
    long UncompressedSize,
    long CompressedSize,
    uint Encodings,
    Bounds Bounds,
    PageLocation[] Pages,
    int[] PagesByEncoding);

/// <summary>A column's least and greatest values, compared in its domain, as the PLAIN bytes statistics hold.</summary>
internal readonly record struct Bounds(bool Present, StatisticsDomain Domain, long Min, long Max)
{
    internal static readonly Bounds Empty = new(false, StatisticsDomain.None, 0, 0);

    /// <summary>The bounds of <paramref name="count"/> PLAIN values of <paramref name="domain"/>.</summary>
    internal static Bounds Of(StatisticsDomain domain, ReadOnlySpan<byte> values, int count)
    {
        if (count == 0 || domain == StatisticsDomain.None)
        {
            return new Bounds(false, domain, 0, 0);
        }

        switch (domain)
        {
            case StatisticsDomain.Signed32:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, int>(values[..(count * 4)]), out int min32, out int max32);
                return new Bounds(true, domain, min32, max32);
            case StatisticsDomain.Unsigned32:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, uint>(values[..(count * 4)]), out uint minU32, out uint maxU32);
                return new Bounds(true, domain, minU32, maxU32);
            case StatisticsDomain.Signed64:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, long>(values[..(count * 8)]), out long min64, out long max64);
                return new Bounds(true, domain, min64, max64);
            default:
                BlockStatsPass.Bounds(MemoryMarshal.Cast<byte, ulong>(values[..(count * 8)]), out ulong minU64, out ulong maxU64);
                return new Bounds(true, domain, (long)minU64, (long)maxU64);
        }
    }

    /// <summary>The bounds of both.</summary>
    internal Bounds Merge(Bounds other)
    {
        if (!other.Present)
        {
            return this;
        }

        if (!Present)
        {
            return other;
        }

        return Domain == StatisticsDomain.Unsigned64
            ? new Bounds(true, Domain, (long)Math.Min((ulong)Min, (ulong)other.Min), (long)Math.Max((ulong)Max, (ulong)other.Max))
            : new Bounds(true, Domain, Math.Min(Min, other.Min), Math.Max(Max, other.Max));
    }

    /// <summary>Writes the bound <paramref name="value"/> as PLAIN bytes of the domain's width, returning them.</summary>
    internal ReadOnlySpan<byte> Plain(long value, Span<byte> buffer)
    {
        if (Domain is StatisticsDomain.Signed32 or StatisticsDomain.Unsigned32)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)value);
            return buffer[..4];
        }

        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return buffer[..8];
    }
}
