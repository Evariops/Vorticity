using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>A number column the counts of every other column carry the states of, and its values in the batch being fed.</summary>
internal sealed class CountedMeasure(int field, MeasureKind kind, int width)
{
    /// <summary>The top-level column.</summary>
    internal int Field { get; } = field;

    /// <summary>How its values are summed.</summary>
    internal MeasureKind Kind { get; } = kind;

    /// <summary>The bytes of one of its values.</summary>
    internal int Width { get; } = width;

    /// <summary>
    /// Whether the counts still carry it: a number of few values counts as a column of its own instead, and one whose
    /// batch came in a form the plan does not read is carried by none. Once false, never true again; a measure the
    /// counts stopped carrying is written by none, whatever each folded of it before.
    /// </summary>
    internal volatile bool Live = true;

    /// <summary>The batch's integers, unsigned ones as their bits, by row.</summary>
    internal long[] Integers = [];

    /// <summary>The batch's floats, by row.</summary>
    internal double[] Floats = [];

    /// <summary>The batch's floats split once into their bins' parts, which every column's entry of a row adds (<see cref="IndexedSum.Split"/>).</summary>
    internal IndexedSum.Split[] Splits = [];

    /// <summary>Whether each row of the batch holds a value, 1 or 0; null when every one does.</summary>
    internal byte[]? Valid;
}

/// <summary>
/// A text column of many values that repeat, whose distinct values the counts of every column of few values keep, an entry
/// each: its sheet hashes each row of a batch for them.
/// </summary>
internal sealed class WideColumn(int field, ColumnSheetBuilder sheet)
{
    /// <summary>The top-level column.</summary>
    internal int Field { get; } = field;

    /// <summary>Its sheet, which hashes its rows.</summary>
    internal ColumnSheetBuilder Sheet { get; } = sheet;

    /// <summary>Whether the counts still keep its distinct values; once false, never true again.</summary>
    internal bool Live { get; set; } = true;
}

/// <summary>
/// What the counts by hour of a file's columns share (<see cref="ColumnCountsBuilder"/>): the instant whose hour each
/// row is counted in, the hours met, numbered as they were met, and the number columns every column's counts carry
/// as measures. Each batch's instants and measures are read once, before any column is counted (<see cref="Prepare"/>),
/// so that the columns, counted side by side, only read them.
/// </summary>
internal sealed class CountsPlan
{
    /// <summary>The hour of a row whose instant is null.</summary>
    internal const long NullHour = long.MinValue;

    private readonly Dictionary<long, int> _slots = [];
    private long _hourStart = 1;
    private long _hourEnd;
    private int _hourSlot;

    private CountsPlan(int instantField, long unitsPerHour, CountedMeasure[] measures)
    {
        InstantField = instantField;
        UnitsPerHour = unitsPerHour;
        Measures = measures;
    }

    /// <summary>The top-level instant column.</summary>
    internal int InstantField { get; }

    /// <summary>The instant's units in an hour.</summary>
    internal long UnitsPerHour { get; }

    /// <summary>The number columns the counts carry the states of: every top-level integer and float but the instant.</summary>
    internal CountedMeasure[] Measures { get; }

    /// <summary>
    /// The text columns whose distinct values the counts of the columns of at most <see cref="ColumnCountsBuilder.AttachValues"/>
    /// values keep, an entry each: every text column with a sheet at first, then those of more than
    /// <see cref="ColumnCountsBuilder.WideValues"/> values whose values repeat (<see cref="SettleWide"/>).
    /// </summary>
    internal List<WideColumn> Wide { get; } = [];

    /// <summary>
    /// Settles which text columns stay wide: one whose values hardly repeat is an identifier, whose distinct values in an
    /// entry are the entry's rows, which the counts give, and one of at most <see cref="ColumnCountsBuilder.WideValues"/> values
    /// is counted as a column instead: once its sheet gave its counts up, or once its first rows are in or the file ends.
    /// </summary>
    internal void SettleWide(bool end)
    {
        foreach (WideColumn wide in Wide)
        {
            ColumnSheetBuilder sheet = wide.Sheet;
            bool settled = end || sheet.Rows - sheet.Nulls >= ColumnCountsBuilder.FirstCheck;
            long valid = sheet.Rows - sheet.Nulls;
            if (wide.Live && (!sheet.Counting || (settled && (sheet.Distinct <= ColumnCountsBuilder.WideValues || sheet.Distinct * 4L > valid))))
            {
                wide.Live = false;
                sheet.StopHashing();
            }
        }
    }

    /// <summary>The hours met, by their number: hours since the Unix epoch, <see cref="NullHour"/> for a null instant.</summary>
    internal List<long> Hours { get; } = [];

    /// <summary>The number of each row's hour in the batch being fed.</summary>
    internal int[] HourSlots { get; private set; } = [];

    /// <summary>
    /// The least hour of a row of the batch being fed, a null instant's aside; <see cref="long.MaxValue"/> for none. The
    /// writer feeds a sealed object's slices in the order of their time, so an hour before it is done.
    /// </summary>
    internal long LeastHour { get; private set; } = long.MaxValue;

    /// <summary>Whether the instant came in a form the plan does not read: no column is counted from then on.</summary>
    internal bool Dead { get; private set; }

    /// <summary>
    /// The plan of a file of <paramref name="schema"/> counted by the hours of its top-level column
    /// <paramref name="instant"/>, a timestamp; null when it is none.
    /// </summary>
    internal static CountsPlan? For(DType schema, string instant)
    {
        int instantField = schema.IndexOfField(instant);
        if (instantField < 0)
        {
            return null;
        }

        VortexType type = VortexTypes.FromDType(schema.GetField(instantField));
        if (type.ExtensionId != ExtensionIds.Timestamp)
        {
            return null;
        }

        long unitsPerHour = (type.Unit ?? TimeUnit.Microseconds) switch
        {
            TimeUnit.Seconds => 3_600L,
            TimeUnit.Milliseconds => 3_600_000L,
            TimeUnit.Microseconds => 3_600_000_000L,
            _ => 3_600_000_000_000L,
        };

        List<CountedMeasure> measures = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            DType dtype = schema.GetField(field);
            if (field == instantField || dtype.Kind != DTypeKind.Primitive)
            {
                continue;
            }

            MeasureKind? kind = dtype.PType switch
            {
                PType.I8 or PType.I16 or PType.I32 or PType.I64 => MeasureKind.Signed,
                PType.U8 or PType.U16 or PType.U32 or PType.U64 => MeasureKind.Unsigned,
                PType.F32 or PType.F64 => MeasureKind.Float,
                _ => null,
            };
            if (kind is { } measured)
            {
                measures.Add(new CountedMeasure(field, measured, dtype.PType.ByteWidth()));
            }
        }

        return new CountsPlan(instantField, unitsPerHour, [.. measures]);
    }

    /// <summary>The measure column <paramref name="field"/> is, or -1.</summary>
    internal int MeasureOf(int field) => Array.FindIndex(Measures, m => m.Field == field);

    /// <summary>Reads the batch's instants into hours, and its measures' values, before its columns are counted.</summary>
    internal void Prepare(CanonicalArena arena, ReadOnlySpan<int> fieldNodes, int rows)
    {
        if (Dead)
        {
            return;
        }

        if (HourSlots.Length < rows)
        {
            HourSlots = new int[Math.Max(rows, HourSlots.Length * 2)];
        }

        if (!Instants(arena, fieldNodes[InstantField], rows))
        {
            Dead = true;
            return;
        }

        long least = long.MaxValue;
        int last = -1;
        foreach (int slot in HourSlots.AsSpan(0, rows))
        {
            if (slot != last)
            {
                last = slot;
                long hour = Hours[slot];
                least = hour != NullHour && hour < least ? hour : least;
            }
        }

        LeastHour = least;

        foreach (CountedMeasure measure in Measures)
        {
            if (measure.Live && !Read(measure, arena, fieldNodes[measure.Field], rows))
            {
                measure.Live = false;
            }
        }
    }

    /// <summary>The hour of each row of the instant column's node <paramref name="nodeIndex"/>; false for a form it does not read.</summary>
    private bool Instants(CanonicalArena arena, int nodeIndex, int rows)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        bool allValid = own.AllValid && wrapper.AllValid;
        bool noneValid = own.AllInvalid || wrapper.AllInvalid;
        Span<int> slots = HourSlots.AsSpan(0, rows);
        switch (node.Kind)
        {
            case CanonicalKind.Primitive when node.PType == PType.I64:
            {
                ReadOnlySpan<long> instants = MemoryMarshal.Cast<byte, long>(node.Values.Span);
                for (int row = 0; row < rows; row++)
                {
                    slots[row] = noneValid || !(allValid || (own.IsValid(row) && wrapper.IsValid(row))) ? SlotOf(NullHour) : HourOf(instants[row]);
                }

                return true;
            }

            case CanonicalKind.Constant:
            {
                int slot = noneValid ? SlotOf(NullHour) : HourOf(MemoryMarshal.Read<long>(node.ConstantElement));
                for (int row = 0; row < rows; row++)
                {
                    slots[row] = allValid || (own.IsValid(row) && wrapper.IsValid(row)) ? slot : SlotOf(NullHour);
                }

                return true;
            }

            default:
                return noneValid && Fill(slots, SlotOf(NullHour));
        }
    }

    private static bool Fill(Span<int> slots, int slot)
    {
        slots.Fill(slot);
        return true;
    }

    /// <summary>The number of the hour <paramref name="instant"/> falls in; the hour of the row before, most of the time.</summary>
    private int HourOf(long instant)
    {
        if (instant >= _hourStart && instant < _hourEnd)
        {
            return _hourSlot;
        }

        long hour = instant >= 0 ? instant / UnitsPerHour : ((instant + 1) / UnitsPerHour) - 1;
        _hourSlot = SlotOf(hour);

        // The hour's instants, for the rows after; none at either end of the longs, where they would wrap.
        long start = unchecked(hour * UnitsPerHour);
        bool fits = start <= instant && start / UnitsPerHour == hour && start <= long.MaxValue - UnitsPerHour;
        (_hourStart, _hourEnd) = fits ? (start, start + UnitsPerHour) : (1, 0);
        return _hourSlot;
    }

    private int SlotOf(long hour)
    {
        if (!_slots.TryGetValue(hour, out int slot))
        {
            slot = Hours.Count;
            Hours.Add(hour);
            _slots.Add(hour, slot);
        }

        return slot;
    }

    /// <summary>The values of measure <paramref name="measure"/>'s node <paramref name="nodeIndex"/>; false for a form it does not read.</summary>
    private static bool Read(CountedMeasure measure, CanonicalArena arena, int nodeIndex, int rows)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        ValidityMask valid = ValidityMask.From(arena, node.Validity);
        if (valid.AllInvalid)
        {
            Valid(measure, rows).Clear();
            return true;
        }

        bool floats = measure.Kind == MeasureKind.Float;
        if (floats && measure.Floats.Length < rows)
        {
            measure.Floats = new double[Math.Max(rows, measure.Floats.Length * 2)];
        }
        else if (!floats && measure.Integers.Length < rows)
        {
            measure.Integers = new long[Math.Max(rows, measure.Integers.Length * 2)];
        }

        if (node.Kind == CanonicalKind.Constant)
        {
            ReadOnlySpan<byte> element = node.ConstantElement;
            if (floats)
            {
                measure.Floats.AsSpan(0, rows).Fill(Float(element, measure.Width, 0));
            }
            else
            {
                measure.Integers.AsSpan(0, rows).Fill(Integer(element, measure.Width, 0, measure.Kind == MeasureKind.Signed));
            }
        }
        else if (node.Kind == CanonicalKind.Primitive)
        {
            ReadOnlySpan<byte> values = node.Values.Span;
            if (floats)
            {
                Span<double> into = measure.Floats.AsSpan(0, rows);
                if (measure.Width == 8)
                {
                    MemoryMarshal.Cast<byte, double>(values)[..rows].CopyTo(into);
                }
                else
                {
                    ReadOnlySpan<float> narrow = MemoryMarshal.Cast<byte, float>(values);
                    for (int row = 0; row < rows; row++)
                    {
                        into[row] = narrow[row];
                    }
                }
            }
            else
            {
                Span<long> into = measure.Integers.AsSpan(0, rows);
                bool signed = measure.Kind == MeasureKind.Signed;
                switch (measure.Width)
                {
                    case 8:
                        MemoryMarshal.Cast<byte, long>(values)[..rows].CopyTo(into);
                        break;
                    case 4 when signed:
                        Widen(MemoryMarshal.Cast<byte, int>(values), into);
                        break;
                    case 4:
                        Widen(MemoryMarshal.Cast<byte, uint>(values), into);
                        break;
                    case 2 when signed:
                        Widen(MemoryMarshal.Cast<byte, short>(values), into);
                        break;
                    case 2:
                        Widen(MemoryMarshal.Cast<byte, ushort>(values), into);
                        break;
                    case 1 when signed:
                        Widen(MemoryMarshal.Cast<byte, sbyte>(values), into);
                        break;
                    default:
                        Widen(values, into);
                        break;
                }
            }
        }
        else
        {
            return false;
        }

        if (floats)
        {
            // Each float split once, for the entries of every column its row falls in.
            if (measure.Splits.Length < rows)
            {
                measure.Splits = new IndexedSum.Split[Math.Max(rows, measure.Splits.Length * 2)];
            }

            ReadOnlySpan<double> values = measure.Floats.AsSpan(0, rows);
            Span<IndexedSum.Split> splits = measure.Splits.AsSpan(0, rows);
            for (int row = 0; row < rows; row++)
            {
                splits[row] = IndexedSum.SplitOf(values[row]);
            }
        }

        if (valid.AllValid)
        {
            measure.Valid = null;
            return true;
        }

        Span<byte> flags = Valid(measure, rows);
        for (int row = 0; row < rows; row++)
        {
            flags[row] = valid.IsValid(row) ? (byte)1 : (byte)0;
        }

        return true;
    }

    private static Span<byte> Valid(CountedMeasure measure, int rows)
    {
        if (measure.Valid is not { } valid || valid.Length < rows)
        {
            measure.Valid = valid = new byte[Math.Max(rows, (measure.Valid?.Length ?? 0) * 2)];
        }

        return valid.AsSpan(0, rows);
    }

    private static void Widen<T>(ReadOnlySpan<T> values, Span<long> into)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        for (int row = 0; row < into.Length; row++)
        {
            into[row] = long.CreateTruncating(values[row]);
        }
    }

    private static long Integer(ReadOnlySpan<byte> value, int width, int at, bool signed) => (width, signed) switch
    {
        (8, _) => MemoryMarshal.Read<long>(value[(at * 8)..]),
        (4, true) => MemoryMarshal.Read<int>(value[(at * 4)..]),
        (4, false) => MemoryMarshal.Read<uint>(value[(at * 4)..]),
        (2, true) => MemoryMarshal.Read<short>(value[(at * 2)..]),
        (2, false) => MemoryMarshal.Read<ushort>(value[(at * 2)..]),
        (_, true) => (sbyte)value[at],
        _ => value[at],
    };

    private static double Float(ReadOnlySpan<byte> value, int width, int at) =>
        width == 8 ? MemoryMarshal.Read<double>(value[(at * 8)..]) : MemoryMarshal.Read<float>(value[(at * 4)..]);
}
/// <summary>
/// Builds one column's counts by hour (<see cref="ColumnCounts"/>) as the column is written: each row's value, numbered
/// by the column's sheet, and its hour, numbered by the plan, name an entry that counts its rows and folds every measure
/// the plan carries, its values, its exact sum, its least and its greatest. A column whose values hardly repeat, one
/// whose sheet gave its counts up, and a number of more than <see cref="FewValues"/> values, a measure, keep none.
/// </summary>
/// <remarks>
/// <para>
/// A number of at most <see cref="FewValues"/> values in its first <see cref="FirstCheck"/> rows, a status or a code,
/// is counted as a column and stops being a measure of the others; one of more is a measure and is not counted. A file
/// of fewer rows settles it at its end (<see cref="Settle"/>), before any column's counts are written.
/// </para>
/// <para>
/// An entry is one record of words in <see cref="StateWords"/>' layout over every measure of the plan, so that a row
/// folds into the few lines of the cache its entry takes, a float sum in place; the counts write the words of the
/// measures still carried. A row's entry is looked up in its hour's table of the values' numbers, the hour's table read
/// once for a run of rows of the same hour.
/// </para>
/// <para>
/// The entries are an absolute budget, <see cref="EntryLimit"/> a column: past it, the values whose rows are fewest
/// so far are left out, their entries merged into an entry of the values left out an hour, exact, and a value left out
/// stays out, so that every value kept is counted from its first row.
/// </para>
/// </remarks>
internal sealed class ColumnCountsBuilder : IndexBuilder
{
    /// <summary>The entries a column's counts keep at most, the values left out aside.</summary>
    internal const int EntryLimit = 1 << 20;

    /// <summary>
    /// The words a column's entries hold at most, 128 MiB: a table of many number columns, whose entries are long, keeps
    /// fewer of them.
    /// </summary>
    private const int WordLimit = 1 << 24;

    /// <summary>The most values a number column holds and is counted as a column rather than carried as a measure.</summary>
    internal const int FewValues = 64;

    /// <summary>
    /// The most values a column holds and keeps, for each entry, a quantile sketch of each measure, the distribution of its
    /// values by hour: past them, the sketches would weigh more than the column's rows give.
    /// </summary>
    internal const int SketchValues = 1_000;

    /// <summary>The valid rows at which a number shows whether it is counted or carried, and a text column whether it is wide.</summary>
    internal const long FirstCheck = 1 << 16;

    /// <summary>The fewest values a text column holds past which it is wide: the columns of few values keep its distinct values.</summary>
    internal const int WideValues = 1_000;

    /// <summary>The most values a column holds and keeps, for each entry, the distinct values of every wide column.</summary>
    internal const int AttachValues = 100;

    /// <summary>The bits of the sketches' grid: a relative error of 1 %, the engine's for a quantile asked with none.</summary>
    private static readonly int SketchBits = QuantileSketch.BitsFor(0.01);

    /// <summary>The share of the valid rows the distinct values may reach for the column to be counted: a quarter.</summary>
    private const int RepeatShare = 4;
    private const int NullValue = -1;
    private const int OtherValues = -2;

    private readonly ColumnSheetBuilder _sheet;
    private readonly CountsPlan _plan;
    private readonly int _limit;
    private readonly int _self;
    private readonly bool _number;
    private int _entryLimit;

    // The measures an entry's record holds, by their number in the plan, how each is summed, and where its words lie past
    // the entry's rows: every measure the plan still carries, the column itself aside.
    private int[] _measures;
    private MeasureKind[] _kinds;
    private int[] _at;
    private int _stride;
    private bool _settled;
    private bool _dropped;

    // The entries: each one's value's number, NullValue or OtherValues, its hour's number, and its record of words.
    private int _entries;
    private int[] _entryValue = new int[1_024];
    private int[] _entryHour = new int[1_024];
    private long[] _words;

    // By hour, the entry of each value's number, -1 for none; and the entries of the nulls and of the values left out.
    private readonly List<int[]> _entryOf = [];
    private readonly List<int> _nullEntry = [];
    private readonly List<int> _otherEntry = [];
    private bool[]? _leftOut;

    // Each row's entry, as where its record starts and as its number.
    private int[] _rowEntries = new int[1_024];
    private int[] _rowIndexes = new int[1_024];

    // For each measure the records hold, the quantile sketch of each entry, made at its first value and encoded once its
    // hour is done (_sealed), when a row of an hour before the batch's least can no longer come but late; null once the
    // column holds more than SketchValues values. The hours whose sketches are still open, by number.
    private QuantileSketch?[][]? _sketches;
    private byte[]?[][]? _sealed;
    private readonly HashSet<int> _openHours = [];

    // Each row's entry by its row in the batch being fed, which the distinct values read once the batch is in; and for each
    // wide column of the plan, the registers of each entry, null once the column holds more than AttachValues values.
    private readonly int _field;
    private int[] _batchEntries = new int[1_024];
    private byte[]?[][]? _distinct;

    /// <param name="sheet">The column's sheet, fed first, which numbers its rows' values.</param>
    /// <param name="plan">What the file's counts share.</param>
    /// <param name="field">The column.</param>
    /// <param name="entryLimit">The entries kept at most, <see cref="EntryLimit"/> unless a test asks for fewer.</param>
    internal ColumnCountsBuilder(ColumnSheetBuilder sheet, CountsPlan plan, int field, int entryLimit = EntryLimit)
    {
        _sheet = sheet;
        _plan = plan;
        _field = field;
        _distinct = new byte[]?[plan.Wide.Count][];
        for (int w = 0; w < _distinct.Length; w++)
        {
            _distinct[w] = [];
        }

        _self = plan.MeasureOf(field);
        _number = _self >= 0;
        _settled = !_number;
        _limit = entryLimit;
        (_measures, _kinds, _at, _stride) = Layout([.. Enumerable.Range(0, plan.Measures.Length).Where(m => m != _self)]);
        _entryLimit = Math.Min(_limit, Math.Max(1_024, WordLimit / _stride));
        _words = new long[1_024 * _stride];
        if (_measures.Length > 0)
        {
            _sketches = new QuantileSketch?[_measures.Length][];
            _sealed = new byte[]?[_measures.Length][];
            for (int k = 0; k < _measures.Length; k++)
            {
                _sketches[k] = new QuantileSketch?[1_024];
                _sealed[k] = new byte[]?[1_024];
            }
        }

        sheet.NumberRows();
    }

    /// <summary>The layout of a record of the measures <paramref name="measures"/>: their kinds, where their words lie, and the record's words.</summary>
    private (int[] Measures, MeasureKind[] Kinds, int[] At, int Stride) Layout(int[] measures)
    {
        MeasureKind[] kinds = new MeasureKind[measures.Length];
        int[] at = new int[measures.Length];
        int next = 1;
        for (int k = 0; k < measures.Length; k++)
        {
            kinds[k] = _plan.Measures[measures[k]].Kind;
            at[k] = next;
            next += MeasureLayout.Words(kinds[k] == MeasureKind.Float);
        }

        return (measures, kinds, at, next);
    }

    /// <summary>
    /// Drops from every record the words of the measures the plan no longer carries, a number counted as a column since:
    /// the records shorter, the rows that follow reach fewer lines of the cache.
    /// </summary>
    private void Relayout()
    {
        int[] live = [.. _measures.Where(m => _plan.Measures[m].Live)];
        if (live.Length == _measures.Length)
        {
            return;
        }

        int[] oldAt = _at;
        int[] oldMeasures = _measures;
        int oldStride = _stride;
        long[] old = _words;
        if (_sketches is { } sketches && _sealed is { } sealedSketches)
        {
            int[] from = Array.ConvertAll(live, m => Array.IndexOf(oldMeasures, m));
            _sketches = Array.ConvertAll(from, o => sketches[o]);
            _sealed = Array.ConvertAll(from, o => sealedSketches[o]);
        }

        (_measures, _kinds, _at, _stride) = Layout(live);
        _entryLimit = Math.Min(_limit, Math.Max(1_024, WordLimit / _stride));
        _words = new long[_entryValue.Length * _stride];
        for (int e = 0; e < _entries; e++)
        {
            _words[e * _stride] = old[e * oldStride];
            for (int k = 0, o = 0; k < _measures.Length; k++)
            {
                while (oldMeasures[o] != _measures[k])
                {
                    o++;
                }

                old.AsSpan((e * oldStride) + oldAt[o], MeasureLayout.Words(_kinds[k] == MeasureKind.Float))
                    .CopyTo(_words.AsSpan((e * _stride) + _at[k]));
            }
        }
    }

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (_dropped || count <= 0)
        {
            return;
        }

        ReadOnlySpan<int> ids = _sheet.RowIds;
        if (_plan.Dead || ids.IsEmpty || _sheet.Rows >= WideIndexedSum.Epoch)
        {
            // The sheet numbers no row: its counts went, the column's values hardly repeating, or its kind is none an
            // index keys. The instant in a form the plan does not read counts nothing either, nor does a file of an epoch
            // of rows, some 34 billion, whose entries' float sums would have to settle.
            Drop();
            return;
        }

        // The measures these rows fold: those the plan still carries, the column itself aside.
        Relayout();

        // The rows' entries, and their rows; then each measure folded over the rows, a loop of its kind, so that a row's
        // words are reached from its entry with no slice of them.
        if (_rowEntries.Length < count)
        {
            _rowEntries = new int[Math.Max(count, _rowEntries.Length * 2)];
            _rowIndexes = new int[_rowEntries.Length];
        }

        Span<int> entries = _rowEntries.AsSpan(0, count);
        Span<int> indexes = _rowIndexes.AsSpan(0, count);
        if (_distinct is not null && _batchEntries.Length < start + count)
        {
            Array.Resize(ref _batchEntries, Math.Max(start + count, _batchEntries.Length * 2));
        }
        ReadOnlySpan<int> hours = _plan.HourSlots.AsSpan(start, count);
        int lastHour = -1;
        int[] map = [];
        for (int i = 0; i < count; i++)
        {
            int hour = hours[i];
            if (hour != lastHour)
            {
                map = MapOf(hour);
                lastHour = hour;
            }

            int id = ids[i];
            int entry;
            if ((uint)id >= (uint)map.Length || (entry = map[id]) < 0)
            {
                entry = NewEntryOf(hour, id);
                map = _entryOf[hour];
            }

            entries[i] = entry * _stride;
            indexes[i] = entry;
            if (_distinct is not null)
            {
                _batchEntries[start + i] = entry;
            }
        }

        ref long words = ref MemoryMarshal.GetArrayDataReference(_words);
        foreach (int at in entries)
        {
            Unsafe.Add(ref words, at)++;
        }

        for (int k = 0; k < _measures.Length; k++)
        {
            CountedMeasure measure = _plan.Measures[_measures[k]];
            ref long states = ref Unsafe.Add(ref words, _at[k]);
            switch (measure.Kind)
            {
                case MeasureKind.Float:
                    FoldFloats(ref states, entries, measure.Splits.AsSpan(start, count), measure.Floats.AsSpan(start, count), Valid(measure, start, count));
                    break;
                case MeasureKind.Signed:
                    FoldSigned(ref states, entries, measure.Integers.AsSpan(start, count), Valid(measure, start, count));
                    break;
                default:
                    FoldUnsigned(ref states, entries, measure.Integers.AsSpan(start, count), Valid(measure, start, count));
                    break;
            }
        }

        if (_sketches is not null)
        {
            if (_sheet.Distinct > SketchValues)
            {
                _sketches = null;
                _sealed = null;
                _openHours.Clear();
            }
            else
            {
                Seal(_plan.LeastHour);
                Sketch(indexes, hours, start);
            }
        }

        if (_entries > _entryLimit)
        {
            LeaveOut();
        }

        if (!_settled && _sheet.Rows - _sheet.Nulls >= FirstCheck)
        {
            Settle();
        }
    }

    /// <summary>
    /// Raises, for each of the batch's <paramref name="rows"/>, the register its wide columns' hashes call for in its entry's
    /// registers of each: once the whole batch is in, every column's sheet having hashed its rows.
    /// </summary>
    internal void Attach(int rows)
    {
        if (_distinct is not { } distinct || _dropped)
        {
            return;
        }

        if (_sheet.Distinct > AttachValues || !_sheet.Counting)
        {
            _distinct = null;
            return;
        }

        for (int w = 0; w < distinct.Length; w++)
        {
            WideColumn wide = _plan.Wide[w];
            ReadOnlySpan<ulong> hashes = wide.Sheet.RowHashes;
            if (!wide.Live || wide.Field == _field)
            {
                distinct[w] = [];
                continue;
            }

            if (hashes.Length < rows)
            {
                // The wide column's sheet gave its hashes up: its counts went.
                wide.Live = false;
                distinct[w] = [];
                continue;
            }

            byte[]?[] registers = distinct[w];
            if (registers.Length < _entries)
            {
                Array.Resize(ref distinct[w], Math.Max(_entries, Math.Max(16, registers.Length * 2)));
                registers = distinct[w];
            }

            for (int row = 0; row < rows; row++)
            {
                ulong hash = hashes[row];
                if (hash == 0)
                {
                    continue;
                }

                byte[] ranks = registers[_batchEntries[row]] ??= new byte[ColumnSheet.Registers];
                ref byte rank = ref ranks[HyperLogLog.Register(hash, ColumnSheet.Precision)];
                rank = Math.Max(rank, HyperLogLog.Rank(hash, ColumnSheet.Precision));
            }
        }
    }

    /// <summary>Adds the rows' values of each measure to the sketch of their entry, from the batch's row <paramref name="start"/>.</summary>
    private void Sketch(ReadOnlySpan<int> indexes, ReadOnlySpan<int> hours, int start)
    {
        int count = indexes.Length;
        int last = -1;
        foreach (int hour in hours)
        {
            if (hour != last)
            {
                last = hour;
                _openHours.Add(hour);
            }
        }

        for (int k = 0; k < _measures.Length; k++)
        {
            CountedMeasure measure = _plan.Measures[_measures[k]];
            ReadOnlySpan<byte> valid = Valid(measure, start, count);
            for (int i = 0; i < count; i++)
            {
                if (!valid.IsEmpty && valid[i] == 0)
                {
                    continue;
                }

                QuantileSketch sketch = Open(k, indexes[i]);
                switch (measure.Kind)
                {
                    case MeasureKind.Float:
                        sketch.Add(measure.Floats[start + i]);
                        break;
                    case MeasureKind.Signed:
                        sketch.Add(measure.Integers[start + i]);
                        break;
                    default:
                        sketch.Add((ulong)measure.Integers[start + i]);
                        break;
                }
            }
        }
    }

    /// <summary>The open sketch of measure <paramref name="k"/> over entry <paramref name="entry"/>: made, or opened again from its encoded bytes.</summary>
    private QuantileSketch Open(int k, int entry)
    {
        QuantileSketch?[] sketches = _sketches![k];
        if (entry >= sketches.Length)
        {
            Array.Resize(ref _sketches[k], Math.Max(entry + 1, sketches.Length * 2));
            Array.Resize(ref _sealed![k], _sketches[k].Length);
            sketches = _sketches[k];
        }

        if (sketches[entry] is { } open)
        {
            return open;
        }

        open = sketches[entry] = new QuantileSketch(SketchBits);
        if (_sealed![k][entry] is { } sealedBytes)
        {
            // A late row of an hour done: its sketch opened again.
            open.MergeEncoded(sealedBytes);
            _sealed[k][entry] = null;
            _openHours.Add(_entryHour[entry]);
        }

        return open;
    }

    /// <summary>Encodes and lets go of the sketches of every hour before <paramref name="least"/>, which no row comes for but a late one.</summary>
    private void Seal(long least)
    {
        // The hours are numbered as they were met, not in their order: each open one is looked at.
        List<int>? done = null;
        foreach (int open in _openHours)
        {
            long at = _plan.Hours[open];
            if (at != CountsPlan.NullHour && at < least)
            {
                (done ??= []).Add(open);
            }
        }

        foreach (int hour in done ?? [])
        {
            _openHours.Remove(hour);
            foreach (int entry in EntriesOf(hour))
            {
                for (int k = 0; k < _measures.Length; k++)
                {
                    if (entry < _sketches![k].Length && _sketches[k][entry] is { } sketch)
                    {
                        _sealed![k][entry] = Encoded(sketch);
                        _sketches[k][entry] = null;
                    }
                }
            }
        }
    }

    /// <summary>The entries of the hour numbered <paramref name="hour"/>: its values', its nulls' and its values left out's.</summary>
    private IEnumerable<int> EntriesOf(int hour)
    {
        if (hour >= _entryOf.Count)
        {
            yield break;
        }

        foreach (int entry in _entryOf[hour])
        {
            if (entry >= 0)
            {
                yield return entry;
            }
        }

        if (_nullEntry[hour] >= 0)
        {
            yield return _nullEntry[hour];
        }

        if (_otherEntry[hour] >= 0)
        {
            yield return _otherEntry[hour];
        }
    }

    private static byte[] Encoded(QuantileSketch sketch)
    {
        System.Buffers.ArrayBufferWriter<byte> writer = new System.Buffers.ArrayBufferWriter<byte>(256);
        sketch.Encode(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>The encoded sketch of measure <paramref name="k"/> over entry <paramref name="entry"/>, open or sealed; null for none.</summary>
    private byte[]? SketchBytes(int k, int entry) =>
        _sketches is null || entry >= _sketches[k].Length ? null
        : _sketches[k][entry] is { } open ? Encoded(open)
        : _sealed![k][entry];

    /// <summary>Whether each of the rows <c>[start, start + count)</c> holds a value of <paramref name="measure"/>; empty when every one does.</summary>
    private static ReadOnlySpan<byte> Valid(CountedMeasure measure, int start, int count) =>
        measure.Valid is { } valid ? valid.AsSpan(start, count) : default;

    /// <summary>
    /// Folds the rows' floats into the states that start at <paramref name="states"/> past each row's entry: the values,
    /// the exact sum from the parts each value was split into once, the least and the greatest, a NaN's aside.
    /// </summary>
    private static void FoldFloats(ref long states, ReadOnlySpan<int> entries, ReadOnlySpan<IndexedSum.Split> splits, ReadOnlySpan<double> floats, ReadOnlySpan<byte> valid)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (!valid.IsEmpty && valid[i] == 0)
            {
                continue;
            }

            ref long state = ref Unsafe.Add(ref states, entries[i]);
            Unsafe.Add(ref state, MeasureLayout.Values)++;

            // The sum inside the wide one, whose high part stays nought below an epoch of values, which no entry of a file
            // that keeps counts reaches. The extremes start at the infinities, past which a NaN never compares.
            Unsafe.As<long, WideIndexedSum>(ref Unsafe.Add(ref state, MeasureLayout.Sum)).Sum.Add(in splits[i]);
            double value = floats[i];
            ref long least = ref Unsafe.Add(ref state, MeasureLayout.FloatLeast);
            ref long greatest = ref Unsafe.Add(ref state, MeasureLayout.FloatGreatest);
            if (value < BitConverter.Int64BitsToDouble(least))
            {
                least = BitConverter.DoubleToInt64Bits(value);
            }

            if (value > BitConverter.Int64BitsToDouble(greatest))
            {
                greatest = BitConverter.DoubleToInt64Bits(value);
            }
        }
    }

    /// <summary>Folds the rows' signed integers into the states that start at <paramref name="states"/> past each row's entry.</summary>
    private static void FoldSigned(ref long states, ReadOnlySpan<int> entries, ReadOnlySpan<long> integers, ReadOnlySpan<byte> valid)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (!valid.IsEmpty && valid[i] == 0)
            {
                continue;
            }

            ref long state = ref Unsafe.Add(ref states, entries[i]);
            long value = integers[i];
            Unsafe.Add(ref state, MeasureLayout.Values)++;
            ref long low = ref Unsafe.Add(ref state, MeasureLayout.Sum);
            ulong before = (ulong)low;
            ulong after = before + (ulong)value;
            low = (long)after;
            Unsafe.Add(ref state, MeasureLayout.Sum + 1) += (value >> 63) + (after < before ? 1 : 0);
            ref long least = ref Unsafe.Add(ref state, MeasureLayout.IntegerLeast);
            ref long greatest = ref Unsafe.Add(ref state, MeasureLayout.IntegerGreatest);
            least = Math.Min(least, value);
            greatest = Math.Max(greatest, value);
        }
    }

    /// <summary>Folds the rows' unsigned integers, as their bits, into the states that start at <paramref name="states"/> past each row's entry.</summary>
    private static void FoldUnsigned(ref long states, ReadOnlySpan<int> entries, ReadOnlySpan<long> integers, ReadOnlySpan<byte> valid)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (!valid.IsEmpty && valid[i] == 0)
            {
                continue;
            }

            ref long state = ref Unsafe.Add(ref states, entries[i]);
            ulong value = (ulong)integers[i];
            Unsafe.Add(ref state, MeasureLayout.Values)++;
            ref long low = ref Unsafe.Add(ref state, MeasureLayout.Sum);
            ulong before = (ulong)low;
            ulong after = before + value;
            low = (long)after;
            Unsafe.Add(ref state, MeasureLayout.Sum + 1) += after < before ? 1 : 0;
            ref long least = ref Unsafe.Add(ref state, MeasureLayout.IntegerLeast);
            ref long greatest = ref Unsafe.Add(ref state, MeasureLayout.IntegerGreatest);
            least = (long)Math.Min((ulong)least, value);
            greatest = (long)Math.Max((ulong)greatest, value);
        }
    }

    internal override void CloseBlock()
    {
    }

    /// <summary>
    /// Settles whether a number column is counted or carried: of at most <see cref="FewValues"/> values, counted, and
    /// carried by no other column's counts from then on; of more, carried, and not counted.
    /// </summary>
    internal void Settle()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        if (!_dropped && _sheet.Counting && _sheet.Distinct <= FewValues)
        {
            _plan.Measures[_self].Live = false;
            return;
        }

        Drop();
    }

    /// <summary>The counts' bytes, once every row is in and every number settled; null for a column that keeps none.</summary>
    internal byte[]? Finish()
    {
        long valid = _sheet.Rows - _sheet.Nulls;
        int distinct = _sheet.Distinct;
        if (_dropped || _plan.Dead || !_sheet.Counting || distinct == 0 || distinct * (long)RepeatShare > valid
            || (_number && distinct > FewValues))
        {
            return null;
        }

        // A value whose bytes the sheet could not keep is left out with the others.
        byte[]?[] values = _sheet.ValuesByNumber();
        bool more = false;
        for (int id = 0; id < values.Length; id++)
        {
            if (values[id] is null && !IsLeftOut(id))
            {
                if (_leftOut is not { } held || held.Length < values.Length)
                {
                    bool[] grown = new bool[values.Length];
                    _leftOut?.CopyTo(grown, 0);
                    _leftOut = grown;
                }

                _leftOut[id] = more = true;
            }
        }

        if (more)
        {
            Merge();
        }

        // The measures the records hold that the plan still carries, by their place in the records.
        List<int> carried = [];
        for (int k = 0; k < _measures.Length; k++)
        {
            if (_plan.Measures[_measures[k]].Live)
            {
                carried.Add(k);
            }
        }

        int[] fields = new int[carried.Count];
        MeasureKind[] kinds = new MeasureKind[carried.Count];
        for (int i = 0; i < carried.Count; i++)
        {
            (fields[i], kinds[i]) = (_plan.Measures[_measures[carried[i]]].Field, _kinds[carried[i]]);
        }

        // The entries in the counts' order: the nulls, the values left out, then the values by their bytes, each by hour.
        List<long> hours = _plan.Hours;
        int[] order = EntryOrder(values, hours);

        int width = StateWords.Count(kinds);
        long[] words = new long[_entries * width];
        long[] entryHours = new long[_entries];
        byte[]?[]? sketchBytes = _sketches is null ? null : new byte[]?[_entries * carried.Count];

        // The wide columns whose distinct values every entry keeps.
        _plan.SettleWide(end: true);
        List<int> attached = [];
        for (int w = 0; _distinct is not null && distinct <= AttachValues && w < _distinct.Length; w++)
        {
            if (_plan.Wide[w].Live && _plan.Wide[w].Field != _field)
            {
                attached.Add(w);
            }
        }

        byte[]?[]? registers = attached.Count > 0 ? new byte[]?[_entries * attached.Count] : null;
        List<CountGroup> groups = [];
        for (int i = 0; i < order.Length; i++)
        {
            int entry = order[i];
            entryHours[i] = hours[_entryHour[entry]];
            ReadOnlySpan<long> record = _words.AsSpan(entry * _stride, _stride);
            Span<long> state = words.AsSpan(i * width, width);
            state[0] = record[0];
            int at = 1;
            for (int a = 0; registers is not null && a < attached.Count; a++)
            {
                byte[]?[] held = _distinct![attached[a]];
                registers[(i * attached.Count) + a] = entry < held.Length && held[entry] is { } ranks ? HyperLogLog.Encode(ranks) : null;
            }

            for (int c = 0; c < carried.Count; c++)
            {
                if (sketchBytes is not null)
                {
                    sketchBytes[(i * carried.Count) + c] = SketchBytes(carried[c], entry);
                }

                int length = MeasureLayout.Words(kinds[c] == MeasureKind.Float);
                Span<long> into = state.Slice(at, length);
                record.Slice(_at[carried[c]], length).CopyTo(into);

                // A measure that met no value holds what StateWords clears it to, not the ends its extremes start from; and a
                // float's extremes that met no number but a NaN are NaN.
                bool none = into[MeasureLayout.Values] == 0;
                if (none)
                {
                    into.Clear();
                }

                if (kinds[c] == MeasureKind.Float && (none
                    || (into[MeasureLayout.FloatLeast] == BitConverter.DoubleToInt64Bits(double.PositiveInfinity)
                        && into[MeasureLayout.FloatGreatest] == BitConverter.DoubleToInt64Bits(double.NegativeInfinity))))
                {
                    into[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(double.NaN);
                    into[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(double.NaN);
                }

                at += length;
            }

            int value = _entryValue[entry];
            if (groups.Count == 0 || _entryValue[order[i - 1]] != value)
            {
                groups.Add(new CountGroup(
                    value switch { NullValue => CountedValue.Null, OtherValues => CountedValue.Others, _ => CountedValue.Value },
                    value >= 0 ? values[value] : null,
                    i,
                    0));
            }

            groups[^1] = groups[^1] with { Entries = groups[^1].Entries + 1 };
        }

        return new ColumnCounts(fields, kinds, [.. groups], entryHours, words, sketchBytes)
        {
            AttachedFields = [.. attached.Select(w => _plan.Wide[w].Field)],
            Distinct = registers,
        }.ToBytes();
    }

    /// <summary>
    /// The entries in the counts' order: the nulls, the values left out, then the values by their bytes, each group's
    /// entries by hour. The values are sorted once, and the entries placed by their value's rank, in one pass.
    /// </summary>
    private int[] EntryOrder(byte[]?[] values, List<long> hours)
    {
        // Each value's rank among those that hold an entry, in the order of their bytes; the nulls 0, the values left out 1.
        bool[] held = new bool[values.Length];
        int distinct = 0;
        for (int e = 0; e < _entries; e++)
        {
            if (_entryValue[e] >= 0 && !held[_entryValue[e]])
            {
                held[_entryValue[e]] = true;
                distinct++;
            }
        }

        int[] ids = new int[distinct];
        byte[][] keys = new byte[distinct][];
        for (int id = 0, next = 0; id < held.Length; id++)
        {
            if (held[id])
            {
                (ids[next], keys[next]) = (id, values[id]!);
                next++;
            }
        }

        Array.Sort(keys, ids, Comparer<byte[]>.Create(static (left, right) => left.AsSpan().SequenceCompareTo(right)));
        int[] group = new int[values.Length];
        for (int rank = 0; rank < ids.Length; rank++)
        {
            group[ids[rank]] = rank + 2;
        }

        // The entries by group, counted then placed; within a group, by hour.
        int[] starts = new int[distinct + 3];
        for (int e = 0; e < _entries; e++)
        {
            starts[GroupOf(e, group) + 1]++;
        }

        for (int g = 1; g < starts.Length; g++)
        {
            starts[g] += starts[g - 1];
        }

        int[] order = new int[_entries];
        int[] placed = (int[])starts.Clone();
        for (int e = 0; e < _entries; e++)
        {
            order[placed[GroupOf(e, group)]++] = e;
        }

        for (int g = 0; g + 1 < starts.Length; g++)
        {
            for (int i = starts[g] + 1; i < starts[g + 1]; i++)
            {
                int entry = order[i];
                long hour = hours[_entryHour[entry]];
                int j = i - 1;
                while (j >= starts[g] && hours[_entryHour[order[j]]] > hour)
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = entry;
            }
        }

        return order;
    }

    /// <summary>Entry <paramref name="entry"/>'s group: 0 for the nulls, 1 for the values left out, its value's rank plus two otherwise.</summary>
    private int GroupOf(int entry, int[] group) => _entryValue[entry] switch
    {
        NullValue => 0,
        OtherValues => 1,
        int value => group[value],
    };

    /// <summary>The table of the values' entries in the hour numbered <paramref name="hour"/>, which every list then covers.</summary>
    private int[] MapOf(int hour)
    {
        while (_entryOf.Count <= hour)
        {
            _entryOf.Add([]);
            _nullEntry.Add(-1);
            _otherEntry.Add(-1);
        }

        return _entryOf[hour];
    }

    /// <summary>The entry of value <paramref name="id"/>, -1 for a null, in the hour numbered <paramref name="hour"/>, which its table lacks.</summary>
    private int NewEntryOf(int hour, int id)
    {
        if (id < 0)
        {
            return _nullEntry[hour] >= 0 ? _nullEntry[hour] : _nullEntry[hour] = NewEntry(NullValue, hour);
        }

        int[] map = _entryOf[hour];
        if (id >= map.Length)
        {
            int[] grown = new int[Math.Max(id + 1, Math.Max(16, map.Length * 2))];
            map.CopyTo(grown, 0);
            grown.AsSpan(map.Length).Fill(-1);
            _entryOf[hour] = map = grown;
        }

        if (map[id] >= 0)
        {
            return map[id];
        }

        int entry = IsLeftOut(id)
            ? _otherEntry[hour] >= 0 ? _otherEntry[hour] : _otherEntry[hour] = NewEntry(OtherValues, hour)
            : NewEntry(id, hour);
        map[id] = entry;
        return entry;
    }

    private bool IsLeftOut(int id) => _leftOut is { } leftOut && id < leftOut.Length && leftOut[id];

    /// <summary>A new entry of value <paramref name="value"/> in hour <paramref name="hour"/>: no row, each measure's extremes at their start.</summary>
    private int NewEntry(int value, int hour)
    {
        int entry = _entries++;
        if (entry == _entryValue.Length)
        {
            int size = _entryValue.Length * 2;
            Array.Resize(ref _entryValue, size);
            Array.Resize(ref _entryHour, size);
            Array.Resize(ref _words, size * _stride);
        }

        _entryValue[entry] = value;
        _entryHour[entry] = hour;
        Open(_words.AsSpan(entry * _stride, _stride));
        return entry;
    }

    /// <summary>A record of no row: each measure's extremes at the ends they move from, a float's at the infinities.</summary>
    private void Open(Span<long> record)
    {
        record.Clear();
        for (int k = 0; k < _kinds.Length; k++)
        {
            Span<long> state = record[_at[k]..];
            switch (_kinds[k])
            {
                case MeasureKind.Float:
                    state[MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(double.PositiveInfinity);
                    state[MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
                    break;
                case MeasureKind.Signed:
                    state[MeasureLayout.IntegerLeast] = long.MaxValue;
                    state[MeasureLayout.IntegerGreatest] = long.MinValue;
                    break;
                default:
                    state[MeasureLayout.IntegerLeast] = -1L;
                    state[MeasureLayout.IntegerGreatest] = 0L;
                    break;
            }
        }
    }

    /// <summary>
    /// Leaves out the values whose rows are fewest so far, until the entries of those kept fill half the budget: their
    /// entries merge into the entries of the values left out, an hour each.
    /// </summary>
    private void LeaveOut()
    {
        long[] totals = new long[_sheet.Numbered];
        int[] entriesOf = new int[totals.Length];
        for (int e = 0; e < _entries; e++)
        {
            if (_entryValue[e] >= 0)
            {
                totals[_entryValue[e]] += _words[e * _stride];
                entriesOf[_entryValue[e]]++;
            }
        }

        int[] ids = new int[totals.Length];
        for (int id = 0; id < ids.Length; id++)
        {
            ids[id] = id;
        }

        Array.Sort(ids, (left, right) => totals[right] != totals[left] ? totals[right].CompareTo(totals[left]) : left.CompareTo(right));
        bool[] leftOut = new bool[ids.Length];
        _leftOut?.AsSpan(0, Math.Min(_leftOut.Length, leftOut.Length)).CopyTo(leftOut);
        long kept = 0;
        foreach (int id in ids)
        {
            if (leftOut[id] || entriesOf[id] == 0)
            {
                continue;
            }

            if (kept + entriesOf[id] > _entryLimit / 2)
            {
                leftOut[id] = true;
                continue;
            }

            kept += entriesOf[id];
        }

        _leftOut = leftOut;
        Merge();
    }

    /// <summary>Merges the entries of every value left out into the entries of the values left out, an hour each, and renumbers the entries.</summary>
    private void Merge()
    {
        // Entries renumbered, the registers would follow them; a column of at most AttachValues values reaches the budget
        // only over thousands of hours, and lets them go.
        _distinct = null;

        int total = _entries;
        int[] values = _entryValue;
        int[] hoursOf = _entryHour;
        long[] words = _words;

        // Each entry's sketches, open or sealed, read before the entries move.
        QuantileSketch?[][]? sketches = null;
        if (_sketches is not null)
        {
            sketches = new QuantileSketch?[_measures.Length][];
            for (int k = 0; k < _measures.Length; k++)
            {
                sketches[k] = new QuantileSketch?[total];
                for (int e = 0; e < total; e++)
                {
                    sketches[k][e] = SketchBytes(k, e) is { } bytes ? Decoded(bytes) : null;
                }

                _sketches[k] = new QuantileSketch?[Math.Max(1_024, values.Length)];
                _sealed![k] = new byte[]?[_sketches[k].Length];
            }

            foreach (int hour in Enumerable.Range(0, _entryOf.Count))
            {
                _openHours.Add(hour);
            }
        }

        _entryValue = new int[values.Length];
        _entryHour = new int[hoursOf.Length];
        _words = new long[words.Length];
        _entries = 0;
        for (int hour = 0; hour < _entryOf.Count; hour++)
        {
            _entryOf[hour].AsSpan().Fill(-1);
            _nullEntry[hour] = -1;
            _otherEntry[hour] = -1;
        }

        // Every entry again, in its order, a value left out's into its hour's entry of the values left out.
        for (int e = 0; e < total; e++)
        {
            int value = values[e];
            int hour = hoursOf[e];
            ReadOnlySpan<long> record = words.AsSpan(e * _stride, _stride);
            if (value >= 0 && !IsLeftOut(value))
            {
                int entry = NewEntry(value, hour);
                _entryOf[hour][value] = entry;
                record.CopyTo(_words.AsSpan(entry * _stride, _stride));
                MoveSketches(sketches, e, entry);
                continue;
            }

            if (value == NullValue)
            {
                int entry = _nullEntry[hour] = NewEntry(NullValue, hour);
                record.CopyTo(_words.AsSpan(entry * _stride, _stride));
                MoveSketches(sketches, e, entry);
                continue;
            }

            int into = _otherEntry[hour] >= 0 ? _otherEntry[hour] : _otherEntry[hour] = NewEntry(OtherValues, hour);
            StateWords.Merge(_words.AsSpan(into * _stride, _stride), record, _kinds);
            for (int k = 0; sketches is not null && k < _measures.Length; k++)
            {
                if (sketches[k][e] is { } merged)
                {
                    Open(k, into).Merge(merged);
                }
            }
        }
    }

    /// <summary>Puts the sketches entry <paramref name="from"/> held before a merge on entry <paramref name="to"/>.</summary>
    private void MoveSketches(QuantileSketch?[][]? sketches, int from, int to)
    {
        for (int k = 0; sketches is not null && k < _measures.Length; k++)
        {
            if (sketches[k][from] is { } sketch)
            {
                if (to >= _sketches![k].Length)
                {
                    Array.Resize(ref _sketches[k], Math.Max(to + 1, _sketches[k].Length * 2));
                    Array.Resize(ref _sealed![k], _sketches[k].Length);
                }

                _sketches[k][to] = sketch;
            }
        }
    }

    private static QuantileSketch Decoded(byte[] bytes)
    {
        QuantileSketch sketch = new QuantileSketch(SketchBits);
        sketch.MergeEncoded(bytes);
        return sketch;
    }

    private void Drop()
    {
        _dropped = true;
        _distinct = null;
        _sketches = null;
        _sealed = null;
        _openHours.Clear();
        _entries = 0;
        _entryValue = [];
        _entryHour = [];
        _words = [];
        _entryOf.Clear();
        _nullEntry.Clear();
        _otherEntry.Clear();
    }
}
