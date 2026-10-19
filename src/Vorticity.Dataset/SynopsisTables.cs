using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;

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
/// The coarse tables and value counts merge the deepest run statistics of every slice: their keys coded
/// across the object, sorted by bucket and codes with a counting sort a column, and the states of each
/// combination added, without a hash table.
/// </para>
/// </remarks>
internal sealed class SynopsisTables : IDisposable
{
    private static readonly long NoFloat = BitConverter.DoubleToInt64Bits(double.NaN);

    private readonly DType _schema;
    private readonly int[] _measures;
    private readonly Kind[] _kinds;
    private readonly TimeUnit _unit;
    private readonly ScanContext _context = new ScanContext([]);
    private readonly DTypeArena _types = new DTypeArena();
    private readonly Dictionary<int, DType> _keyTypes = [];
    private readonly List<Slice> _slices = [];
    private readonly Dictionary<long, int> _pairs = [];
    private int[] _keys = [];
    private DType? _keysType;

    // Each depth's group of every row of the slice counted, reused from slice to slice.
    private int[][] _groups = [];

    /// <summary>The tables of an object of <paramref name="schema"/>, its instant in <paramref name="unit"/>, the measures <paramref name="measures"/>.</summary>
    internal SynopsisTables(DType schema, TimeUnit unit, int[] measures)
    {
        _schema = schema;
        _unit = unit;
        _measures = measures;
        _kinds = new Kind[measures.Length];
        for (int m = 0; m < measures.Length; m++)
        {
            PType ptype = Storage(schema.GetField(measures[m])).PType;
            _kinds[m] = ptype.IsFloat() ? Kind.Float : ptype.IsUnsignedInteger() ? Kind.Unsigned : Kind.Signed;
        }
    }

    /// <summary>
    /// The measure columns of <paramref name="schema"/>: its top-level numbers, but the instant and the
    /// synopsis columns <paramref name="order"/> names.
    /// </summary>
    internal static int[] Measures(DType schema, int instant, IReadOnlyList<int> order)
    {
        List<int> measures = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            DType dtype = Storage(schema.GetField(field));
            if (field != instant && !Contains(order, field) && dtype.Kind == DTypeKind.Primitive && PTypeExtensions.IsDefined(dtype.PType) && dtype.PType != PType.F16)
            {
                measures.Add(field);
            }
        }

        return [.. measures];
    }

    /// <summary>
    /// Counts the slice starting at <paramref name="start"/>, its rows in <paramref name="sorted"/>, sorted by
    /// the entries <paramref name="order"/> names, whose codes in the sorted order <paramref name="codes"/>
    /// holds: the states of its groups at every depth, and the keys of its deepest groups.
    /// </summary>
    internal void AddSlice(RecordBatch sorted, long start, IReadOnlyList<int> order, IReadOnlyList<int[]> codes)
    {
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

        _keys = [.. keys];
        int depth = _keys.Length;
        leading = leading < 0 ? depth : leading;
        int rows = sorted.RowCount;
        if (rows == 0)
        {
            return;
        }

        // Each depth's groups, numbered in the order of their first rows.
        Grow(depth, rows);
        int[] counts = new int[depth + 1];
        _groups[0].AsSpan(0, rows).Clear();
        counts[0] = 1;
        for (int d = 1; d <= depth; d++)
        {
            counts[d] = d <= leading
                ? Runs(keyCodes[d - 1], _groups[d - 1], _groups[d], rows)
                : Pairs(keyCodes[d - 1], _groups[d - 1], _groups[d], rows, _pairs);
        }

        int[][] firsts = new int[depth + 1][];
        for (int d = 0; d <= depth; d++)
        {
            firsts[d] = Firsts(_groups[d], counts[d], rows);
        }

        // The measures folded into the deepest groups, then merged up a depth at a time.
        CanonicalArena arena = sorted.Arena;
        Accumulators[] states = new Accumulators[depth + 1];
        states[depth] = Fold(arena, sorted.RootIndex, rows, _groups[depth], counts[depth]);
        for (int d = depth - 1; d >= 0; d--)
        {
            int[] parent = new int[counts[d + 1]];
            for (int g = 0; g < parent.Length; g++)
            {
                parent[g] = _groups[d][firsts[d + 1][g]];
            }

            states[d] = states[d + 1].Merge(parent, counts[d]);
        }

        // Each depth's groups in the order of their keys' values, which their numbering is down to the
        // shapes; and where each finds its keys among the deepest groups'.
        int[]?[] places = new int[]?[depth + 1];
        for (int d = leading + 1; d <= depth; d++)
        {
            places[d] = Places(firsts[d], keyCodes, d);
            states[d] = states[d].Merge(places[d], counts[d]);
        }

        int[][] keyRows = new int[depth + 1][];
        for (int d = 0; d <= depth; d++)
        {
            keyRows[d] = new int[counts[d]];
            for (int g = 0; g < counts[d]; g++)
            {
                int deep = _groups[depth][firsts[d][g]];
                keyRows[d][places[d]?[g] ?? g] = places[depth]?[deep] ?? deep;
            }
        }

        _slices.Add(new Slice(start, depth == 0 ? -1 : Keys(sorted, firsts[depth], places[depth]), counts[depth], states, keyRows));
    }

    /// <summary>
    /// The object's tables: the run statistics at every depth, the coarse tables per hour, and per object
    /// when the object spans more than an hour, at every depth, and the value counts per hour of every
    /// synopsis column after the first.
    /// </summary>
    internal List<(SynopsisTableInfo Table, int Node)> Build()
    {
        List<(SynopsisTableInfo, int)> tables = [];
        int depth = _keys.Length;
        int first = _measures.Length > 0 ? 0 : 1;
        if (_slices.Count == 0 || first > depth)
        {
            return tables;
        }

        // The deepest groups of every slice, one after another: their keys, and where each slice's begin.
        int deepRows = 0;
        int[] offsets = new int[_slices.Count];
        for (int s = 0; s < _slices.Count; s++)
        {
            offsets[s] = deepRows;
            deepRows += _slices[s].Deep;
        }

        int keys = depth == 0 ? -1 : ConcatKeys(deepRows);

        // The run statistics at every depth, a slice's groups after another's.
        for (int d = first; d <= depth; d++)
        {
            List<Accumulators> parts = new List<Accumulators>(_slices.Count);
            int count = 0;
            foreach (Slice slice in _slices)
            {
                parts.Add(slice.States[d]);
                count += slice.States[d].Groups;
            }

            int[] at = new int[count];
            long[] buckets = new long[count];
            int row = 0;
            for (int s = 0; s < _slices.Count; s++)
            {
                foreach (int key in _slices[s].KeyRows[d])
                {
                    at[row] = offsets[s] + key;
                    buckets[row++] = _slices[s].Start;
                }
            }

            Accumulators states = Accumulators.Concat(_kinds, parts);
            tables.Add((Info(SynopsisTableKind.Runs, SynopsisBucket.Slice, _keys[..d], count), Table(keys, Positions(d), at, buckets, states)));
        }

        // The deepest run statistics coded across the object: each one's hour, and its keys' codes.
        List<Accumulators> deepest = new List<Accumulators>(_slices.Count);
        long[] hours = new long[deepRows];
        for (int s = 0; s < _slices.Count; s++)
        {
            deepest.Add(_slices[s].States[depth]);
            hours.AsSpan(offsets[s], _slices[s].Deep).Fill(TimeSlicer.Floor(_slices[s].Start, _unit, SliceWidth.Hour));
        }

        Accumulators all = Accumulators.Concat(_kinds, deepest);
        (int[] Codes, int Distinct) hourCodes = Dense(hours);
        List<(int[] Codes, int Distinct)> keyCodes = [];
        for (int k = 0; k < depth; k++)
        {
            keyCodes.Add(SliceSort.CodesOf(_context.Canonical, keys, k, deepRows, null));
        }

        // The coarse tables per hour, the deepest first, each depth merged from the one below it.
        int[] prefixes = new int[depth - first + 1];
        for (int i = 0; i < prefixes.Length; i++)
        {
            prefixes[i] = 1 + depth - i;
        }

        List<(int[] Firsts, Accumulators States)> hourly = Levels([hourCodes, .. keyCodes], prefixes, all);
        for (int i = 0; i < hourly.Count; i++)
        {
            (int[] at, Accumulators states) = hourly[i];
            tables.Add((Info(SynopsisTableKind.Coarse, SynopsisBucket.Hour, _keys[..(depth - i)], states.Groups), Table(keys, Positions(depth - i), at, Gather(hours, at), states)));
        }

        // From the deepest of them, each later key's value counts per hour, the first key's being the coarse
        // table of depth 1, and the object's coarse tables when it spans more than an hour.
        (int[] deepFirsts, Accumulators deepHourly) = hourly[0];
        (int[] Codes, int Distinct) hourOf = (Gather(hourCodes.Codes, deepFirsts), hourCodes.Distinct);
        for (int k = 1; k < depth; k++)
        {
            (int[] firsts, Accumulators states) = Levels([hourOf, (Gather(keyCodes[k].Codes, deepFirsts), keyCodes[k].Distinct)], [2], deepHourly)[0];
            int[] at = Gather(deepFirsts, firsts);
            tables.Add((Info(SynopsisTableKind.Values, SynopsisBucket.Hour, [_keys[k]], states.Groups), Table(keys, [k], at, Gather(hours, at), states)));
        }

        if (hourCodes.Distinct > 1)
        {
            List<(int[] Codes, int Distinct)> columns = [];
            foreach ((int[] codes, int distinct) in keyCodes)
            {
                columns.Add((Gather(codes, deepFirsts), distinct));
            }

            int[] keyPrefixes = new int[prefixes.Length];
            for (int i = 0; i < keyPrefixes.Length; i++)
            {
                keyPrefixes[i] = prefixes[i] - 1;
            }

            List<(int[] Firsts, Accumulators States)> whole = Levels(columns, keyPrefixes, deepHourly);
            for (int i = 0; i < whole.Count; i++)
            {
                (int[] firsts, Accumulators states) = whole[i];
                int[] at = Gather(deepFirsts, firsts);
                tables.Add((Info(SynopsisTableKind.Coarse, SynopsisBucket.Object, _keys[..(depth - i)], states.Groups), Table(keys, Positions(depth - i), at, new long[at.Length], states)));
            }
        }

        return tables;
    }

    /// <summary>The arena the tables' nodes lie in.</summary>
    internal CanonicalArena Arena => _context.Canonical;

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// The rows of <paramref name="source"/> grouped by each prefix of <paramref name="columns"/> that
    /// <paramref name="prefixes"/> lists, longest first, in the order the codes sort them: each group's first
    /// row of <paramref name="source"/>, and its states, each prefix's merged from the previous one's groups.
    /// </summary>
    private static List<(int[] Firsts, Accumulators States)> Levels(List<(int[] Codes, int Distinct)> columns, int[] prefixes, Accumulators source)
    {
        int rows = source.Groups;
        int[] permutation = SliceSort.Permutation(columns, rows);

        // For each row in that order, the first column whose code differs from the row before's.
        int[] differs = new int[rows];
        for (int i = 1; i < rows; i++)
        {
            int c = 0;
            while (c < columns.Count && columns[c].Codes[permutation[i]] == columns[c].Codes[permutation[i - 1]])
            {
                c++;
            }

            differs[i] = c;
        }

        List<(int[], Accumulators)> levels = [];
        Accumulators states = source;
        int[]? starts = null;
        List<int> begins = [];
        foreach (int prefix in prefixes)
        {
            // A group starts where a code of the prefix changes, among the rows, then among the groups before.
            begins.Clear();
            int[] groupOf = new int[starts?.Length ?? rows];
            for (int j = 0; j < groupOf.Length; j++)
            {
                int i = starts?[j] ?? j;
                if (i == 0 || differs[i] < prefix)
                {
                    begins.Add(i);
                }

                groupOf[starts is null ? permutation[i] : j] = begins.Count - 1;
            }

            states = states.Merge(groupOf, begins.Count);
            starts = [.. begins];
            int[] firsts = new int[starts.Length];
            for (int g = 0; g < firsts.Length; g++)
            {
                firsts[g] = permutation[starts[g]];
            }

            levels.Add((firsts, states));
        }

        return levels;
    }

    /// <summary>
    /// A table's struct node: the bucket, the keys at <paramref name="positions"/> of <paramref name="keys"/>
    /// gathered at rows <paramref name="at"/>, the count, then each measure's states.
    /// </summary>
    private int Table(int keys, int[] positions, int[] at, long[] buckets, Accumulators states)
    {
        CanonicalArena arena = _context.Canonical;
        DType longs = _types.Primitive(PType.I64, Nullability.NonNullable);
        List<string> names = ["bucket"];
        List<int> fields = [Longs(arena, longs, buckets)];
        List<DType> dtypes = [longs];
        foreach (int k in positions)
        {
            names.Add(_schema.GetFieldName(_keys[k]));
            fields.Add(CanonicalFilter.Apply(arena, arena.GetNode(keys).GetFieldIndex(k), at));
            dtypes.Add(KeyType(_keys[k]));
        }

        states.Emit(arena, longs, _measures, _schema, names, fields, dtypes);
        DType dtype = _types.Struct([.. names], [.. dtypes], Nullability.NonNullable);
        return arena.AddStruct(dtype, buckets.Length, Validity.NonNullable, [.. fields]);
    }

    /// <summary>The keys of a slice's deepest groups, gathered at their first rows in the order of their keys, copied into the tables' arena.</summary>
    private int Keys(RecordBatch sorted, int[] firsts, int[]? places)
    {
        int[] at = new int[firsts.Length];
        for (int g = 0; g < firsts.Length; g++)
        {
            at[places?[g] ?? g] = firsts[g];
        }

        CanonicalArena arena = sorted.Arena;
        CanonicalNode node = arena.GetNode(sorted.RootIndex);
        if (_keysType is null)
        {
            string[] names = new string[_keys.Length];
            DType[] dtypes = new DType[_keys.Length];
            for (int k = 0; k < _keys.Length; k++)
            {
                names[k] = _schema.GetFieldName(_keys[k]);
                dtypes[k] = KeyType(_keys[k]);
            }

            _keysType = _types.Struct(names, dtypes, Nullability.NonNullable);
        }

        int[] fields = new int[_keys.Length];
        for (int k = 0; k < _keys.Length; k++)
        {
            fields[k] = CanonicalFilter.Apply(arena, node.GetFieldIndex(_keys[k]), at);
        }

        int keys = arena.AddStruct(_keysType.Value, at.Length, Validity.NonNullable, fields);
        return _context.Canonical.CopyFrom(arena, keys);
    }

    /// <summary>The keys of every slice's deepest groups, one slice after another.</summary>
    private int ConcatKeys(int rows)
    {
        if (_slices.Count == 1)
        {
            return _slices[0].Keys;
        }

        int[] nodes = new int[_slices.Count];
        for (int s = 0; s < nodes.Length; s++)
        {
            nodes[s] = _slices[s].Keys;
        }

        return CanonicalConcat.Concat(_context.Decode, _keysType!.Value, rows, nodes);
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

    /// <summary>Folds every row's measures into its group's states.</summary>
    private Accumulators Fold(CanonicalArena arena, int root, int rows, int[] groups, int count)
    {
        Accumulators states = new Accumulators(_kinds, count);
        ReadOnlySpan<int> groupOf = groups.AsSpan(0, rows);
        foreach (int group in groupOf)
        {
            states.Count[group]++;
        }

        CanonicalNode node = arena.GetNode(root);
        for (int m = 0; m < _measures.Length; m++)
        {
            int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, node.GetFieldIndex(_measures[m])));
            ref readonly CanonicalRecord record = ref arena.RecordRef(column);
            states.Fold(m, record.PType, record.BufferA.Span, ArenaWords.Validity(arena, column), groupOf);
        }

        return states;
    }

    /// <summary>Each value's rank among the distinct ones, and how many there are.</summary>
    private static (int[] Codes, int Distinct) Dense(long[] values)
    {
        long[] distinct = [.. new SortedSet<long>(values)];
        int[] codes = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            codes[i] = Array.BinarySearch(distinct, values[i]);
        }

        return (codes, distinct.Length);
    }

    private static T[] Gather<T>(T[] values, int[] at)
    {
        T[] gathered = new T[at.Length];
        for (int i = 0; i < at.Length; i++)
        {
            gathered[i] = values[at[i]];
        }

        return gathered;
    }

    /// <summary>The positions of the first <paramref name="depth"/> keys among the object's.</summary>
    private static int[] Positions(int depth)
    {
        int[] positions = new int[depth];
        for (int k = 0; k < depth; k++)
        {
            positions[k] = k;
        }

        return positions;
    }

    private SynopsisTableInfo Info(SynopsisTableKind kind, SynopsisBucket bucket, int[] keys, int rows) => new SynopsisTableInfo(kind, bucket, keys, _measures, rows);

    /// <summary>The dtype of the object's field <paramref name="field"/>, in the tables' arena.</summary>
    private DType KeyType(int field)
    {
        if (!_keyTypes.TryGetValue(field, out DType dtype))
        {
            _keyTypes[field] = dtype = VortexTypes.ToDType(VortexTypes.FromDType(_schema.GetField(field)), _types);
        }

        return dtype;
    }

    /// <summary>A non-null column of longs.</summary>
    private static int Longs(CanonicalArena arena, DType longs, ReadOnlySpan<long> values)
    {
        VortexBuffer buffer = arena.Allocate(Math.Max(values.Length * 8, 8), 64, out Span<byte> into).Slice(0, values.Length * 8);
        MemoryMarshal.AsBytes(values).CopyTo(into);
        return arena.AddPrimitive(longs, values.Length, Validity.NonNullable, PType.I64, buffer);
    }

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
    /// A slice counted: its start, the keys of its deepest groups (a struct node of the tables' arena, -1
    /// without keys) and how many, each depth's states, and where each depth's groups find their keys.
    /// </summary>
    private sealed record Slice(long Start, int Keys, int Deep, Accumulators[] States, int[][] KeyRows);

    /// <summary>How a measure's states are kept: an exact signed or unsigned integer sum, or a float sum.</summary>
    private enum Kind : byte
    {
        Signed,
        Unsigned,
        Float,
    }

    /// <summary>
    /// The states of every group: a count of rows, then per measure its values, its sum, its least and its
    /// greatest; a float's extremes NaN while it has seen none but NaN.
    /// </summary>
    private sealed class Accumulators
    {
        private readonly Kind[] _kinds;
        private readonly long[][] _values;
        private readonly Int128[][] _integers;
        private readonly WideIndexedSum[][] _floats;
        private readonly long[][] _least;
        private readonly long[][] _greatest;

        internal Accumulators(Kind[] kinds, int groups)
        {
            _kinds = kinds;
            Count = new long[groups];
            _values = new long[kinds.Length][];
            _integers = new Int128[kinds.Length][];
            _floats = new WideIndexedSum[kinds.Length][];
            _least = new long[kinds.Length][];
            _greatest = new long[kinds.Length][];
            for (int m = 0; m < kinds.Length; m++)
            {
                _values[m] = new long[groups];
                _least[m] = new long[groups];
                _greatest[m] = new long[groups];
                if (kinds[m] == Kind.Float)
                {
                    _floats[m] = new WideIndexedSum[groups];
                    _least[m].AsSpan().Fill(NoFloat);
                    _greatest[m].AsSpan().Fill(NoFloat);
                }
                else
                {
                    _integers[m] = new Int128[groups];
                }
            }
        }

        internal int Groups => Count.Length;

        internal long[] Count { get; }

        /// <summary>The states of <paramref name="parts"/>, one after another.</summary>
        internal static Accumulators Concat(Kind[] kinds, List<Accumulators> parts)
        {
            if (parts.Count == 1)
            {
                return parts[0];
            }

            int total = 0;
            foreach (Accumulators part in parts)
            {
                total += part.Groups;
            }

            Accumulators all = new Accumulators(kinds, total);
            int at = 0;
            foreach (Accumulators part in parts)
            {
                part.Count.CopyTo(all.Count, at);
                for (int m = 0; m < kinds.Length; m++)
                {
                    part._values[m].CopyTo(all._values[m], at);
                    part._least[m].CopyTo(all._least[m], at);
                    part._greatest[m].CopyTo(all._greatest[m], at);
                    if (kinds[m] == Kind.Float)
                    {
                        part._floats[m].CopyTo(all._floats[m], at);
                    }
                    else
                    {
                        part._integers[m].CopyTo(all._integers[m], at);
                    }
                }

                at += part.Groups;
            }

            return all;
        }

        /// <summary>The states merged into <paramref name="count"/> groups, each group's into group <paramref name="groupOf"/> of it.</summary>
        internal Accumulators Merge(ReadOnlySpan<int> groupOf, int count)
        {
            Accumulators merged = new Accumulators(_kinds, count);
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
                if (_kinds[m] == Kind.Float)
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

                bool unsigned = _kinds[m] == Kind.Unsigned;
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

        /// <summary>Appends the count and each measure's state columns.</summary>
        internal void Emit(CanonicalArena arena, DType longs, int[] measures, DType schema, List<string> names, List<int> fields, List<DType> dtypes)
        {
            void Column(string name, ReadOnlySpan<long> values)
            {
                names.Add(name);
                fields.Add(Longs(arena, longs, values));
                dtypes.Add(longs);
            }

            Column("count", Count);
            for (int m = 0; m < _kinds.Length; m++)
            {
                string name = schema.GetFieldName(measures[m]);
                Column($"{name}.values", _values[m]);
                if (_kinds[m] == Kind.Float)
                {
                    long[] m0 = new long[Groups];
                    long[] m1 = new long[Groups];
                    long[] m2 = new long[Groups];
                    long[] meta = new long[Groups];
                    long[] high = new long[Groups];
                    PersistedStates.Write(_floats[m], m0, m1, m2, meta, high);
                    Column($"{name}.sum.m0", m0);
                    Column($"{name}.sum.m1", m1);
                    Column($"{name}.sum.m2", m2);
                    Column($"{name}.sum.meta", meta);
                    Column($"{name}.sum.high", high);
                }
                else
                {
                    long[] low = new long[Groups];
                    long[] high = new long[Groups];
                    PersistedStates.Write(_integers[m], low, high);
                    Column($"{name}.sum.low", low);
                    Column($"{name}.sum.high", high);
                }

                Column($"{name}.min", _least[m]);
                Column($"{name}.max", _greatest[m]);
            }
        }
    }
}
