using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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
    private long[] _ownValues = [];
    private byte[] _ownValid = [];

    // The rows taken; the open granule's first row and nulls; the granules closed, and those that list their values.
    private long _row;
    private long _granuleStart;
    private long _granuleNulls;
    private int _granules;
    private int _listing;
    private long _listedRows;

    // Whether each part still holds: every row numbered, every batch of the measure read.
    private bool _values = true;
    private bool _measured;

    // The values met in the open granule: each number's rows and the granule it was last met in, plus one; the numbers met.
    private int[] _rowsOf = [];
    private int[] _seenIn = [];
    private int[] _met = new int[64];
    private int _metCount;

    // The open granule's measure, a record in StateWords' layout; the rows' entries, all of it.
    private readonly long[] _state;
    private int[] _zeros = new int[1_024];

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
        _histogram = _measure >= 0 ? new QuantileSketch(GranuleHistograms.WrittenBits) : null;
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
        if (count <= 0 || (!_values && !_measured))
        {
            _row += count;
            return;
        }

        ReadOnlySpan<int> ids = _sheet.RowIds;
        _values &= !ids.IsEmpty;
        _measured = _measured && (_own is not null || _plan.Measures[_measure].Live);
        _granuleNulls += Nulls(arena, nodeIndex, start, count);
        if (_values)
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
    private static long Nulls(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return count;
        }

        if (own.AllValid && wrapper.AllValid)
        {
            return 0;
        }

        long nulls = 0;
        for (int row = start; row < start + count; row++)
        {
            nulls += own.IsValid(row) && wrapper.IsValid(row) ? 0 : 1;
        }

        return nulls;
    }

    /// <summary>Counts the rows' values in the open granule, each by its number, a null's -1 aside.</summary>
    private void Count(ReadOnlySpan<int> ids)
    {
        int mark = _granules + 1;
        int[] rowsOf = _rowsOf;
        int[] seenIn = _seenIn;
        foreach (int id in ids)
        {
            if (id < 0)
            {
                continue;
            }

            if ((uint)id >= (uint)rowsOf.Length)
            {
                Grow(id);
                (rowsOf, seenIn) = (_rowsOf, _seenIn);
            }

            if (seenIn[id] != mark)
            {
                seenIn[id] = mark;
                if (_metCount == _met.Length)
                {
                    Array.Resize(ref _met, _met.Length * 2);
                }

                _met[_metCount++] = id;
            }

            rowsOf[id]++;
        }
    }

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = Math.Max(id + 1, Math.Max(64, _rowsOf.Length * 2));
        Array.Resize(ref _rowsOf, size);
        Array.Resize(ref _seenIn, size);
    }

    /// <summary>Folds the measure's values of the batch's rows <c>[start, start + count)</c> into the open granule's record.</summary>
    private void Fold(int start, int count)
    {
        // A batch whose values are all null leaves the plan's arrays as they were, perhaps shorter, and holds no value.
        CountedMeasure measure = _plan.Measures[_measure];
        if ((measure.Kind == MeasureKind.Float ? measure.Floats.Length : measure.Integers.Length) < start + count)
        {
            return;
        }

        if (_zeros.Length < count)
        {
            _zeros = new int[Math.Max(count, _zeros.Length * 2)];
        }

        ReadOnlySpan<int> entries = _zeros.AsSpan(0, count);
        ReadOnlySpan<byte> valid = measure.Valid is { } flags ? flags.AsSpan(start, count) : default;
        ref long state = ref MemoryMarshal.GetArrayDataReference(_state);
        switch (measure.Kind)
        {
            case MeasureKind.Float:
                ColumnCountsBuilder.FoldFloats(ref state, entries, measure.Splits.AsSpan(start, count), measure.Floats.AsSpan(start, count), valid);
                break;
            case MeasureKind.Signed:
                ColumnCountsBuilder.FoldSigned(ref state, entries, measure.Integers.AsSpan(start, count), valid);
                break;
            default:
                ColumnCountsBuilder.FoldUnsigned(ref state, entries, measure.Integers.AsSpan(start, count), valid);
                break;
        }

        if (_histogram is { } histogram)
        {
            Sketch(histogram, measure, start, count, valid);
        }
    }

    /// <summary>
    /// Adds the measure's values of the rows <c>[start, start + count)</c>, those <paramref name="valid"/> keeps, to the
    /// granule's histogram, each run of valid rows in one call: every row at once when none is null.
    /// </summary>
    private static void Sketch(QuantileSketch histogram, CountedMeasure measure, int start, int count, ReadOnlySpan<byte> valid)
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

            switch (measure.Kind)
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
            _ownValues = new long[Math.Max(count, _ownValues.Length * 2)];
            _ownValid = new byte[_ownValues.Length];
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

        if (_zeros.Length < count)
        {
            _zeros = new int[Math.Max(count, _zeros.Length * 2)];
        }

        ref long state = ref MemoryMarshal.GetArrayDataReference(_state);
        if (signed)
        {
            ColumnCountsBuilder.FoldSigned(ref state, _zeros.AsSpan(0, count), into, flags);
        }
        else
        {
            ColumnCountsBuilder.FoldUnsigned(ref state, _zeros.AsSpan(0, count), into, flags);
        }
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

    /// <summary>Ends the open granule: its rows and nulls, its values, its measure.</summary>
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
            Span<int> met = _met.AsSpan(0, _metCount);
            met.Sort();
            int listed = met.Length <= GranuleStats.ListedValues ? met.Length : 0;
            if (met.Length <= GranuleStats.ListedValues)
            {
                _listing++;
                _listedRows += rows;
            }

            Span<long> counted = listed <= 64 ? stackalloc long[listed] : new long[listed];
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
    /// values (<see cref="ObjectContents.ListedRows"/>), 0 with no values' part: null for a column that keeps neither part.
    /// The values' part needs every value's bytes, which its dictionary keeps, and a granule that lists them: past
    /// <see cref="GranuleStats.ListedValues"/> in every granule, it answers nothing and is not written. The measure's is the
    /// plan's verdict, asked once every column's counts settled it.
    /// </summary>
    internal (byte[] Statistics, byte[]? Histograms, long ListedRows)? Finish(long rows)
    {
        End();
        _measured = _measured && (_own is not null || _plan.Measures[_measure].Live);
        byte[]?[]? values = _values && _listing > 0 && _sheet.Counting ? _sheet.ValuesByNumber() : null;
        if (values is not null && Array.Exists(values, value => value is null))
        {
            values = null;
        }

        if ((values is null && !_measured) || _row != rows || _granules == 0)
        {
            return null;
        }

        ProtoWriter writer = new ProtoWriter(_rowsSection.WrittenSpan.Length + _valuesSection.WrittenSpan.Length + _measureSection.WrittenSpan.Length + 16);
        try
        {
            GranuleStats.WriteHeader(ref writer, _granules, values is not null, _measured ? Kind : null);
            writer.WriteRawBytes(_rowsSection.WrittenSpan);
            if (values is not null)
            {
                writer.WriteRawBytes(_valuesSection.WrittenSpan);
            }

            if (_measured)
            {
                writer.WriteRawBytes(_measureSection.WrittenSpan);
            }

            if (values is not null)
            {
                GranuleStats.WriteDictionary(ref writer, values);
            }

            return (writer.WrittenSpan.ToArray(), _measured && _histogram is not null ? Histograms() : null, values is null ? 0 : _listedRows);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The measure's histograms over the granules, as <see cref="GranuleHistograms"/> encodes them.</summary>
    private byte[] Histograms()
    {
        ProtoWriter writer = new ProtoWriter(_histogramSection.WrittenSpan.Length + 16);
        try
        {
            GranuleHistograms.WriteHeader(ref writer, _granules);
            writer.WriteRawBytes(_histogramSection.WrittenSpan);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public override void Dispose()
    {
        _rowsSection.Dispose();
        _valuesSection.Dispose();
        _measureSection.Dispose();
        _histogramSection.Dispose();
    }
}
