using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The tables of an object's synopsis, built from its slices as they are sorted: per slice and per depth of
/// its synopsis columns, every combination of their values with its count and the states of every measure
/// column, which are the run statistics; their merges per hour, and per object when it spans more than an
/// hour, the coarse tables; and per synopsis column after the first, its value counts per hour, the first's
/// being the coarse table of depth 1. Depth 0, a slice's rows whatever their keys, is kept when there are
/// measures: without them its counts are the slice table's.
/// </summary>
/// <remarks>
/// <para>
/// A table's row is a bucket, the values of its keys, a count of rows, then for each measure column the
/// count of its values, their exact sum (an <see cref="Int128"/> for integers, a <see cref="WideIndexedSum"/>
/// for floats, in the words <see cref="PersistedStates"/> lays out), and their least and greatest, widened to
/// 64 bits, a float's NaN aside as the engine's Min and Max take it. Rows are in the order of their bucket,
/// then of their keys' values, so that the same rows give the same tables, whoever writes them.
/// </para>
/// <para>
/// A slice is grouped by the codes its sort gave each row, in the sorted order. Down to the first variant's
/// shapes in the object's order, a combination's rows are one run, and a group starts where a code changes.
/// Past them, the rows of a combination may lie apart, and pairs of a group and a code number the groups.
/// The measures are folded once, into the deepest groups, whose states merge into each shallower depth's.
/// </para>
/// <para>
/// Nothing is held for longer than a slice but a chunk of each table and what a merge reads. A slice's runs go
/// out as rows of the tables of slices at once; its groups at the deepest depth a coarser table still asks for,
/// and the values of each column, in the order of their keys, go to a scratch store as runs of records, memory
/// up to its budget and the disk past it. At the end of each hour, a merge of those runs gives every depth of
/// the hour's coarse tables in one pass, a group of a depth ending where its prefix of the keys does, and the
/// value counts likewise; the hour's deepest groups go on to the object's store, whose merge at the end gives
/// the object's tables. Every table goes to the writer a chunk at a time (<see cref="ISynopsisChunkSink"/>),
/// <see cref="ChunkRows"/> rows at most, so that no array holds more than a chunk, whatever the object's rows;
/// an hour's rows are chunks of their own when they weigh enough for a question of the hour to read them alone.
/// </para>
/// <para>
/// A table whose chunks take more than the budget of the data written so far alone is not worth its bytes, and
/// is given up as it grows. So is a coarser table that the rows counted already rule out against the budget of
/// the whole object, which its rows, known before a row is written, scale: it holds at least the groups an hour
/// or a slice held at its depth, each at the fewest bytes a row of a table has taken yet. The merges then read
/// the shallower depths only, which cuts the runs of the hours that follow.
/// </para>
/// </remarks>
internal sealed class SynopsisTables : IDisposable
{
    /// <summary>The rows a chunk of a table holds at most: what a reader decodes at once, and what a stage holds.</summary>
    internal const int ChunkRows = 1 << 16;

    /// <summary>
    /// The rows of a table's first chunk, at most: enough for the bytes a row takes once encoded to say what the rest will
    /// take, few enough for a table that grows fast to say it early.
    /// </summary>
    internal const int ProbeRows = 1 << 12;

    /// <summary>
    /// The bytes, before they are encoded, past which the rows of an hour go out as chunks of their own when the hour
    /// ends, so that a question of an hour reads that hour's alone: about what a round trip to an object store costs
    /// in transfer once they are encoded. Fewer wait for the next hour's, since a read of them costs its round trip
    /// whatever it holds.
    /// </summary>
    private const long HourChunkBytes = 4L << 20;

    /// <summary>The bytes of records a merge's output holds before it goes to its store.</summary>
    private const int RunFlushBytes = 1 << 20;

    /// <summary>
    /// How many times the budget a coarser table's projected rows must take for it to be given up before its rows are
    /// all counted: a projection fitted to the growth so far overshoots where a key's values come in a burst and then
    /// repeat, while a table of keys almost unique to each row, the waste worth cutting, takes many times the budget.
    /// </summary>
    private const double ProjectionMargin = 2;

    private readonly DType _schema;
    private readonly IReadOnlyList<SynopsisPath> _paths;
    private readonly TimeUnit _unit;
    private readonly ISynopsisChunkSink _sink;
    private readonly bool _spansHours;
    private readonly long _objectRows;
    private readonly (int Rows, long HourBytes) _chunking;
    private readonly Dictionary<long, long> _hourRows = [];
    private readonly ScanContext _context = new ScanContext([]);
    private readonly DTypeArena _types = new DTypeArena();
    private readonly Dictionary<int, DType> _keyTypes = [];
    private readonly Dictionary<long, int> _pairs = [];
    private readonly RunBuffer _run = new RunBuffer();
    private readonly RunBuffer _key = new RunBuffer();

    // The first key of each depth's group a merge is in, which the group's row carries.
    private RunBuffer?[] _starts = [];
    private int[] _measures;
    private MeasureKind[] _kinds;
    private int _words;
    private long[] _state = [];
    private int[] _keys = [];
    private int _first;
    private bool _settled;
    private KeyShape[] _keyShapes = [];

    // The columns of the slice being counted's paths, in its batch's arena, -1 for a path not read.
    private IReadOnlyList<int> _pathColumns = [];

    // Each depth's group of every row of the slice counted, reused from slice to slice.
    private int[][] _groups = [];

    // The paths left in the residual whose values the tables count, which the first slice settles.
    private int[] _counted = [];

    // The tables the object may keep, in the order the choice weighs them; and each kind by its depth, the value
    // counts by the key they count, the paths counted after the keys, of the hour and of each slice.
    private readonly List<Candidate> _candidates = [];
    private Candidate?[] _runs = [];
    private Candidate?[] _hourly = [];
    private Candidate?[] _whole = [];
    private Candidate?[] _values = [];
    private Candidate?[] _sliceValues = [];

    // What the coarser tables' rows are weighed from: at each depth, the groups of the hours merged, and the distinct
    // groups of the hour and of the object as their slices come; for each column counted, its values of the hours merged
    // and the distinct ones of the hour.
    private long[] _hourSum = [];
    private Growth?[] _hourGrowth = [];
    private Growth?[] _objectGrowth = [];
    private long[] _valueSum = [];
    private Growth?[] _valueGrowth = [];

    // The hash of each group's keys at each depth of the slice counted, and the keys themselves, each encoded once.
    private ulong[][] _prefixHashes = [];
    private readonly SliceKeys _sliceKeys = new SliceKeys();

    // The rows of the slices counted, the one being counted's among them; of those handed to the writer since; of the
    // hours merged, and of the hour's. Once the last slice is counted, the tables are finished, and none is given up.
    private long _rowsCounted;
    private long _rowsWritten;
    private long _rowsClosed;
    private long _hourRowsSeen;
    private bool _finishing;

    // The hour being counted, the depth its runs hold, the runs of its slices, each column's runs of values, and the
    // store they wait in.
    private long _hour = long.MinValue;
    private int _hourDepth = -1;
    private RunScratch? _hourScratch;
    private readonly List<RunSpan> _hourRuns = [];
    private List<RunSpan>[] _valueRuns = [];
    private long _hours;

    // The object's hours at the depth of its own tables, and the store they wait in.
    private int _wholeDepth = -1;
    private RunScratch? _wholeScratch;
    private readonly List<RunSpan> _wholeRuns = [];

    /// <summary>
    /// The tables of an object of <paramref name="schema"/>, its instant in <paramref name="unit"/>, the
    /// measures <paramref name="measures"/>, its synopsis paths <paramref name="paths"/>, its chunks handed to
    /// <paramref name="sink"/>: the first slice adds to the measures the numeric paths the order does not place.
    /// Its slices <paramref name="slices"/>, known before a row is written, say whether it spans hours, which its
    /// tables of the object then sum up, and its rows and each hour's, which weigh what its tables may take; without
    /// them, it is taken to span hours, and no table is given up before the end. A test sets <paramref name="chunking"/>,
    /// the rows of a chunk and the bytes past which an hour's rows are chunks of their own, to cut tables of a few rows.
    /// </summary>
    internal SynopsisTables(
        DType schema, TimeUnit unit, int[] measures, ISynopsisChunkSink sink, IReadOnlyList<SynopsisPath>? paths = null,
        IReadOnlyList<TimeSlice>? slices = null, (int Rows, long HourBytes)? chunking = null)
    {
        _schema = schema;
        _unit = unit;
        _paths = paths ?? [];
        _sink = sink;
        _chunking = chunking ?? (ChunkRows, HourChunkBytes);
        (_measures, _kinds) = KindsOf(measures);
        _spansHours = slices is null;
        _cutsHours = slices is null;
        for (int s = 0; s < (slices?.Count ?? 0); s++)
        {
            long hour = TimeSlicer.Floor(slices![s].Start, unit, SliceWidth.Hour);
            _cutsHours |= _hourRows.ContainsKey(hour);
            _hourRows[hour] = _hourRows.GetValueOrDefault(hour) + slices[s].Rows;
            _objectRows += slices[s].Rows;
        }

        _spansHours |= _hourRows.Count > 1;
    }

    // Whether an hour of the object holds several slices: otherwise a slice is its hour, and a table of its slices'
    // value counts holds the rows of its hour's.
    private readonly bool _cutsHours;

    /// <summary>The measures <paramref name="measures"/>, fields or paths, and how each one's states are kept.</summary>
    private (int[] Measures, MeasureKind[] Kinds) KindsOf(int[] measures)
    {
        MeasureKind[] kinds = new MeasureKind[measures.Length];
        for (int m = 0; m < measures.Length; m++)
        {
            if (SynopsisPath.Names(measures[m]))
            {
                kinds[m] = _paths[SynopsisPath.Place(measures[m])].Class == Types.Variants.VariantClass.Double ? MeasureKind.Float : MeasureKind.Signed;
                continue;
            }

            PType ptype = Storage(_schema.GetField(measures[m])).PType;
            kinds[m] = ptype.IsFloat() ? MeasureKind.Float : ptype.IsUnsignedInteger() ? MeasureKind.Unsigned : MeasureKind.Signed;
        }

        return (measures, kinds);
    }

    /// <summary>
    /// The measure columns of <paramref name="schema"/>: its top-level numbers but the instant. A synopsis column among
    /// them is a measure of every table it is no key of: a duration the order places last, its values repeating a few
    /// times over a large object, keeps its sums and extremes in the tables of the columns before it.
    /// </summary>
    internal static int[] Measures(DType schema, int instant)
    {
        List<int> measures = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            DType dtype = Storage(schema.GetField(field));
            if (field != instant && dtype.Kind == DTypeKind.Primitive && PTypeExtensions.IsDefined(dtype.PType) && dtype.PType != PType.F16)
            {
                measures.Add(field);
            }
        }

        return [.. measures];
    }

    /// <summary>
    /// Counts the slice starting at <paramref name="start"/>, its rows in <paramref name="sorted"/>, sorted by
    /// the entries <paramref name="order"/> names, whose codes in the sorted order <paramref name="codes"/>
    /// holds, the columns of its paths in <paramref name="paths"/>, the paths left in the residual whose
    /// values it counts <paramref name="counted"/>: its runs go out as rows of the tables of slices, and its
    /// groups and values to the runs the hour's merge reads. Slices come in time order. Where the entries' codes packed
    /// into the words <paramref name="packed"/> the rows were sorted by, its groups are read off them.
    /// </summary>
    internal async ValueTask AddSliceAsync(
        RecordBatch sorted, long start, IReadOnlyList<int> order, IReadOnlyList<int[]> codes, IReadOnlyList<int>? paths = null,
        IReadOnlyList<int>? counted = null, PackedKeys? packed = null, CancellationToken cancellationToken = default)
    {
        _pathColumns = paths ?? [];
        int rows = sorted.RowCount;
        if (rows == 0)
        {
            return;
        }

        // The keys, and how many lead the order before a variant's shapes.
        List<int> keys = [];
        List<int[]> keyCodes = [];
        int leading = -1;
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] >= 0)
            {
                keys.Add(order[i]);
                keyCodes.Add(codes[i]);
            }
            else if (leading < 0)
            {
                leading = keys.Count;
            }
        }

        if (!_settled)
        {
            Settle(keys, keyCodes, rows, counted);
        }

        int depth = _keys.Length;
        leading = leading < 0 ? depth : leading;
        long hour = TimeSlicer.Floor(start, _unit, SliceWidth.Hour);
        if (hour != _hour)
        {
            if (_hour != long.MinValue)
            {
                await CloseHourAsync(cancellationToken).ConfigureAwait(false);
            }

            OpenHour(hour);
        }

        _rowsCounted = _rowsWritten + rows;

        // Each depth's groups, numbered in the order of their first rows, down to the deepest a table still asks for: the
        // tables of slices', or the hour's runs'. Past it, no group is counted, so a depth all of whose tables were given
        // up costs nothing more.
        int used = Math.Max(Math.Max(Deepest(_runs), _hourDepth), 0);
        int[] counts = new int[used + 1];
        int[][] firsts = new int[used + 1][];
        int[][] parents = new int[used + 1][];
        bool runs = used <= leading;
        if (packed is not null && runs)
        {
            Boundaries(packed, rows, used, counts, firsts, parents);
        }
        else
        {
            Grouped(keyCodes, leading, rows, used, counts, firsts, parents);
        }

        // The measures folded into the deepest groups, a run of the rows each down to the shapes, then merged up a depth
        // at a time.
        CanonicalArena arena = sorted.Arena;
        Accumulators[] states = new Accumulators[used + 1];
        states[used] = runs
            ? FoldRuns(arena, sorted.RootIndex, rows, firsts[used], counts[used])
            : Fold(arena, sorted.RootIndex, rows, _groups[used], counts[used]);
        for (int d = used - 1; d >= 0; d--)
        {
            states[d] = states[d + 1].Merge(parents[d + 1].AsSpan(0, counts[d + 1]), counts[d], RentStates(counts[d]));
        }

        // Each depth's groups in the order of their keys' values, which their numbering is down to the shapes, and
        // the first row of each in that order.
        // The group at each place, where a depth's groups are not numbered in the order of their keys.
        int[][] keyed = new int[used + 1][];
        int[]?[] placed = new int[used + 1][];
        for (int d = 0; d <= used; d++)
        {
            keyed[d] = firsts[d];
            if (d > leading)
            {
                int[] places = Places(firsts[d], keyCodes, d);
                states[d] = states[d].Merge(places, counts[d], RentStates(counts[d]));
                keyed[d] = new int[counts[d]];
                placed[d] = new int[counts[d]];
                for (int g = 0; g < counts[d]; g++)
                {
                    keyed[d][places[g]] = firsts[d][g];
                    placed[d]![places[g]] = g;
                }
            }
        }

        // The key columns as their values lie, a column's encodings decoded, and each depth's groups' keys.
        CanonicalNode node = arena.GetNode(sorted.RootIndex);
        int[] columns = new int[depth];
        for (int k = 0; k < depth; k++)
        {
            columns[k] = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, ColumnOf(node, _keys[k])));
        }

        _sliceKeys.Build(arena, columns, _keyShapes, counts, firsts, parents, used);
        CountDistinct(counts, parents);

        // The slice's runs, rows of the tables of slices.
        for (int d = _first; d <= used; d++)
        {
            if (_runs[d] is { Dead: false } table)
            {
                for (int p = 0; p < counts[d]; p++)
                {
                    states[d].Words(p, _state);
                    if (Hold(table, start, _sliceKeys.Of(d, placed[d]?[p] ?? p), _state))
                    {
                        await FlushAsync(table, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        // Its groups at the depth the hour's merge reads, in the order of their keys.
        if (_hourDepth >= 0)
        {
            int at = _hourDepth;
            _run.Clear();
            for (int p = 0; p < counts[at]; p++)
            {
                _run.Bytes(_sliceKeys.Of(at, placed[at]?[p] ?? p));
                states[at].Words(p, _state);
                _run.Words(_state);
            }

            _hourRuns.Add(await AppendAsync(HourStore(), at, cancellationToken).ConfigureAwait(false));
        }

        // Each later key's values, and each counted path's, in their order, with their states: a key the deepest groups
        // counted hold folds from their states, a group a value, one deeper from the rows.
        for (int k = 1; k < depth; k++)
        {
            if (!CountsValues(k))
            {
                continue;
            }

            int[] valueCodes = keyCodes[k];
            if (k < used)
            {
                int[] at = keyed[used];
                int[] groupOf = new int[counts[used]];
                int distinct = 0;
                for (int p = 0; p < groupOf.Length; p++)
                {
                    groupOf[p] = valueCodes[at[p]];
                    distinct = Math.Max(distinct, groupOf[p] + 1);
                }

                int[] first = new int[distinct];
                first.AsSpan().Fill(-1);
                for (int p = 0; p < groupOf.Length; p++)
                {
                    first[groupOf[p]] = first[groupOf[p]] < 0 ? at[p] : first[groupOf[p]];
                }

                await EmitValuesAsync(k, start, arena, columns[k], _keyShapes[k], states[used].Merge(groupOf, distinct, RentStates(distinct)), first, distinct, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            int values = 0;
            for (int row = 0; row < rows; row++)
            {
                values = Math.Max(values, valueCodes[row] + 1);
            }

            await ValuesAsync(k, start, arena, sorted.RootIndex, rows, columns[k], _keyShapes[k], valueCodes, values, cancellationToken).ConfigureAwait(false);
        }

        for (int c = 0; c < _counted.Length; c++)
        {
            if (CountsValues(depth + c))
            {
                int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, _pathColumns[SynopsisPath.Place(_counted[c])]));
                (int[] valueCodes, int distinct) = SliceSort.CodesOfColumn(arena, column, rows);
                await ValuesAsync(depth + c, start, arena, sorted.RootIndex, rows, column, _values[depth + c]!.Shapes[0], valueCodes, distinct, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _hourRowsSeen += rows;
        Note(_hourly, _hourGrowth, _hourRowsSeen);
        Note(_whole, _objectGrowth, _rowsWritten + rows);
        Note(_values, _valueGrowth, _hourRowsSeen);
        Prune();
        _rowsWritten += rows;

        // Every state the slice counted in goes back for the next slice's.
        _spareStates.AddRange(_rentedStates);
        _rentedStates.Clear();
    }

    /// <summary>
    /// Adds each group of the slice to the distinct groups of its depth, the hour's and the object's, down to the deepest
    /// depth a coarser table still counts: a group's hash its parent's mixed with its own key's, the slice's keys built,
    /// each group's parent at the depth above in <paramref name="parentsOf"/>.
    /// </summary>
    private void CountDistinct(int[] counts, int[][] parentsOf)
    {
        int deepest = 0;
        for (int d = 1; d < _hourGrowth.Length; d++)
        {
            deepest = Counts(_hourly[d], _hourGrowth[d]) is not null || Counts(_whole[d], _objectGrowth[d]) is not null ? d : deepest;
        }

        if (deepest == 0)
        {
            return;
        }

        if (_prefixHashes.Length <= deepest)
        {
            Array.Resize(ref _prefixHashes, deepest + 1);
        }

        ulong[] parents = Hashes(0, 1);
        parents[0] = 0;
        for (int d = 1; d <= deepest; d++)
        {
            ulong[] hashes = Hashes(d, counts[d]);
            int[] parentOf = parentsOf[d];
            for (int g = 0; g < counts[d]; g++)
            {
                hashes[g] = Mix(parents[parentOf[g]], CardinalitySlot.Hash(_sliceKeys.Component(d, g)));
            }

            Add(Counts(_hourly[d], _hourGrowth[d]), hashes, counts[d]);
            Add(Counts(_whole[d], _objectGrowth[d]), hashes, counts[d]);
            parents = hashes;
        }

        // The hashes of depth d's groups, in an array at least as long as they are many.
        ulong[] Hashes(int d, int count)
        {
            if (_prefixHashes[d] is null || _prefixHashes[d].Length < count)
            {
                _prefixHashes[d] = new ulong[Math.Max(count, 64)];
            }

            return _prefixHashes[d];
        }
    }

    /// <summary>The growth that counts the distinct rows of <paramref name="table"/>, while it is still kept.</summary>
    private static Growth? Counts(Candidate? table, Growth? growth) => table is { Dead: false } ? growth : null;

    private static void Add(Growth? growth, ulong[] hashes, int count)
    {
        if (growth is null)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            growth.Sketch.Add(hashes[i]);
        }
    }

    /// <summary>Notes what the growth of each table of <paramref name="tables"/> still kept holds after <paramref name="rows"/> rows.</summary>
    private static void Note(Candidate?[] tables, Growth?[] growths, long rows)
    {
        for (int i = 0; i < growths.Length; i++)
        {
            Counts(tables[i], growths[i])?.Note(rows);
        }
    }

    /// <summary>Two hashes mixed into one, as the finalizer of MurmurHash3 mixes a word.</summary>
    private static ulong Mix(ulong parent, ulong own)
    {
        ulong h = (parent * 0x9E3779B97F4A7C15UL) ^ own;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    /// <summary>Whether a value count of slot <paramref name="slot"/> is still kept, the hour's or the slices'.</summary>
    private bool CountsValues(int slot) => _values[slot] is { Dead: false } || _sliceValues[slot] is { Dead: false };

    /// <summary>
    /// The value counts of the slice starting at <paramref name="start"/> for counted column <paramref name="slot"/>: its
    /// rows grouped by their codes, a value's code its rank, a null's coded last and left out of a path's counts, as
    /// rows of the slices' table and a run of the hour's.
    /// </summary>
    private async ValueTask ValuesAsync(
        int slot, long start, CanonicalArena arena, int root, int rows, int column, KeyShape shape, int[] valueCodes, int distinct,
        CancellationToken cancellationToken)
    {
        // A null codes after every value: a group of its own, which a path's counts leave out.
        bool path = slot >= _keys.Length;
        int groups = path ? distinct + 1 : distinct;
        Accumulators states = Fold(arena, root, rows, valueCodes, groups);
        int[] first = new int[groups];
        first.AsSpan().Fill(-1);
        for (int row = 0; row < rows; row++)
        {
            if (first[valueCodes[row]] < 0)
            {
                first[valueCodes[row]] = row;
            }
        }

        await EmitValuesAsync(slot, start, arena, column, shape, states, first, path ? distinct : groups, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The value counts of the slice starting at <paramref name="start"/> for counted column <paramref name="slot"/>, the
    /// states of its <paramref name="kept"/> first values in their order and the row that holds each, -1 for a value the
    /// slice does not hold: rows of the slices' table, and a run of the hour's.
    /// </summary>
    private async ValueTask EmitValuesAsync(
        int slot, long start, CanonicalArena arena, int column, KeyShape shape, Accumulators states, int[] first, int kept,
        CancellationToken cancellationToken)
    {
        bool hourly = _values[slot] is { Dead: false };
        Growth? growth = Counts(_values[slot], _valueGrowth[slot]);
        _run.Clear();
        for (int g = 0; g < kept; g++)
        {
            if (first[g] < 0)
            {
                continue;
            }

            _key.Clear();
            KeyCodec.Append(_key, arena, column, first[g], shape);
            states.Words(g, _state);
            if (hourly)
            {
                growth?.Sketch.Add(CardinalitySlot.Hash(_key.Span));
                _run.Bytes(_key.Span);
                _run.Words(_state);
            }

            if (_sliceValues[slot] is { Dead: false } slices && Hold(slices, start, _key.Span, _state))
            {
                await FlushAsync(slices, cancellationToken).ConfigureAwait(false);
            }
        }

        if (hourly)
        {
            _valueRuns[slot].Add(await AppendAsync(HourStore(), 1, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// The object's tables, once every slice is counted: the last hour merged, then the object's hours when it spans
    /// more than one; each table still kept with the place its chunks are held under in the writer, which chooses.
    /// </summary>
    internal async ValueTask<List<(SynopsisTableInfo Table, int Held)>> FinishAsync(CancellationToken cancellationToken = default)
    {
        List<(SynopsisTableInfo, int)> tables = [];
        if (!_settled)
        {
            return tables;
        }

        if (_hour != long.MinValue)
        {
            await CloseHourAsync(cancellationToken).ConfigureAwait(false);
            _hour = long.MinValue;
        }

        // The object's tables, when its hours are several: one table of each depth over them all. An object of one hour
        // has none, as its coarse tables of the hour say the same.
        _finishing = true;
        if (_hours > 1 && _wholeDepth >= 0 && _wholeRuns.Count > 0)
        {
            await MergeAsync(_wholeScratch!, _wholeRuns, _wholeDepth, _whole, 0, wholeRuns: false, cancellationToken).ConfigureAwait(false);
        }
        else if (_hours <= 1)
        {
            foreach (Candidate? table in _whole)
            {
                if (table is not null)
                {
                    table.Dead = true;
                    table.Held = -1;
                    table.Stage.Release();
                }
            }
        }

        // Every table with rows, and every one given up, which the choice still weighs as a grain asked.
        foreach (Candidate table in _candidates)
        {
            await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            if (table.Held >= 0)
            {
                tables.Add((new SynopsisTableInfo(table.Kind, table.Bucket, table.Keys, table.Measures, checked((int)table.Rows)), table.Held));
            }
        }

        return tables;
    }

    /// <summary>
    /// Settles what the first slice says of every other: the keys, the measures with the numeric paths read, the counted
    /// paths, and the tables the object may keep, in the order the choice weighs them.
    /// </summary>
    private void Settle(List<int> keys, List<int[]> keyCodes, int rows, IReadOnlyList<int>? counted)
    {
        _settled = true;

        // A number the order places is a key, and a measure too of the tables it is no key of when its values repeat less
        // than twice in the slice, which only a slice too small to judge its columns leaves in the order (SortedSlices),
        // where a status code repeats and is a key alone.
        bool Measured(int entry)
        {
            int k = keys.IndexOf(entry);
            if (k < 0)
            {
                return true;
            }

            int distinct = 0;
            foreach (int code in keyCodes[k].AsSpan(0, rows))
            {
                distinct = Math.Max(distinct, code + 1);
            }

            return distinct * 2L > rows;
        }

        // The numeric paths whose values are not counted are measures, summed as the numbers of the schema are.
        _counted = [.. counted ?? []];
        List<int> measures = [.. _measures.Where(Measured)];
        for (int p = 0; p < _paths.Count; p++)
        {
            if (_paths[p].Class is Types.Variants.VariantClass.Integer or Types.Variants.VariantClass.Double
                && !Contains(_counted, SynopsisPath.Entry(p))
                && p < _pathColumns.Count && _pathColumns[p] >= 0
                && Measured(SynopsisPath.Entry(p)))
            {
                measures.Add(SynopsisPath.Entry(p));
            }
        }

        (_measures, _kinds) = KindsOf([.. measures]);
        _words = StateWords.Count(_kinds);
        _state = new long[_words];
        _keys = [.. keys];
        int depth = _keys.Length;
        _first = _measures.Length > 0 ? 0 : 1;
        _keyShapes = new KeyShape[depth];
        for (int k = 0; k < depth; k++)
        {
            _keyShapes[k] = KeyShape.Of(KeyType(_keys[k]));
        }

        _runs = new Candidate?[depth + 1];
        _hourly = new Candidate?[depth + 1];
        _whole = new Candidate?[depth + 1];
        _values = new Candidate?[depth + _counted.Length];
        _sliceValues = new Candidate?[depth + _counted.Length];
        _valueRuns = new List<RunSpan>[depth + _counted.Length];
        for (int v = 0; v < _valueRuns.Length; v++)
        {
            _valueRuns[v] = [];
        }

        _hourSum = new long[depth + 1];
        _valueSum = new long[depth + _counted.Length];
        for (int c = 0; c < _counted.Length; c++)
        {
            _values[depth + c] = Add(SynopsisTableKind.Values, SynopsisBucket.Hour, [_counted[c]], [KeyShape.Of(KeyType(_counted[c]))]);
        }

        for (int d = _first; d <= depth; d++)
        {
            _runs[d] = Add(SynopsisTableKind.Runs, SynopsisBucket.Slice, _keys[..d], _keyShapes[..d]);
        }

        for (int d = depth; d >= _first; d--)
        {
            _hourly[d] = Add(SynopsisTableKind.Coarse, SynopsisBucket.Hour, _keys[..d], _keyShapes[..d]);
        }

        for (int k = 1; k < depth; k++)
        {
            _values[k] = Add(SynopsisTableKind.Values, SynopsisBucket.Hour, [_keys[k]], [_keyShapes[k]]);
        }

        // Each value count of a slice too, where an hour holds several: a question of one column past the first of the
        // order, at the grain of a slice or over a window that cuts hours, reads its values rather than the runs of
        // every key before it, nor the rows of a column no run holds. Where a slice is its hour, the table would hold
        // the hour's rows again, a grain more for the choice to weigh them by.
        for (int v = 0; v < _values.Length && _cutsHours; v++)
        {
            if (_values[v] is { } hour)
            {
                _sliceValues[v] = Add(SynopsisTableKind.Values, SynopsisBucket.Slice, hour.Keys, hour.Shapes);
            }
        }

        if (_spansHours)
        {
            for (int d = depth; d >= _first; d--)
            {
                _whole[d] = Add(SynopsisTableKind.Coarse, SynopsisBucket.Object, _keys[..d], _keyShapes[..d]);
            }
        }

        // The distinct groups and values of each coarser table with keys, counted for an object whose rows weigh its
        // tables before the end.
        _hourGrowth = new Growth?[depth + 1];
        _objectGrowth = new Growth?[depth + 1];
        _valueGrowth = new Growth?[_values.Length];
        for (int d = 1; d <= depth && _objectRows > 0; d++)
        {
            _hourGrowth[d] = _hourly[d] is null ? null : new Growth();
            _objectGrowth[d] = _whole[d] is null ? null : new Growth();
        }

        for (int v = 0; v < _values.Length && _objectRows > 0; v++)
        {
            _valueGrowth[v] = _values[v] is null ? null : new Growth();
        }
    }

    /// <summary>
    /// A table the object may keep, added to those the choice weighs: its measures every measure but its keys, whose
    /// states would only repeat them, and the words of a record that hold the count and those measures' states.
    /// </summary>
    private Candidate Add(SynopsisTableKind kind, SynopsisBucket bucket, int[] keys, KeyShape[] shapes)
    {
        List<int> measures = [];
        List<int> words = [0];
        int at = 1;
        for (int m = 0; m < _measures.Length; m++)
        {
            int width = _kinds[m] == MeasureKind.Float ? 8 : 5;
            if (!Contains(keys, _measures[m]))
            {
                measures.Add(_measures[m]);
                for (int w = 0; w < width; w++)
                {
                    words.Add(at + w);
                }
            }

            at += width;
        }

        int[] kept = [.. measures];
        DType rowType = new SynopsisTableInfo(kind, bucket, keys, kept, 0).RowType(_schema, _paths, _types);
        Candidate table = new Candidate(kind, bucket, keys, kept, shapes, rowType, new ChunkStage(shapes, [.. words]));
        _candidates.Add(table);
        return table;
    }

    /// <summary>Starts counting the hour <paramref name="hour"/>, its runs at the deepest depth a coarser table still asks for.</summary>
    private void OpenHour(long hour)
    {
        _hour = hour;
        _hourDepth = DeepestCoarse();
        _wholeDepth = Deepest(_whole);
    }

    /// <summary>
    /// Ends the hour: the chunks of its slices' tables, then its runs merged into every depth of its coarse tables, its
    /// value counts, and, when the object's tables are kept, its deepest groups for the object's merge.
    /// </summary>
    private async ValueTask CloseHourAsync(CancellationToken cancellationToken)
    {
        await FlushHourAsync(_runs, cancellationToken).ConfigureAwait(false);
        await FlushHourAsync(_sliceValues, cancellationToken).ConfigureAwait(false);
        _hourDepth = Math.Min(_hourDepth, DeepestCoarse());
        _wholeDepth = Math.Min(_wholeDepth, Deepest(_whole));
        if (_hourDepth >= 0 && _hourRuns.Count > 0)
        {
            long[] groups = await MergeAsync(_hourScratch!, _hourRuns, _hourDepth, _hourly, _hour, wholeRuns: _wholeDepth >= 0, cancellationToken).ConfigureAwait(false);
            for (int d = 0; d < groups.Length; d++)
            {
                _hourSum[d] += groups[d];
            }
        }

        for (int v = 0; v < _values.Length; v++)
        {
            if (_values[v] is { Dead: false } table && _valueRuns[v].Count > 0)
            {
                using RunMerger merger = await RunMerger.OpenAsync(_hourScratch!, _valueRuns[v], table.Shapes, 1, _kinds, cancellationToken).ConfigureAwait(false);
                while (await merger.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    _valueSum[v]++;
                    if (Hold(table, _hour, merger.Key, merger.Words))
                    {
                        await FlushAsync(table, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            _valueRuns[v].Clear();
        }

        await FlushHourAsync(_hourly, cancellationToken).ConfigureAwait(false);
        await FlushHourAsync(_values, cancellationToken).ConfigureAwait(false);
        _hourRuns.Clear();
        _hourScratch?.Dispose();
        _hourScratch = null;
        _hours++;
        _rowsClosed += _hourRowsSeen;
        _hourRowsSeen = 0;
        foreach (Growth? growth in _hourGrowth)
        {
            growth?.Clear();
        }

        foreach (Growth? growth in _valueGrowth)
        {
            growth?.Clear();
        }

        Prune();
    }

    /// <summary>
    /// Merges <paramref name="runs"/> of <paramref name="scratch"/> at depth <paramref name="depth"/> into each depth of
    /// <paramref name="into"/>, its rows in bucket <paramref name="bucket"/>: a group of a depth ends where its prefix of
    /// the keys changes. With <paramref name="wholeRuns"/>, the groups at the object's depth go on as a run of its store.
    /// </summary>
    /// <returns>The groups of each depth, whether its table is kept or not.</returns>
    private async ValueTask<long[]> MergeAsync(
        RunScratch scratch, List<RunSpan> runs, int depth, Candidate?[] into, long bucket, bool wholeRuns, CancellationToken cancellationToken)
    {
        long[] emitted = new long[depth + 1];
        long[][] groups = new long[depth + 1][];
        for (int d = _first; d <= depth; d++)
        {
            groups[d] = new long[_words];
            StateWords.Clear(groups[d], _kinds);
        }

        long runStart = -1;
        long runLength = 0;
        _run.Clear();
        bool any = false;
        if (_starts.Length <= depth)
        {
            Array.Resize(ref _starts, depth + 1);
        }

        using (RunMerger merger = await RunMerger.OpenAsync(scratch, runs, _keyShapes, depth, _kinds, cancellationToken).ConfigureAwait(false))
        {
            while (await merger.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                // The groups the key leaves end, and those it enters start with it: a group's key is its first's, as a
                // slice's group's is its first row's.
                int from = _first;
                if (any)
                {
                    KeyCodec.Compare(_keyShapes, depth, _key.Span, merger.Key, out int differs);
                    for (int d = depth; d >= Math.Max(_first, differs + 1); d--)
                    {
                        await EmitAsync(d).ConfigureAwait(false);
                    }

                    from = Math.Max(_first, differs + 1);
                }

                for (int d = from; d <= depth; d++)
                {
                    RunBuffer start = _starts[d] ??= new RunBuffer();
                    start.Clear();
                    start.Bytes(merger.Key[..KeyCodec.Prefix(_keyShapes, d, merger.Key)]);
                }

                // A record's states go to its deepest group, and a group's to its parent's as it ends: a merge a record,
                // where one into every depth took seven of them for the design's keys. The sums are exact, so the states
                // are those of every record merged into every depth.
                StateWords.Merge(groups[depth], merger.Words, _kinds);
                _key.Clear();
                _key.Bytes(merger.Key);
                any = true;
            }
        }

        if (any)
        {
            for (int d = depth; d >= _first; d--)
            {
                await EmitAsync(d).ConfigureAwait(false);
            }
        }

        if (wholeRuns)
        {
            await FlushRunAsync().ConfigureAwait(false);
            if (runLength > 0)
            {
                _wholeRuns.Add(new RunSpan(runStart, runLength, _wholeDepth));
            }
        }

        return emitted;

        // The group of depth d that the next key leaves: the prefix of its first key, its states.
        async ValueTask EmitAsync(int d)
        {
            emitted[d]++;
            ReadOnlySpan<byte> key = _starts[d]!.Span;
            if (into[d] is { Dead: false } table && Hold(table, bucket, key, groups[d]))
            {
                await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            }

            if (wholeRuns && d == _wholeDepth)
            {
                _run.Bytes(_starts[d]!.Span);
                _run.Words(groups[d]);
                if (_run.Length >= RunFlushBytes)
                {
                    await FlushRunAsync().ConfigureAwait(false);
                }
            }

            if (d > _first)
            {
                StateWords.Merge(groups[d - 1], groups[d], _kinds);
            }

            StateWords.Clear(groups[d], _kinds);
        }

        // The object's run so far, appended to its store, where it lies after its earlier bytes.
        async ValueTask FlushRunAsync()
        {
            if (_run.Length == 0)
            {
                return;
            }

            _wholeScratch ??= NewStore();
            long at = await _wholeScratch.AppendAsync(_run.Memory, cancellationToken).ConfigureAwait(false);
            runStart = runStart < 0 ? at : runStart;
            runLength += _run.Length;
            _run.Clear();
        }
    }

    /// <summary>At an hour's end, hands its rows to the writer as chunks of their own for those of <paramref name="tables"/> that hold enough.</summary>
    private async ValueTask FlushHourAsync(Candidate?[] tables, CancellationToken cancellationToken)
    {
        foreach (Candidate? table in tables)
        {
            if (table is not null && table.Stage.RawBytes >= _chunking.HourBytes)
            {
                await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Holds a row of <paramref name="table"/>; whether its stage is full, for the caller to flush.</summary>
    private bool Hold(Candidate table, long bucket, ReadOnlySpan<byte> key, ReadOnlySpan<long> words)
    {
        if (table.Dead)
        {
            return false;
        }

        table.Stage.Append(bucket, key, words);
        return table.Stage.Rows >= (table.Held < 0 ? Math.Max(_chunking.Rows / (ChunkRows / ProbeRows), 1) : _chunking.Rows);
    }

    /// <summary>
    /// Hands the rows <paramref name="table"/>'s stage holds to the writer as a chunk. Before the end, a table whose bytes
    /// already take more than the budget of the whole object is given up, and so is one whose bytes so far, scaled to the
    /// object's rows from those they cover, take <see cref="ProjectionMargin"/> times more.
    /// </summary>
    private async ValueTask FlushAsync(Candidate table, CancellationToken cancellationToken)
    {
        if (table.Dead || table.Stage.Rows == 0)
        {
            return;
        }

        CanonicalArena arena = _context.Canonical;
        try
        {
            int rows = table.Stage.Rows;
            int node = table.Stage.Build(arena, table.RowType, _types);
            if (table.Held < 0)
            {
                table.Held = _sink.OpenTable();
            }

            long bytes = await _sink.SpoolChunkAsync(table.Held, arena, node, table.Stage.FirstBucket, table.Stage.LastBucket, cancellationToken).ConfigureAwait(false);
            table.Rows += rows;
            table.Bytes += bytes;
        }
        finally
        {
            table.Stage.Clear();
            arena.Reset();
        }

        if (_finishing || ObjectBudget() is not { } whole)
        {
            return;
        }

        double projected = table.Bytes * (double)_objectRows / Math.Max(_rowsCounted, 1);
        if (table.Bytes > whole || projected > ProjectionMargin * whole)
        {
            Drop(table, FormattableString.Invariant(
                $"given up as it grew: {table.Bytes} bytes over {_rowsCounted} rows, {projected:F0} over the object's {_objectRows}, against a budget of {whole:F0}"));
        }
    }

    /// <summary>
    /// The budget of the whole object, that of the data so far scaled to the object's rows from those it holds; null while
    /// it cannot be said, its rows unknown or its data too little.
    /// </summary>
    private double? ObjectBudget()
    {
        long budget = _sink.BudgetSoFar;
        long rows = _sink.RowsSoFar;
        return budget == long.MaxValue || _objectRows <= 0 || rows <= 0 ? null : budget * (double)_objectRows / rows;
    }

    /// <summary>
    /// Gives a table up, for the reason <paramref name="verdict"/>: it is not written, its rows are no longer held, and the
    /// choice still weighs it, as a grain asked.
    /// </summary>
    private void Drop(Candidate table, string verdict)
    {
        if (table.Dead)
        {
            return;
        }

        table.Dead = true;
        table.Stage.Release();
        if (table.Held < 0)
        {
            table.Held = _sink.OpenTable();
        }

        _sink.DropTable(table.Held, verdict);
    }

    /// <summary>
    /// Gives up the tables the rows counted rule out against the budget of the whole object, the budget of the data so
    /// far scaled to the object's rows: those whose rows so far already take more, and those whose projection takes
    /// <see cref="ProjectionMargin"/> times more. A table of slices holds the rows its slices gave, scaled to the
    /// object's rows; a coarse table of an hour, or a value count, the groups of the hours merged and the hour's
    /// projected to its rows, scaled likewise; one of the object, its distinct groups projected to the object's rows.
    /// A row takes the bytes a row of its table took, or the fewest a row of any table took.
    /// </summary>
    private void Prune()
    {
        if (ObjectBudget() is not { } whole)
        {
            return;
        }

        double perRow = double.MaxValue;
        foreach (Candidate table in _candidates)
        {
            if (table.Rows > 0)
            {
                perRow = Math.Min(perRow, table.Bytes / (double)table.Rows);
            }
        }

        if (perRow == double.MaxValue)
        {
            return;
        }

        // A table of slices holds the rows its slices gave, scaled to the object's rows, each at the bytes a row of it took.
        foreach (Candidate?[] sliced in (ReadOnlySpan<Candidate?[]>)[_runs, _sliceValues])
        {
            foreach (Candidate? table in sliced)
            {
                if (table is { Dead: false } && table.Rows + table.Stage.Rows > 0)
                {
                    double seen = table.Rows + table.Stage.Rows;
                    RuleOut(table, seen, seen * _objectRows / Math.Max(_rowsCounted, 1), table.Rows > 0 ? table.Bytes / (double)table.Rows : perRow);
                }
            }
        }

        long hourRows = _hourRowsSeen > 0 ? _hourRows.GetValueOrDefault(_hour, _hourRowsSeen) : 0;
        double scale = (double)_objectRows / Math.Max(_rowsClosed + hourRows, 1);
        for (int d = 1; d < _hourGrowth.Length; d++)
        {
            if (Counts(_hourly[d], _hourGrowth[d]) is { } hour)
            {
                RuleOut(_hourly[d]!, _hourSum[d] + hour.Distinct, (_hourSum[d] + hour.Projected(hourRows)) * scale, perRow);
            }

            if (Counts(_whole[d], _objectGrowth[d]) is { } all)
            {
                RuleOut(_whole[d]!, all.Distinct, all.Projected(_objectRows), perRow);
            }
        }

        for (int v = 0; v < _valueGrowth.Length; v++)
        {
            if (Counts(_values[v], _valueGrowth[v]) is { } values)
            {
                RuleOut(_values[v]!, _valueSum[v] + values.Distinct, (_valueSum[v] + values.Projected(hourRows)) * scale, perRow);
            }
        }

        _hourDepth = Math.Min(_hourDepth, DeepestCoarse());
        _wholeDepth = Math.Min(_wholeDepth, Deepest(_whole));

        // Gives the table up when its rows seen so far, or projected, take more than the object's budget allows.
        void RuleOut(Candidate table, double seen, double projected, double rowBytes)
        {
            if (seen * rowBytes > whole || projected * rowBytes > ProjectionMargin * whole)
            {
                Drop(table, FormattableString.Invariant(
                    $"given up as its rows were counted: {seen:F0} rows so far, {projected:F0} projected, at {rowBytes:F1} bytes a row against a budget of {whole:F0}"));
            }
        }
    }

    /// <summary>The deepest depth of a coarse table of the hour or of the object still kept; -1 for none.</summary>
    private int DeepestCoarse() => Math.Max(Deepest(_hourly), Deepest(_whole));

    private static int Deepest(Candidate?[] tables)
    {
        for (int d = tables.Length - 1; d >= 0; d--)
        {
            if (tables[d] is { Dead: false })
            {
                return d;
            }
        }

        return -1;
    }

    /// <summary>The run of records <see cref="_run"/> holds, appended to <paramref name="store"/>, its keys of <paramref name="components"/> components.</summary>
    private async ValueTask<RunSpan> AppendAsync(RunScratch store, int components, CancellationToken cancellationToken)
    {
        long at = await store.AppendAsync(_run.Memory, cancellationToken).ConfigureAwait(false);
        return new RunSpan(at, _run.Length, components);
    }

    private RunScratch HourStore() => _hourScratch ??= NewStore();

    /// <summary>A store for runs, as the writer's scratch is, at half its memory: the hour's and the object's may both be open.</summary>
    private RunScratch NewStore()
    {
        (long memory, string? directory, bool seal) = _sink.Scratch;
        return new RunScratch(memory / 2, directory, seal);
    }

    public void Dispose()
    {
        _hourScratch?.Dispose();
        _wholeScratch?.Dispose();
        _run.Dispose();
        _key.Dispose();
        _sliceKeys.Dispose();
        foreach (RunBuffer? start in _starts)
        {
            start?.Dispose();
        }

        _context.Dispose();
    }

    /// <summary>Makes room for the groups of <paramref name="rows"/> rows at depths 0 to <paramref name="depth"/>.</summary>
    private void Grow(int depth, int rows)
    {
        if (_groups.Length <= depth)
        {
            Array.Resize(ref _groups, depth + 1);
        }

        for (int d = 0; d <= depth; d++)
        {
            if (_groups[d] is null || _groups[d].Length < rows)
            {
                _groups[d] = new int[rows];
            }
        }
    }

    /// <summary>
    /// Each depth's groups down to <paramref name="used"/>, numbered in the order of their first rows, from the keys' codes
    /// <paramref name="keyCodes"/> in the sorted order: a pass a depth, the groups of the first <paramref name="leading"/>
    /// depths runs of the rows, those past them pairs of a group and a code anywhere in the slice. Their counts, their
    /// first rows and each one's parent at the depth above go to <paramref name="counts"/>, <paramref name="firsts"/> and
    /// <paramref name="parents"/>, and each row's group at each depth to the groups' arrays.
    /// </summary>
    private void Grouped(List<int[]> keyCodes, int leading, int rows, int used, int[] counts, int[][] firsts, int[][] parents)
    {
        Grow(used, rows);
        _groups[0].AsSpan(0, rows).Clear();
        counts[0] = 1;
        for (int d = 1; d <= used; d++)
        {
            counts[d] = d <= leading
                ? Runs(keyCodes[d - 1], _groups[d - 1], _groups[d], rows)
                : Pairs(keyCodes[d - 1], _groups[d - 1], _groups[d], rows, _pairs);
        }

        for (int d = 0; d <= used; d++)
        {
            firsts[d] = Firsts(_groups[d], counts[d], rows);
            if (d > 0)
            {
                int[] parent = new int[counts[d]];
                for (int g = 0; g < parent.Length; g++)
                {
                    parent[g] = _groups[d - 1][firsts[d][g]];
                }

                parents[d] = parent;
            }
        }

    }

    /// <summary>
    /// <see cref="Grouped"/> read off the words <paramref name="packed"/> the rows were sorted by, the first keys' codes in
    /// their highest bits, where every depth counted is a run of the rows: a row starts a group of every depth from the
    /// first whose key's bits differ from the row before's. One pass over the words, where the codes took a pass a depth and
    /// the first rows another; each group's parent is the last group of the depth above to start at or before it.
    /// </summary>
    private void Boundaries(PackedKeys packed, int rows, int used, int[] counts, int[][] firsts, int[][] parents)
    {
        // The depth a change of each bit starts groups at: a key's bits its own depth's.
        Span<byte> depthOf = stackalloc byte[64];
        depthOf.Clear();
        for (int k = 0; k < used; k++)
        {
            for (int bit = packed.Shifts[k]; bit < packed.Shifts[k] + packed.Widths[k]; bit++)
            {
                depthOf[bit] = (byte)(k + 1);
            }
        }

        ulong mask = used == 0 ? 0 : ulong.MaxValue << packed.Shifts[used - 1];
        if (_bounds.Length <= used)
        {
            int had = _bounds.Length;
            Array.Resize(ref _bounds, used + 1);
            Array.Resize(ref _parentsOf, used + 1);
            for (int d = had; d <= used; d++)
            {
                _bounds[d] = new int[256];
                _parentsOf[d] = new int[256];
            }
        }

        for (int d = 0; d <= used; d++)
        {
            _bounds[d][0] = 0;
            counts[d] = 1;
        }

        ulong[] words = packed.Words;
        for (int row = 1; row < rows; row++)
        {
            ulong changed = (words[row] ^ words[row - 1]) & mask;
            if (changed != 0)
            {
                for (int d = depthOf[63 - BitOperations.LeadingZeroCount(changed)]; d <= used; d++)
                {
                    if (counts[d] == _bounds[d].Length)
                    {
                        Array.Resize(ref _bounds[d], _bounds[d].Length * 2);
                    }

                    _bounds[d][counts[d]++] = row;
                }
            }
        }

        for (int d = 0; d <= used; d++)
        {
            firsts[d] = _bounds[d];
            if (d == 0)
            {
                continue;
            }

            if (_parentsOf[d].Length < counts[d])
            {
                _parentsOf[d] = new int[Math.Max(counts[d], _parentsOf[d].Length * 2)];
            }

            int[] parent = _parentsOf[d];
            int[] above = _bounds[d - 1];
            int at = 0;
            for (int g = 0; g < counts[d]; g++)
            {
                while (at + 1 < counts[d - 1] && above[at + 1] <= _bounds[d][g])
                {
                    at++;
                }

                parent[g] = at;
            }

            parents[d] = parent;
        }

    }

    // What Boundaries reads the groups into, kept from one slice to the next: each depth's first rows and parents.
    private int[][] _bounds = [];
    private int[][] _parentsOf = [];

    /// <summary>Refines the groups of rows sorted by their keys: a group starts where the <paramref name="parent"/> group or the <paramref name="column"/> code changes.</summary>
    private static int Runs(int[] column, int[] parent, int[] groups, int rows)
    {
        int group = 0;
        groups[0] = 0;
        for (int row = 1; row < rows; row++)
        {
            group += parent[row] != parent[row - 1] || column[row] != column[row - 1] ? 1 : 0;
            groups[row] = group;
        }

        return group + 1;
    }

    /// <summary>Refines the groups by each pair of a <paramref name="parent"/> group and a <paramref name="column"/> code, the rows of a pair anywhere in the slice.</summary>
    private static int Pairs(int[] column, int[] parent, int[] groups, int rows, Dictionary<long, int> pairs)
    {
        pairs.Clear();
        for (int row = 0; row < rows; row++)
        {
            long pair = ((long)parent[row] << 32) | (uint)column[row];
            if (!pairs.TryGetValue(pair, out int group))
            {
                pairs[pair] = group = pairs.Count;
            }

            groups[row] = group;
        }

        return pairs.Count;
    }

    /// <summary>The first row of each of <paramref name="count"/> groups, numbered in the order of their first rows.</summary>
    private static int[] Firsts(int[] groups, int count, int rows)
    {
        int[] firsts = new int[count];
        int next = 0;
        for (int row = 0; row < rows && next < count; row++)
        {
            if (groups[row] == next)
            {
                firsts[next++] = row;
            }
        }

        return firsts;
    }

    /// <summary>Each group's place in the order of the codes of its first row, key by key down to <paramref name="depth"/>.</summary>
    private static int[] Places(int[] firsts, List<int[]> codes, int depth)
    {
        int[] order = new int[firsts.Length];
        for (int g = 0; g < order.Length; g++)
        {
            order[g] = g;
        }

        Array.Sort(order, (left, right) =>
        {
            for (int k = 0; k < depth; k++)
            {
                int compared = codes[k][firsts[left]].CompareTo(codes[k][firsts[right]]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return left.CompareTo(right);
        });

        int[] places = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            places[order[i]] = i;
        }

        return places;
    }

    /// <summary>
    /// States of <paramref name="groups"/> groups of no row, kept from a slice before when there are some, which go back once
    /// the slice is counted: a slice's states allocate nothing once they have grown to its groups.
    /// </summary>
    private Accumulators RentStates(int groups)
    {
        Accumulators? states = null;
        while (_spareStates.Count > 0 && states is null)
        {
            Accumulators spare = _spareStates[^1];
            _spareStates.RemoveAt(_spareStates.Count - 1);
            states = ReferenceEquals(spare.Kinds, _kinds) ? spare : null;
        }

        if (states is null)
        {
            states = new Accumulators(_kinds, groups);
        }
        else
        {
            states.Reset(groups);
        }

        _rentedStates.Add(states);
        return states;
    }

    private readonly List<Accumulators> _spareStates = [];
    private readonly List<Accumulators> _rentedStates = [];

    /// <summary>Folds every row's measures into its group's states.</summary>
    private Accumulators Fold(CanonicalArena arena, int root, int rows, int[] groups, int count)
    {
        Accumulators states = RentStates(count);
        ReadOnlySpan<int> groupOf = groups.AsSpan(0, rows);
        foreach (int group in groupOf)
        {
            states.Count[group]++;
        }

        CanonicalNode node = arena.GetNode(root);
        for (int m = 0; m < _measures.Length; m++)
        {
            int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, ColumnOf(node, _measures[m])));
            ref readonly CanonicalRecord record = ref arena.RecordRef(column);
            states.Fold(m, record.PType, record.BufferA.Span, ArenaWords.Validity(arena, column), groupOf);
        }

        return states;
    }

    /// <summary>
    /// <see cref="Fold"/> of <paramref name="count"/> groups that are runs of the rows, each starting at its row of
    /// <paramref name="starts"/>: a run's states summed in registers and stored once, where a row's went to memory and
    /// back, the same values added in the same order.
    /// </summary>
    private Accumulators FoldRuns(CanonicalArena arena, int root, int rows, int[] starts, int count)
    {
        Accumulators states = RentStates(count);
        ReadOnlySpan<int> runs = starts.AsSpan(0, count);
        for (int g = 0; g < count; g++)
        {
            states.Count[g] = (g + 1 < count ? runs[g + 1] : rows) - runs[g];
        }

        CanonicalNode node = arena.GetNode(root);
        for (int m = 0; m < _measures.Length; m++)
        {
            int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, ColumnOf(node, _measures[m])));
            ref readonly CanonicalRecord record = ref arena.RecordRef(column);
            states.FoldRuns(m, record.PType, record.BufferA.Span, ArenaWords.Validity(arena, column), runs, rows);
        }

        return states;
    }

    /// <summary>The dtype of key <paramref name="entry"/>, a field or a path, in the tables' arena.</summary>
    private DType KeyType(int entry)
    {
        if (!_keyTypes.TryGetValue(entry, out DType dtype))
        {
            _keyTypes[entry] = dtype = SynopsisPath.Names(entry)
                ? SynopsisTableInfo.ClassType(_paths[SynopsisPath.Place(entry)].Class, _types)
                : VortexTypes.ToDType(VortexTypes.FromDType(_schema.GetField(entry)), _types);
        }

        return dtype;
    }

    /// <summary>The column of a slice's struct <paramref name="node"/> a key or measure <paramref name="entry"/> reads: its field's, or its path's.</summary>
    private int ColumnOf(CanonicalNode node, int entry) =>
        SynopsisPath.Names(entry) ? _pathColumns[SynopsisPath.Place(entry)] : node.GetFieldIndex(entry);

    private static DType Storage(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        foreach (int item in list)
        {
            if (item == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The distinct groups or values of a scope, an hour or the object, as its slices come: a sketch of their hashes,
    /// and what it held after each slice, which the scope's end is projected from.
    /// </summary>
    private sealed class Growth
    {
        private readonly List<(long Rows, long Distinct)> _points = [];

        internal CardinalitySketch Sketch { get; private set; } = new CardinalitySketch();

        /// <summary>The distinct ones after the last slice noted.</summary>
        internal long Distinct => _points.Count == 0 ? 0 : _points[^1].Distinct;

        /// <summary>Notes what the sketch holds after <paramref name="rows"/> rows of the scope.</summary>
        internal void Note(long rows) => _points.Add((rows, Sketch.Estimate()));

        /// <summary>Forgets the scope, for the next one.</summary>
        internal void Clear()
        {
            Sketch = new CardinalitySketch();
            _points.Clear();
        }

        /// <summary>
        /// The distinct ones projected to <paramref name="rows"/> rows of the scope, their count taken as a power of its
        /// rows, as Heaps' law has it, through the last point and the latest at half its rows or fewer; the distinct
        /// ones seen while no point lies that far back, or they stopped growing.
        /// </summary>
        internal double Projected(long rows)
        {
            if (_points.Count == 0)
            {
                return 0;
            }

            (long lastRows, long last) = _points[^1];
            for (int i = _points.Count - 2; i >= 0; i--)
            {
                (long earlierRows, long earlier) = _points[i];
                if (earlierRows * 2 > lastRows)
                {
                    continue;
                }

                if (earlier <= 0 || last <= earlier || rows <= lastRows)
                {
                    return last;
                }

                double exponent = Math.Clamp(Math.Log((double)last / earlier) / Math.Log((double)lastRows / earlierRows), 0, 1);
                return last * Math.Pow((double)rows / lastRows, exponent);
            }

            return last;
        }
    }

    /// <summary>
    /// A table the object may keep: what it holds, its keys and their shapes, its rows' struct, the stage of its chunk
    /// being filled, and what the writer holds of it so far.
    /// </summary>
    private sealed class Candidate(
        SynopsisTableKind kind, SynopsisBucket bucket, int[] keys, int[] measures, KeyShape[] shapes, DType rowType, ChunkStage stage)
    {
        internal SynopsisTableKind Kind { get; } = kind;

        internal SynopsisBucket Bucket { get; } = bucket;

        internal int[] Keys { get; } = keys;

        /// <summary>The measures it keeps states of: every measure but its keys.</summary>
        internal int[] Measures { get; } = measures;

        internal KeyShape[] Shapes { get; } = shapes;

        internal DType RowType { get; } = rowType;

        internal ChunkStage Stage { get; } = stage;

        /// <summary>The place the writer holds its chunks under; -1 before its first.</summary>
        internal int Held { get; set; } = -1;

        internal long Rows { get; set; }

        internal long Bytes { get; set; }

        internal bool Dead { get; set; }
    }

    /// <summary>
    /// The states of every group: a count of rows, then per measure its values, its sum, its least and its
    /// greatest; a float's extremes NaN while it has seen none but NaN.
    /// </summary>
    private sealed class Accumulators
    {
        private static readonly long NoFloat = BitConverter.DoubleToInt64Bits(double.NaN);

        private readonly MeasureKind[] _kinds;
        private readonly long[][] _values;
        private readonly Int128[][] _integers;
        private readonly WideIndexedSum[][] _floats;
        private readonly long[][] _least;
        private readonly long[][] _greatest;
        private int _groups;

        internal Accumulators(MeasureKind[] kinds, int groups)
        {
            _kinds = kinds;
            _values = new long[kinds.Length][];
            _integers = new Int128[kinds.Length][];
            _floats = new WideIndexedSum[kinds.Length][];
            _least = new long[kinds.Length][];
            _greatest = new long[kinds.Length][];
            Reset(groups);
        }

        /// <summary>The measures' kinds the states are of.</summary>
        internal MeasureKind[] Kinds => _kinds;

        internal int Groups => _groups;

        /// <summary>Each group's rows, valid over the first <see cref="Groups"/>.</summary>
        internal long[] Count { get; private set; } = [];

        /// <summary>
        /// The states of <paramref name="groups"/> groups of no row, in the arrays kept from the last use, grown when they hold
        /// fewer: what a synopsis reuses slice after slice, so that a slice's states allocate nothing once they have grown.
        /// </summary>
        internal void Reset(int groups)
        {
            if (Count.Length < groups)
            {
                int capacity = Math.Max(groups, Count.Length * 2);
                Count = new long[capacity];
                for (int m = 0; m < _kinds.Length; m++)
                {
                    _values[m] = new long[capacity];
                    _least[m] = new long[capacity];
                    _greatest[m] = new long[capacity];
                    if (_kinds[m] == MeasureKind.Float)
                    {
                        _floats[m] = new WideIndexedSum[capacity];
                    }
                    else
                    {
                        _integers[m] = new Int128[capacity];
                    }
                }
            }

            _groups = groups;
            Count.AsSpan(0, groups).Clear();
            for (int m = 0; m < _kinds.Length; m++)
            {
                _values[m].AsSpan(0, groups).Clear();
                if (_kinds[m] == MeasureKind.Float)
                {
                    _floats[m].AsSpan(0, groups).Clear();
                    _least[m].AsSpan(0, groups).Fill(NoFloat);
                    _greatest[m].AsSpan(0, groups).Fill(NoFloat);
                }
                else
                {
                    _integers[m].AsSpan(0, groups).Clear();
                }
            }
        }

        /// <summary>Group <paramref name="group"/>'s states as the words of a record (<see cref="StateWords"/>).</summary>
        internal void Words(int group, Span<long> words)
        {
            words[0] = Count[group];
            int at = 1;
            for (int m = 0; m < _kinds.Length; m++)
            {
                words[at] = _values[m][group];
                if (_kinds[m] == MeasureKind.Float)
                {
                    ref readonly WideIndexedSum sum = ref _floats[m][group];
                    (words[at + 1], words[at + 2], words[at + 3], words[at + 4], words[at + 5]) = (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High);
                    words[at + 6] = _least[m][group];
                    words[at + 7] = _greatest[m][group];
                    at += 8;
                }
                else
                {
                    Int128 sum = _integers[m][group];
                    words[at + 1] = unchecked((long)(ulong)sum);
                    words[at + 2] = (long)(sum >> 64);
                    words[at + 3] = _least[m][group];
                    words[at + 4] = _greatest[m][group];
                    at += 5;
                }
            }
        }

        /// <summary>
        /// The states merged into the <paramref name="merged"/> states of <paramref name="count"/> groups, which it resets, each
        /// group's into group <paramref name="groupOf"/> of it.
        /// </summary>
        internal Accumulators Merge(ReadOnlySpan<int> groupOf, int count, Accumulators merged)
        {
            merged.Reset(count);
            for (int g = 0; g < groupOf.Length; g++)
            {
                merged.Count[groupOf[g]] += Count[g];
            }

            for (int m = 0; m < _kinds.Length; m++)
            {
                long[] values = _values[m];
                long[] least = _least[m];
                long[] greatest = _greatest[m];
                long[] intoValues = merged._values[m];
                long[] intoLeast = merged._least[m];
                long[] intoGreatest = merged._greatest[m];
                if (_kinds[m] == MeasureKind.Float)
                {
                    WideIndexedSum[] sums = _floats[m];
                    WideIndexedSum[] intoSums = merged._floats[m];
                    for (int g = 0; g < groupOf.Length; g++)
                    {
                        int into = groupOf[g];
                        intoValues[into] += values[g];
                        intoSums[into].Merge(in sums[g]);
                        double low = BitConverter.Int64BitsToDouble(least[g]);
                        double high = BitConverter.Int64BitsToDouble(greatest[g]);
                        double intoLow = BitConverter.Int64BitsToDouble(intoLeast[into]);
                        double intoHigh = BitConverter.Int64BitsToDouble(intoGreatest[into]);
                        intoLeast[into] = !double.IsNaN(low) && (double.IsNaN(intoLow) || low < intoLow) ? least[g] : intoLeast[into];
                        intoGreatest[into] = !double.IsNaN(high) && (double.IsNaN(intoHigh) || high > intoHigh) ? greatest[g] : intoGreatest[into];
                    }

                    continue;
                }

                bool unsigned = _kinds[m] == MeasureKind.Unsigned;
                Int128[] integers = _integers[m];
                Int128[] intoIntegers = merged._integers[m];
                for (int g = 0; g < groupOf.Length; g++)
                {
                    if (values[g] == 0)
                    {
                        continue;
                    }

                    int into = groupOf[g];
                    bool none = intoValues[into] == 0;
                    intoValues[into] += values[g];
                    intoIntegers[into] += integers[g];
                    intoLeast[into] = none || (unsigned ? (ulong)least[g] < (ulong)intoLeast[into] : least[g] < intoLeast[into]) ? least[g] : intoLeast[into];
                    intoGreatest[into] = none || (unsigned ? (ulong)greatest[g] > (ulong)intoGreatest[into] : greatest[g] > intoGreatest[into])
                        ? greatest[g]
                        : intoGreatest[into];
                }
            }

            return merged;
        }

        /// <summary>Folds the values of measure <paramref name="m"/>, of <paramref name="ptype"/>, each into its row's group of <paramref name="groups"/>.</summary>
        internal void Fold(int m, PType ptype, ReadOnlySpan<byte> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> groups)
        {
            switch (ptype)
            {
                case PType.I8:
                    Signed(m, MemoryMarshal.Cast<byte, sbyte>(values), valid, groups);
                    break;
                case PType.I16:
                    Signed(m, MemoryMarshal.Cast<byte, short>(values), valid, groups);
                    break;
                case PType.I32:
                    Signed(m, MemoryMarshal.Cast<byte, int>(values), valid, groups);
                    break;
                case PType.I64:
                    Signed(m, MemoryMarshal.Cast<byte, long>(values), valid, groups);
                    break;
                case PType.U8:
                    Unsigned(m, values, valid, groups);
                    break;
                case PType.U16:
                    Unsigned(m, MemoryMarshal.Cast<byte, ushort>(values), valid, groups);
                    break;
                case PType.U32:
                    Unsigned(m, MemoryMarshal.Cast<byte, uint>(values), valid, groups);
                    break;
                case PType.U64:
                    Unsigned(m, MemoryMarshal.Cast<byte, ulong>(values), valid, groups);
                    break;
                case PType.F32:
                    Float(m, MemoryMarshal.Cast<byte, float>(values), valid, groups);
                    break;
                default:
                    Float(m, MemoryMarshal.Cast<byte, double>(values), valid, groups);
                    break;
            }
        }

        /// <summary>
        /// <see cref="Fold"/> of groups that are runs of the rows, group <c>g</c>'s from row <paramref name="starts"/>[g] to the
        /// next group's start, the last's to <paramref name="rows"/>.
        /// </summary>
        internal void FoldRuns(int m, PType ptype, ReadOnlySpan<byte> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> starts, int rows)
        {
            switch (ptype)
            {
                case PType.I8:
                    SignedRuns(m, MemoryMarshal.Cast<byte, sbyte>(values), valid, starts, rows);
                    break;
                case PType.I16:
                    SignedRuns(m, MemoryMarshal.Cast<byte, short>(values), valid, starts, rows);
                    break;
                case PType.I32:
                    SignedRuns(m, MemoryMarshal.Cast<byte, int>(values), valid, starts, rows);
                    break;
                case PType.I64:
                    SignedRuns(m, MemoryMarshal.Cast<byte, long>(values), valid, starts, rows);
                    break;
                case PType.U8:
                    UnsignedRuns(m, values, valid, starts, rows);
                    break;
                case PType.U16:
                    UnsignedRuns(m, MemoryMarshal.Cast<byte, ushort>(values), valid, starts, rows);
                    break;
                case PType.U32:
                    UnsignedRuns(m, MemoryMarshal.Cast<byte, uint>(values), valid, starts, rows);
                    break;
                case PType.U64:
                    UnsignedRuns(m, MemoryMarshal.Cast<byte, ulong>(values), valid, starts, rows);
                    break;
                case PType.F32:
                    FloatRuns(m, MemoryMarshal.Cast<byte, float>(values), valid, starts, rows);
                    break;
                default:
                    FloatRuns(m, MemoryMarshal.Cast<byte, double>(values), valid, starts, rows);
                    break;
            }
        }

        private void SignedRuns<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> starts, int rows)
            where T : unmanaged, IBinaryInteger<T>
        {
            long[] count = _values[m];
            Int128[] sums = _integers[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int g = 0; g < starts.Length; g++)
            {
                int end = g + 1 < starts.Length ? starts[g + 1] : rows;
                long held = 0;
                long low = long.MaxValue;
                long high = long.MinValue;
                Int128 sum = 0;
                for (int row = starts[g]; row < end; row++)
                {
                    if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                    {
                        continue;
                    }

                    long value = long.CreateTruncating(values[row]);
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                    sum += value;
                    held++;
                }

                // A run of no value keeps the states it had, as a row's fold leaves them.
                if (held > 0)
                {
                    count[g] += held;
                    least[g] = low;
                    greatest[g] = high;
                    sums[g] += sum;
                }
            }
        }

        private void UnsignedRuns<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> starts, int rows)
            where T : unmanaged, IBinaryInteger<T>
        {
            long[] count = _values[m];
            Int128[] sums = _integers[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int g = 0; g < starts.Length; g++)
            {
                int end = g + 1 < starts.Length ? starts[g + 1] : rows;
                long held = 0;
                ulong low = ulong.MaxValue;
                ulong high = ulong.MinValue;
                Int128 sum = 0;
                for (int row = starts[g]; row < end; row++)
                {
                    if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                    {
                        continue;
                    }

                    ulong value = ulong.CreateTruncating(values[row]);
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                    sum += value;
                    held++;
                }

                if (held > 0)
                {
                    count[g] += held;
                    least[g] = (long)low;
                    greatest[g] = (long)high;
                    sums[g] += sum;
                }
            }
        }

        private void FloatRuns<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> starts, int rows)
            where T : unmanaged, IFloatingPointIeee754<T>
        {
            long[] count = _values[m];
            WideIndexedSum[] sums = _floats[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int g = 0; g < starts.Length; g++)
            {
                int end = g + 1 < starts.Length ? starts[g + 1] : rows;
                ref WideIndexedSum sum = ref sums[g];
                long held = 0;
                long lowBits = least[g];
                long highBits = greatest[g];
                double low = BitConverter.Int64BitsToDouble(lowBits);
                double high = BitConverter.Int64BitsToDouble(highBits);
                for (int row = starts[g]; row < end; row++)
                {
                    if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                    {
                        continue;
                    }

                    double value = double.CreateTruncating(values[row]);
                    held++;
                    sum.Add(value);
                    if (double.IsNaN(value))
                    {
                        continue;
                    }

                    if (double.IsNaN(low) || value < low)
                    {
                        (low, lowBits) = (value, BitConverter.DoubleToInt64Bits(value));
                    }

                    if (double.IsNaN(high) || value > high)
                    {
                        (high, highBits) = (value, BitConverter.DoubleToInt64Bits(value));
                    }
                }

                count[g] += held;
                least[g] = lowBits;
                greatest[g] = highBits;
            }
        }

        private void Signed<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> groups)
            where T : unmanaged, IBinaryInteger<T>
        {
            long[] count = _values[m];
            Int128[] sums = _integers[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int row = 0; row < groups.Length; row++)
            {
                if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                {
                    continue;
                }

                long value = long.CreateTruncating(values[row]);
                int group = groups[row];
                if (count[group]++ == 0)
                {
                    least[group] = value;
                    greatest[group] = value;
                }
                else
                {
                    least[group] = Math.Min(least[group], value);
                    greatest[group] = Math.Max(greatest[group], value);
                }

                sums[group] += value;
            }
        }

        private void Unsigned<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> groups)
            where T : unmanaged, IBinaryInteger<T>
        {
            long[] count = _values[m];
            Int128[] sums = _integers[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int row = 0; row < groups.Length; row++)
            {
                if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                {
                    continue;
                }

                ulong value = ulong.CreateTruncating(values[row]);
                int group = groups[row];
                if (count[group]++ == 0)
                {
                    least[group] = (long)value;
                    greatest[group] = (long)value;
                }
                else
                {
                    least[group] = (long)Math.Min((ulong)least[group], value);
                    greatest[group] = (long)Math.Max((ulong)greatest[group], value);
                }

                sums[group] += value;
            }
        }

        private void Float<T>(int m, ReadOnlySpan<T> values, ReadOnlySpan<ulong> valid, ReadOnlySpan<int> groups)
            where T : unmanaged, IFloatingPointIeee754<T>
        {
            long[] count = _values[m];
            WideIndexedSum[] sums = _floats[m];
            long[] least = _least[m];
            long[] greatest = _greatest[m];
            for (int row = 0; row < groups.Length; row++)
            {
                if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                {
                    continue;
                }

                double value = double.CreateTruncating(values[row]);
                int group = groups[row];
                count[group]++;
                sums[group].Add(value);
                if (double.IsNaN(value))
                {
                    continue;
                }

                double low = BitConverter.Int64BitsToDouble(least[group]);
                double high = BitConverter.Int64BitsToDouble(greatest[group]);
                least[group] = double.IsNaN(low) || value < low ? BitConverter.DoubleToInt64Bits(value) : least[group];
                greatest[group] = double.IsNaN(high) || value > high ? BitConverter.DoubleToInt64Bits(value) : greatest[group];
            }
        }
    }
}
