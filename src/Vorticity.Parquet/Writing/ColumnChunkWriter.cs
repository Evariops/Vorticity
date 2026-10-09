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
    private readonly byte[] _levelBytes;

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

    private readonly uint[] _pageCodes;
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

    private int _dictionaryPages;
    private int _plainPages;

    /// <summary>The codes the chunk's dictionary pages took, and the PLAIN bytes they stand for.</summary>
    private long _codeBytes;
    private long _plainBytes;
    private int _pageRows;
    private int _pageValues;
    private int _boolBits;
    private long _chunkRows;
    private long _chunkNulls;
    private long _chunkUncompressed;
    private Bounds _chunkBounds;

    internal ColumnChunkWriter(WriteColumn column, CompressionCodec codec, int level, ZstdCompressor? zstd, int blockRows, bool dictionaries, AlignedBufferPool pool)
    {
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
        _eligible = dictionaries && column.Conversion is not (ValueConversion.Bool or ValueConversion.Null) && !column.FixedElements;
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

    /// <summary>Appends <paramref name="count"/> rows of <paramref name="node"/> from <paramref name="start"/>, closing pages as blocks fill.</summary>
    internal void Append(CanonicalArena arena, int node, int start, int count)
    {
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
        if (_pageRows == 0)
        {
            return;
        }

        int rows = _pageRows;
        int nulls = rows - _pageValues;
        _levels.Clear();
        if (_column.Nullable)
        {
            Span<byte> levels = _levelBytes.AsSpan(0, rows);
            BitPacking.Unpack8(_validity, 1, levels);
            int size = RleHybridEncoder.Size(levels, 1);
            RleHybridEncoder.Encode(levels, 1, _levels.Reserve(size));
        }

        ReadOnlySpan<byte> body = _column.Conversion == ValueConversion.Bool
            ? _values.WrittenSpan[..((_boolBits + 7) / 8)]
            : _values.WrittenSpan;
        Bounds bounds = Bounds.Of(_column.Domain, body, _pageValues);
        _chunkBounds = _chunkBounds.Merge(bounds);

        ParquetEncoding encoding = ParquetEncoding.Plain;
        if (_dictionary && _pageFirstCode >= 0)
        {
            switch (EncodeCodes(rows, body.Length))
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

        if (encoding == ParquetEncoding.Plain)
        {
            _plainPages++;
        }
        else
        {
            _dictionaryPages++;
        }

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
            ValueCount = rows,
            NullCount = nulls,
            RowCount = rows,
            Encoding = encoding,
            DefinitionLevelsLength = _levels.Length,
            RepetitionLevelsLength = 0,
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
        _chunkNulls += nulls;

        _pageRows = 0;
        _pageValues = 0;
        _boolBits = 0;
        _pageFirstCode = -1;
        _values.Clear();
        Array.Clear(_validity);
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

        uint encodings = 0;
        if (_plainPages > 0 || dictionary > 0)
        {
            encodings |= 1u << (int)ParquetEncoding.Plain;
        }

        if (_dictionaryPages > 0)
        {
            encodings |= 1u << (int)ParquetEncoding.RleDictionary;
        }

        if (_column.Nullable)
        {
            encodings |= 1u << (int)ParquetEncoding.Rle;
        }

        return new ChunkResult(
            offset,
            offset + dictionary,
            dictionary > 0 ? offset : -1,
            _chunkRows,
            _chunkNulls,
            _chunkUncompressed,
            _chunk.Length + dictionary,
            encodings,
            _chunkBounds,
            pages,
            _dictionaryPages,
            _plainPages);
    }

    /// <summary>Forgets the closed chunk, keeping the buffers for the next row group's.</summary>
    internal void Reset()
    {
        _chunk.Clear();
        _pages.Clear();
        _chunkRows = 0;
        _chunkNulls = 0;
        _chunkUncompressed = 0;
        _chunkBounds = Bounds.Empty;
        _table?.Reset();
        _entries.Clear();
        _dictionaryPage.Clear();
        _entryCount = 0;
        _frozenEntries = 0;
        _frozenBytes = 0;
        _dictionaryPages = 0;
        _plainPages = 0;
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
            if (_column.Nullable && !CanonicalSupport.BitAt(_validity, row))
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

    /// <summary>The dictionary page, when a data page used the dictionary: the values coded until the last such page.</summary>
    private void WriteDictionaryPage()
    {
        _dictionaryPage.Clear();
        if (_dictionaryPages == 0)
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
/// <param name="Nulls">Its null rows.</param>
/// <param name="UncompressedSize">Its pages' bytes, headers included, before compression.</param>
/// <param name="CompressedSize">Its bytes in the file.</param>
/// <param name="Encodings">The encodings its pages use, a bit per encoding.</param>
/// <param name="Bounds">Its least and greatest values, where its domain has them.</param>
/// <param name="Pages">Where each data page lies, for the offset index.</param>
/// <param name="DictionaryPages">The data pages that are codes into the dictionary.</param>
/// <param name="PlainPages">The data pages that are PLAIN.</param>
internal sealed record ChunkResult(
    long Offset,
    long DataPageOffset,
    long DictionaryPageOffset,
    long Rows,
    long Nulls,
    long UncompressedSize,
    long CompressedSize,
    uint Encodings,
    Bounds Bounds,
    PageLocation[] Pages,
    int DictionaryPages,
    int PlainPages);

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
