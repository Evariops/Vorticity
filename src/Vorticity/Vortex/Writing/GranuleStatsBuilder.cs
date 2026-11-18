using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Builds a column's statistics over the file's granules (<see cref="GranuleStats"/>, spec D, 6) as the column is written, a
/// granule ending where the writer's block does: its values are those the column's sheet numbers, fed right before it, each
/// counted in an array by its number; a measure's
/// values are those the counts' plan read once for the batch, folded into one record a granule in
/// <see cref="StateWords"/>' layout.
/// </summary>
/// <remarks>
/// <para>
/// A row costs a look at its value's mark and a count, and a measure's fold; a granule, a sort of the numbers it met.
/// </para>
/// <para>
/// The values' part holds while the sheet numbers every row and keeps every value's bytes, and a granule lists them; the
/// measure's while the plan carries the column as a measure, read from every batch. An integer under an extension, an
/// instant or a date, which the plan does not carry, is a measure of its own granules: its extremes say which group of a
/// function of it, a truncation, a granule falls in whole. A column that keeps neither part keeps no statistics.
/// </para>
/// </remarks>
internal sealed class GranuleStatsBuilder : IndexBuilder
{
    private readonly ColumnSheetBuilder _sheet;
    private readonly CountsPlan _plan;
    private readonly int _measure;

    // An integer under an extension, which the plan does not carry, read here: how it is summed and its width; null for none.
    private readonly (MeasureKind Kind, int Width)? _own;
    private Block<long> _ownValues;
    private Block<byte> _ownValid;

    // The rows taken; the open granule's first row and nulls; the granules closed, those that list their values, and the
    // rows of those that hold one value, or nulls alone.
    private long _row;
    private long _granuleStart;
    private long _granuleNulls;
    private int _granules;
    private int _listing;
    private long _listedRows;
    private long _singleRows;

    // Whether each part still holds: every row numbered, every batch of the measure read.
    private bool _values = true;
    private bool _measured;

    // The values met in the open granule: each number's rows and the granule it was last met in, plus one; the numbers met.
    private Block<int> _rowsOf;
    private Block<int> _seenIn;
    private Block<int> _met;
    private int _metCount;

    // The open granule's measure, a record in StateWords' layout; the rows' entries, all of it.
    private readonly long[] _state;

    // The open granule's histogram of the measure the plan reads, a sketch of GranuleHistograms.WrittenBits, its encoding,
    // and the histograms, written as granules close; null for a column the plan reads as no measure.
    private readonly QuantileSketch? _histogram;
    private readonly ArrayBufferWriter<byte> _encoded = new ArrayBufferWriter<byte>(256);
    private ProtoWriter _histogramSection = new ProtoWriter(256);

    // The sections, written as granules close.
    private ProtoWriter _rowsSection = new ProtoWriter(256);
    private ProtoWriter _valuesSection = new ProtoWriter(256);
    private ProtoWriter _measureSection = new ProtoWriter(256);

    /// <param name="sheet">The column's sheet, fed first, which numbers its rows' values.</param>
    /// <param name="plan">What the file's counts share, which reads the measures.</param>
    /// <param name="field">The column.</param>
    /// <param name="dtype">The column's type.</param>
    internal GranuleStatsBuilder(ColumnSheetBuilder sheet, CountsPlan plan, int field, DType dtype)
    {
        _sheet = sheet;
        _plan = plan;
        _measure = plan.MeasureOf(field);
        _histogram = _measure >= 0 ? new QuantileSketch(GranuleHistograms.WrittenBits, TableArrays.Uncounted) : null;
        _own = _measure < 0 ? OwnOf(dtype) : null;
        _measured = _measure >= 0 || _own is not null;
        _state = new long[_measured ? MeasureLayout.Words(Kind == MeasureKind.Float) : 0];
        if (_measured)
        {
            ColumnCountsBuilder.OpenState(_state, Kind);
        }

        sheet.NumberRows();
    }

    private MeasureKind Kind => _own?.Kind ?? _plan.Measures[_measure].Kind;

    /// <summary>
    /// Whether the statistics are written for their rows and nulls alone where they keep neither values nor a measure: a
    /// shredded key's, whose nulls, the rows that hold no value of it, no summary of the object says, and which prove a filter
    /// that leaves a value of the key out true over a granule the key's index does not place the value in. A user's key among
    /// 200 000 listed no granule's values and kept nothing before.
    /// </summary>
    internal bool KeepsNulls { get; init; }

    /// <summary>How an integer under an extension is summed, and its width; null for any other type.</summary>
    private static (MeasureKind Kind, int Width)? OwnOf(DType dtype)
    {
        if (dtype.Kind != DTypeKind.Extension)
        {
            return null;
        }

        DType storage = dtype;
        while (storage.Kind == DTypeKind.Extension)
        {
            storage = storage.StorageType;
        }

        return storage.Kind != DTypeKind.Primitive ? null : storage.PType switch
        {
            PType.I8 or PType.I16 or PType.I32 or PType.I64 => (MeasureKind.Signed, storage.PType.ByteWidth()),
            PType.U8 or PType.U16 or PType.U32 or PType.U64 => (MeasureKind.Unsigned, storage.PType.ByteWidth()),
            _ => null,
        };
    }

    /// <inheritdoc/>
    protected override long OpenBytes =>
        _rowsSection.WrittenSpan.Length + _valuesSection.WrittenSpan.Length + _measureSection.WrittenSpan.Length
        + _histogramSection.WrittenSpan.Length + ((long)_rowsOf.Length * 2 * sizeof(int));

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (count <= 0 || (!_values && !_measured && !KeepsNulls))
        {
            _row += count;
            return;
        }

        ReadOnlySpan<int> ids = _sheet.RowIds;
        _values &= !ids.IsEmpty;
        _measured = _measured && (_own is not null || _plan.Measures[_measure].Live);
        long nulls = Nulls(arena, nodeIndex, start, count);
        _granuleNulls += nulls;
        if (_values && nulls * 2 > count && ValidityMask.OneBitmap(arena, nodeIndex, out ReadOnlySpan<byte> bits, out int bitOffset))
        {
            // Most rows hold no value, a shredded key's: the rows that do, found a word of the bitmap at a time.
            CountValid(ids[..count], bits, bitOffset + start);
        }
        else if (_values)
        {
            Count(ids[..count]);
        }

        if (_measured && _own is { } own)
        {
            FoldOwn(arena, nodeIndex, start, count, own);
        }
        else if (_measured)
        {
            Fold(start, count);
        }

        _row += count;
    }

    /// <summary>Ends the open granule, where the writer's block ends.</summary>
    internal override void CloseBlock() => End();

    /// <summary>
    /// The nulls among rows <c>[start, start + count)</c> of the batch's column node <paramref name="nodeIndex"/>: by its
    /// validity and that of the extension around it, as the sheet reads them.
    /// </summary>
    /// <remarks>
    /// A bitmap is counted eight bytes at a time (<see cref="BitmapKernels.CountSet"/>) when the other validity holds every
    /// row, as it does for any column but an extension, whose two are the same. Tested a row at a time, the nulls of the
    /// hundreds of shredded keys of a log's attributes, absent from most rows, made this builder the first of a tier merge's
    /// profile at degree 14, 11.2 % of its samples (2026-11-17).
    /// </remarks>
    private static long Nulls(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        bool extension = false;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
            extension = true;
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = extension ? ValidityMask.From(arena, outer.Validity) : own;
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return count;
        }

        if (own.AllValid && wrapper.AllValid)
        {
            return 0;
        }

        if (!extension || wrapper.AllValid)
        {
            return count - BitmapKernels.CountSet(own.Bits, own.BitOffset + start, count);
        }

        if (own.AllValid)
        {
            return count - BitmapKernels.CountSet(wrapper.Bits, wrapper.BitOffset + start, count);
        }

        long nulls = 0;
        for (int row = start; row < start + count; row++)
        {
            nulls += own.IsValid(row) && wrapper.IsValid(row) ? 0 : 1;
        }

        return nulls;
    }

    /// <summary>Counts the rows' values in the open granule, each by its number, a null's -1 aside.</summary>
    /// <remarks>
    /// A run of rows of one value is counted at once, its end found a vector at a time: a block's rows come in the order of
    /// their keys, and a series' rows hold its attributes row after row. Counted a row at a time, the keys of a consolidation
    /// of metric records' attributes took 2.7 % of its processor (2026-11-18).
    /// </remarks>
    private void Count(ReadOnlySpan<int> ids)
    {
        int mark = _granules + 1;
        Span<int> rowsOf = _rowsOf;
        Span<int> seenIn = _seenIn;
        int at = 0;
        while (at < ids.Length)
        {
            // A value the next row does not repeat is counted without looking for its run's end.
            int id = ids[at];
            int run = 1;
            if (at + 1 < ids.Length && ids[at + 1] == id)
            {
                int past = ids[(at + 2)..].IndexOfAnyExcept(id);
                run = past < 0 ? ids.Length - at : past + 2;
            }

            at += run;
            if (id < 0)
            {
                continue;
            }

            if ((uint)id >= (uint)rowsOf.Length)
            {
                Grow(id);
                rowsOf = _rowsOf;
                seenIn = _seenIn;
            }

            if (seenIn[id] != mark)
            {
                seenIn[id] = mark;
                if (_metCount == _met.Length)
                {
                    Blocks.Grow(ref _met, Math.Max(64, _met.Length * 2));
                }

                _met[_metCount++] = id;
            }

            rowsOf[id] += run;
        }
    }

    /// <summary>
    /// <see cref="Count"/> over the rows <paramref name="bits"/> says hold a value, from bit <paramref name="bitOffset"/>,
    /// sixty-four rows a word: the rows without one, most of a shredded key's, skipped a word at a time.
    /// </summary>
    private void CountValid(ReadOnlySpan<int> ids, ReadOnlySpan<byte> bits, int bitOffset)
    {
        for (int at = 0; at < ids.Length; at += 64)
        {
            ulong word = BitmapKernels.Word(bits, bitOffset + at, Math.Min(64, ids.Length - at));
            while (word != 0)
            {
                Count(ids.Slice(at + BitOperations.TrailingZeroCount(word), 1));
                word &= word - 1;
            }
        }
    }

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(id + 1, Math.Max(64, _rowsOf.Length * 2)));
        int old = _rowsOf.Length;

        // In blocks of the pool, at a power of two, the old ones given back: the builders of every column and shredded key
        // of every object written made them anew, 22 MB of a consolidation of four hours of log records.
        Blocks.Resize(ref _rowsOf, size);
        Blocks.Resize(ref _seenIn, size);
        _rowsOf.AsSpan(old).Clear();
        _seenIn.AsSpan(old).Clear();
    }

    // Zeros, the entries of a granule's rows, which every builder reads and none writes.
    private static int[] s_zeros = new int[8_192];

    /// <summary><paramref name="count"/> zeros.</summary>
    private static ReadOnlySpan<int> Zeros(int count)
    {
        int[] zeros = Volatile.Read(ref s_zeros);
        if (zeros.Length < count)
        {
            zeros = new int[BitOperations.RoundUpToPowerOf2((uint)count)];
            Volatile.Write(ref s_zeros, zeros);
        }

        return zeros.AsSpan(0, count);
    }

    /// <summary>Gives <paramref name="block"/> back to the pool, once.</summary>
    private static void Give<T>(ref Block<T> block)
        where T : unmanaged
    {
        Blocks.Return(block);
        block = default;
    }

    /// <summary>Folds the measure's values of the batch's rows <c>[start, start + count)</c> into the open granule's record.</summary>
    private void Fold(int start, int count)
    {
        // A batch whose values are all null leaves the batch's arrays as they were, perhaps shorter, and holds no value.
        MeasureKind kind = _plan.Measures[_measure].Kind;
        MeasureRows measure = Counted?.Measure(_measure) ?? throw new InvalidOperationException("The statistics were fed a batch without its counted rows.");
        if ((kind == MeasureKind.Float ? measure.Floats.Length : measure.Integers.Length) < start + count)
        {
            return;
        }

        ReadOnlySpan<byte> valid = measure.ValidOf(start, count);
        ref long state = ref MemoryMarshal.GetArrayDataReference(_state);
        if (kind == MeasureKind.Float)
        {
            ColumnCountsBuilder.FoldFloats(ref state, Zeros(count), measure.Splits.AsSpan(start, count), measure.Floats.AsSpan(start, count), valid);
        }
        else
        {
            ColumnCountsBuilder.Reduce(ref state, measure.Integers.AsSpan(start, count), valid, kind == MeasureKind.Signed);
        }

        if (_histogram is { } histogram)
        {
            Sketch(histogram, kind, measure, start, count, valid);
        }
    }

    /// <summary>
    /// Adds the measure's values of the rows <c>[start, start + count)</c>, those <paramref name="valid"/> keeps, to the
    /// granule's histogram, each run of valid rows in one call: every row at once when none is null.
    /// </summary>
    private static void Sketch(QuantileSketch histogram, MeasureKind kind, MeasureRows measure, int start, int count, ReadOnlySpan<byte> valid)
    {
        int from = 0;
        while (from < count)
        {
            int to = count;
            if (!valid.IsEmpty)
            {
                int skipped = valid[from..].IndexOfAnyExcept((byte)0);
                if (skipped < 0)
                {
                    return;
                }

                from += skipped;
                int run = valid[from..].IndexOf((byte)0);
                to = run < 0 ? count : from + run;
            }

            switch (kind)
            {
                case MeasureKind.Float:
                    histogram.AddAll<double>(measure.Floats.AsSpan(start + from, to - from));
                    break;
                case MeasureKind.Signed:
                    histogram.AddAll<long>(measure.Integers.AsSpan(start + from, to - from));
                    break;
                default:
                    histogram.AddAll<ulong>(MemoryMarshal.Cast<long, ulong>(measure.Integers.AsSpan(start + from, to - from)));
                    break;
            }

            from = to;
        }
    }

    /// <summary>
    /// Folds the values of an integer under an extension of the batch's rows <c>[start, start + count)</c> into the open
    /// granule's record, read from its storage; a form other than a flat or a constant one, or nulls the extension holds
    /// over it, give the measure up.
    /// </summary>
    private void FoldOwn(CanonicalArena arena, int nodeIndex, int start, int count, (MeasureKind Kind, int Width) own)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        ValidityMask valid = ValidityMask.From(arena, node.Validity);
        if (node.Kind is not (CanonicalKind.Primitive or CanonicalKind.Constant) || (outer.Kind == CanonicalKind.Extension && !wrapper.AllValid))
        {
            _measured = false;
            return;
        }

        if (_ownValues.Length < count)
        {
            int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, _ownValues.Length * 2));
            Blocks.Ensure(ref _ownValues, size);
            Blocks.Ensure(ref _ownValid, size);
        }

        bool constant = node.Kind == CanonicalKind.Constant;
        ReadOnlySpan<byte> values = constant ? node.ConstantElement : node.Values.Span;
        bool signed = own.Kind == MeasureKind.Signed;
        if (!constant && signed && own.Width == sizeof(long) && valid.AllValid)
        {
            // An instant's usual form, every row valid: its sum and extremes in one pass over its values, with no copy.
            FoldLongs(MemoryMarshal.Cast<byte, long>(values).Slice(start, count));
            return;
        }

        Span<long> into = _ownValues.AsSpan(0, count);
        Span<byte> flags = _ownValid.AsSpan(0, count);
        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            into[i] = CountsPlan.Integer(values, own.Width, constant ? 0 : row, signed);
            flags[i] = valid.AllInvalid ? (byte)0 : valid.AllValid || valid.IsValid(row) ? (byte)1 : (byte)0;
        }

        ColumnCountsBuilder.Reduce(ref MemoryMarshal.GetArrayDataReference(_state), into, flags, signed);
    }

    /// <summary>Folds <paramref name="values"/>, every one valid, into the open granule's record of a signed measure: their count, exact sum and extremes.</summary>
    private void FoldLongs(ReadOnlySpan<long> values)
    {
        Int128 sum = 0;
        long least = _state[MeasureLayout.IntegerLeast];
        long greatest = _state[MeasureLayout.IntegerGreatest];
        foreach (long value in values)
        {
            sum += value;
            least = Math.Min(least, value);
            greatest = Math.Max(greatest, value);
        }

        _state[MeasureLayout.Values] += values.Length;
        StateWords.Write(StateWords.IntegerOf(_state, MeasureLayout.Sum) + sum, _state, MeasureLayout.Sum);
        _state[MeasureLayout.IntegerLeast] = least;
        _state[MeasureLayout.IntegerGreatest] = greatest;
    }

    /// <summary>
    /// Ends the open granule: its rows and nulls, its values, its measure. The listing on the stack is written before it is
    /// read, so it is not zeroed first: zeroing its 2 KiB at every granule of every column and shredded key took 4.9 s of a
    /// backlog's compaction at degree 14.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void End()
    {
        long rows = _row - _granuleStart;
        if (rows <= 0)
        {
            return;
        }

        GranuleStats.WriteRows(ref _rowsSection, rows, _granuleNulls);
        if (_values)
        {
            // The values are put in order for the listing alone: a granule of more than it lists writes their count, and
            // sorted them all the same, a granule of a measure of many values thousands of them.
            Span<int> met = _met.AsSpan(0, _metCount);
            int listed = met.Length <= GranuleStats.ListedValues ? met.Length : 0;
            met[..listed].Sort();
            if (met.Length <= GranuleStats.ListedValues)
            {
                _listing++;
                _listedRows += rows;
            }

            if (met.Length + (_granuleNulls > 0 ? 1 : 0) <= 1)
            {
                _singleRows += rows;
            }

            // A granule lists 256 values at most (GranuleStats.ListedValues): on the stack, where past 64 each granule made its array.
            Span<long> listing = stackalloc long[GranuleStats.ListedValues];
            Span<long> counted = listing[..listed];
            for (int i = 0; i < met.Length; i++)
            {
                int id = met[i];
                if (i < listed)
                {
                    counted[i] = _rowsOf[id];
                }

                _rowsOf[id] = 0;
            }

            GranuleStats.WriteValues(ref _valuesSection, met.Length, met[..listed], counted);
        }

        if (_measured)
        {
            ColumnCountsBuilder.CloseState(_state, Kind);
            GranuleStats.WriteMeasure(ref _measureSection, Kind, _state);
            _state.AsSpan().Clear();
            ColumnCountsBuilder.OpenState(_state, Kind);
            if (_histogram is { } histogram)
            {
                _encoded.Clear();
                histogram.Encode(_encoded);
                _histogramSection.WriteVarint((ulong)_encoded.WrittenCount);
                _histogramSection.WriteRawBytes(_encoded.WrittenSpan);
                histogram.Clear();
            }
        }

        _metCount = 0;
        _granuleNulls = 0;
        _granuleStart = _row;
        _granules++;
    }

    /// <summary>
    /// The statistics, once every one of the file's <paramref name="rows"/> is in, the measure's histograms over the
    /// granules (<see cref="GranuleHistograms"/>) for a measure the plan reads, and the rows of the granules that list their
    /// values (<see cref="ObjectContents.ListedRows"/>) and of those that hold one value (<see cref="ObjectContents.SingleRows"/>), 0
    /// with no values' part: null for a column that keeps neither part, unless it keeps its nulls (<see cref="KeepsNulls"/>).
    /// The values' part needs every value's bytes, which its dictionary keeps, and a granule that lists them: past
    /// <see cref="GranuleStats.ListedValues"/> in every granule, it answers nothing and is not written. The measure's is the
    /// plan's verdict, asked once every column's counts settled it.
    /// </summary>
    internal (byte[] Statistics, byte[]? Histograms, long ListedRows, long SingleRows)? Finish(long rows)
    {
        End();
        _measured = _measured && (_own is not null || _plan.Measures[_measure].Live);
        bool listed = _values && _listing > 0 && _sheet.Counting && _sheet.KeptEveryValue;
        if ((!listed && !_measured && !KeepsNulls) || _row != rows || _granules == 0)
        {
            return null;
        }

        // Laid out once at its length, the sections copied in: through a writer rented at their sum, then copied out, the
        // statistics of every column and key of every object came twice, 21 MB of a consolidation of log records.
        Span<byte> header = stackalloc byte[32];
        header = header[..GranuleStats.WriteHeader(header, _granules, listed, _measured ? Kind : null)];
        ColumnSheetBuilder.Values values = new ColumnSheetBuilder.Values(_sheet, default, _sheet.Numbered);
        ReadOnlySpan<byte> rowsPart = _rowsSection.WrittenSpan;
        ReadOnlySpan<byte> valuesPart = listed ? _valuesSection.WrittenSpan : default;
        ReadOnlySpan<byte> measurePart = _measured ? _measureSection.WrittenSpan : default;
        long dictionary = listed ? GranuleStats.DictionaryBytes(values) : 0;
        byte[] statistics = new byte[checked((int)(header.Length + rowsPart.Length + valuesPart.Length + measurePart.Length + dictionary))];
        Span<byte> into = statistics;
        into = Copied(header, into);
        into = Copied(rowsPart, into);
        into = Copied(valuesPart, into);
        into = Copied(measurePart, into);

        if (listed)
        {
            GranuleStats.WriteDictionary(into, values);
        }

        return (statistics, _measured && _histogram is not null ? Histograms() : null, listed ? _listedRows : 0, listed ? _singleRows : 0);
    }

    /// <summary>The measure's histograms over the granules, as <see cref="GranuleHistograms"/> encodes them.</summary>
    /// <summary>Copies <paramref name="part"/> to the start of <paramref name="into"/>: what of <paramref name="into"/> follows it.</summary>
    private static Span<byte> Copied(scoped ReadOnlySpan<byte> part, Span<byte> into)
    {
        part.CopyTo(into);
        return into[part.Length..];
    }

    private byte[] Histograms()
    {
        Span<byte> header = stackalloc byte[32];
        header = header[..GranuleHistograms.WriteHeader(header, _granules)];
        ReadOnlySpan<byte> section = _histogramSection.WrittenSpan;
        byte[] histograms = new byte[header.Length + section.Length];
        header.CopyTo(histograms);
        section.CopyTo(histograms.AsSpan(header.Length));
        return histograms;
    }

    public override void Dispose()
    {
        Give(ref _rowsOf);
        Give(ref _seenIn);
        Give(ref _met);
        Give(ref _ownValues);
        Give(ref _ownValid);
        _histogram?.Release();
        _rowsSection.Dispose();
        _valuesSection.Dispose();
        _measureSection.Dispose();
        _histogramSection.Dispose();
    }
}
