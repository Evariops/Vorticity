using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
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
    private int[] _rowsOf = [];
    private int[] _seenIn = [];
    private int[] _met = [];
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
        _histogram = _measure >= 0 ? new QuantileSketch(GranuleHistograms.WrittenBits, ArrayShelf.Retained) : null;
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
                    _met = Regrown(_met, Math.Max(64, _met.Length * 2));
                }

                _met[_metCount++] = id;
            }

            rowsOf[id]++;
        }
    }

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(id + 1, Math.Max(64, _rowsOf.Length * 2)));
        int old = _rowsOf.Length;
        _rowsOf = Regrown(_rowsOf, size);
        _seenIn = Regrown(_seenIn, size);
        _rowsOf.AsSpan(old).Clear();
        _seenIn.AsSpan(old).Clear();
    }

    /// <summary>
    /// <paramref name="held"/>'s elements in an array of the process's shelf's of <paramref name="length"/>, a power of two,
    /// <paramref name="held"/> given back: the arrays of the builders of every column and shredded key of every object
    /// written, which made them anew, 22 MB of a consolidation of four hours of log records.
    /// </summary>
    private static T[] Regrown<T>(T[] held, int length)
    {
        T[] grown = ArrayShelf.Retained.Take<T>(length, zeroed: false);
        held.CopyTo(grown, 0);
        if (held.Length > 0)
        {
            ArrayShelf.Retained.Give(held);
        }

        return grown;
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

    /// <summary>Gives <paramref name="array"/> back to the process's shelf, once.</summary>
    private static void Give<T>(ref T[] array)
    {
        if (array.Length > 0)
        {
            ArrayShelf.Retained.Give(array);
            array = [];
        }
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

        ReadOnlySpan<byte> valid = measure.Valid is { } flags ? flags.AsSpan(start, count) : default;
        ref long state = ref MemoryMarshal.GetArrayDataReference(_state);
        if (measure.Kind == MeasureKind.Float)
        {
            ColumnCountsBuilder.FoldFloats(ref state, Zeros(count), measure.Splits.AsSpan(start, count), measure.Floats.AsSpan(start, count), valid);
        }
        else
        {
            ColumnCountsBuilder.Reduce(ref state, measure.Integers.AsSpan(start, count), valid, measure.Kind == MeasureKind.Signed);
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
            int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, _ownValues.Length * 2));
            Give(ref _ownValues);
            Give(ref _ownValid);
            _ownValues = ArrayShelf.Retained.Take<long>(size, zeroed: false);
            _ownValid = ArrayShelf.Retained.Take<byte>(size, zeroed: false);
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
        ColumnSheetBuilder.Values values = new ColumnSheetBuilder.Values(_sheet, null, _sheet.Numbered);
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
