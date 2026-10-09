using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Parquet.Encodings;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A column under a list, a map or a struct, read by its levels for the field above it to assemble:
/// each page's levels decoded a byte per entry, and its values densely, one per entry that reaches
/// the leaf.
/// </summary>
/// <remarks>
/// <para>
/// A batch takes the entries of whole rows: a row starts at an entry of repetition level zero and
/// runs to the next one, across pages where a v1 page cuts it, so the reader looks at the page after
/// the last one it takes whole to see whether its last row goes on. The batch's levels are copied
/// into a block the reader keeps for the assembler to walk; its values are gathered into the arena,
/// or are the page's own when the batch takes every value of one page.
/// </para>
/// <para>
/// Every level is checked against the column's maximum as its page is decoded, and the page's counts
/// against its levels, so that the assembler's walk can trust them: a level past its maximum would
/// count a slot no value fills.
/// </para>
/// </remarks>
internal sealed partial class ColumnChunkReader
{
    private readonly bool _nested;
    private readonly int _repetitionWidth;
    private readonly int _definitionWidth;
    private readonly List<Segment> _plan = [];

    /// <summary>The levels of the batch read last: its repetition levels, then its definition levels.</summary>
    private NativeSegmentOwner? _levels;

    /// <summary>Whether a data page of the chunk was decoded, after which a v1 page may go on with a row.</summary>
    private bool _pastFirstPage;

    /// <summary>The values of the batch read last, one per entry that reaches the leaf.</summary>
    private VortexBuffer _dense;

    /// <summary>The buffers <see cref="_dense"/>'s views point into, at the head of <see cref="_gathered"/>.</summary>
    private int _denseBuffers;

    /// <summary>The column read.</summary>
    internal ParquetColumn Column => _leaf;

    /// <summary>The entries of the batch read last.</summary>
    internal int Entries { get; private set; }

    /// <summary>The values of the batch read last: its entries whose definition level is the column's maximum.</summary>
    internal int ValueCount { get; private set; }

    /// <summary>Zeros, a batch's of the arena: the levels of a column that does not repeat, or that is required at every level.</summary>
    private VortexBuffer _zeros;

    /// <summary>The page whose levels the batch read last are, from <see cref="_batchFrom"/>, when it took one page's entries; else null, and they are in <see cref="_levels"/>.</summary>
    private Page? _batchPage;
    private int _batchFrom;

    /// <summary>The repetition level of each entry of the batch read last.</summary>
    internal ReadOnlySpan<byte> Repetition =>
        _leaf.MaxRepetitionLevel == 0 ? _zeros.Span
        : _batchPage is { } page ? page.Repetition(true).Slice(_batchFrom, Entries)
        : _levels!.WritableSpan[..Entries];

    /// <summary>The definition level of each entry of the batch read last.</summary>
    internal ReadOnlySpan<byte> Definition =>
        _leaf.MaxDefinitionLevel == 0 ? _zeros.Span
        : _batchPage is { } page ? page.Definition(true).Slice(_batchFrom, Entries)
        : _levels!.WritableSpan.Slice(Entries, Entries);

    /// <summary>Reads the entries of the next <paramref name="rows"/> rows: their levels and their values.</summary>
    internal void ReadNested(ScanContext context, int rows)
    {
        // The batch that read the retired pages was released when this one was asked for.
        ReleaseRetired();
        if (rows > _rowsLeft)
        {
            ParquetThrow.Format($"The column chunk of '{Name}' holds fewer rows than its row group.");
        }

        bool repeats = _leaf.MaxRepetitionLevel > 0;
        bool optional = _leaf.MaxDefinitionLevel > 0;
        byte defined = (byte)_leaf.MaxDefinitionLevel;
        _plan.Clear();
        int started = 0;
        long entries = 0;
        long values = 0;
        Page? page = _page ??= TryNextPage(context);
        while (page is not null)
        {
            int from = page.EntriesRead;
            int cut;
            if (repeats)
            {
                // A row starts at each entry of repetition level zero, and the one past the batch's
                // last row ends the batch: a page whose rows the batch takes whole is not searched.
                int need = rows - started;
                if (need >= page.RowsUnread)
                {
                    cut = page.Entries;
                    started += page.RowsUnread;
                    page.RowsUnread = 0;
                }
                else
                {
                    cut = from + LevelKernels.NthZero(page.Repetition(true)[from..page.Entries], need);
                    started = rows;
                    page.RowsUnread -= need;
                }
            }
            else
            {
                cut = from + Math.Min(page.Entries - from, rows - started);
                started += cut - from;
            }

            if (cut > from)
            {
                int taken = _form == LeafForm.Null ? 0
                    : optional ? page.Definition(true)[from..cut].Count(defined)
                    : cut - from;
                _plan.Add(new Segment(page, from, cut, taken));
                entries += cut - from;
                values += taken;
            }

            if (cut < page.Entries)
            {
                break;
            }

            // The page is taken whole: its buffers stay until the next batch, and the page after it
            // says whether its last row goes on.
            _retired.Add(page);
            _page = null;
            if (!repeats && started == rows)
            {
                break;
            }

            page = _page = TryNextPage(context);
        }

        if (started < rows)
        {
            ParquetThrow.Format($"The column chunk of '{Name}' ends before its rows do.");
        }

        if (entries > int.MaxValue / 2)
        {
            ParquetThrow.Format($"A batch of '{Name}' spans {entries} entries, more than a batch holds.");
        }

        Cap(2 * entries);
        Gather(context, (int)entries, (int)values, repeats, optional);
        _rowsLeft -= rows;
    }

    /// <summary>
    /// The leaf's node over <paramref name="slots"/> slots: the values read last spread over those
    /// <paramref name="present"/> sets, <paramref name="valid"/> of them, a slot it clears zero.
    /// </summary>
    /// <param name="context">The scan's context, whose arena takes the node.</param>
    /// <param name="slots">The slots the field above gives the leaf.</param>
    /// <param name="present">A bit per slot, set where the slot holds a value.</param>
    /// <param name="valid">The bits <paramref name="present"/> sets.</param>
    internal int LeafNode(ScanContext context, int slots, VortexBuffer present, int valid)
    {
        CanonicalArena arena = context.Canonical;
        if (_form == LeafForm.Null)
        {
            return arena.AddNull(_type, slots);
        }

        if (valid != ValueCount)
        {
            ParquetThrow.Format($"The levels of '{Name}' place {valid} values where its pages hold {ValueCount}.");
        }

        Validity validity = Validity.NonNullable;
        if (_type.IsNullable)
        {
            validity = valid == slots ? Validity.AllValid
                : valid == 0 ? Validity.AllInvalid
                : Validity.Bitmap(arena.AddBool(_validityType, slots, Validity.NonNullable, present, 0));
        }

        ReadOnlySpan<VortexBuffer> data = _gathered.AsSpan(0, _denseBuffers);
        if (valid == slots)
        {
            return Node(arena, slots, validity, _dense, data);
        }

        int bytes = _slot == 0 ? CanonicalSupport.BitmapByteCount(slots) : checked(slots * _slot);
        VortexBuffer spread = CanonicalSupport.Allocate(context.Decode, Math.Max(bytes, 1), 64, out Span<byte> into);
        if (_slot == 0)
        {
            SpreadBits(_dense.Span, present.Span, into, slots);
        }
        else if (valid > 0)
        {
            ValidRows.Spread(_dense.Span, into, ValidityMask.Bitmap(present.Span, 0), slots, _slot, Encoding);
        }

        return Node(arena, slots, validity, spread.Slice(0, bytes), data);
    }

    /// <summary>
    /// The planned entries' levels and values: a page's own when the batch takes its entries from one
    /// page, its values too when they are all the page's; else copied, the levels into the block the
    /// assembler walks, the values into the arena.
    /// </summary>
    private void Gather(ScanContext context, int entries, int values, bool repeats, bool optional)
    {
        // A column that does not repeat starts a row at every entry, and one that is required
        // everywhere reaches its maximum, zero, at every entry: the levels it has not are zeros.
        if (!repeats || !optional)
        {
            _zeros = CanonicalSupport.Allocate(context.Decode, Math.Max(entries, 1), 64, out _).Slice(0, entries);
        }

        Entries = entries;
        ValueCount = values;
        _denseBuffers = 0;
        _batchPage = null;
        if (_plan.Count == 0)
        {
            _dense = default;
            return;
        }

        Segment first = _plan[0];
        bool single = _plan.Count == 1;
        bool whole = single && first.Page.Read == 0 && first.Values == first.Page.Rows;
        Span<byte> into = default;
        if (_form == LeafForm.Null)
        {
            _dense = default;
        }
        else if (whole)
        {
            _dense = first.Page.Values;
            if (_views)
            {
                _gathered[0] = first.Page.Data;
                _denseBuffers = 1;
            }
        }
        else
        {
            // Every byte is copied over: bits, values, and views rebased.
            int bytes = _slot == 0 ? CanonicalSupport.BitmapByteCount(values) : checked(values * _slot);
            _dense = CanonicalSupport.AllocateUninitialized(context.Decode, Math.Max(bytes, 1), 64, out into).Slice(0, bytes);
        }

        Span<byte> repetition = default;
        Span<byte> definition = default;
        if (single)
        {
            _batchPage = first.Page;
            _batchFrom = first.From;
        }
        else
        {
            Span<byte> levels = LevelScratch(2 * entries);
            repetition = levels[..entries];
            definition = levels.Slice(entries, entries);
        }

        int at = 0;
        int done = 0;
        int buffers = _denseBuffers;
        foreach (Segment segment in _plan)
        {
            Page page = segment.Page;
            int length = segment.To - segment.From;
            if (!single && repeats)
            {
                page.Repetition(true).Slice(segment.From, length).CopyTo(repetition[at..]);
            }

            if (!single && optional)
            {
                page.Definition(true).Slice(segment.From, length).CopyTo(definition[at..]);
            }

            if (!whole && segment.Values > 0)
            {
                CopySlots(page, segment.Values, into, done, ref buffers);
            }

            page.Read += segment.Values;
            page.EntriesRead = segment.To;
            at += length;
            done += segment.Values;
        }

        _plan.Clear();
        _denseBuffers = buffers;
    }

    /// <summary>The block of the batch's levels, kept between batches and grown when a batch needs more.</summary>
    private Span<byte> LevelScratch(int bytes)
    {
        if (_levels is null || _levels.WritableSpan.Length < bytes)
        {
            int size = Math.Max(bytes, _levels is null ? 4096 : 2 * _levels.WritableSpan.Length);
            _levels?.Dispose();
            _levels = null;
            _levels = _pool.Rent(size, 64);
        }

        return _levels.WritableSpan[..bytes];
    }

    /// <summary>A nested column's v2 page: its levels, ahead of its values and never compressed; the entries that hold a value.</summary>
    private int NestedLevelsV2(Page page, in PageHeader header, int at)
    {
        int entries = NestedEntries(header.ValueCount);
        Span<byte> levels = LevelBlock(page, entries);
        if (_leaf.MaxRepetitionLevel > 0)
        {
            new RleHybridDecoder(_repetitionWidth).Read(_chunk.Slice(at, header.RepetitionLevelsLength).Span, levels[..entries]);
        }

        if (_leaf.MaxDefinitionLevel > 0)
        {
            ReadOnlySpan<byte> runs = _chunk.Slice(at + header.RepetitionLevelsLength, header.DefinitionLevelsLength).Span;
            new RleHybridDecoder(_definitionWidth).Read(runs, levels.Slice(entries, entries));
        }

        int valid = NestedPage(page, entries);
        if (header.NullCount != entries - valid)
        {
            ParquetThrow.Format($"A page of '{Name}' declares {header.NullCount} nulls where its levels hold {entries - valid}.");
        }

        return valid;
    }

    /// <summary>A nested column's v1 page: its levels, inside its bytes ahead of its values; the entries that hold a value.</summary>
    private int NestedLevelsV1(Page page, in PageHeader header, ReadOnlySpan<byte> body, out int position)
    {
        int entries = NestedEntries(header.ValueCount);
        Span<byte> levels = LevelBlock(page, entries);
        position = 0;
        if (_leaf.MaxRepetitionLevel > 0)
        {
            LevelsV1(body, header.RepetitionLevelEncoding, _repetitionWidth, levels[..entries], "repetition", ref position);
        }

        if (_leaf.MaxDefinitionLevel > 0)
        {
            LevelsV1(body, header.DefinitionLevelEncoding, _definitionWidth, levels.Slice(entries, entries), "definition", ref position);
        }

        return NestedPage(page, entries);
    }

    /// <summary>One kind of a v1 page's levels: RLE behind its length, or the deprecated BIT_PACKED.</summary>
    private void LevelsV1(ReadOnlySpan<byte> body, ParquetEncoding encoding, int width, Span<byte> into, string kind, ref int position)
    {
        if (encoding == ParquetEncoding.BitPacked)
        {
            int bytes = LegacyBitPacked.Bytes(into.Length, width);
            if (bytes > body.Length - position)
            {
                ParquetThrow.Format($"A page of '{Name}' ends inside its {kind} levels.");
            }

            LegacyBitPacked.ReadLevels(body.Slice(position, bytes), width, into);
            position += bytes;
            return;
        }

        new RleHybridDecoder(width).Read(RleLevels(body, encoding, kind, ref position), into);
    }

    /// <summary>A page's count of entries, checked and charged against the cap for the levels it decodes to.</summary>
    private int NestedEntries(int entries)
    {
        if (entries <= 0)
        {
            ParquetThrow.Format($"A page of '{Name}' declares {entries} values.");
        }

        Cap(2L * entries);
        return entries;
    }

    /// <summary>The page's block of levels, a byte per entry of each kind.</summary>
    private Span<byte> LevelBlock(Page page, int entries)
    {
        page.Entries = entries;
        if (_leaf.MaxRepetitionLevel == 0 && _leaf.MaxDefinitionLevel == 0)
        {
            return default;
        }

        page.Levels = _pool.Rent(2 * entries, 64);
        return page.Levels.WritableSpan[..(2 * entries)];
    }

    /// <summary>
    /// Checks a nested page's decoded levels against the column's maxima and counts its rows, which
    /// the chunk must have left, and its values; the entries that hold a value.
    /// </summary>
    private int NestedPage(Page page, int entries)
    {
        int rows = entries;
        if (_leaf.MaxRepetitionLevel > 0)
        {
            ReadOnlySpan<byte> repetition = page.Repetition(true);
            RequireLevels(repetition, _leaf.MaxRepetitionLevel, "repetition");
            if (!_pastFirstPage && repetition[0] != 0)
            {
                ParquetThrow.Format($"The column chunk of '{Name}' starts inside a row.");
            }

            rows = repetition.Count((byte)0);
        }

        int valid = entries;
        if (_leaf.MaxDefinitionLevel > 0)
        {
            ReadOnlySpan<byte> definition = page.Definition(true);
            RequireLevels(definition, _leaf.MaxDefinitionLevel, "definition");
            valid = definition.Count((byte)_leaf.MaxDefinitionLevel);
        }

        if (rows > _rowsUnread)
        {
            ParquetThrow.Format($"A page of '{Name}' starts {rows} rows where its chunk has {_rowsUnread} left.");
        }

        _rowsUnread -= rows;
        _pastFirstPage = true;
        page.Rows = _form == LeafForm.Null ? 0 : valid;
        page.RowsUnread = rows;
        return valid;
    }

    private void RequireLevels(ReadOnlySpan<byte> levels, int max, string kind)
    {
        if (levels.IndexOfAnyInRange((byte)(max + 1), byte.MaxValue) >= 0)
        {
            ParquetThrow.Format($"A {kind} level of '{Name}' passes the column's maximum, {max}.");
        }
    }

    /// <summary>A run of a page's entries a batch takes: the entries from <see cref="From"/> to <see cref="To"/>, and the values among them.</summary>
    private readonly record struct Segment(Page Page, int From, int To, int Values);
}
