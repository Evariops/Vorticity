using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
    private readonly Compressors? _compressors;
    private readonly int _blockRows;
    private readonly PooledBytes _values;
    private readonly PooledBytes _levels;
    private readonly PooledBytes _compressed;
    private readonly ChunkBytes _chunk;
    private readonly byte[] _validity;
    private NativeSegmentOwner? _levelBytes;

    /// <summary>The page's validity as the words the compressing kernel takes, a block's worth.</summary>
    private readonly ulong[] _mask;
    private readonly List<PageLocation> _pages = [];

    /// <summary>Whether the column's values are of a kind a dictionary serves, under the profile or the hint.</summary>
    private readonly bool _eligible;

    /// <summary>The encoding the caller pinned the column's data pages to, or <see cref="ParquetEncodingHint.Auto"/>.</summary>
    private readonly ParquetEncodingHint _hint;

    /// <summary>The dictionary's values in their PLAIN form, in code order, the null left out.</summary>
    private readonly PooledBytes _entries;

    /// <summary>
    /// The 16 bytes that open this writer's extension of a page header, the identifier the standard
    /// asks an extension to start with, then room for 63 bytes of padding, zero.
    /// </summary>
    private static readonly byte[] Extension =
    [
        0x56, 0x6F, 0x72, 0x74, 0x69, 0x63, 0x69, 0x74, 0x79, 0x2E, 0x61, 0x6C, 0x69, 0x67, 0x6E, 0x01,
        .. new byte[63],
    ];

    /// <summary>The identifier's bytes, and the field's own: its type, its long-form id and its length.</summary>
    private const int ExtensionBytes = 16 + 1 + 3 + 1;

    /// <summary>
    /// The chunk's first data page's header, written as the chunk closes, where its place in the file
    /// is known: its length then puts every aligned page of the chunk on its boundary.
    /// </summary>
    private PageHeader _first;
    private readonly PooledBytes _firstHeader;

    /// <summary>
    /// Where, counted from a 64-byte boundary, the chunk's data pages were laid out as starting: the
    /// residue the first page's header brings the chunk's place in the file to.
    /// </summary>
    private int _origin;

    /// <summary>Whether a page of the chunk stores its values as they are, aligned.</summary>
    private bool _aligned;

    /// <summary>A header measured before its extension is sized.</summary>
    private readonly PooledBytes _measured;

    /// <summary>A v1 page's levels and values, assembled to be compressed together.</summary>
    private readonly PooledBytes _page;

    /// <summary>A page's codes, RLE/bit-packed behind their width.</summary>
    private readonly PooledBytes _codes;

    /// <summary>The dictionary page, written ahead of the data pages when the chunk closes.</summary>
    private readonly PooledBytes _dictionaryPage;

    private NativeSegmentOwner? _pageCodes;
    private NativeSegmentOwner? _firstOccurrences;
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

    /// <summary>Whether a PLAIN page takes the encoding a trial of every candidate found smallest after compression: under Smallest.</summary>
    private readonly bool _trials;

    /// <summary>Under Smallest, what the trial on the chunk's first PLAIN page chose, or null before it.</summary>
    private ParquetEncoding? _smallest;

    /// <summary>A page encoded otherwise than PLAIN, and a byte array page's values without their lengths.</summary>
    private readonly PooledBytes _encoded;
    private readonly PooledBytes _data;
    private NativeSegmentOwner? _lengths;
    private NativeSegmentOwner? _prefixes;
    private NativeSegmentOwner? _suffixes;

    /// <summary>Per encoding, the chunk's data pages that took it.</summary>
    private readonly int[] _pagesBy = new int[16];

    /// <summary>Whether the chunk's pages split their bytes into streams: decided by a trial on the first page to weigh it.</summary>
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
    private readonly ChunkStatistics _statistics;

    /// <summary>The chunk's size statistics, page by page.</summary>
    private readonly SizeCollector _sizes;

    /// <summary>The CRC-32 a page's checksum is computed by, made at the first.</summary>
    private Crc32? _crc;

    /// <summary>The chunk's Bloom filter, when the column asked for one.</summary>
    private readonly BloomCollector? _bloom;

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

    /// <summary>The fewest rows a page of a block cut into fractions holds: below them, a page is cut by bytes.</summary>
    internal const int MinimumPageRows = 1_024;

    /// <summary>
    /// Whether a page that fills its block is left staged for the writer to close, on its threads,
    /// beside the other columns': a writer of more than one lane.
    /// </summary>
    internal bool DefersPages { get; init; }

    /// <summary>Whether each page gets a <c>crc</c>: the CRC-32 of its bytes as stored past its header.</summary>
    internal bool WriteChecksums { get; init; }

    /// <summary>The bytes a block's staged values may reach in one page before the block is cut into fractions of itself.</summary>
    internal int PageBytes { get; init; } = 1 << 20;

    /// <summary>The form of the data pages: v2, or v1 with the levels inside the compressed bytes.</summary>
    internal DataPageVersion DataPages { get; init; } = DataPageVersion.V2;

    /// <summary>
    /// Whether a page whose values are stored as they are starts them on a 64-byte boundary of the
    /// file, behind an extension of its header that a reader skips.
    /// </summary>
    internal bool AlignUncompressedPages { get; init; } = true;

    /// <summary>What encrypts the column's modules, or null for a column written in plaintext.</summary>
    internal Encryption.ColumnEncryptor? Encryptor { get; init; }

    /// <summary>The ordinal of the row group the pages being written belong to, which their modules' AAD names.</summary>
    internal int RowGroup { get; set; }

    /// <summary>A module's plaintext, made whole before it is encrypted: a header, or a page's levels and values.</summary>
    private PooledBytes? _plain;

    /// <summary>The pool the writer's buffers are of.</summary>
    private readonly AlignedBufferPool _pool;

    /// <summary>
    /// The encoding the caller pinned the column to, which the writer checked the column takes: a
    /// dictionary is then built whatever the profile and kept while it stays within its bound, and
    /// any other encoding builds none.
    /// </summary>
    internal ParquetEncodingHint Hint
    {
        get => _hint;
        init
        {
            _hint = value;
            if (value != ParquetEncodingHint.Auto)
            {
                _eligible = value == ParquetEncodingHint.Dictionary;
                _dictionary = _eligible;
            }
        }
    }

    /// <summary>The codec of the column's pages.</summary>
    internal CompressionCodec Codec => _codec;

    /// <summary>Whether a FLOAT or DOUBLE page may be written ALP, which joins the trial of its chunk's first page.</summary>
    internal bool AllowAlp { get; init; }

    /// <summary>What the column's ALP pages encode a vector through, made at the first.</summary>
    private Encodings.Alp.Scratch? _alp;

    internal ColumnChunkWriter(WriteColumn column, CompressionCodec codec, int level, Compressors? compressors, int blockRows, CompressionProfile profile, AlignedBufferPool pool, double bloomRate = 0, int rowGroupRows = 0)
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
        _compressors = compressors;
        _blockRows = blockRows;
        _values = new PooledBytes(pool);
        _levels = new PooledBytes(pool);
        _compressed = new PooledBytes(pool);
        _chunk = new ChunkBytes(pool);
        _entries = new PooledBytes(pool);
        _codes = new PooledBytes(pool);
        _page = new PooledBytes(pool);
        _dictionaryPage = new PooledBytes(pool);
        _firstHeader = new PooledBytes(pool);
        _pool = pool;
        _measured = new PooledBytes(pool);
        _validity = new byte[(blockRows + 7) / 8 + 8];
        _mask = new ulong[(blockRows + 63) >> 6];
        _statistics = new ChunkStatistics(column);
        _sizes = new SizeCollector(column);
        if (bloomRate > 0 && column.Conversion is not (ValueConversion.Bool or ValueConversion.Null))
        {
            // A flat column's chunk holds a value a row at most, a nested one's any number.
            _bloom = new BloomCollector(column, bloomRate, column.Nested || rowGroupRows <= 0 ? long.MaxValue : rowGroupRows, pool);
        }
        _eligible = profile != CompressionProfile.None && column.Conversion is not (ValueConversion.Bool or ValueConversion.Null) && !column.FixedElements;
        _encodings = profile is CompressionProfile.Auto or CompressionProfile.Smallest;
        _trials = profile == CompressionProfile.Smallest;
        _encoded = new PooledBytes(pool);
        _data = new PooledBytes(pool);
        _dictionary = _eligible;
    }

    internal WriteColumn Column => _column;

    /// <summary>The bytes of the pages closed so far, waiting for the row group to close.</summary>
    internal long BufferedBytes => _chunk.Length + _values.Length + _entries.Length;

    /// <summary>
    /// Lends the closed chunk's bytes to <paramref name="sink"/>, its dictionary page first, between
    /// <see cref="Close"/> and <see cref="Reset"/>: they stay as they are until the sink's next flush
    /// completes, which the caller awaits before <see cref="Reset"/>.
    /// </summary>
    internal async ValueTask LendChunkAsync(ISegmentSink sink, CancellationToken cancellationToken)
    {
        if (_dictionaryPage.Length > 0)
        {
            await sink.LendAsync(_dictionaryPage.Written, cancellationToken).ConfigureAwait(false);
        }

        await sink.LendAsync(_firstHeader.Written, cancellationToken).ConfigureAwait(false);
        await _chunk.LendToAsync(sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the closed chunk has a Bloom filter for <see cref="WriteBloomAsync"/> to write.</summary>
    internal bool HasBloom => _bloom is { Closed: true };

    /// <summary>Writes the closed chunk's Bloom filter, its header then its bitset, after the row group's chunks.</summary>
    /// <returns>The bytes written.</returns>
    internal ValueTask<int> WriteBloomAsync(ISegmentSink sink, CancellationToken cancellationToken) =>
        _bloom!.WriteToAsync(sink, Encryptor, RowGroup, cancellationToken);

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
            if (_pageRows == _blockRows && !DefersPages)
            {
                ClosePage();
            }
        }
    }

    /// <summary>
    /// Closes the page being staged, if it holds a row: at the end of its block, or of a row group.
    /// </summary>
    /// <remarks>
    /// A block whose staged values pass <see cref="PageBytes"/> is written as pages of power-of-two
    /// fractions of it, each range halved while it passes, down to <see cref="MinimumPageRows"/>
    /// rows, so that every page still starts on the grid; a range of those that still passes is cut
    /// by bytes, which leaves the grid for this column alone. A page the dictionary codes is written
    /// whole, its codes far smaller than its values, and so is a page of booleans, a bit a row.
    /// </remarks>
    internal void ClosePage()
    {
        bool nested = _shredder is not null;
        int rows = nested ? _pageNestedRows : _pageRows;
        if (rows == 0)
        {
            return;
        }

        if (!nested && _column.Nullable)
        {
            // Every row's level, of which each page encodes its own.
            BitPacking.Unpack8(_validity, 1, Scratch<byte>(ref _levelBytes, rows));
        }

        // The dictionary weighs the whole page first: a page it codes is never cut.
        Coding coding = Coding.Plain;
        if (_dictionary && _pageFirstCode >= 0)
        {
            coding = EncodeCodes(_pageRows, _values.Length);
            if (coding == Coding.Fallback)
            {
                GiveUpDictionary();
            }
        }

        int entries = nested ? _pageEntries : rows;
        if (coding == Coding.Codes || _values.Length <= PageBytes || _column.Conversion == ValueConversion.Bool)
        {
            WritePage(new PageRange(0, rows, 0, entries, 0, _pageValues, 0, _values.Length), coding);
        }
        else
        {
            using PageCuts cuts = new(this, rows, entries);
            Split(cuts, 0, _blockRows, rows);
        }

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

    /// <summary>
    /// Writes the staged rows from <paramref name="from"/> of a range of the block of
    /// <paramref name="size"/> rows, short of <paramref name="rows"/>: as one page when its values stay
    /// within <see cref="PageBytes"/>, as its two halves while they hold <see cref="MinimumPageRows"/>,
    /// and otherwise as pages of as many rows as stay within it, one at least.
    /// </summary>
    private void Split(PageCuts cuts, int from, int size, int rows)
    {
        int to = Math.Min(from + size, rows);
        if (from >= to)
        {
            return;
        }

        if (cuts.Bytes(from, to) <= PageBytes)
        {
            WritePage(cuts.Range(from, to), Coding.Plain);
            return;
        }

        int half = size / 2;
        if (half >= MinimumPageRows)
        {
            Split(cuts, from, half, rows);
            Split(cuts, from + half, size - half, rows);
            return;
        }

        while (from < to)
        {
            int low = from + 1;
            int high = to;
            while (low < high)
            {
                int middle = low + ((high - low + 1) / 2);
                if (cuts.Bytes(from, middle) <= PageBytes)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            WritePage(cuts.Range(from, low), Coding.Plain);
            from = low;
        }
    }

    /// <summary>
    /// Encodes, compresses and appends the page of the staged rows <paramref name="range"/> covers:
    /// as the dictionary's codes when <paramref name="coding"/> says the whole page took them.
    /// </summary>
    private void WritePage(in PageRange range, Coding coding)
    {
        bool nested = _shredder is not null;
        int rows = range.To - range.From;
        int entries = range.EndEntry - range.FirstEntry;
        int values = range.EndValue - range.FirstValue;
        int nulls = entries - values;
        _levels.Clear();
        int repetitionLength = 0;
        if (nested)
        {
            // The repetition levels, then the definition levels, each at the width of its maximum.
            // A level of maximum 0 is neither staged nor written.
            if (_column.MaxRepetitionLevel > 0)
            {
                repetitionLength = Levels(_repetitionLevels!.WrittenSpan[range.FirstEntry..range.EndEntry], _column.MaxRepetitionLevel);
            }

            if (_column.MaxDefinitionLevel > 0)
            {
                Levels(_definitionLevels!.WrittenSpan[range.FirstEntry..range.EndEntry], _column.MaxDefinitionLevel);
            }
        }
        else if (_column.Nullable)
        {
            _levels.Truncate(RleHybridEncoder.Encode(Held<byte>(_levelBytes).Slice(range.From, rows), 1, _levels.Reserve(RleHybridEncoder.MaxSize(rows, 1))));
        }

        ReadOnlySpan<byte> body = _column.Conversion == ValueConversion.Bool
            ? _values.WrittenSpan[..((_boolBits + 7) / 8)]
            : _values.WrittenSpan[range.FirstByte..range.EndByte];
        _statistics.AddPage(body, values, nulls);
        _sizes.AddPage(
            body,
            values,
            nested && _column.MaxRepetitionLevel > 0 ? _repetitionLevels!.WrittenSpan[range.FirstEntry..range.EndEntry] : default,
            nested && _column.MaxDefinitionLevel > 1 ? _definitionLevels!.WrittenSpan[range.FirstEntry..range.EndEntry] : default);

        ParquetEncoding encoding = ParquetEncoding.Plain;
        if (coding == Coding.Codes)
        {
            body = _codes.WrittenSpan;
            encoding = ParquetEncoding.RleDictionary;
            _frozenEntries = _entryCount;
            _frozenBytes = _entries.Length;
        }

        // A page of codes holds the dictionary's entries, which the filter takes as the chunk closes.
        if (encoding != ParquetEncoding.RleDictionary)
        {
            _bloom?.AddPage(body, values);
        }

        if (encoding == ParquetEncoding.Plain && values > 0)
        {
            if (_hint is ParquetEncodingHint.Auto)
            {
                encoding = _trials ? Smallest(ref body, values) : _encodings ? Choose(ref body, values) : encoding;
            }
            else if (_hint is not (ParquetEncodingHint.Plain or ParquetEncodingHint.Dictionary))
            {
                encoding = Pin(ref body, values);
            }
        }

        _pagesBy[(int)encoding]++;

        bool compressed = _codec != CompressionCodec.Uncompressed;
        ReadOnlySpan<byte> levels = _levels.WrittenSpan;
        ReadOnlySpan<byte> stored = body;
        PageHeader header;
        if (DataPages == DataPageVersion.V1)
        {
            // The levels inside the page's bytes, and the whole compressed, whatever it saves.
            ReadOnlySpan<byte> page = V1(levels, repetitionLength, body);
            stored = compressed ? Compress(page) : page;
            header = new PageHeader
            {
                Type = PageType.DataPage,
                UncompressedPageSize = page.Length,
                CompressedPageSize = stored.Length,
                ValueCount = entries,
                Encoding = encoding,
                DefinitionLevelEncoding = ParquetEncoding.Rle,
                RepetitionLevelEncoding = ParquetEncoding.Rle,
            };
            levels = default;
        }
        else
        {
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

            header = new PageHeader
            {
                Type = PageType.DataPageV2,
                UncompressedPageSize = levels.Length + body.Length,
                CompressedPageSize = levels.Length + stored.Length,
                ValueCount = entries,
                NullCount = nulls,
                RowCount = rows,
                Encoding = encoding,
                DefinitionLevelsLength = levels.Length - repetitionLength,
                RepetitionLevelsLength = repetitionLength,
                IsCompressed = compressed || _codec == CompressionCodec.Uncompressed,
            };
        }

        if (WriteChecksums)
        {
            header.HasCrc = true;
            header.Crc = Checksum(levels, stored);
        }

        // Values stored as they are start on a 64-byte boundary of the file, behind the header's
        // extension; what lies between the header and them is the levels, inside a v1 page's bytes.
        bool aligned = AlignUncompressedPages && Encryptor is null && !compressed && body.Length > 0;
        int lead = levels.Length + (stored.Length - body.Length);
        long pageStart = _chunk.Length;
        int headerLength = 0;
        if (Encryptor is { } encryptor)
        {
            // Two modules: the body, the page's levels and values, and the header, whose size of the
            // page is the body's module, length to tag. The first header waits for the close.
            header.CompressedPageSize = levels.Length + stored.Length + encryptor.Overhead(Encryption.ModuleType.DataPage);
            if (_pages.Count == 0)
            {
                _first = header;
                _origin = 0;
            }
            else
            {
                headerLength = EncryptHeader(encryptor, header, Encryption.ModuleType.DataPageHeader, _pages.Count, _chunk);
            }

            EncryptBody(encryptor, levels, stored, Encryption.ModuleType.DataPage, _pages.Count, _chunk);
        }
        else if (_pages.Count == 0)
        {
            // The chunk's first data page: its header waits for the close, and the pages count their
            // places from where its values start, a boundary when they are aligned.
            _first = header;
            _origin = aligned ? -lead & 63 : 0;
        }
        else
        {
            ThriftCompactWriter writer = new(_chunk);
            header.Write(ref writer, aligned ? Padding(header, _origin + pageStart, lead) : default);
            writer.Flush();
            headerLength = (int)(_chunk.Length - pageStart);
        }

        _aligned |= aligned;
        if (Encryptor is null)
        {
            _chunk.Write(levels);
            _chunk.Write(stored);
        }

        _pages.Add(new PageLocation(pageStart, (int)(_chunk.Length - pageStart), _chunkRows));
        _chunkUncompressed += headerLength + header.UncompressedPageSize;
        _chunkRows += rows;
        _chunkEntries += entries;
        _chunkNulls += nulls;
    }

    /// <summary>
    /// Appends <paramref name="header"/> to <paramref name="into"/> as a module of
    /// <paramref name="type"/>, of data page <paramref name="page"/> or of the dictionary's; its bytes.
    /// </summary>
    private int EncryptHeader(Encryption.ColumnEncryptor encryptor, in PageHeader header, Encryption.ModuleType type, int page, System.Buffers.IBufferWriter<byte> into)
    {
        PooledBytes plain = _plain ??= new PooledBytes(_pool);
        plain.Clear();
        ThriftCompactWriter writer = new(plain);
        header.Write(ref writer, default);
        writer.Flush();
        return encryptor.Encrypt(plain.Written.Span, type, RowGroup, page, into);
    }

    /// <summary>Appends a page's <paramref name="levels"/> then <paramref name="stored"/> values to <paramref name="into"/> as one module of <paramref name="type"/>.</summary>
    private void EncryptBody(Encryption.ColumnEncryptor encryptor, ReadOnlySpan<byte> levels, ReadOnlySpan<byte> stored, Encryption.ModuleType type, int page, System.Buffers.IBufferWriter<byte> into)
    {
        if (levels.IsEmpty)
        {
            encryptor.Encrypt(stored, type, RowGroup, page, into);
            return;
        }

        PooledBytes plain = _plain ??= new PooledBytes(_pool);
        plain.Clear();
        plain.Write(levels);
        plain.Write(stored);
        encryptor.Encrypt(plain.Written.Span, type, RowGroup, page, into);
    }

    /// <summary>
    /// This writer's extension of <paramref name="header"/>, its identifier then as many zeros as put
    /// the values it heads, <paramref name="lead"/> bytes past it, on a 64-byte boundary, when the
    /// header starts <paramref name="at"/> bytes past one.
    /// </summary>
    private ReadOnlySpan<byte> Padding(scoped in PageHeader header, long at, int lead)
    {
        _measured.Clear();
        ThriftCompactWriter writer = new(_measured);
        header.Write(ref writer, default);
        writer.Flush();
        int pad = (int)(-(at + _measured.Length + ExtensionBytes + lead) & 63);
        return Extension.AsSpan(0, 16 + pad);
    }

    /// <summary>
    /// A v1 page's bytes before compression: the repetition levels, then the definition levels, each
    /// kind its column has behind its length, then <paramref name="body"/>.
    /// </summary>
    private ReadOnlySpan<byte> V1(ReadOnlySpan<byte> levels, int repetitionLength, ReadOnlySpan<byte> body)
    {
        bool nested = _shredder is not null;
        _page.Clear();
        if (nested && _column.MaxRepetitionLevel > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_page.Reserve(sizeof(int)), repetitionLength);
            _page.Write(levels[..repetitionLength]);
        }

        if (nested ? _column.MaxDefinitionLevel > 0 : _column.Nullable)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_page.Reserve(sizeof(int)), levels.Length - repetitionLength);
            _page.Write(levels[repetitionLength..]);
        }

        _page.Write(body);
        return _page.WrittenSpan;
    }

    /// <summary>A page's share of the staged rows: its rows, its entries, its values and their bytes.</summary>
    private readonly record struct PageRange(int From, int To, int FirstEntry, int EndEntry, int FirstValue, int EndValue, int FirstByte, int EndByte);

    /// <summary>
    /// Where each staged row's entries, values and bytes begin, which a block cut into pages is cut
    /// by: rows to entries by the repetition levels, entries to values by the definition levels or the
    /// validity, values to bytes by their width or their lengths. Made only for a block that is cut,
    /// over arrays of the shared pool.
    /// </summary>
    private sealed class PageCuts : IDisposable
    {
        /// <summary>Per row, and one past the last, its first entry.</summary>
        private readonly int[] _entries;

        /// <summary>Per entry, and one past the last, the values before it.</summary>
        private readonly int[] _values;

        /// <summary>Per value of a byte array, and one past the last, its first byte; null for a fixed width.</summary>
        private readonly int[]? _bytes;

        private readonly int _width;

        internal PageCuts(ColumnChunkWriter writer, int rows, int entries)
        {
            WriteColumn column = writer._column;
            bool nested = writer._shredder is not null;
            _entries = ArrayPool<int>.Shared.Rent(rows + 1);
            _values = ArrayPool<int>.Shared.Rent(entries + 1);
            if (nested && column.MaxRepetitionLevel > 0)
            {
                // A row begins at each entry of repetition level 0.
                ReadOnlySpan<byte> repetition = writer._repetitionLevels!.WrittenSpan;
                int row = 0;
                for (int entry = 0; entry < entries; entry++)
                {
                    if (repetition[entry] == 0)
                    {
                        _entries[row++] = entry;
                    }
                }
            }
            else
            {
                for (int row = 0; row < rows; row++)
                {
                    _entries[row] = row;
                }
            }

            _entries[rows] = entries;
            _values[0] = 0;
            if (nested && column.MaxDefinitionLevel > 0)
            {
                ReadOnlySpan<byte> definition = writer._definitionLevels!.WrittenSpan;
                int defined = column.MaxDefinitionLevel;
                for (int entry = 0; entry < entries; entry++)
                {
                    _values[entry + 1] = _values[entry] + (definition[entry] == defined ? 1 : 0);
                }
            }
            else if (!nested && column.Nullable)
            {
                for (int row = 0; row < rows; row++)
                {
                    _values[row + 1] = _values[row] + (CanonicalSupport.BitAt(writer._validity, row) ? 1 : 0);
                }
            }
            else
            {
                for (int entry = 1; entry <= entries; entry++)
                {
                    _values[entry] = entry;
                }
            }

            if (column.Conversion == ValueConversion.ByteArray)
            {
                int values = writer._pageValues;
                _bytes = ArrayPool<int>.Shared.Rent(values + 1);
                ReadOnlySpan<byte> plain = writer._values.WrittenSpan;
                int at = 0;
                for (int value = 0; value < values; value++)
                {
                    _bytes[value] = at;
                    at += sizeof(int) + BinaryPrimitives.ReadInt32LittleEndian(plain[at..]);
                }

                _bytes[values] = at;
            }
            else
            {
                _width = column.ValueWidth;
            }
        }

        /// <summary>The staged bytes of the values of rows <paramref name="from"/> to <paramref name="to"/>.</summary>
        internal long Bytes(int from, int to) => (long)ByteOf(_values[_entries[to]]) - ByteOf(_values[_entries[from]]);

        /// <summary>The page of rows <paramref name="from"/> to <paramref name="to"/>.</summary>
        internal PageRange Range(int from, int to)
        {
            int firstEntry = _entries[from];
            int endEntry = _entries[to];
            int firstValue = _values[firstEntry];
            int endValue = _values[endEntry];
            return new PageRange(from, to, firstEntry, endEntry, firstValue, endValue, ByteOf(firstValue), ByteOf(endValue));
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_entries);
            ArrayPool<int>.Shared.Return(_values);
            if (_bytes is not null)
            {
                ArrayPool<int>.Shared.Return(_bytes);
            }
        }

        private int ByteOf(int value) => _bytes is null ? value * _width : _bytes[value];
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

    /// <summary>
    /// <paramref name="count"/> elements of one of the arrays a page's values are encoded through,
    /// made at its first use and grown with the pages, a nested page's holding more values than rows:
    /// a column allocates only the arrays its encodings use, a fixed-width one with no null and no
    /// dictionary none.
    /// </summary>
    private Span<T> Scratch<T>(ref NativeSegmentOwner? block, int count)
        where T : unmanaged
    {
        int held = Held<T>(block).Length;
        if (held < count)
        {
            // A block of the engine's pool, which the writer gives back as it closes: the next
            // writer's columns rent the same blocks rather than allocate their arrays again.
            int grown = Math.Max(count, Math.Max(_blockRows, held * 2));
            block?.Dispose();
            block = null;
            block = _pool.Rent(checked(grown * Unsafe.SizeOf<T>()), 64);
        }

        return Held<T>(block)[..count];
    }

    /// <summary>The elements one of the scratch arrays holds; empty before its first use.</summary>
    private static Span<T> Held<T>(NativeSegmentOwner? block)
        where T : unmanaged =>
        block is null ? default : MemoryMarshal.Cast<byte, T>(block.WritableSpan);

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
            if (_pageNestedRows == _blockRows && !DefersPages)
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
    /// <see cref="LendChunkAsync"/> and calls <see cref="Reset"/> once the sink has flushed it.
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

        WriteDictionaryPage(offset);
        long dictionary = _dictionaryPage.Length;
        WriteFirstHeader(offset + dictionary);
        long first = _firstHeader.Length;
        PageLocation[] pages = new PageLocation[_pages.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            PageLocation page = _pages[i];
            pages[i] = i == 0
                ? page with { Offset = offset + dictionary, Size = (int)first + page.Size }
                : page with { Offset = offset + dictionary + first + page.Offset };
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

        // The dictionary page's entries are the values of the pages of codes.
        _bloom?.Close(_entries.WrittenSpan[.._frozenBytes], _pagesBy[(int)ParquetEncoding.RleDictionary] > 0 ? _frozenEntries : 0);
        return new ChunkResult(
            offset,
            offset + dictionary,
            dictionary > 0 ? offset : -1,
            _chunkRows,
            _chunkEntries,
            _chunkNulls,
            _chunkUncompressed,
            _chunk.Length + dictionary + first,
            encodings,
            _statistics.Close(),
            pages,
            (int[])_pagesBy.Clone(),
            _sizes.Close(),
            DataPages == DataPageVersion.V1 ? PageType.DataPage : PageType.DataPageV2);
    }

    /// <summary>Forgets the closed chunk, keeping the buffers for the next row group's.</summary>
    internal void Reset()
    {
        _chunk.Clear();
        _firstHeader.Clear();
        _first = default;
        _origin = 0;
        _aligned = false;
        _pages.Clear();
        _chunkRows = 0;
        _chunkEntries = 0;
        _chunkNulls = 0;
        _chunkUncompressed = 0;
        _table?.Reset();
        _entries.Clear();
        _dictionaryPage.Clear();
        _entryCount = 0;
        _frozenEntries = 0;
        _frozenBytes = 0;
        Array.Clear(_pagesBy);
        _split = null;
        _smallest = null;
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
        _page.Dispose();
        _dictionaryPage.Dispose();
        _firstHeader.Dispose();
        _plain?.Dispose();
        _measured.Dispose();
        _encoded.Dispose();
        _data.Dispose();
        _repetitionLevels?.Dispose();
        _bloom?.Dispose();
        _shredder?.Dispose();
        _definitionLevels?.Dispose();
        _table?.Reset();
        _levelBytes?.Dispose();
        _pageCodes?.Dispose();
        _firstOccurrences?.Dispose();
        _lengths?.Dispose();
        _prefixes?.Dispose();
        _suffixes?.Dispose();
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
    /// Gives the dictionary up for the rest of the chunk, and the distinct table's buffers back to
    /// the pool at once rather than at the chunk's close: the columns of a wide schema whose values
    /// are all distinct then pass the same few buffers on, where each would hold its own.
    /// </summary>
    private void GiveUpDictionary()
    {
        _dictionary = false;
        _table?.Reset();
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
            GiveUpDictionary();
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
        Span<int> firsts = Scratch<int>(ref _firstOccurrences, distinct - distinctBefore);
        int found = 0;
        for (int code = distinctBefore; code < distinct; code++)
        {
            if (code != nullCode)
            {
                firsts[found++] = start + (first[code] - rowsBefore);
            }
        }

        if (found > 0)
        {
            int entries = CanonicalFilter.Apply(arena, index, firsts[..found]);
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
        Span<uint> kept = Scratch<uint>(ref _pageCodes, rows);
        for (int row = 0; row < rows; row++)
        {
            if (_rowValidity && !CanonicalSupport.BitAt(_validity, row))
            {
                continue;
            }

            int code = codes[row];
            kept[count++] = (uint)(nullCode >= 0 && code > nullCode ? code - 1 : code);
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
        ReadOnlySpan<uint> pageCodes = Held<uint>(_pageCodes)[..count];

        // Priced by writing them, and dropped when the dictionary stops here.
        _codes.Clear();
        Span<byte> into = _codes.Reserve(1 + RleHybridEncoder.MaxSize(count, width));
        into[0] = (byte)width;
        int size = 1 + RleHybridEncoder.Encode(pageCodes, width, into[1..]);
        _codes.Truncate(size);

        // The dictionary pays while the chunk's codes and the dictionary page together take fewer
        // bytes than the values they stand for: a column of values that seldom repeat stops here,
        // its dictionary as large as its values. A column pinned to it keeps it within its bound.
        if (_hint != ParquetEncodingHint.Dictionary && _entries.Length + _codeBytes + size >= _plainBytes + plain)
        {
            return Coding.Fallback;
        }

        _codeBytes += size;
        _plainBytes += plain;
        return Coding.Codes;
    }

    /// <summary>
    /// The encoding a PLAIN page takes where another pays, <paramref name="body"/> then its bytes:
    /// DELTA_BINARY_PACKED for integers and DELTA_BYTE_ARRAY for fixed-length byte arrays, priced
    /// exactly, and where neither pays BYTE_STREAM_SPLIT under a codec, by a trial, as for floats; the
    /// delta encodings of byte arrays, priced exactly; RLE for booleans. An encoding is taken when it
    /// saves an eighth of the bytes, which pays for its slower decode, but for a byte array's lengths,
    /// which always do.
    /// </summary>
    private ParquetEncoding Choose(ref ReadOnlySpan<byte> body, int count)
    {
        switch (_column.Physical)
        {
            // Under a codec, an integer's encodings are priced by what the codec makes of them, on the
            // chunk's first PLAIN page: deltas a codec cannot compress further can store more than
            // the plain values or their split bytes it does, timestamps a few seconds apart among them.
            case PhysicalType.Int32 or PhysicalType.Int64 when _codec != CompressionCodec.Uncompressed:
                return Smallest(ref body, count, eighth: true);

            case PhysicalType.Int32:
            {
                // Priced by writing it, and dropped when it does not pay.
                ReadOnlySpan<int> values = MemoryMarshal.Cast<byte, int>(body);
                _encoded.Clear();
                int size = DeltaBinaryPacked.Encode32(values, _encoded.Reserve(DeltaBinaryPacked.MaxSize32(values.Length)));
                if (size > body.Length - (body.Length / 8))
                {
                    return Split(ref body, sizeof(int));
                }

                _encoded.Truncate(size);
                body = _encoded.WrittenSpan;
                return ParquetEncoding.DeltaBinaryPacked;
            }

            case PhysicalType.Int64:
            {
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(body);
                _encoded.Clear();
                int size = DeltaBinaryPacked.Encode64(values, _encoded.Reserve(DeltaBinaryPacked.MaxSize64(values.Length)));
                if (size > body.Length - (body.Length / 8))
                {
                    return Split(ref body, sizeof(long));
                }

                _encoded.Truncate(size);
                body = _encoded.WrittenSpan;
                return ParquetEncoding.DeltaBinaryPacked;
            }

            case PhysicalType.Float:
                return AllowAlp ? Smallest(ref body, count, eighth: true) : Split(ref body, sizeof(float));

            case PhysicalType.Double:
                return AllowAlp ? Smallest(ref body, count, eighth: true) : Split(ref body, sizeof(double));

            case PhysicalType.FixedLenByteArray:
                return ChooseFixed(ref body, count);

            case PhysicalType.ByteArray when _column.Conversion == ValueConversion.ByteArray:
                return ChooseBytes(ref body, count);

            case PhysicalType.Boolean:
            {
                int size = RunsSize(body, count);
                if (size >= body.Length)
                {
                    return ParquetEncoding.Plain;
                }

                body = Runs(count, size);
                return ParquetEncoding.Rle;
            }

            default:
                return ParquetEncoding.Plain;
        }
    }

    /// <summary>
    /// The page in the encoding the caller pinned its column to, <paramref name="body"/> then its
    /// bytes, unpriced: the writer checked the column's type takes it.
    /// </summary>
    private ParquetEncoding Pin(ref ReadOnlySpan<byte> body, int count)
    {
        ParquetEncoding encoding = EncodingOf(_hint);
        body = Encode(encoding, body, count);
        return encoding;
    }

    /// <summary>
    /// Under <see cref="CompressionProfile.Smallest"/>: the encoding that stores the chunk's first
    /// PLAIN page in the fewest bytes, each candidate encoded and compressed by the chunk's codec at
    /// its level, PLAIN among them; every later PLAIN page of the chunk then takes it.
    /// </summary>
    private ParquetEncoding Smallest(ref ReadOnlySpan<byte> body, int count, bool eighth = false)
    {
        if (_smallest is null)
        {
            // Under Auto, a candidate is taken when it saves an eighth of what PLAIN stores, which
            // pays for its slower decode; under Smallest, when it saves a byte.
            ParquetEncoding best = ParquetEncoding.Plain;
            long plain = Stored(body);
            long least = eighth ? plain - (plain / 8) + 1 : plain;
            foreach (ParquetEncoding candidate in Candidates())
            {
                long stored = Stored(Encode(candidate, body, count));
                if (stored < least)
                {
                    least = stored;
                    best = candidate;
                }
            }

            _smallest = best;
        }

        if (_smallest is ParquetEncoding.Plain)
        {
            return ParquetEncoding.Plain;
        }

        body = Encode(_smallest.Value, body, count);
        return _smallest.Value;
    }

    /// <summary>The encodings other than PLAIN the standard gives the column's physical type, as a dictionary's fallback.</summary>
    private ReadOnlySpan<ParquetEncoding> Candidates() => _column.Physical switch
    {
        PhysicalType.Int32 or PhysicalType.Int64 => [ParquetEncoding.DeltaBinaryPacked, ParquetEncoding.ByteStreamSplit],
        PhysicalType.Float or PhysicalType.Double when AllowAlp => [ParquetEncoding.ByteStreamSplit, ParquetEncoding.Alp],
        PhysicalType.Float or PhysicalType.Double => [ParquetEncoding.ByteStreamSplit],
        PhysicalType.ByteArray => [ParquetEncoding.DeltaLengthByteArray, ParquetEncoding.DeltaByteArray],
        PhysicalType.FixedLenByteArray => [ParquetEncoding.DeltaByteArray, ParquetEncoding.ByteStreamSplit],
        PhysicalType.Boolean => [ParquetEncoding.Rle],
        _ => [],
    };

    /// <summary>The bytes a page of <paramref name="bytes"/> takes in the file: compressed when that saves an eighth, as is.</summary>
    private long Stored(ReadOnlySpan<byte> bytes)
    {
        if (_codec == CompressionCodec.Uncompressed)
        {
            return bytes.Length;
        }

        int compressed = Compress(bytes).Length;
        return compressed <= bytes.Length - (bytes.Length / 8) ? compressed : bytes.Length;
    }

    /// <summary>
    /// The PLAIN page <paramref name="body"/> of <paramref name="count"/> values in
    /// <paramref name="encoding"/>, one the standard gives the column's physical type; its bytes.
    /// </summary>
    private ReadOnlySpan<byte> Encode(ParquetEncoding encoding, ReadOnlySpan<byte> body, int count)
    {
        _encoded.Clear();
        switch (encoding)
        {
            case ParquetEncoding.DeltaBinaryPacked when _column.Physical == PhysicalType.Int32:
            {
                ReadOnlySpan<int> values = MemoryMarshal.Cast<byte, int>(body);
                _encoded.Truncate(DeltaBinaryPacked.Encode32(values, _encoded.Reserve(DeltaBinaryPacked.MaxSize32(values.Length))));
                break;
            }

            case ParquetEncoding.DeltaBinaryPacked:
            {
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(body);
                _encoded.Truncate(DeltaBinaryPacked.Encode64(values, _encoded.Reserve(DeltaBinaryPacked.MaxSize64(values.Length))));
                break;
            }

            case ParquetEncoding.ByteStreamSplit:
                ByteStreamSplit.Encode(body, _column.ValueWidth, _encoded.Reserve(body.Length));
                break;

            case ParquetEncoding.DeltaLengthByteArray:
            {
                ReadOnlySpan<byte> data = Unprefix(body, count);
                ReadOnlySpan<int> lengths = Held<int>(_lengths)[..count];
                DeltaByteArrays.EncodeLengths(lengths, data, _encoded.Reserve(DeltaByteArrays.SizeLengths(lengths, data.Length)));
                break;
            }

            case ParquetEncoding.DeltaByteArray:
            {
                // A fixed-length array's values lie back to back, each the type's length.
                ReadOnlySpan<byte> data = body;
                if (_column.Physical == PhysicalType.ByteArray)
                {
                    data = Unprefix(body, count);
                }
                else
                {
                    Scratch<int>(ref _lengths, count).Fill(_column.ValueWidth);
                }

                ReadOnlySpan<int> lengths = Held<int>(_lengths)[..count];
                Span<int> prefixes = Scratch<int>(ref _prefixes, count);
                Span<int> suffixes = Scratch<int>(ref _suffixes, count);
                int rest = DeltaByteArrays.Prefixes(data, lengths, prefixes, suffixes);
                DeltaByteArrays.EncodePrefixes(data, lengths, prefixes, suffixes, _encoded.Reserve(DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest)));
                break;
            }

            case ParquetEncoding.Alp when _column.Physical == PhysicalType.Float:
            {
                ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(body)[..count];
                _encoded.Truncate(Encodings.Alp.Encode(values, _encoded.Reserve(Encodings.Alp.MaxSize(count, sizeof(float))), _alp ??= new()));
                break;
            }

            case ParquetEncoding.Alp:
            {
                ReadOnlySpan<double> values = MemoryMarshal.Cast<byte, double>(body)[..count];
                _encoded.Truncate(Encodings.Alp.Encode(values, _encoded.Reserve(Encodings.Alp.MaxSize(count, sizeof(double))), _alp ??= new()));
                break;
            }

            case ParquetEncoding.Rle:
                return Runs(count, RunsSize(body, count));

            default:
                return body;
        }

        return _encoded.WrittenSpan;
    }

    /// <summary>The encoding a hint names.</summary>
    internal static ParquetEncoding EncodingOf(ParquetEncodingHint hint) => hint switch
    {
        ParquetEncodingHint.Dictionary => ParquetEncoding.RleDictionary,
        ParquetEncodingHint.DeltaBinaryPacked => ParquetEncoding.DeltaBinaryPacked,
        ParquetEncodingHint.DeltaLengthByteArray => ParquetEncoding.DeltaLengthByteArray,
        ParquetEncodingHint.DeltaByteArray => ParquetEncoding.DeltaByteArray,
        ParquetEncodingHint.ByteStreamSplit => ParquetEncoding.ByteStreamSplit,
        ParquetEncodingHint.Rle => ParquetEncoding.Rle,
        ParquetEncodingHint.Alp => ParquetEncoding.Alp,
        _ => ParquetEncoding.Plain,
    };

    /// <summary>
    /// The bytes of a boolean page as RLE, behind its four-byte length: its bits unpacked into a byte
    /// each first, which <see cref="Runs"/> then encodes.
    /// </summary>
    private int RunsSize(ReadOnlySpan<byte> body, int count)
    {
        Span<byte> values = Scratch<byte>(ref _levelBytes, count);
        BitPacking.Unpack8(body, 1, values);
        return sizeof(int) + RleHybridEncoder.Size(values, 1);
    }

    /// <summary>The booleans <see cref="RunsSize"/> unpacked, as RLE behind their length in <paramref name="size"/> bytes.</summary>
    private ReadOnlySpan<byte> Runs(int count, int size)
    {
        _encoded.Clear();
        Span<byte> into = _encoded.Reserve(size);
        BinaryPrimitives.WriteInt32LittleEndian(into, size - sizeof(int));
        RleHybridEncoder.Encode(Held<byte>(_levelBytes)[..count], 1, into[sizeof(int)..]);
        return _encoded.WrittenSpan;
    }

    /// <summary>A PLAIN byte array page's values without their lengths, which go to <see cref="_lengths"/>.</summary>
    private ReadOnlySpan<byte> Unprefix(ReadOnlySpan<byte> body, int count)
    {
        Span<int> lengths = Scratch<int>(ref _lengths, count);
        _data.Clear();
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
            lengths[i] = length;
            _data.Write(body.Slice(at + sizeof(int), length));
            at += sizeof(int) + length;
        }

        return _data.WrittenSpan;
    }

    /// <summary>
    /// BYTE_STREAM_SPLIT for values of <paramref name="width"/> bytes under a codec, when the trial on
    /// the chunk's first page to get here found the codec makes the split values an eighth smaller
    /// than the PLAIN ones; PLAIN otherwise.
    /// </summary>
    private ParquetEncoding Split(ref ReadOnlySpan<byte> body, int width)
    {
        if (_codec == CompressionCodec.Uncompressed || _split == false)
        {
            return ParquetEncoding.Plain;
        }

        _encoded.Clear();
        ByteStreamSplit.Encode(body, width, _encoded.Reserve(body.Length));
        if (_split is null)
        {
            // The trial: what the codec makes of either form of the page.
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

    /// <summary>
    /// A fixed-length byte array page's encoding: DELTA_BYTE_ARRAY when the prefixes neighbours share
    /// save an eighth, as the sign bytes of a wide decimal's small values do, priced exactly; else
    /// BYTE_STREAM_SPLIT by trial.
    /// </summary>
    private ParquetEncoding ChooseFixed(ref ReadOnlySpan<byte> body, int count)
    {
        int width = _column.ValueWidth;
        Span<int> lengths = Scratch<int>(ref _lengths, count);
        lengths.Fill(width);
        Span<int> prefixes = Scratch<int>(ref _prefixes, count);
        Span<int> suffixes = Scratch<int>(ref _suffixes, count);
        int rest = DeltaByteArrays.Prefixes(body, lengths, prefixes, suffixes);
        int size = DeltaByteArrays.SizePrefixes(prefixes, suffixes, rest);
        if (size > body.Length - (body.Length / 8))
        {
            return Split(ref body, width);
        }

        _encoded.Clear();
        DeltaByteArrays.EncodePrefixes(body, lengths, prefixes, suffixes, _encoded.Reserve(size));
        body = _encoded.WrittenSpan;
        return ParquetEncoding.DeltaByteArray;
    }

    /// <summary>
    /// A byte array page's encoding: DELTA_LENGTH_BYTE_ARRAY, its lengths delta-encoded ahead of its
    /// bytes, which the standard prefers to PLAIN, or DELTA_BYTE_ARRAY when the prefixes values share
    /// save an eighth more, its rebuild costing a copy per value on read.
    /// </summary>
    private ParquetEncoding ChooseBytes(ref ReadOnlySpan<byte> body, int count)
    {
        ReadOnlySpan<byte> data = Unprefix(body, count);
        Span<int> lengths = Held<int>(_lengths)[..count];
        int byLength = DeltaByteArrays.SizeLengths(lengths, data.Length);
        Span<int> prefixes = Scratch<int>(ref _prefixes, count);
        Span<int> suffixes = Scratch<int>(ref _suffixes, count);
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

    /// <summary>The checksum of a page whose bytes past its header are <paramref name="levels"/> then <paramref name="stored"/>.</summary>
    private int Checksum(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> stored)
    {
        Crc32 crc = _crc ??= new Crc32();
        crc.Append(levels);
        crc.Append(stored);
        uint hash = crc.GetCurrentHashAsUInt32();
        crc.Reset();
        return unchecked((int)hash);
    }

    /// <summary>
    /// The chunk's first data page's header, which starts at <paramref name="start"/> in the file: with
    /// an extension, when a page of the chunk is aligned, as long as brings the place the pages were
    /// laid out from to a 64-byte boundary.
    /// </summary>
    private void WriteFirstHeader(long start)
    {
        _firstHeader.Clear();
        if (_pages.Count == 0)
        {
            return;
        }

        if (Encryptor is { } encryptor)
        {
            _chunkUncompressed += EncryptHeader(encryptor, _first, Encryption.ModuleType.DataPageHeader, 0, _firstHeader);
            return;
        }

        ThriftCompactWriter writer = new(_firstHeader);
        _first.Write(ref writer, _aligned ? Padding(_first, start - _origin, 0) : default);
        writer.Flush();
        _chunkUncompressed += _firstHeader.Length;
    }

    /// <summary>The dictionary page, when a data page used the dictionary: the values coded until the last such page.</summary>
    private void WriteDictionaryPage(long offset)
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
        if (WriteChecksums)
        {
            header.HasCrc = true;
            header.Crc = Checksum(default, stored);
        }

        if (Encryptor is { } encryptor)
        {
            header.CompressedPageSize = stored.Length + encryptor.Overhead(Encryption.ModuleType.DictionaryPage);
            int headerBytes = EncryptHeader(encryptor, header, Encryption.ModuleType.DictionaryPageHeader, -1, _dictionaryPage);
            EncryptBody(encryptor, default, stored, Encryption.ModuleType.DictionaryPage, -1, _dictionaryPage);
            _chunkUncompressed += headerBytes + values.Length;
            return;
        }

        // Its values start on a 64-byte boundary when they are stored as they are.
        bool aligned = AlignUncompressedPages && _codec == CompressionCodec.Uncompressed && !values.IsEmpty;
        ThriftCompactWriter writer = new(_dictionaryPage);
        header.Write(ref writer, aligned ? Padding(header, offset, 0) : default);
        writer.Flush();
        _chunkUncompressed += _dictionaryPage.Length + values.Length;
        _dictionaryPage.Write(stored);
    }

    private ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> body)
    {
        _compressed.Clear();
        Span<byte> destination = _compressed.GetSpan(PageCodecs.MaxCompressedLength(_codec, body.Length));
        ZstdCompressor? zstd = _codec == CompressionCodec.Zstd ? _compressors!.Rent() : null;
        try
        {
            int size = PageCodecs.Compress(_codec, _level, body, destination, zstd);
            return destination[..size];
        }
        finally
        {
            if (zstd is not null)
            {
                _compressors!.Return(zstd);
            }
        }
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

    /// <summary>
    /// The values of the rows that hold one, PLAIN: each its length's four little-endian bytes, then
    /// its own, into one reservation of their whole size.
    /// </summary>
    /// <remarks>
    /// A view of a value it holds inline is that value's PLAIN form already, its length then its
    /// bytes, so it is copied whole, one vector a value; the vector's bytes past the value fall where
    /// the next value goes, or past the reservation's end, in room taken for them. A value held out
    /// of line is copied from its buffer, which <see cref="ViewValues"/> resolves and checks.
    /// </remarks>
    private void StageBytes(CanonicalNode node, int start, int count, bool dense, int first, PooledBytes target)
    {
        const int ViewSize = CanonicalSupport.ViewSize;
        ReadOnlySpan<byte> raw = node.Views.Span.Slice(start * ViewSize, count * ViewSize);
        long bytes = 0;
        for (int row = 0; row < count; row++)
        {
            if (dense || CanonicalSupport.BitAt(_validity, first + row))
            {
                bytes += sizeof(int) + (long)BinaryPrimitives.ReadUInt32LittleEndian(raw[(row * ViewSize)..]);
            }
        }

        if (bytes > Array.MaxLength - Vector128<byte>.Count)
        {
            throw new InvalidOperationException($"The page's values take {bytes} bytes, past what one buffer can hold.");
        }

        ViewValues views = new(node);
        Span<byte> into = target.GetSpan((int)bytes + Vector128<byte>.Count);
        ref byte to = ref MemoryMarshal.GetReference(into);
        ref byte view = ref MemoryMarshal.GetReference(raw);
        int at = 0;
        for (int row = 0; row < count; row++)
        {
            if (!dense && !CanonicalSupport.BitAt(_validity, first + row))
            {
                continue;
            }

            ref byte here = ref Unsafe.Add(ref view, row * ViewSize);
            uint size = Unsafe.ReadUnaligned<uint>(ref here);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, at), Unsafe.ReadUnaligned<Vector128<byte>>(ref here));
            }
            else
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, at), size);
                views.At(start + row).CopyTo(into[(at + sizeof(int))..]);
            }

            at += sizeof(int) + (int)size;
        }

        target.Advance((int)bytes);
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
/// <param name="Statistics">Its bounds and counts, and its pages'.</param>
/// <param name="Pages">Where each data page lies, for the offset index.</param>
/// <param name="PagesByEncoding">Per encoding, the data pages that took it.</param>
/// <param name="Sizes">Its size statistics, where the column has any.</param>
/// <param name="DataPageType">The type of its data pages, v1 or v2.</param>
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
    WrittenStatistics Statistics,
    PageLocation[] Pages,
    int[] PagesByEncoding,
    ChunkSizes? Sizes,
    PageType DataPageType);
