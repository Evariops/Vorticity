using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// One column's chunk in the row group being read: its pages parsed in place, each decoded whole
/// into buffers of the engine's pool, and cut into the nodes of the batches the scan asks for.
/// </summary>
/// <remarks>
/// <para>
/// A flat column's page decodes into what its canonical node holds: a validity bitmap from its
/// definition levels, none when it has no null, and one slot per row for its values, a null row's
/// slot zero — the values themselves when the page has no null, the decompression's destination
/// when it was compressed. A batch that is one whole page, every batch of a file this package
/// wrote, is the page's own buffers; any other batch copies its rows out of the pages it spans
/// into its arena. A column under a list, a map or a struct is read by its levels instead, as
/// <c>ColumnChunkReader.Nested.cs</c> describes.
/// </para>
/// <para>
/// A page's buffers stay with the reader until the batch after the last one that read them is asked
/// for, since a batch is borrowed until then. The chunk's bytes, which the pages are parsed from and
/// a byte array's views may point into, stay until the row group ends.
/// </para>
/// </remarks>
internal sealed partial class ColumnChunkReader : IDisposable
{
    private const string Encoding = "parquet.plain";

    private readonly ParquetColumn _leaf;
    private readonly LeafForm _form;

    /// <summary>The column's dtype.</summary>
    private readonly DType _type;

    /// <summary>The dtype of the node holding the values: an extension's storage, or the column's.</summary>
    private readonly DType _values;

    private readonly DType _validityType;

    /// <summary>A fixed-size list's elements, for FIXED_LEN_BYTE_ARRAY.</summary>
    private readonly DType _elements;

    /// <summary>The bytes of one PLAIN value, 0 for BOOLEAN and BYTE_ARRAY.</summary>
    private readonly int _width;

    /// <summary>The bytes a row takes in the values buffer: 0 for bits, 16 for views.</summary>
    private readonly int _slot;

    /// <summary>Whether the values are views into a page's bytes, which a batch copies with the page's buffer.</summary>
    private readonly bool _views;

    private readonly PType _ptype;
    private readonly DecimalStorageType _decimal;
    private readonly AlignedBufferPool _pool;
    private readonly long _cap;
    private readonly List<Page> _retired = [];
    private readonly Stack<Page> _free = [];
    private readonly VortexBuffer[] _data = new VortexBuffer[1];

    private CompressionCodec _codec;

    /// <summary>
    /// The chunk's bytes the scan read, each run at its place counted from the chunk's start: the whole
    /// chunk, or the pages its batches need and what comes before the first, the dictionary's.
    /// </summary>
    private readonly List<(int Start, VortexBuffer Bytes)> _runs = [];

    /// <summary>The run the reader is in, and where it starts in the chunk.</summary>
    private int _run;
    private int _runStart;
    private VortexBuffer _chunk;

    /// <summary>What decrypts the pages of the chunk being read, or null for a chunk in plaintext.</summary>
    private Encryption.PageDecryption? _decryption;

    /// <summary>The chunk's bytes, read or not.</summary>
    private int _chunkLength;

    /// <summary>The chunk's rows.</summary>
    private long _rowsTotal;

    /// <summary>
    /// Where each data page starts, counted from the chunk's start, and its first row, from the offset
    /// index: what lets a skip step over a page whose bytes it never read; null without one.
    /// </summary>
    private PageLocation[]? _map;

    /// <summary>The next page of the map.</summary>
    private int _mapped;

    /// <summary>Where the reader is in the chunk, counted from its start.</summary>
    private int _position;
    private long _rowsLeft;
    private long _rowsUnread;
    private Page? _page;

    /// <summary>The chunk's dictionary, decoded once as a page of one slot per entry.</summary>
    private Page? _dictionary;

    /// <summary>
    /// The dictionary a pruning decoded of the chunk started last and handed over, which the chunk's
    /// dictionary page then is, rather than decoded again; null for none.
    /// </summary>
    private Page? _handed;

    /// <summary>A reference to what holds the bytes the handed dictionary was decoded from, which its values may lie in.</summary>
    private SegmentOwner? _handedBytes;

    private VortexBuffer[] _gathered = new VortexBuffer[4];

    /// <summary>A reader of <paramref name="leaf"/>'s chunks.</summary>
    /// <param name="leaf">The column.</param>
    /// <param name="type">The column's dtype where it sits in the schema read.</param>
    /// <param name="validityType">The dtype of a validity bitmap's node.</param>
    /// <param name="pool">The pool pages decode into.</param>
    /// <param name="cap">The most bytes a page may decode to.</param>
    /// <param name="nested">Whether the column is read by its levels, for the field above it to assemble.</param>
    internal ColumnChunkReader(ParquetColumn leaf, DType type, DType validityType, AlignedBufferPool pool, long cap, bool nested = false)
    {
        if (!nested && (leaf.MaxRepetitionLevel > 0 || leaf.MaxDefinitionLevel > 1))
        {
            ParquetThrow.Format($"The column '{string.Join('.', leaf.Path)}' is nested and read as a flat one.");
        }

        _nested = nested;
        _repetitionWidth = 32 - BitOperations.LeadingZeroCount((uint)leaf.MaxRepetitionLevel);
        _definitionWidth = 32 - BitOperations.LeadingZeroCount((uint)leaf.MaxDefinitionLevel);
        _leaf = leaf;
        _form = leaf.Form;
        _type = type;
        _values = type.Kind == DTypeKind.Extension ? type.StorageType : type;
        _validityType = validityType;
        _pool = pool;
        _cap = cap;
        _ptype = leaf.Storage;
        switch (_form)
        {
            case LeafForm.Bool:
            case LeafForm.Null:
                break;
            case LeafForm.Binary:
            case LeafForm.Utf8:
                _slot = CanonicalSupport.ViewSize;
                _views = true;
                break;
            case LeafForm.FixedBytes:
                _elements = _values.ElementType;
                _width = leaf.TypeLength;
                _slot = leaf.TypeLength;
                break;
            case LeafForm.BigEndianDecimal:
                _decimal = DecimalStorage.ForPrecision((byte)_values.Precision);
                _width = leaf.Physical == PhysicalType.FixedLenByteArray ? leaf.TypeLength : 0;
                _slot = DecimalStorage.ByteWidth(_decimal);
                break;
            case LeafForm.Narrowed:
                _width = sizeof(int);
                _slot = _ptype.ByteWidth();
                break;
            default:
                _width = _ptype.ByteWidth();
                _slot = _width;
                _decimal = _values.Kind == DTypeKind.Decimal ? DecimalStorage.FromByteWidth(_width) : default;
                break;
        }
    }

    private string Name => string.Join('.', _leaf.Path);

    /// <summary>
    /// Whether each page this reader decodes is held to its <c>crc</c>, where it has one: the CRC-32
    /// of its bytes as stored past its header. A page skipped by its header is never read, nor checked.
    /// </summary>
    internal bool VerifyChecksums { get; set; }

    /// <summary>What counts the pages this reader decodes and decompresses, and the batches it gathers; null for none.</summary>
    internal PageCounters? Counters { get; set; }

    /// <summary>
    /// Whether a dictionary-encoded page of a flat column is kept as its codes over the dictionary's
    /// entries, read as a dictionary node by a batch that is the page whole, for a scan that keeps
    /// encodings: a predicate is then answered once per entry, and a group keyed by its code. Any
    /// other batch gathers the page's values.
    /// </summary>
    internal bool KeepEncodings { get; set; }

    /// <summary>Starts the chunk of a row group: its bytes, its codec and its rows.</summary>
    internal void Start(VortexBuffer chunk, CompressionCodec codec, long rows)
    {
        Release();
        _runs.Add((0, chunk));
        Begin(chunk.Length, null, codec, rows);
    }

    /// <summary>
    /// Starts the chunk of a row group of which the scan read <paramref name="runs"/> alone, each at its
    /// place in the chunk's <paramref name="length"/> bytes: a page outside them is one the scan steps
    /// over, by its place in <paramref name="map"/>, never by its header.
    /// </summary>
    internal void Start(ReadOnlySpan<(int Start, VortexBuffer Bytes)> runs, int length, PageLocation[] map, CompressionCodec codec, long rows)
    {
        Release();
        foreach ((int Start, VortexBuffer Bytes) run in runs)
        {
            _runs.Add(run);
        }

        Begin(length, map, codec, rows);
    }

    /// <summary>
    /// Adds <paramref name="runs"/> of the chunk started last, each at its place in the chunk and past
    /// the runs before it: the pages of a windowed read's next window, read since.
    /// </summary>
    internal void Extend(ReadOnlySpan<(int Start, VortexBuffer Bytes)> runs)
    {
        foreach ((int Start, VortexBuffer Bytes) run in runs)
        {
            if (_runs.Count > 0 && run.Start < _runs[^1].Start + _runs[^1].Bytes.Length)
            {
                throw new InvalidOperationException($"A window of '{Name}' reads bytes of its chunk a window before it read.");
            }

            _runs.Add(run);
            if (_runs.Count == 1)
            {
                // The chunk's first window read nothing of it: its first run is this one.
                (_runStart, _chunk) = run;
            }
        }
    }

    private void Begin(int length, PageLocation[]? map, CompressionCodec codec, long rows)
    {
        _run = 0;
        (_runStart, _chunk) = _runs.Count > 0 ? _runs[0] : (0, default);
        _chunkLength = length;
        _map = map;
        _mapped = 0;
        _codec = codec;
        _position = 0;
        _rowsTotal = rows;
        _rowsLeft = rows;
        _rowsUnread = rows;
        _pastFirstPage = false;
    }

    /// <summary>
    /// Decrypts the pages of the chunks started next with <paramref name="decryption"/>, or reads them
    /// in plaintext when it is null.
    /// </summary>
    internal void Encrypted(Encryption.PageDecryption? decryption) => _decryption = decryption;

    /// <summary>
    /// The header of the page at <see cref="_position"/>, <paramref name="rest"/> the chunk's bytes
    /// from there: decrypted from its module where the chunk is encrypted. <paramref name="body"/> is
    /// where the page's stored bytes start in the chunk and <paramref name="bytes"/> how many it holds
    /// of them, an encrypted body's module whole.
    /// </summary>
    private PageHeader NextHeader(ReadOnlySpan<byte> rest, out int body, out int bytes)
    {
        if (_decryption is not { } decryption)
        {
            PageHeader header = PageHeader.Read(rest);
            if (header.CompressedPageSize > rest.Length - header.HeaderLength)
            {
                ParquetThrow.Format($"A page of '{Name}' runs past its column chunk.");
            }

            body = _position + header.HeaderLength;
            bytes = header.CompressedPageSize;
            return header;
        }

        // An encrypted page's size is its module's, length to tag: the page's own is that less what the
        // module adds, which the decoders read as its stored bytes. Its checksum, if any, is of bytes
        // the module authenticates.
        PageHeader decrypted = decryption.Header(rest, _position, out int headerBytes);
        body = _position + headerBytes;
        bytes = Encryption.ModuleCipher.ModuleBytes(From(body), decryption.BodyOverhead);
        if (bytes != decrypted.CompressedPageSize)
        {
            ParquetThrow.Format($"An encrypted page of '{Name}' is a module of {bytes} bytes where its header declares {decrypted.CompressedPageSize}.");
        }

        decrypted.CompressedPageSize -= decryption.BodyOverhead;
        decrypted.HasCrc = false;
        return decrypted;
    }

    /// <summary>
    /// The stored bytes of the page <see cref="NextHeader"/> read: a view of the chunk, or its module
    /// decrypted into a block <paramref name="owner"/> holds, which the caller takes.
    /// </summary>
    private VortexBuffer Body(in PageHeader header, int body, int bytes, out NativeSegmentOwner? owner)
    {
        if (_decryption is not { } decryption)
        {
            owner = null;
            return Bytes(body, bytes);
        }

        owner = decryption.Body(header, Bytes(body, bytes).Span, _pool);
        return owner.Buffer.Slice(0, header.CompressedPageSize);
    }

    /// <summary>
    /// The bytes from <paramref name="position"/> of the chunk to the end of the run read there: empty
    /// at the chunk's end.
    /// </summary>
    private ReadOnlySpan<byte> From(int position)
    {
        Seek(position);
        if (position == _runStart + _chunk.Length && position < _chunkLength)
        {
            Unread();
        }

        return _chunk.Span[(position - _runStart)..];
    }

    /// <summary><paramref name="length"/> bytes at <paramref name="position"/> of the chunk, which a run read.</summary>
    private VortexBuffer Bytes(int position, int length)
    {
        Seek(position);
        return _chunk.Slice(position - _runStart, length);
    }

    /// <summary>Moves to the run that holds <paramref name="position"/>: forward, as the pages are visited.</summary>
    private void Seek(int position)
    {
        while (_run + 1 < _runs.Count && position >= _runs[_run + 1].Start)
        {
            _run++;
            (_runStart, _chunk) = _runs[_run];
        }

        if (position < _runStart || position > _runStart + _chunk.Length)
        {
            Unread();
        }
    }

    private void Unread() =>
        throw new InvalidOperationException($"The scan of '{Name}' reached bytes of its chunk it did not read.");

    /// <summary>Moves the map past the data page at <paramref name="position"/>, when it is the one the map holds next.</summary>
    private void Mapped(int position, PageType type)
    {
        if (_map is { } map && _mapped < map.Length && position == map[_mapped].Offset && type is PageType.DataPage or PageType.DataPageV2)
        {
            _mapped++;
        }
    }

    /// <summary>
    /// The dictionary page <paramref name="bytes"/> hold, a chunk's first page, decoded as a node of
    /// its entries, none null; the reader holds the page until <see cref="Release"/>.
    /// </summary>
    internal int ReadDictionary(ScanContext context, VortexBuffer bytes, CompressionCodec codec)
    {
        Start(bytes, codec, 0);
        ReadOnlySpan<byte> page = bytes.Span;
        PageHeader header = PageHeader.Read(page);
        if (header.Type != PageType.DictionaryPage || header.CompressedPageSize > page.Length - header.HeaderLength)
        {
            ParquetThrow.Format($"The column chunk of '{Name}' does not start with the dictionary page its metadata places there.");
        }

        _dictionary = DecodeDictionary(context, header, Bytes(header.HeaderLength, header.CompressedPageSize), null);
        return Whole(context.Canonical, _dictionary);
    }

    /// <summary>
    /// Hands the dictionary this reader decoded last to <paramref name="reader"/>, a reader of the same
    /// column started on the same chunk, with <paramref name="bytes"/>, a reference to what holds the
    /// page bytes it was decoded from, which passes with it: false, and nothing passed, where this
    /// reader holds none, or reads the column into other slots than the other does.
    /// </summary>
    internal bool HandDictionary(ColumnChunkReader reader, SegmentOwner bytes)
    {
        if (_dictionary is not { } dictionary || reader._handed is not null || reader._nested != _nested || reader._form != _form || reader._slot != _slot)
        {
            return false;
        }

        _dictionary = null;
        reader._handed = dictionary;
        reader._handedBytes = bytes;
        return true;
    }

    /// <summary>The chunk's dictionary: the one a pruning handed over, or its page decoded.</summary>
    private Page Dictionary(ScanContext context, in PageHeader header, int at, int stored)
    {
        if (_handed is { } handed)
        {
            _handed = null;
            return handed;
        }

        return DecodeDictionary(context, header, Body(header, at, stored, out NativeSegmentOwner? owner), owner);
    }

    /// <summary>The next <paramref name="rows"/> rows, as a node of <paramref name="context"/>'s arena.</summary>
    internal int Read(ScanContext context, int rows)
    {
        // The batch that read the retired pages was released when this one was asked for.
        ReleaseRetired();
        if (rows > _rowsLeft)
        {
            ParquetThrow.Format($"The column chunk of '{Name}' holds fewer rows than its row group.");
        }

        if (_form == LeafForm.Null)
        {
            _rowsLeft -= rows;
            return context.Canonical.AddNull(_type, rows);
        }

        _page ??= NextPage(context);
        int node;
        if (_page.Read == 0 && _page.Rows == rows)
        {
            node = Whole(context.Canonical, _page);
            Retire();
        }
        else if (_slot != 0 && _page.Rows - _page.Read >= rows)
        {
            // A batch inside a page larger than it, as other writers' pages often are: in place.
            node = Slice(context.Canonical, _page, rows);
            _page.Read += rows;
            if (_page.Read == _page.Rows)
            {
                Retire();
            }
        }
        else
        {
            node = Gather(context, rows);
        }

        _rowsLeft -= rows;
        return node;
    }

    /// <summary>
    /// Steps over the next <paramref name="rows"/> rows of a flat column: whole pages by their
    /// headers alone, never decompressed, and the rest of a decoded page by its count.
    /// </summary>
    internal void Skip(ScanContext context, int rows)
    {
        ReleaseRetired();
        if (rows > _rowsLeft)
        {
            ParquetThrow.Format($"The column chunk of '{Name}' holds fewer rows than its row group.");
        }

        _rowsLeft -= rows;
        if (_form == LeafForm.Null)
        {
            return;
        }

        while (rows > 0)
        {
            if (_page is null && TrySkipPage(context, rows, out int skipped))
            {
                rows -= skipped;
                continue;
            }

            Page page = _page ??= NextPage(context);
            int take = Math.Min(rows, page.Rows - page.Read);
            page.Read += take;
            rows -= take;
            if (page.Read == page.Rows)
            {
                Retire();
            }
        }
    }

    /// <summary>
    /// Steps over the next data page by its header alone when a skip of <paramref name="rows"/> rows
    /// takes its every row: a dictionary page on the way decoded, since later pages need it, an
    /// index page stepped over. False when the next data page is not one the skip takes whole, as a
    /// nested column's v1 page, whose rows only its levels say, never is, or when the chunk has ended.
    /// </summary>
    private bool TrySkipPage(ScanContext context, int rows, out int skipped)
    {
        skipped = 0;
        while (true)
        {
            if (_map is { } map && _mapped < map.Length && _position == map[_mapped].Offset)
            {
                // A page the offset index places is stepped over by its place, unread.
                long next = _mapped + 1 < map.Length ? map[_mapped + 1].FirstRow : _rowsTotal;
                long mappedRows = next - map[_mapped].FirstRow;
                if (mappedRows <= 0 || mappedRows > rows || mappedRows > _rowsUnread)
                {
                    return false;
                }

                _position = _mapped + 1 < map.Length ? (int)map[_mapped + 1].Offset : _chunkLength;
                _mapped++;
                _rowsUnread -= mappedRows;
                _pastFirstPage = true;
                skipped = (int)mappedRows;
                return true;
            }

            ReadOnlySpan<byte> rest = From(_position);
            if (rest.IsEmpty)
            {
                return false;
            }

            PageHeader header = NextHeader(rest, out int at, out int stored);
            int pageRows;
            switch (header.Type)
            {
                case PageType.DictionaryPage:
                    _position = at + stored;
                    _dictionary = Dictionary(context, header, at, stored);
                    continue;
                case PageType.DataPageV2 when _nested || header.ValueCount == header.RowCount:
                    pageRows = header.RowCount;
                    break;
                case PageType.DataPage when !_nested:
                    pageRows = header.ValueCount;
                    break;
                case PageType.DataPage:
                case PageType.DataPageV2:
                    return false;
                default:
                    _position = at + stored;
                    continue;
            }

            if (pageRows <= 0 || pageRows > rows || pageRows > _rowsUnread)
            {
                return false;
            }

            Mapped(_position, header.Type);
            _position = at + stored;
            _rowsUnread -= pageRows;
            _pastFirstPage = true;
            skipped = pageRows;
            return true;
        }
    }

    /// <summary>Gives every page back to the pool: at the end of the row group, or of the scan.</summary>
    internal void Release()
    {
        ReleaseRetired();
        if (_page is { } page)
        {
            page.Release();
            _free.Push(page);
            _page = null;
        }

        if (_dictionary is { } dictionary)
        {
            dictionary.Release();
            _free.Push(dictionary);
            _dictionary = null;
        }

        if (_handed is { } handed)
        {
            handed.Release();
            _free.Push(handed);
            _handed = null;
        }

        _handedBytes?.Release();
        _handedBytes = null;
        _plan.Clear();
        _dense = default;
        _zeros = default;
        _denseBuffers = 0;
        _batchPage = null;
        Entries = 0;
        ValueCount = 0;
        _chunk = default;
        _runs.Clear();
        _map = null;
    }

    public void Dispose()
    {
        Release();
        _levels?.Dispose();
        _levels = null;
    }

    private void Retire()
    {
        _retired.Add(_page!);
        _page = null;
    }

    private void ReleaseRetired()
    {
        foreach (Page page in _retired)
        {
            page.Release();
            _free.Push(page);
        }

        _retired.Clear();
    }

    /// <summary>A node over a whole page's own buffers.</summary>
    private int Whole(CanonicalArena arena, Page page)
    {
        Validity validity = Validity.NonNullable;
        if (_type.IsNullable)
        {
            validity = page.Validity is { } bits
                ? (page.Nulls == page.Rows ? Validity.AllInvalid : Validity.Bitmap(arena.AddBool(_validityType, page.Rows, Validity.NonNullable, bits.Buffer, 0)))
                : Validity.AllValid;
        }

        if (page.Codes is { } codes)
        {
            // The codes, held to the dictionary as they were kept, over its entries.
            Page dictionary = _dictionary!;
            _data[0] = dictionary.Data;
            int entries = Storage(arena, dictionary.Rows, _values.IsNullable ? Validity.AllValid : Validity.NonNullable, dictionary.Values, _data);
            return Wrap(arena, page.Rows, arena.AddDictionary(_values, page.Rows, validity, codes.Buffer.Slice(0, page.Rows * sizeof(uint)), entries));
        }

        _data[0] = page.Data;
        return Node(arena, page.Rows, validity, page.Values, _data);
    }

    /// <summary>
    /// The next <paramref name="rows"/> rows of a page that holds them all, in place: its slots, its
    /// validity and its codes sliced, nothing copied. The page stays until a later batch retires it.
    /// </summary>
    private int Slice(CanonicalArena arena, Page page, int rows)
    {
        int from = page.Read;
        Validity validity = Validity.NonNullable;
        if (_type.IsNullable)
        {
            validity = Validity.AllValid;
            if (page.Validity is { } bits)
            {
                int nulls = rows - BitmapKernels.CountSet(bits.Buffer.Span, from, rows);
                validity = nulls == 0 ? Validity.AllValid
                    : nulls == rows ? Validity.AllInvalid
                    : Validity.Bitmap(arena.AddBool(_validityType, rows, Validity.NonNullable, bits.Buffer.Slice(from >> 3), from & 7));
            }
        }

        if (page.Codes is { } codes)
        {
            Page dictionary = _dictionary!;
            _data[0] = dictionary.Data;
            int entries = Storage(arena, dictionary.Rows, _values.IsNullable ? Validity.AllValid : Validity.NonNullable, dictionary.Values, _data);
            return Wrap(arena, rows, arena.AddDictionary(_values, rows, validity, codes.Buffer.Slice(from * sizeof(uint), rows * sizeof(uint)), entries));
        }

        _data[0] = page.Data;
        return Node(arena, rows, validity, page.Values.Slice(from * _slot, rows * _slot), _data);
    }

    /// <summary>A node of <paramref name="rows"/> rows copied out of the pages they span into the arena.</summary>
    private int Gather(ScanContext context, int rows)
    {
        Counters?.AddGather();
        ArrayDecodeContext decode = context.Decode;
        bool nullable = _type.IsNullable;
        Span<byte> bits = default;
        VortexBuffer validityBuffer = default;
        if (nullable)
        {
            validityBuffer = CanonicalSupport.Allocate(decode, CanonicalSupport.BitmapByteCount(rows), 64, out bits);
        }

        int bytes = _slot == 0 ? CanonicalSupport.BitmapByteCount(rows) : checked(rows * _slot);
        VortexBuffer values = CanonicalSupport.Allocate(decode, bytes, 64, out Span<byte> into);
        int buffers = 0;
        int done = 0;
        int nulls = 0;
        while (done < rows)
        {
            Page page = _page ??= NextPage(context);
            int take = Math.Min(rows - done, page.Rows - page.Read);
            if (nullable)
            {
                if (page.Validity is { } pageBits)
                {
                    BitmapKernels.CopyRange(pageBits.Buffer.Span, page.Read, bits, done, take);
                    nulls += take - BitmapKernels.CountSet(pageBits.Buffer.Span, page.Read, take);
                }
                else
                {
                    BitmapKernels.SetRange(bits, done, take);
                }
            }

            Materialize(page);
            CopySlots(page, take, into, done, ref buffers);
            page.Read += take;
            done += take;
            if (page.Read == page.Rows)
            {
                Retire();
            }
        }

        Validity validity = Validity.NonNullable;
        if (nullable)
        {
            validity = nulls == 0 ? Validity.AllValid
                : nulls == rows ? Validity.AllInvalid
                : Validity.Bitmap(context.Canonical.AddBool(_validityType, rows, Validity.NonNullable, validityBuffer, 0));
        }

        return Node(context.Canonical, rows, validity, values, _gathered.AsSpan(0, buffers));
    }

    /// <summary>
    /// Copies <paramref name="take"/> slots of <paramref name="page"/> from its read position into
    /// <paramref name="into"/> from slot <paramref name="done"/>: bits, values, or views rebased onto
    /// the gathered buffers, which the page's joins unless it is the one before it.
    /// </summary>
    private void CopySlots(Page page, int take, Span<byte> into, int done, ref int buffers)
    {
        ReadOnlySpan<byte> from = page.Values.Span;
        if (_slot == 0)
        {
            BitmapKernels.CopyRange(from, page.Read, into, done, take);
        }
        else if (_views)
        {
            // Pages whose views point into one buffer, the dictionary's, share its entry.
            int bufferBase = buffers > 0 && Same(_gathered[buffers - 1], page.Data) ? buffers - 1 : buffers;
            if (bufferBase == buffers)
            {
                if (buffers == _gathered.Length)
                {
                    Array.Resize(ref _gathered, buffers * 2);
                }

                _gathered[buffers++] = page.Data;
            }

            CanonicalConcat.RebaseInto(
                from.Slice(page.Read * _slot, take * _slot), into.Slice(done * _slot, take * _slot), take, bufferBase, 1, 0);
        }
        else
        {
            from.Slice(page.Read * _slot, take * _slot).CopyTo(into[(done * _slot)..]);
        }
    }

    /// <summary>The node of the column's type over <paramref name="values"/>.</summary>
    private int Node(CanonicalArena arena, int rows, Validity validity, VortexBuffer values, ReadOnlySpan<VortexBuffer> data) =>
        Wrap(arena, rows, Storage(arena, rows, validity, values, data));

    /// <summary>An extension column's node over its storage's; any other's itself.</summary>
    private int Wrap(CanonicalArena arena, int rows, int node) =>
        _type.Kind == DTypeKind.Extension ? arena.AddExtension(_type, rows, node) : node;

    /// <summary>The node of the column's storage over <paramref name="values"/>.</summary>
    private int Storage(CanonicalArena arena, int rows, Validity validity, VortexBuffer values, ReadOnlySpan<VortexBuffer> data)
    {
        int node;
        switch (_form)
        {
            case LeafForm.Bool:
                node = arena.AddBool(_values, rows, validity, values, 0);
                break;
            case LeafForm.Binary:
            case LeafForm.Utf8:
                node = arena.AddVarBinView(_values, rows, validity, values, data);
                break;
            case LeafForm.FixedBytes:
                int elements = arena.AddPrimitive(_elements, rows * _width, Validity.NonNullable, PType.U8, values);
                node = arena.AddFixedSizeList(_values, rows, validity, elements, (uint)_width);
                break;
            default:
                node = _values.Kind == DTypeKind.Decimal
                    ? arena.AddDecimal(_values, rows, validity, _decimal, (byte)_values.Precision, (sbyte)_values.Scale, values)
                    : arena.AddPrimitive(_values, rows, validity, _ptype, values);
                break;
        }

        return node;
    }

    /// <summary>Parses pages until a data page, which it decodes whole.</summary>
    private Page NextPage(ScanContext context) =>
        TryNextPage(context) ?? ParquetThrow.Format<Page>($"The column chunk of '{Name}' ends before its rows do.");

    /// <summary>Parses pages until a data page, which it decodes whole, or the chunk's end.</summary>
    private Page? TryNextPage(ScanContext context)
    {
        while (true)
        {
            ReadOnlySpan<byte> rest = From(_position);
            if (rest.IsEmpty)
            {
                return null;
            }

            PageHeader header = NextHeader(rest, out int at, out int stored);
            Mapped(_position, header.Type);
            _position = at + stored;
            NativeSegmentOwner? owner;
            switch (header.Type)
            {
                case PageType.DataPageV2:
                    Counters?.AddPage();
                    return DecodeV2(context, header, Body(header, at, stored, out owner), owner);
                case PageType.DataPage:
                    Counters?.AddPage();
                    return DecodeV1(context, header, Body(header, at, stored, out owner), owner);
                case PageType.DictionaryPage:
                    _dictionary = Dictionary(context, header, at, stored);
                    continue;
                default:
                    // An index page, or a kind a later version of the standard adds: not data.
                    continue;
            }
        }
    }

    /// <summary>
    /// A v2 data page of stored bytes <paramref name="body"/>, which <paramref name="owner"/> holds
    /// where they are not the chunk's: the page takes the block, or gives it back.
    /// </summary>
    private Page DecodeV2(ScanContext context, in PageHeader header, VortexBuffer body, NativeSegmentOwner? owner)
    {
        try
        {
            Check(header, body);
        }
        catch
        {
            owner?.Dispose();
            throw;
        }

        // A flat column's repetition levels say nothing, and a required one's definition levels
        // neither: some writers write them anyway, and they are stepped over.
        int levels = header.RepetitionLevelsLength + header.DefinitionLevelsLength;
        if (header.RepetitionLevelsLength < 0 || header.DefinitionLevelsLength < 0 || levels > header.CompressedPageSize || levels > header.UncompressedPageSize)
        {
            owner?.Dispose();
            ParquetThrow.Format($"A page of '{Name}' declares levels its bytes do not hold.");
        }

        Page page = Rent(0);
        try
        {
            int valid;
            if (_nested)
            {
                page.EndsRows = true;
                valid = NestedLevelsV2(page, header, body);
                if (_form == LeafForm.Null)
                {
                    owner?.Dispose();
                    return page;
                }
            }
            else
            {
                int rows = Rows(header.RowCount);
                if (header.ValueCount != rows)
                {
                    ParquetThrow.Format($"A page of the flat column '{Name}' holds {header.ValueCount} values for {rows} rows.");
                }

                page.Rows = rows;
                valid = _leaf.MaxDefinitionLevel == 0
                    ? rows
                    : Levels(page, body.Slice(header.RepetitionLevelsLength, header.DefinitionLevelsLength).Span, rows);
                if (header.NullCount != rows - valid)
                {
                    ParquetThrow.Format($"A page of '{Name}' declares {header.NullCount} nulls where its levels hold {rows - valid}.");
                }
            }

            RequireEncoding(header.Encoding);
            int size = header.UncompressedPageSize - levels;
            VortexBuffer stored = body.Slice(levels, header.CompressedPageSize - levels);

            // A page of nulls may have no values at all, and no bytes the codec would make of none.
            if (size == 0)
            {
                owner?.Dispose();
                owner = null;
                Decode(page, header.Encoding, default, null, valid);
            }
            else if (header.IsCompressed && _codec != CompressionCodec.Uncompressed)
            {
                Cap(size);
                NativeSegmentOwner values = _pool.Rent(size, 64);
                try
                {
                    Counters?.AddDecompression();
                    PageCodecs.Decompress(_codec, stored.Span, values.WritableSpan, context.Zstd);
                }
                catch
                {
                    values.Dispose();
                    throw;
                }

                owner?.Dispose();
                owner = null;
                Decode(page, header.Encoding, values.Buffer, values, valid);
            }
            else
            {
                if (stored.Length != size)
                {
                    ParquetThrow.Format($"An uncompressed page of '{Name}' holds {stored.Length} bytes of values where its header declares {size}.");
                }

                // The page's values are its stored bytes: the block that holds them is the page's.
                NativeSegmentOwner? taken = owner;
                owner = null;
                Decode(page, header.Encoding, stored, taken, valid);
            }

            return page;
        }
        catch
        {
            owner?.Dispose();
            page.Release();
            _free.Push(page);
            throw;
        }
    }

    /// <summary>
    /// A v1 data page of stored bytes <paramref name="body"/>, which <paramref name="stored"/> holds
    /// where they are not the chunk's: the page takes the block, or gives it back.
    /// </summary>
    private Page DecodeV1(ScanContext context, in PageHeader header, VortexBuffer body, NativeSegmentOwner? stored)
    {
        NativeSegmentOwner? owner = stored;
        Page page = Rent(0);
        try
        {
            Check(header, body);
            if (_form != LeafForm.Null)
            {
                RequireEncoding(header.Encoding);
            }

            int size = header.UncompressedPageSize;
            if (_codec != CompressionCodec.Uncompressed)
            {
                Cap(size);
                NativeSegmentOwner decompressed = _pool.Rent(size, 64);
                try
                {
                    Counters?.AddDecompression();
                    PageCodecs.Decompress(_codec, body.Span, decompressed.WritableSpan, context.Zstd);
                }
                catch
                {
                    decompressed.Dispose();
                    throw;
                }

                owner?.Dispose();
                owner = decompressed;
                body = decompressed.Buffer;
            }
            else if (body.Length != size)
            {
                ParquetThrow.Format($"An uncompressed page of '{Name}' holds {body.Length} bytes where its header declares {size}.");
            }

            int position;
            int valid;
            if (_nested)
            {
                valid = NestedLevelsV1(page, header, body.Span, out position);
                if (_form == LeafForm.Null)
                {
                    owner?.Dispose();
                    return page;
                }
            }
            else
            {
                valid = FlatLevelsV1(page, header, body.Span, out position);
            }

            // The page's values take the decompression's block: it keeps it or gives it back.
            NativeSegmentOwner? values = owner;
            owner = null;
            Decode(page, header.Encoding, body.Slice(position, body.Length - position), values, valid);
            return page;
        }
        catch
        {
            owner?.Dispose();
            page.Release();
            _free.Push(page);
            throw;
        }
    }

    /// <summary>A flat column's v1 page: its rows, and the definition levels inside its bytes; the rows that hold a value.</summary>
    private int FlatLevelsV1(Page page, in PageHeader header, ReadOnlySpan<byte> body, out int position)
    {
        int rows = Rows(header.ValueCount);
        page.Rows = rows;

        // The levels of a v1 page are inside its bytes: each behind its length when RLE.
        position = 0;
        if (_leaf.MaxDefinitionLevel == 0)
        {
            return rows;
        }

        if (header.DefinitionLevelEncoding == ParquetEncoding.BitPacked)
        {
            // The deprecated packing of older files: no length before it, its bytes the levels'.
            position = LegacyBitPacked.Bytes(rows, 1);
            return LegacyLevels(page, body, rows);
        }

        ReadOnlySpan<byte> levels = RleLevels(body, header.DefinitionLevelEncoding, "definition", ref position);
        return Levels(page, levels, rows);
    }

    /// <summary>
    /// A v1 page's run of RLE levels at <paramref name="position"/>: the hybrid behind its four-byte
    /// length, which the position steps past.
    /// </summary>
    private ReadOnlySpan<byte> RleLevels(ReadOnlySpan<byte> body, ParquetEncoding encoding, string kind, ref int position)
    {
        if (encoding != ParquetEncoding.Rle)
        {
            throw new ParquetUnsupportedException(encoding.ToString(), ParquetComponentKind.Encoding,
                $"The {kind} levels of '{Name}' are {encoding}; the standard writes them RLE or BIT_PACKED.");
        }

        if (body.Length - position < sizeof(int))
        {
            ParquetThrow.Format($"A page of '{Name}' ends inside the length of its {kind} levels.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(body[position..]);
        if (length < 0 || length > body.Length - position - sizeof(int))
        {
            ParquetThrow.Format($"A page of '{Name}' declares {kind} levels past its bytes.");
        }

        ReadOnlySpan<byte> levels = body.Slice(position + sizeof(int), length);
        position += sizeof(int) + length;
        return levels;
    }

    /// <summary>A v1 page's levels in the deprecated BIT_PACKED encoding, into its validity; the rows that hold a value.</summary>
    private int LegacyLevels(Page page, ReadOnlySpan<byte> levels, int rows)
    {
        NativeSegmentOwner bits = _pool.Rent(CanonicalSupport.BitmapByteCount(rows), 64);
        int valid = LegacyBitPacked.ReadBits(levels, rows, bits.WritableSpan);
        if (valid == rows)
        {
            bits.Dispose();
            return rows;
        }

        page.Validity = bits;
        page.Nulls = rows - valid;
        return valid;
    }

    /// <summary>Decodes a page's definition levels into its validity; the rows that hold a value.</summary>
    private int Levels(Page page, ReadOnlySpan<byte> levels, int rows)
    {
        if (_leaf.MaxDefinitionLevel == 0)
        {
            if (!levels.IsEmpty)
            {
                ParquetThrow.Format($"A page of the required column '{Name}' carries definition levels.");
            }

            return rows;
        }

        NativeSegmentOwner bits = _pool.Rent(CanonicalSupport.BitmapByteCount(rows), 64);
        int valid = new RleHybridDecoder(1).ReadBits(levels, bits.WritableSpan, 0, rows);
        if (valid == rows)
        {
            bits.Dispose();
            return rows;
        }

        page.Validity = bits;
        page.Nulls = rows - valid;
        return valid;
    }

    /// <summary>
    /// Decodes a page's PLAIN values into one slot per row: <paramref name="values"/> itself when it
    /// is already that, as a page without a null of a fixed width is, else a buffer of the pool.
    /// </summary>
    /// <param name="page">The page, its validity decoded.</param>
    /// <param name="values">The values, decompressed.</param>
    /// <param name="owner">The pool's block holding <paramref name="values"/>, or null when they are the chunk's.</param>
    /// <param name="valid">The rows that hold a value.</param>
    private void Values(Page page, VortexBuffer values, NativeSegmentOwner? owner, int valid)
    {
        int rows = page.Rows;
        ReadOnlySpan<byte> source = values.Span;
        switch (_form)
        {
            case LeafForm.Bool:
                if ((long)valid > (long)source.Length * 8)
                {
                    ParquetThrow.Format($"A page of '{Name}' holds fewer booleans than its rows.");
                }

                if (page.Validity is null)
                {
                    Keep(page, values, owner);
                    return;
                }

                NativeSegmentOwner bits = Slots(page, CanonicalSupport.BitmapByteCount(rows));
                SpreadBits(source, page.Validity.Buffer.Span, bits.WritableSpan, rows);
                owner?.Dispose();
                return;

            case LeafForm.Binary:
            case LeafForm.Utf8:
                Views(page, values, owner, valid);
                return;

            case LeafForm.Narrowed:
                Narrow(page, source, valid);
                owner?.Dispose();
                return;

            case LeafForm.BigEndianDecimal:
                Widen(page, source, valid);
                owner?.Dispose();
                return;

            default:
                long need = (long)valid * _width;
                if (source.Length != need)
                {
                    ParquetThrow.Format($"A page of '{Name}' holds {source.Length} bytes of values where its {valid} values take {need}.");
                }

                if (page.Validity is null)
                {
                    Keep(page, values, owner);
                    return;
                }

                NativeSegmentOwner slots = Slots(page, rows * _slot);
                Span<byte> into = slots.WritableSpan;
                into.Clear();
                ValidRows.Spread(source, into, ValidityMask.Bitmap(page.Validity.Buffer.Span, 0), rows, _width, Encoding);
                owner?.Dispose();
                return;
        }
    }

    /// <summary>A page's values as they lie: the decompression's destination, or the chunk's bytes.</summary>
    private static void Keep(Page page, VortexBuffer values, NativeSegmentOwner? owner)
    {
        page.Slots = owner;
        page.Values = values;
    }

    /// <summary>A PLAIN BYTE_ARRAY page cut into views over its own bytes, a null row's view empty.</summary>
    private void Views(Page page, VortexBuffer values, NativeSegmentOwner? owner, int valid)
    {
        int rows = page.Rows;
        NativeSegmentOwner views = Slots(page, rows * CanonicalSupport.ViewSize);
        Span<byte> into = views.WritableSpan;
        if (page.Validity is null)
        {
            Cut(values.Span, into, valid);
        }
        else
        {
            NativeSegmentOwner dense = _pool.Rent(Math.Max(valid, 1) * CanonicalSupport.ViewSize, 64);
            try
            {
                Cut(values.Span, dense.WritableSpan, valid);
                into.Clear();
                ValidRows.Spread(dense.WritableSpan[..(valid * CanonicalSupport.ViewSize)], into, ValidityMask.Bitmap(page.Validity.Buffer.Span, 0), rows, CanonicalSupport.ViewSize, Encoding);
            }
            finally
            {
                dense.Dispose();
            }
        }

        page.DataOwner = owner;
        page.Data = values;
    }

    private void Cut(ReadOnlySpan<byte> heap, Span<byte> views, int count)
    {
        if (ViewKernels.BuildFromPrefixed(heap, views, count) != count)
        {
            ParquetThrow.Format($"A byte array of '{Name}' runs past its page.");
        }
    }

    /// <summary>
    /// INT32 values narrowed to the column's width, the range of an annotated integer checked: by the
    /// values' extremes, a register at a time, then each truncated, a register at a time, by the core's
    /// kernels; an unsigned width's bits are its signed twin's.
    /// </summary>
    private void Narrow(Page page, ReadOnlySpan<byte> source, int valid)
    {
        if (source.Length != (long)valid * sizeof(int))
        {
            ParquetThrow.Format($"A page of '{Name}' holds {source.Length} bytes of values where its {valid} values take {valid * sizeof(int)}.");
        }

        ReadOnlySpan<int> ints = MemoryMarshal.Cast<byte, int>(source);
        if (valid > 0)
        {
            Vorticity.Writing.BlockStatsPass.Bounds(ints, out int least, out int most);
            (int Low, int High) range = _ptype switch
            {
                PType.I8 => (sbyte.MinValue, sbyte.MaxValue),
                PType.U8 => (byte.MinValue, byte.MaxValue),
                PType.I16 => (short.MinValue, short.MaxValue),
                _ => (ushort.MinValue, ushort.MaxValue),
            };
            if (least < range.Low || most > range.High)
            {
                ParquetThrow.Format($"A value of '{Name}' is out of the range of its {_ptype} annotation.");
            }
        }

        Span<byte> dense = Dense(page, valid, out NativeSegmentOwner? spread);
        try
        {
            IntegerNarrowing.Truncate(ints, _slot == sizeof(byte) ? PType.I8 : PType.I16, dense);
            Spread(page, dense, valid, spread);
        }
        finally
        {
            spread?.Dispose();
        }
    }

    /// <summary>
    /// Where <paramref name="valid"/> values of the slot's width are decoded: the page's own slots when
    /// it holds no null, so that nothing is copied after; else a block of <paramref name="spread"/>,
    /// which <see cref="Spread"/> spreads over the page's rows and the caller gives back.
    /// </summary>
    private Span<byte> Dense(Page page, int valid, out NativeSegmentOwner? spread)
    {
        if (page.Validity is null)
        {
            spread = null;
            return Slots(page, page.Rows * _slot).WritableSpan[..(valid * _slot)];
        }

        spread = _pool.Rent(Math.Max(valid, 1) * _slot, 64);
        return spread.WritableSpan[..(valid * _slot)];
    }

    /// <summary>The values <see cref="Dense"/> gave a block for spread over the page's rows; nothing where they are its slots.</summary>
    private void Spread(Page page, ReadOnlySpan<byte> dense, int valid, NativeSegmentOwner? spread)
    {
        if (spread is not null)
        {
            Place(page, dense, valid);
        }
    }

    /// <summary>
    /// Big-endian two's-complement decimals sign-extended into the column's little-endian storage: a
    /// fixed-length array's all at once, a length-prefixed one's each where its length says.
    /// </summary>
    private void Widen(Page page, ReadOnlySpan<byte> source, int valid)
    {
        if (_width > 0)
        {
            if (source.Length != (long)valid * _width)
            {
                ParquetThrow.Format(source.Length < (long)valid * _width
                    ? $"A decimal of '{Name}' runs past its page."
                    : $"A page of '{Name}' holds bytes past its {valid} decimals.");
            }

            Wide(_width);
        }

        Span<byte> wide = Dense(page, valid, out NativeSegmentOwner? spread);
        try
        {
            if (_width > 0)
            {
                BigEndianDecimals.WidenFixed(source, _width, wide, _slot, valid);
            }
            else
            {
                int position = 0;
                for (int i = 0; i < valid; i++)
                {
                    if (source.Length - position < sizeof(int))
                    {
                        ParquetThrow.Format($"A decimal of '{Name}' runs past its page.");
                    }

                    int length = BinaryPrimitives.ReadInt32LittleEndian(source[position..]);
                    position += sizeof(int);
                    if (length < 0 || length > source.Length - position)
                    {
                        ParquetThrow.Format($"A decimal of '{Name}' runs past its page.");
                    }

                    Wide(length);
                    BigEndianDecimals.Widen(source, position, length, wide.Slice(i * _slot, _slot));
                    position += length;
                }

                if (position != source.Length)
                {
                    ParquetThrow.Format($"A page of '{Name}' holds bytes past its {valid} decimals.");
                }
            }

            Spread(page, wide, valid, spread);
        }
        finally
        {
            spread?.Dispose();
        }
    }

    /// <summary>
    /// Decimals a delta encoding rebuilt back to back in <paramref name="heap"/>, each of its
    /// <paramref name="lengths"/>, sign-extended into the column's storage: a FIXED_LEN_BYTE_ARRAY's
    /// every one the type's length.
    /// </summary>
    private void Widen(Page page, ReadOnlySpan<byte> heap, ReadOnlySpan<int> lengths, int valid)
    {
        Span<byte> wide = Dense(page, valid, out NativeSegmentOwner? spread);
        try
        {
            int position = 0;
            for (int i = 0; i < valid; i++)
            {
                int length = lengths[i];
                if (_width > 0 && length != _width)
                {
                    ParquetThrow.Format($"A decimal of '{Name}' takes {length} bytes where its type's length is {_width}.");
                }

                Wide(length);
                BigEndianDecimals.Widen(heap, position, length, wide.Slice(i * _slot, _slot));
                position += length;
            }

            Spread(page, wide, valid, spread);
        }
        finally
        {
            spread?.Dispose();
        }
    }

    /// <summary>One big-endian two's-complement decimal sign-extended into its little-endian slot.</summary>
    /// <summary>Refuses a decimal of <paramref name="length"/> bytes that its storage does not hold.</summary>
    private void Wide(int length)
    {
        if (length > _slot)
        {
            ParquetThrow.Format($"A decimal of '{Name}' is wider than its precision allows.");
        }
    }

    /// <summary>Dense values of the slot's width copied into the page's slots, spread over its rows when it has a null.</summary>
    private void Place(Page page, ReadOnlySpan<byte> dense, int valid)
    {
        int rows = page.Rows;
        NativeSegmentOwner slots = Slots(page, rows * _slot);
        Span<byte> into = slots.WritableSpan;
        if (page.Validity is null)
        {
            dense.CopyTo(into);
            return;
        }

        into.Clear();
        ValidRows.Spread(dense, into, ValidityMask.Bitmap(page.Validity.Buffer.Span, 0), rows, _slot, Encoding);
    }

    /// <summary>
    /// Dense bits spread over the rows the validity sets, a null row's bit clear: a word of 64 rows
    /// at a time, its valid rows' bits deposited from the stream where BMI2 does it in one instruction.
    /// </summary>
    private static void SpreadBits(ReadOnlySpan<byte> dense, ReadOnlySpan<byte> validity, Span<byte> into, int rows)
    {
        into.Clear();
        int taken = 0;
        for (int start = 0; start < rows; start += 64)
        {
            int count = Math.Min(64, rows - start);
            ulong mask = BitWords.Load(validity, start) & BitWords.Mask(count);
            int set = BitOperations.PopCount(mask);
            if (set == 0)
            {
                continue;
            }

            ulong values = BitWords.Load(dense, taken) & BitWords.Mask(set);
            ulong word;
            if (Bmi2.X64.IsSupported)
            {
                word = Bmi2.X64.ParallelBitDeposit(values, mask);
            }
            else
            {
                word = 0;
                for (ulong remaining = mask; remaining != 0; remaining &= remaining - 1)
                {
                    word |= (values & 1) << BitOperations.TrailingZeroCount(remaining);
                    values >>= 1;
                }
            }

            taken += set;
            for (int b = 0; b < count; b += 8)
            {
                into[(start + b) >> 3] = (byte)(word >> b);
            }
        }
    }

    private NativeSegmentOwner Slots(Page page, int bytes)
    {
        Cap(bytes);
        NativeSegmentOwner owner = _pool.Rent(Math.Max(bytes, 1), 64);
        page.Slots = owner;
        page.Values = owner.Buffer.Slice(0, bytes);
        return owner;
    }

    private Page Rent(int rows)
    {
        Page page = _free.Count > 0 ? _free.Pop() : new Page();
        page.Rows = rows;
        return page;
    }

    /// <summary>A page's row count, checked against the rows the chunk has left to decode.</summary>
    private int Rows(int rows)
    {
        if (rows <= 0 || rows > _rowsUnread)
        {
            ParquetThrow.Format($"A page of '{Name}' declares {rows} rows where its chunk has {_rowsUnread} left.");
        }

        _rowsUnread -= rows;
        return rows;
    }

    private void RequireEncoding(ParquetEncoding encoding)
    {
        bool fits = encoding switch
        {
            ParquetEncoding.Plain or ParquetEncoding.RleDictionary or ParquetEncoding.PlainDictionary => true,
            ParquetEncoding.DeltaBinaryPacked => _form is LeafForm.Fixed or LeafForm.Narrowed && _leaf.Physical is PhysicalType.Int32 or PhysicalType.Int64,
            ParquetEncoding.DeltaLengthByteArray => _views || (_form == LeafForm.BigEndianDecimal && _width == 0),
            ParquetEncoding.DeltaByteArray => _views || _form is LeafForm.FixedBytes or LeafForm.Float16 or LeafForm.BigEndianDecimal,
            ParquetEncoding.ByteStreamSplit => _form is LeafForm.Fixed or LeafForm.Float16 or LeafForm.FixedBytes or LeafForm.Narrowed
                || (_form == LeafForm.BigEndianDecimal && _width > 0),
            ParquetEncoding.Rle => _form == LeafForm.Bool,
            ParquetEncoding.Alp => _form == LeafForm.Fixed && _leaf.Physical is PhysicalType.Float or PhysicalType.Double,
            _ => false,
        };
        if (!fits)
        {
            throw new ParquetUnsupportedException(encoding.ToString(), ParquetComponentKind.Encoding,
                $"A page of '{Name}', a {_leaf.Physical} column, is {encoding}, which this reader does not decode for it.");
        }
    }

    /// <summary>A page's values by their encoding, the page taking <paramref name="owner"/> when its values stay where they lie.</summary>
    private void Decode(Page page, ParquetEncoding encoding, VortexBuffer values, NativeSegmentOwner? owner, int valid)
    {
        switch (encoding)
        {
            case ParquetEncoding.Plain:
                Values(page, values, owner, valid);
                return;
            case ParquetEncoding.DeltaLengthByteArray:
                Lengths(page, values, owner, valid);
                return;
        }

        try
        {
            switch (encoding)
            {
                case ParquetEncoding.DeltaBinaryPacked:
                    Deltas(page, values.Span, valid);
                    break;
                case ParquetEncoding.DeltaByteArray:
                    Prefixed(page, values.Span, valid);
                    break;
                case ParquetEncoding.ByteStreamSplit:
                    Streams(page, values.Span, valid);
                    break;
                case ParquetEncoding.Rle:
                    Runs(page, values.Span, valid);
                    break;
                case ParquetEncoding.Alp:
                    AlpPage(page, values.Span, valid);
                    break;
                default:
                    Codes(page, values.Span, valid);
                    break;
            }
        }
        finally
        {
            owner?.Dispose();
        }
    }

    /// <summary>
    /// A DELTA_BINARY_PACKED page of INT32 or INT64 values: decoded straight into the page's slots
    /// when it has no null and the column's width is the type's, else densely and then placed.
    /// </summary>
    private void Deltas(Page page, ReadOnlySpan<byte> source, int valid)
    {
        int rows = page.Rows;
        bool wide = _leaf.Physical == PhysicalType.Int64;
        int width = wide ? sizeof(long) : sizeof(int);
        if (page.Validity is null && _form == LeafForm.Fixed && _slot == width)
        {
            Span<byte> slots = Slots(page, rows * width).WritableSpan;
            if (wide)
            {
                DeltaBinaryPacked.Decode64(source, MemoryMarshal.Cast<byte, long>(slots)[..rows]);
            }
            else
            {
                DeltaBinaryPacked.Decode32(source, MemoryMarshal.Cast<byte, int>(slots)[..rows]);
            }

            return;
        }

        NativeSegmentOwner dense = _pool.Rent(Math.Max(valid, 1) * width, 64);
        try
        {
            Span<byte> values = dense.WritableSpan[..(valid * width)];
            if (wide)
            {
                DeltaBinaryPacked.Decode64(source, MemoryMarshal.Cast<byte, long>(values));
            }
            else
            {
                DeltaBinaryPacked.Decode32(source, MemoryMarshal.Cast<byte, int>(values));
            }

            if (_form == LeafForm.Narrowed)
            {
                Narrow(page, values, valid);
            }
            else
            {
                Place(page, values, valid);
            }
        }
        finally
        {
            dense.Dispose();
        }
    }

    /// <summary>
    /// A DELTA_LENGTH_BYTE_ARRAY page cut into views over its own bytes, which the page keeps, or a
    /// BYTE_ARRAY decimal's widened out of them.
    /// </summary>
    private void Lengths(Page page, VortexBuffer values, NativeSegmentOwner? owner, int valid)
    {
        NativeSegmentOwner lengths = _pool.Rent(Math.Max(valid, 1) * sizeof(int), 64);
        try
        {
            Span<int> span = MemoryMarshal.Cast<byte, int>(lengths.WritableSpan)[..valid];
            int start = DeltaByteArrays.DecodeLengths(values.Span, span);
            VortexBuffer heap = values.Slice(start, values.Length - start);
            if (_form == LeafForm.BigEndianDecimal)
            {
                Widen(page, heap.Span, span, valid);
                return;
            }

            CutViews(page, span, heap, valid);
            page.Data = heap;
            page.DataOwner = owner;
            owner = null;
        }
        finally
        {
            lengths.Dispose();
            owner?.Dispose();
        }
    }

    /// <summary>A DELTA_BYTE_ARRAY page rebuilt, each value from the one before it, into a heap the page keeps.</summary>
    private void Prefixed(Page page, ReadOnlySpan<byte> source, int valid)
    {
        NativeSegmentOwner lengths = _pool.Rent(Math.Max(valid, 1) * sizeof(int) * 3, 64);
        NativeSegmentOwner? heap = null;
        try
        {
            Span<int> all = MemoryMarshal.Cast<byte, int>(lengths.WritableSpan);
            Span<int> prefixes = all[..valid];
            Span<int> suffixes = all.Slice(valid, valid);
            Span<int> rebuilt = all.Slice(2 * valid, valid);
            long total = DeltaByteArrays.DecodePrefixes(source, prefixes, suffixes, out int suffixStart);
            Cap(total);
            heap = _pool.Rent((int)Math.Max(total, 1), 64);
            DeltaByteArrays.Rebuild(source[suffixStart..], prefixes, suffixes, heap.WritableSpan, rebuilt);
            if (_views)
            {
                CutViews(page, rebuilt, heap.Buffer.Slice(0, (int)total), valid);
                page.Data = heap.Buffer.Slice(0, (int)total);
                page.DataOwner = heap;
                heap = null;
                return;
            }

            if (_form == LeafForm.BigEndianDecimal)
            {
                Widen(page, heap.WritableSpan[..(int)total], rebuilt, valid);
                return;
            }

            // FIXED_LEN_BYTE_ARRAY: every value rebuilt is the type's length, back to back.
            if (total != (long)valid * _width)
            {
                ParquetThrow.Format($"A DELTA_BYTE_ARRAY page of '{Name}' rebuilds {total} bytes for {valid} values of {_width}.");
            }

            Place(page, heap.WritableSpan[..(int)total], valid);
        }
        finally
        {
            lengths.Dispose();
            heap?.Dispose();
        }
    }

    /// <summary>
    /// A BYTE_STREAM_SPLIT page gathered back into values of the column's physical width: straight
    /// into the page's slots when it has no null and the column keeps that width, else densely and
    /// then placed, narrowed or widened.
    /// </summary>
    private void Streams(Page page, ReadOnlySpan<byte> source, int valid)
    {
        if (source.Length != (long)valid * _width)
        {
            ParquetThrow.Format($"A BYTE_STREAM_SPLIT page of '{Name}' holds {source.Length} bytes for {valid} values of {_width}.");
        }

        int rows = page.Rows;
        bool kept = _form is not (LeafForm.Narrowed or LeafForm.BigEndianDecimal);
        if (page.Validity is null && kept)
        {
            ByteStreamSplit.Decode(source, _width, Slots(page, rows * _width).WritableSpan);
            return;
        }

        NativeSegmentOwner dense = _pool.Rent(Math.Max(valid, 1) * _width, 64);
        try
        {
            Span<byte> values = dense.WritableSpan[..(valid * _width)];
            ByteStreamSplit.Decode(source, _width, values);
            if (_form == LeafForm.Narrowed)
            {
                Narrow(page, values, valid);
            }
            else if (_form == LeafForm.BigEndianDecimal)
            {
                Widen(page, values, valid);
            }
            else
            {
                Place(page, values, valid);
            }
        }
        finally
        {
            dense.Dispose();
        }
    }

    /// <summary>
    /// An ALP page of FLOAT or DOUBLE values: decoded straight into the page's slots when it has no
    /// null, else densely and then placed, each vector's deltas unpacked into a scratch of the pool.
    /// </summary>
    private void AlpPage(Page page, ReadOnlySpan<byte> source, int valid)
    {
        int rows = page.Rows;
        bool wide = _leaf.Physical == PhysicalType.Double;
        int width = wide ? sizeof(double) : sizeof(float);
        NativeSegmentOwner scratch = _pool.Rent(Encodings.Alp.VectorSize(source) * width, 64);
        NativeSegmentOwner? dense = null;
        try
        {
            Span<byte> into = page.Validity is null
                ? Slots(page, rows * width).WritableSpan[..(rows * width)]
                : (dense = _pool.Rent(Math.Max(valid, 1) * width, 64)).WritableSpan[..(valid * width)];
            if (wide)
            {
                Encodings.Alp.Decode(source, MemoryMarshal.Cast<byte, double>(into), MemoryMarshal.Cast<byte, ulong>(scratch.WritableSpan));
            }
            else
            {
                Encodings.Alp.Decode(source, MemoryMarshal.Cast<byte, float>(into), MemoryMarshal.Cast<byte, uint>(scratch.WritableSpan));
            }

            if (dense is not null)
            {
                Place(page, into, valid);
            }
        }
        finally
        {
            scratch.Dispose();
            dense?.Dispose();
        }
    }

    /// <summary>An RLE page of booleans: the hybrid behind its four-byte length, at a width of one bit.</summary>
    private void Runs(Page page, ReadOnlySpan<byte> source, int valid)
    {
        if (source.Length < sizeof(int))
        {
            ParquetThrow.Truncated("RLE boolean page");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(source);
        if (length < 0 || length > source.Length - sizeof(int))
        {
            ParquetThrow.Format($"An RLE boolean page of '{Name}' declares {length} bytes where it holds {source.Length - sizeof(int)}.");
        }

        int rows = page.Rows;
        ReadOnlySpan<byte> runs = source.Slice(sizeof(int), length);
        if (page.Validity is null)
        {
            Span<byte> bits = Slots(page, CanonicalSupport.BitmapByteCount(rows)).WritableSpan;
            new RleHybridDecoder(1).ReadBits(runs, bits, 0, rows);
            return;
        }

        NativeSegmentOwner dense = _pool.Rent(CanonicalSupport.BitmapByteCount(Math.Max(valid, 1)), 64);
        try
        {
            new RleHybridDecoder(1).ReadBits(runs, dense.WritableSpan, 0, valid);
            NativeSegmentOwner bits = Slots(page, CanonicalSupport.BitmapByteCount(rows));
            SpreadBits(dense.WritableSpan, page.Validity.Buffer.Span, bits.WritableSpan, rows);
        }
        finally
        {
            dense.Dispose();
        }
    }

    /// <summary>Views over <paramref name="heap"/> cut by <paramref name="lengths"/>, a text column's checked as UTF-8, spread over the page's rows.</summary>
    private void CutViews(Page page, ReadOnlySpan<int> lengths, VortexBuffer heap, int valid)
    {
        int rows = page.Rows;
        NativeSegmentOwner views = Slots(page, rows * CanonicalSupport.ViewSize);
        Span<byte> into = views.WritableSpan;
        NativeSegmentOwner? dense = page.Validity is null ? null : _pool.Rent(Math.Max(valid, 1) * CanonicalSupport.ViewSize, 64);
        try
        {
            Span<byte> cut = dense is null ? into : dense.WritableSpan[..(valid * CanonicalSupport.ViewSize)];
            try
            {
                ViewKernels.BuildFromLengths(MemoryMarshal.AsBytes(lengths), PType.I32, default, heap.Span, cut, valid, requireUtf8: _form == LeafForm.Utf8);
            }
            catch (VortexFormatException invalid)
            {
                throw new ParquetFormatException($"A value of the text column '{Name}' is not UTF-8.", invalid);
            }

            if (dense is not null)
            {
                into.Clear();
                ValidRows.Spread(cut, into, ValidityMask.Bitmap(page.Validity!.Buffer.Span, 0), rows, CanonicalSupport.ViewSize, Encoding);
            }
        }
        finally
        {
            dense?.Dispose();
        }
    }

    /// <summary>Holds the page of stored bytes <paramref name="body"/> to its checksum, when the reader verifies them and the page has one.</summary>
    private void Check(in PageHeader header, VortexBuffer body)
    {
        if (VerifyChecksums && header.HasCrc && Crc32.HashToUInt32(body.Span) != unchecked((uint)header.Crc))
        {
            ParquetThrow.Format($"A page of '{Name}' does not match its checksum: its bytes are not the ones written.");
        }
    }

    /// <summary>The chunk's dictionary page, decoded as a page of one slot per entry and no null.</summary>
    private Page DecodeDictionary(ScanContext context, in PageHeader header, VortexBuffer body, NativeSegmentOwner? stored)
    {
        Counters?.AddDictionary();
        NativeSegmentOwner? owner = stored;
        try
        {
            Check(header, body);
            if (_dictionary is not null)
            {
                ParquetThrow.Format($"The column chunk of '{Name}' holds a second dictionary page.");
            }

            if (header.Encoding is not (ParquetEncoding.Plain or ParquetEncoding.PlainDictionary))
            {
                throw new ParquetUnsupportedException(header.Encoding.ToString(), ParquetComponentKind.Encoding,
                    $"The dictionary page of '{Name}' is {header.Encoding}; the standard writes a dictionary PLAIN.");
            }
        }
        catch
        {
            owner?.Dispose();
            throw;
        }

        Page page = Rent(header.ValueCount);
        try
        {
            int size = header.UncompressedPageSize;
            if (_codec != CompressionCodec.Uncompressed)
            {
                // A dictionary page has no flag that keeps it out of the codec: it is always compressed.
                Cap(size);
                NativeSegmentOwner values = _pool.Rent(size, 64);
                try
                {
                    Counters?.AddDecompression();
                    PageCodecs.Decompress(_codec, body.Span, values.WritableSpan, context.Zstd);
                }
                catch
                {
                    values.Dispose();
                    throw;
                }

                owner?.Dispose();
                owner = null;
                Values(page, values.Buffer, values, header.ValueCount);
            }
            else
            {
                if (body.Length != size)
                {
                    ParquetThrow.Format($"The dictionary page of '{Name}' holds {body.Length} bytes where its header declares {size}.");
                }

                NativeSegmentOwner? taken = owner;
                owner = null;
                Values(page, body, taken, header.ValueCount);
            }

            return page;
        }
        catch
        {
            owner?.Dispose();
            page.Release();
            _free.Push(page);
            throw;
        }
    }

    /// <summary>
    /// A dictionary-encoded page: its codes, behind their width, decoded and gathered from the
    /// chunk's dictionary into one slot per row, every code checked against the dictionary's size.
    /// </summary>
    private void Codes(Page page, ReadOnlySpan<byte> source, int valid)
    {
        Page dictionary = _dictionary ?? ParquetThrow.Format<Page>($"A page of '{Name}' is dictionary-encoded and its chunk holds no dictionary page before it.");
        int rows = page.Rows;
        if (valid == 0)
        {
            // Every row is null: zeroed slots, nothing to gather.
            Slots(page, _slot == 0 ? CanonicalSupport.BitmapByteCount(rows) : rows * _slot).WritableSpan.Clear();
            page.Data = dictionary.Data;
            return;
        }

        if (source.IsEmpty || source[0] > 32)
        {
            ParquetThrow.Format($"A dictionary-encoded page of '{Name}' lacks its codes' width, or declares one past 32 bits.");
        }

        int width = source[0];
        NativeSegmentOwner codes = _pool.Rent(valid * sizeof(uint), 64);
        NativeSegmentOwner? dense = null;
        try
        {
            Span<uint> span = MemoryMarshal.Cast<byte, uint>(codes.WritableSpan)[..valid];
            if (width == 0)
            {
                span.Clear();
            }
            else
            {
                new RleHybridDecoder(width).Read(source[1..], span);
            }

            // The core keeps a dictionary over booleans, primitives, decimals and views; a fixed-size
            // list of bytes is gathered.
            if (KeepEncodings && _form is not (LeafForm.Bool or LeafForm.FixedBytes or LeafForm.Null))
            {
                Keep(page, span, dictionary.Rows);
                return;
            }

            if (_slot == 0)
            {
                // Booleans: a bit per entry, gathered a bit at a time.
                NativeSegmentOwner bits = Slots(page, CanonicalSupport.BitmapByteCount(rows));
                Span<byte> into = bits.WritableSpan;
                into.Clear();
                ReadOnlySpan<byte> entries = dictionary.Values.Span;
                Span<byte> gathered = page.Validity is null ? into : (dense = _pool.Rent(CanonicalSupport.BitmapByteCount(valid), 64)).WritableSpan;
                gathered.Clear();
                for (int i = 0; i < valid; i++)
                {
                    uint code = span[i];
                    if (code >= (uint)dictionary.Rows)
                    {
                        ParquetThrow.Format($"A code of '{Name}', {code}, passes its dictionary's {dictionary.Rows} entries.");
                    }

                    if (CanonicalSupport.BitAt(entries, (int)code))
                    {
                        CanonicalSupport.SetBit(gathered, i);
                    }
                }

                if (page.Validity is not null)
                {
                    SpreadBits(gathered, page.Validity.Buffer.Span, into, rows);
                }

                return;
            }

            ReadOnlySpan<byte> codeBytes = MemoryMarshal.AsBytes((ReadOnlySpan<uint>)span);
            NativeSegmentOwner slots = Slots(page, rows * _slot);
            Span<byte> target = page.Validity is null ? slots.WritableSpan : (dense = _pool.Rent(valid * _slot, 64)).WritableSpan;
            int bad = RowKernels.Gather(codeBytes, PType.U32, dictionary.Values.Span, _slot, dictionary.Rows, target[..(valid * _slot)], valid);
            if (bad >= 0)
            {
                ParquetThrow.Format($"A code of '{Name}', {span[bad]}, passes its dictionary's {dictionary.Rows} entries.");
            }

            if (page.Validity is not null)
            {
                Span<byte> into = slots.WritableSpan;
                into.Clear();
                ValidRows.Spread(target[..(valid * _slot)], into, ValidityMask.Bitmap(page.Validity.Buffer.Span, 0), rows, _slot, Encoding);
            }

            // Gathered views point into the dictionary's bytes, which the chunk keeps.
            page.Data = dictionary.Data;
        }
        finally
        {
            codes.Dispose();
            dense?.Dispose();
        }
    }

    /// <summary>
    /// Keeps a dictionary-encoded page as a code per row: its <paramref name="dense"/> codes, one a value,
    /// held to the dictionary's <paramref name="entries"/> and spread over its rows, a null one coding 0.
    /// </summary>
    private void Keep(Page page, ReadOnlySpan<uint> dense, int entries)
    {
        int outside = RowKernels.FirstCodeOutside(dense, (uint)entries);
        if (outside >= 0)
        {
            ParquetThrow.Format($"A code of '{Name}', {dense[outside]}, passes its dictionary's {entries} entries.");
        }

        int rows = page.Rows;
        Cap((long)rows * sizeof(uint));
        NativeSegmentOwner codes = _pool.Rent(Math.Max(rows * sizeof(uint), 1), 64);
        Span<byte> into = codes.WritableSpan[..(rows * sizeof(uint))];
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(dense);
        if (page.Validity is null)
        {
            source.CopyTo(into);
        }
        else
        {
            into.Clear();
            ValidRows.Spread(source, into, ValidityMask.Bitmap(page.Validity.Buffer.Span, 0), rows, sizeof(uint), Encoding);
        }

        page.Codes = codes;
    }

    /// <summary>Gathers the values of a page kept as its codes: for a batch that is not the page whole.</summary>
    private void Materialize(Page page)
    {
        if (page.Codes is not { } codes)
        {
            return;
        }

        Page dictionary = _dictionary!;
        int rows = page.Rows;
        Span<byte> into = Slots(page, rows * _slot).WritableSpan[..(rows * _slot)];
        if (dictionary.Rows == 0)
        {
            // Every row is null, and names no entry.
            into.Clear();
        }
        else
        {
            // The codes were held to the dictionary when they were kept; a null row's names entry 0.
            RowKernels.Gather(codes.Buffer.Span[..(rows * sizeof(uint))], PType.U32, dictionary.Values.Span, _slot, dictionary.Rows, into, rows);
        }

        page.Data = dictionary.Data;
        page.Codes = null;
        codes.Dispose();
    }

    private static bool Same(VortexBuffer a, VortexBuffer b) =>
        a.Length == b.Length && Unsafe.AreSame(ref MemoryMarshal.GetReference(a.Span), ref MemoryMarshal.GetReference(b.Span));

    private void Cap(long bytes)
    {
        if (bytes > _cap)
        {
            ParquetThrow.Format($"A page of '{Name}' decodes to {bytes} bytes, past the {_cap} the reader allows.");
        }
    }

    /// <summary>A page decoded whole, and how many of its rows batches have read.</summary>
    /// <remarks>
    /// A nested column's page counts its values as rows, one slot each and no validity, and its
    /// entries apart: their levels, a byte per entry, repetition then definition.
    /// </remarks>
    private sealed class Page
    {
        internal int Rows;
        internal int Read;
        internal int Nulls;

        /// <summary>A nested column's entries: the levels the page holds, values or not.</summary>
        internal int Entries;

        /// <summary>The entries batches have read.</summary>
        internal int EntriesRead;

        /// <summary>A nested column's rows that start in the page past what batches have read.</summary>
        internal int RowsUnread;

        /// <summary>
        /// Whether the page's last row ends in it, as a v2 page's does: the standard splits no row
        /// across pages of the second version, so the page after it need not be read to say so.
        /// </summary>
        internal bool EndsRows;

        /// <summary>A nested column's levels, a byte per entry: the repetition levels, then the definition levels.</summary>
        internal NativeSegmentOwner? Levels;

        /// <summary>The validity bitmap, when the page holds a null.</summary>
        internal NativeSegmentOwner? Validity;

        /// <summary>The block holding <see cref="Values"/>, or null when they lie in the chunk.</summary>
        internal NativeSegmentOwner? Slots;

        /// <summary>A dictionary-encoded page's code per row, kept for a scan that keeps encodings, until its values are gathered.</summary>
        internal NativeSegmentOwner? Codes;

        /// <summary>One slot per row: the values, the bits or the views.</summary>
        internal VortexBuffer Values;

        /// <summary>The block holding <see cref="Data"/>, or null when it lies in the chunk.</summary>
        internal NativeSegmentOwner? DataOwner;

        /// <summary>A byte array page's bytes, which its views point into.</summary>
        internal VortexBuffer Data;

        /// <summary>The repetition level of each entry; empty when the column does not repeat.</summary>
        internal ReadOnlySpan<byte> Repetition(bool repeats) => repeats ? Levels!.WritableSpan[..Entries] : default;

        /// <summary>The definition level of each entry; empty when every entry is defined.</summary>
        internal ReadOnlySpan<byte> Definition(bool optional) => optional ? Levels!.WritableSpan.Slice(Entries, Entries) : default;

        internal void Release()
        {
            Validity?.Dispose();
            Slots?.Dispose();
            Codes?.Dispose();
            Codes = null;
            DataOwner?.Dispose();
            Levels?.Dispose();
            Validity = null;
            Slots = null;
            DataOwner = null;
            Levels = null;
            Values = default;
            Data = default;
            Rows = 0;
            Read = 0;
            Nulls = 0;
            Entries = 0;
            EntriesRead = 0;
            RowsUnread = 0;
            EndsRows = false;
        }
    }
}
