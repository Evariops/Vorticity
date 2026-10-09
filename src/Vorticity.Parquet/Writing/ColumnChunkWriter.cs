using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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
/// them, and the validity as a bitmap, so closing a page encodes nothing more than its levels. A
/// page closes at every block of <see cref="WriteColumn"/>'s writer's block rows counted from the
/// file's first row, the same rows in every column, which is the grid a reader of this file reads
/// a batch per page on.
/// </para>
/// <para>
/// A page is a v2 data page: its definition levels uncompressed before its values, the values
/// compressed with the chunk's codec unless that saves less than an eighth, in which case they are
/// stored as they are and the header says so.
/// </para>
/// </remarks>
internal sealed class ColumnChunkWriter : IDisposable
{
    private readonly WriteColumn _column;
    private readonly CompressionCodec _codec;
    private readonly int _level;
    private readonly ZstdCompressor? _zstd;
    private readonly int _blockRows;
    private readonly PooledBytes _values;
    private readonly PooledBytes _levels;
    private readonly PooledBytes _compressed;
    private readonly PooledBytes _chunk;
    private readonly byte[] _validity;
    private readonly byte[] _levelBytes;

    /// <summary>The page's validity as the words the compressing kernel takes, a block's worth.</summary>
    private readonly ulong[] _mask;
    private readonly List<PageLocation> _pages = [];
    private int _pageRows;
    private int _pageValues;
    private int _boolBits;
    private long _chunkRows;
    private long _chunkNulls;
    private long _chunkUncompressed;
    private Bounds _chunkBounds;

    internal ColumnChunkWriter(WriteColumn column, CompressionCodec codec, int level, ZstdCompressor? zstd, int blockRows, MemoryPool<byte> pool)
    {
        _column = column;
        _codec = codec;
        _level = level;
        _zstd = zstd;
        _blockRows = blockRows;
        _values = new PooledBytes(pool);
        _levels = new PooledBytes(pool);
        _compressed = new PooledBytes(pool);
        _chunk = new PooledBytes(pool);
        _validity = new byte[(blockRows + 7) / 8 + 8];
        _levelBytes = new byte[blockRows];
        _mask = new ulong[(blockRows + 63) >> 6];
        _chunkBounds = Bounds.Empty;
    }

    internal WriteColumn Column => _column;

    /// <summary>The bytes of the pages closed so far, waiting for the row group to close.</summary>
    internal long BufferedBytes => _chunk.Length + _values.Length;

    /// <summary>The closed chunk's bytes, from <see cref="Close"/> to <see cref="Reset"/>.</summary>
    internal ReadOnlyMemory<byte> Bytes => _chunk.Written;

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

        bool compressed = _codec != CompressionCodec.Uncompressed;
        ReadOnlySpan<byte> stored = body;
        if (compressed)
        {
            _compressed.Clear();
            Span<byte> destination = _compressed.GetSpan(PageCodecs.MaxCompressedLength(_codec, body.Length));
            int size = PageCodecs.Compress(_codec, _level, body, destination, _zstd);
            if (size <= body.Length - body.Length / 8)
            {
                stored = destination[..size];
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
            Encoding = ParquetEncoding.Plain,
            DefinitionLevelsLength = _levels.Length,
            RepetitionLevelsLength = 0,
            IsCompressed = compressed || _codec == CompressionCodec.Uncompressed,
        };

        int pageStart = _chunk.Length;
        ThriftCompactWriter writer = new(_chunk);
        header.Write(ref writer, default);
        writer.Flush();
        int headerLength = _chunk.Length - pageStart;
        _chunk.Write(_levels.WrittenSpan);
        _chunk.Write(stored);
        _pages.Add(new PageLocation(pageStart, _chunk.Length - pageStart, _chunkRows));
        _chunkUncompressed += headerLength + header.UncompressedPageSize;
        _chunkRows += rows;
        _chunkNulls += nulls;

        _pageRows = 0;
        _pageValues = 0;
        _boolBits = 0;
        _values.Clear();
        Array.Clear(_validity);
    }

    /// <summary>
    /// The chunk's pages and what its metadata says of them, the chunk starting at
    /// <paramref name="offset"/> in the file; the caller writes <see cref="Bytes"/> there and then
    /// calls <see cref="Reset"/>.
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

        PageLocation[] pages = new PageLocation[_pages.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            PageLocation page = _pages[i];
            pages[i] = page with { Offset = offset + page.Offset };
        }

        uint encodings = 1u << (int)ParquetEncoding.Plain;
        if (_column.Nullable)
        {
            encodings |= 1u << (int)ParquetEncoding.Rle;
        }

        return new ChunkResult(
            offset,
            _chunkRows,
            _chunkNulls,
            _chunkUncompressed,
            _chunk.Length,
            encodings,
            _chunkBounds,
            pages);
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
    }

    private void Stage(CanonicalArena arena, int index, int start, int count)
    {
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

        if (values > 0)
        {
            StageValues(arena, node, start, count, values);
        }

        _pageRows += count;
        _pageValues += values;
    }

    /// <summary>The values of the rows that hold one, in their PLAIN form, appended to the page's.</summary>
    private void StageValues(CanonicalArena arena, CanonicalNode node, int start, int count, int values)
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
                StageBytes(node, start, count, dense, first);
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
                    _values.Write(source);
                }
                else
                {
                    Compact(source, width, first, count, _values.Reserve(values * width));
                }

                return;
            default:
                StageConverted(node, start, count, dense, first, values);
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

    private void StageBytes(CanonicalNode node, int start, int count, bool dense, int first)
    {
        ViewValues views = new(node);
        for (int row = 0; row < count; row++)
        {
            if (!dense && !CanonicalSupport.BitAt(_validity, first + row))
            {
                continue;
            }

            ReadOnlySpan<byte> value = views.At(start + row);
            Span<byte> destination = _values.Reserve(4 + value.Length);
            BinaryPrimitives.WriteInt32LittleEndian(destination, value.Length);
            value.CopyTo(destination[4..]);
        }
    }

    private void StageConverted(CanonicalNode node, int start, int count, bool dense, int first, int values)
    {
        int width = _column.ValueWidth;
        Span<byte> destination = _values.Reserve(values * width);
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
                    Span<byte> target = destination.Slice(at, width);
                    if (_column.Conversion == ValueConversion.DecimalToBigEndian)
                    {
                        for (int b = 0; b < width; b++)
                        {
                            target[b] = wide[width - 1 - b];
                        }
                    }
                    else
                    {
                        wide[..width].CopyTo(target);
                    }

                    at += width;
                }

                return;
        }
    }


    public void Dispose()
    {
        _values.Dispose();
        _levels.Dispose();
        _compressed.Dispose();
        _chunk.Dispose();
    }
}

/// <summary>A closed column chunk: where it goes, and what its metadata says of it.</summary>
internal sealed record ChunkResult(
    long Offset,
    long Rows,
    long Nulls,
    long UncompressedSize,
    long CompressedSize,
    uint Encodings,
    Bounds Bounds,
    PageLocation[] Pages);

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
