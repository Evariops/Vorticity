using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>The values of one column of the chosen rows, by group: fetched after the pass, read as results.</summary>
internal abstract class ChosenValues
{
    /// <summary>The groups below which a fetch of some is a few of many: its values at a place of their own.</summary>
    private const int Sparse = 8;

    // The place of each group fetched, when they are a few of many; null when the group is its place.
    private Dictionary<int, int>? _places;

    /// <summary>
    /// Makes room for the values of <paramref name="groups"/>, the groups a fetch reads, each below
    /// <paramref name="most"/>: arrays their number when they are a few of many, a top-k's or a window's,
    /// found through a map of their numbers; arrays indexed by the group otherwise. The arrays are held
    /// under the result's <paramref name="memory"/> until it is delivered, the ones they replace let go. A fetch's values are the ones read until the next fetch.
    /// </summary>
    /// <param name="groups">The groups the fetch reads.</param>
    /// <param name="most">A bound on the groups' numbers.</param>
    /// <param name="memory">What the values are held under; null for none.</param>
    /// <param name="session">The session whose pool holds what the values need of it.</param>
    internal abstract void EnsureGroups(ReadOnlySpan<int> groups, int most, QueryMemory? memory, VortexSession session);

    /// <summary>Lets the arrays go, and what they held of <paramref name="memory"/>.</summary>
    internal abstract void Release(QueryMemory? memory);

    /// <summary>Leaves <paramref name="group"/> without a row: its value null, or a value type's default.</summary>
    internal abstract void Clear(int group);

    /// <summary>
    /// Reads column <paramref name="node"/> of a fetched batch whose first row is the take's row
    /// <paramref name="first"/>: the take's row <c>rows[i]</c> goes to group <c>groups[i]</c>.
    /// </summary>
    internal abstract void Read(RecordBatch batch, int node, int first, ReadOnlySpan<int> rows, ReadOnlySpan<int> groups);

    /// <summary>The place of <paramref name="group"/>'s value: the group itself, or its place among a fetch's few groups.</summary>
    protected int Place(int group) => _places is null ? group : _places[group];

    /// <summary>
    /// The places a fetch of <paramref name="groups"/>, each below <paramref name="most"/>, needs: their number when they
    /// are a few of many, each group then at the place of its first mention; <paramref name="most"/> otherwise.
    /// </summary>
    protected int Places(ReadOnlySpan<int> groups, int most)
    {
        if ((long)groups.Length * Sparse >= most)
        {
            _places = null;
            return most;
        }

        _places ??= [];
        _places.Clear();
        foreach (int group in groups)
        {
            _places.TryAdd(group, _places.Count);
        }

        return _places.Count;
    }

    /// <summary>Forgets the places of the last fetch.</summary>
    protected void ForgetPlaces() => _places = null;
}

/// <summary>The values of a column of chosen rows as <typeparamref name="T"/>, and the groups that have no row.</summary>
internal sealed class ChosenValues<T>(ChosenColumnNode<T> column) : ChosenValues
{
    private T[] _values = [];
    private bool[] _missing = [];
    private T[] _scratch = [];

    /// <summary>The value of <paramref name="group"/>'s chosen row; the default when it has none.</summary>
    internal T At(int group) => _values[Place(group)];

    /// <summary>Whether <paramref name="group"/> has no chosen row: no candidate, or no row its filter keeps.</summary>
    internal bool Missing(int group) => _missing[Place(group)];

    internal override void EnsureGroups(ReadOnlySpan<int> groups, int most, QueryMemory? memory, VortexSession session)
    {
        int length = Places(groups, most);
        if (length > _values.Length)
        {
            // Each value a fetch reads is written, or its group cleared: the arrays come from the process's
            // shelf as they are, under the query's memory, and go back to it once the result is built.
            GiveBack(memory);
            _values = QueryArrays.Rent<T>(memory, length, "fetch of the chosen rows");
            _missing = QueryArrays.Rent<bool>(memory, length, "fetch of the chosen rows");
        }
    }

    internal override void Release(QueryMemory? memory)
    {
        GiveBack(memory);
        Scratch.Return(ref _scratch);
        ForgetPlaces();
    }

    private void GiveBack(QueryMemory? memory)
    {
        if (_values.Length > 0)
        {
            QueryArrays.Return(memory, _values);
            _values = [];
        }

        if (_missing.Length > 0)
        {
            QueryArrays.Return(memory, _missing);
            _missing = [];
        }
    }

    internal override void Clear(int group)
    {
        int place = Place(group);
        _values[place] = default!;
        _missing[place] = true;
    }

    internal override void Read(RecordBatch batch, int node, int first, ReadOnlySpan<int> rows, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _scratch, batch.RowCount);
        ResultValues.Copy(batch, batch.Arena, node, column.Read.Type, column.Read.Extensions, _scratch, null);
        for (int i = 0; i < groups.Length; i++)
        {
            int place = Place(groups[i]);
            _values[place] = _scratch[rows[i] - first];
            _missing[place] = false;
        }
    }
}

/// <summary>
/// A column of chosen rows in a result: the values of the groups' rows, and for a group without one
/// a null where the column holds nulls, a value type's default where it holds none.
/// </summary>
internal sealed class ChosenResultColumn<T>(string name, VortexType type, ChosenColumnNode<T> node) : ResultColumn(name, type)
{
    private T[] _values = [];

    internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups)
    {
        ChosenValues<T> chosen = (ChosenValues<T>)outcome.ChosenOf(node);
        Scratch.Grow(ref _values, groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            _values[i] = chosen.At(groups[i]);
        }

        // Runs of values, and a null for each group without a row where the column takes one.
        int start = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            if (Type.IsNullable && chosen.Missing(groups[i]))
            {
                ElementWriter.Append(store.Leaf, new ReadOnlySpan<T>(_values, start, i - start));
                store.AppendNull();
                start = i + 1;
            }
        }

        ElementWriter.Append(store.Leaf, new ReadOnlySpan<T>(_values, start, groups.Length - start));
    }

    internal override void Release() => Scratch.Return(ref _values);
}

/// <summary>
/// The rows of a column of chosen rows that holds no single value — a list, a variant, a struct — by group: kept as the
/// rows they are, each once, in a store of the column's type, and gathered into the result's column; never made .NET
/// values, so no object is made a value or an element of a list. A fetch's rows are the ones kept until the next fetch.
/// </summary>
/// <param name="type">The type of the result's column, nullable where a group may have no row.</param>
internal sealed class ChosenRows(VortexType type) : ChosenValues
{
    private ColumnStore? _store;
    private CanonicalArena? _arena;
    private QueryMemory? _memory;

    // The row of the store each place's group chose; -1 for a group without one.
    private int[] _rows = [];

    // The rows a gather takes: of a fetched batch, or of the store.
    private int[] _picked = [];

    // The store's row that stands for a group without a row, added once a fetch; -1 until one is asked for.
    private int _missing = -1;

    // What the store holds of the result's memory.
    private long _held;

    internal override void EnsureGroups(ReadOnlySpan<int> groups, int most, QueryMemory? memory, VortexSession session)
    {
        _memory = memory;
        int length = Places(groups, most);
        if (length > _rows.Length)
        {
            int capacity = Scratch.Capacity(length, _rows.Length);
            memory?.Hold((capacity - _rows.Length) * (long)sizeof(int), "fetch of the chosen rows");
            _rows = new int[capacity];
        }

        if (_store is null)
        {
            VortexSessionOptions options = session.Options;
            _store = ColumnStores.Create(VortexTypes.ToDType(type.Nullable, new DTypeArena()), options.EnginePool, options.Extensions);
            _arena = new CanonicalArena(16, options.EnginePool);
        }

        // Each fetch keeps its own rows: the store emptied, its buffers kept, none of its rows accepted.
        _store.Truncate(0);
        _store.Commit();
        _missing = -1;
    }

    internal override void Release(QueryMemory? memory)
    {
        _store?.Release();
        _arena?.Reset();
        _store = null;
        _arena = null;
        memory?.LetGo(_held + (_rows.Length * (long)sizeof(int)));
        _held = 0;
        _rows = [];
        _picked = [];
        _memory = null;
        ForgetPlaces();
    }

    internal override void Clear(int group) => _rows[Place(group)] = -1;

    internal override void Read(RecordBatch batch, int node, int first, ReadOnlySpan<int> rows, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _picked, groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            _picked[i] = rows[i] - first;
        }

        int start = _store!.Rows;
        StoreRows.Append(_store, batch.Arena, CanonicalFilter.Apply(batch.Arena, node, _picked.AsSpan(0, groups.Length)), validated: true);
        Kept();
        for (int i = 0; i < groups.Length; i++)
        {
            _rows[Place(groups[i])] = start + i;
        }
    }

    /// <summary>
    /// Appends to <paramref name="into"/>, a store of the result's column, the rows <paramref name="groups"/> chose, in
    /// order: gathered from the store, built once a call over its buffers; a group without a row takes a null row, or the
    /// type's default where the column holds no null.
    /// </summary>
    internal void Append(ColumnStore into, ReadOnlySpan<int> groups, bool nullable)
    {
        Scratch.Grow(ref _picked, groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            int row = _rows[Place(groups[i])];
            _picked[i] = row >= 0 ? row : Missing(nullable);
        }

        _arena!.ResetKeepingBlocks();
        int root = _store!.Build(_arena, _store.Rows);
        StoreRows.Append(into, _arena, CanonicalFilter.Apply(_arena, root, _picked.AsSpan(0, groups.Length)), validated: true);
    }

    /// <summary>The store's row a group without a row takes, added on the first ask of a fetch.</summary>
    private int Missing(bool nullable)
    {
        if (_missing < 0)
        {
            if (nullable)
            {
                _store!.AppendNull();
            }
            else
            {
                _store!.AppendDefault();
            }

            Kept();
            _missing = _store.Rows - 1;
        }

        return _missing;
    }

    /// <summary>The rows appended accepted, and what the store holds reserved: its buffers, grown by doubling, at most twice its bytes.</summary>
    private void Kept()
    {
        _store!.Commit();
        long reserved = 2 * _store.CommittedBytes;
        if (reserved > _held)
        {
            _memory?.Hold(reserved - _held, "fetch of the chosen rows");
            _held = reserved;
        }
    }
}

/// <summary>A column of chosen rows that holds no single value, in a result: the groups' rows gathered from where the fetch kept them.</summary>
internal sealed class ChosenRowsColumn(string name, VortexType type, IChosenColumn node) : ResultColumn(name, type)
{
    internal override void Append(AggregationOutcome outcome, ColumnStore store, ReadOnlySpan<int> groups) =>
        ((ChosenRows)outcome.ChosenOf(node)).Append(store, groups, Type.IsNullable);
}

/// <summary>
/// The late reading of chosen rows: once a pass has kept each group's rows as positions, the rows
/// of a set of groups are sorted and taken from the source in one take, each row once whatever
/// number of choices and groups it serves, for the columns read from them alone, which decodes only
/// those rows where the encoding allows. A first row and a last that share their chunks decode
/// them once.
/// </summary>
internal static class ChosenFetch
{
    /// <summary>Fetches the columns of the chosen rows of <paramref name="groups"/> into <paramref name="outcome"/>.</summary>
    /// <param name="outcome">The aggregation whose slots hold the positions, and where the values go.</param>
    /// <param name="source">The source the pass read.</param>
    /// <param name="spec">The pass's scan, whose options the take keeps; its filter is not applied again, the rows being kept already.</param>
    /// <param name="metrics">Where the take's reads count.</param>
    /// <param name="groups">The groups whose rows are read.</param>
    /// <param name="cancellationToken">Cancels the take.</param>
    internal static async ValueTask FetchAsync(
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, ReadOnlyMemory<int> groups, CancellationToken cancellationToken)
    {
        // What the take may make of the positions, a long a group and choice, as a dataset's objects each
        // list their own, reserved under the result's memory while it runs; the arrays the fetch sorts and
        // reads by are rented under it (QueryArrays), and the take reads the sorted positions in place.
        QueryMemory? memory = outcome.Memory;
        long scratch = (long)groups.Length * outcome.Plan.ChosenRows.Length * sizeof(long);
        memory?.Hold(scratch, "fetch of the chosen rows");
        try
        {
            await FetchRowsAsync(outcome, source, spec, metrics, groups, memory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            memory?.LetGo(scratch);
        }
    }

    private static async ValueTask FetchRowsAsync(
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, ReadOnlyMemory<int> groups, QueryMemory? memory, CancellationToken cancellationToken)
    {
        IChosenColumn[] chosen = outcome.Plan.Chosen;
        (IAggregateNode Row, int[] Columns)[] choices = outcome.Plan.ChosenRows;
        int most = 0;
        foreach (int group in groups.Span)
        {
            most = Math.Max(most, group + 1);
        }

        for (int c = 0; c < chosen.Length; c++)
        {
            outcome.ChosenOf(chosen[c]).EnsureGroups(groups.Span, most, memory, source.Session);
        }

        // Each row a group chose, with the choice and the group it serves; a group without a row
        // for a choice has the choice's columns left empty.
        int entries = groups.Length * choices.Length;
        long[] positions = QueryArrays.Rent<long>(memory, entries, "fetch of the chosen rows");
        long[] owners = [];
        int[] rowOf = [];
        int[] groupOf = [];
        try
        {
            owners = QueryArrays.Rent<long>(memory, entries, "fetch of the chosen rows");
            rowOf = QueryArrays.Rent<int>(memory, entries, "fetch of the chosen rows");
            groupOf = QueryArrays.Rent<int>(memory, entries, "fetch of the chosen rows");
            await ReadChosenAsync(outcome, source, spec, metrics, groups, chosen, choices, positions, owners, rowOf, groupOf, memory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Return(memory, positions);
            Return(memory, owners);
            Return(memory, rowOf);
            Return(memory, groupOf);
        }
    }

    private static void Return<T>(QueryMemory? memory, T[] array)
    {
        if (array.Length > 0)
        {
            QueryArrays.Return(memory, array);
        }
    }

    /// <summary>
    /// The rows <paramref name="groups"/> chose, sorted into one take of the source, and read into the columns of the
    /// chosen rows: <paramref name="positions"/>, <paramref name="owners"/>, <paramref name="rowOf"/> and
    /// <paramref name="groupOf"/> are scratch an entry a group and choice long at least.
    /// </summary>
    private static async ValueTask ReadChosenAsync(
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanCounters metrics, ReadOnlyMemory<int> groups,
        IChosenColumn[] chosen, (IAggregateNode Row, int[] Columns)[] choices, long[] positions, long[] owners, int[] rowOf, int[] groupOf,
        QueryMemory? memory, CancellationToken cancellationToken)
    {
        int[] ofChoice = new int[choices.Length + 1];
        int count = 0;
        for (int r = 0; r < choices.Length; r++)
        {
            AggregateSlot<long> slot = (AggregateSlot<long>)outcome.SlotOf(choices[r].Row);
            foreach (int group in groups.Span)
            {
                long position = slot.Result(group);
                if (position >= 0)
                {
                    positions[count] = position;
                    owners[count++] = ((long)r << 32) | (uint)group;
                    ofChoice[r + 1]++;
                    continue;
                }

                foreach (int column in choices[r].Columns)
                {
                    outcome.ChosenOf(chosen[column]).Clear(group);
                }
            }
        }

        if (count == 0)
        {
            return;
        }

        // A take reads its positions in order, each once: the rows sorted, a row two choices share
        // read for both. Each choice lists, in that order, the row of the take it reads and the
        // group it serves.
        Sort(positions, owners, count, memory);
        for (int r = 0; r < choices.Length; r++)
        {
            ofChoice[r + 1] += ofChoice[r];
        }

        int[] next = ofChoice[..^1];
        int distinct = 0;
        for (int e = 0; e < count; e++)
        {
            if (distinct == 0 || positions[distinct - 1] != positions[e])
            {
                positions[distinct++] = positions[e];
            }

            int at = next[(int)(owners[e] >> 32)]++;
            rowOf[at] = distinct - 1;
            groupOf[at] = (int)owners[e];
        }

        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (IChosenColumn column in chosen)
        {
            mask.Include(column.Read.FieldPath);
        }

        ScanSpec take = spec with
        {
            Filter = null,
            MatchesNothing = false,
            Rows = null,
            Take = positions.AsMemory(0, distinct),
            Projection = mask.Build(),
            OrderPath = null,
            Descending = false,
            KeepEncodings = false,
            SinkDecodes = false,
            PositionsUnread = false,
            Analysis = null,
            Answering = null,
            Options = spec.Options with { Compact = true },
        };

        // Each batch of the take: for each choice, its rows among the batch's, read into its columns.
        int first = 0;
        int[] read = ofChoice[..^1];
        await foreach (RecordBatch batch in source.Rows.BatchesAsync(take, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            int rows = batch.RowCount;
            if (first + rows > distinct)
            {
                throw new InvalidOperationException($"A take of {distinct} rows delivered more.");
            }

            for (int r = 0; r < choices.Length; r++)
            {
                int from = read[r];
                int until = from;
                while (until < ofChoice[r + 1] && rowOf[until] < first + rows)
                {
                    until++;
                }

                if (until == from)
                {
                    continue;
                }

                foreach (int column in choices[r].Columns)
                {
                    int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, chosen[column].Read.Field, rows);
                    outcome.ChosenOf(chosen[column]).Read(batch, node, first, rowOf.AsSpan(from, until - from), groupOf.AsSpan(from, until - from));
                }

                read[r] = until;
            }

            first += rows;
        }

        if (first != distinct)
        {
            throw new InvalidOperationException($"A take of {distinct} rows delivered {first}.");
        }
    }

    /// <summary>The digits of a position a pass of <see cref="Sort"/> orders by.</summary>
    private const int DigitBits = 11;

    /// <summary>
    /// Sorts the first <paramref name="count"/> positions ascending, their owners with them: below
    /// a few thousand by comparison, above by their digits, from the lowest, each pass stable, as
    /// many passes as the largest position has digits. A position is a row of the source, so two
    /// passes order four million rows; a comparison sort of a million would take ten times longer.
    /// </summary>
    private static void Sort(long[] positions, long[] owners, int count, QueryMemory? memory)
    {
        if (count < 4_096)
        {
            Array.Sort(positions, owners, 0, count);
            return;
        }

        long largest = 0;
        for (int i = 0; i < count; i++)
        {
            largest = Math.Max(largest, positions[i]);
        }

        long[] fromPositions = positions;
        long[] fromOwners = owners;
        long[] intoPositions = QueryArrays.Rent<long>(memory, count, "fetch of the chosen rows");
        long[] intoOwners = QueryArrays.Rent<long>(memory, count, "fetch of the chosen rows");
        int[] starts = new int[1 << DigitBits];
        try
        {
            for (int shift = 0; shift < 64 && (largest >> shift) > 0; shift += DigitBits)
            {
                Array.Clear(starts);
                for (int i = 0; i < count; i++)
                {
                    starts[(int)((fromPositions[i] >> shift) & ((1 << DigitBits) - 1))]++;
                }

                int start = 0;
                for (int d = 0; d < starts.Length; d++)
                {
                    (starts[d], start) = (start, start + starts[d]);
                }

                for (int i = 0; i < count; i++)
                {
                    int at = starts[(int)((fromPositions[i] >> shift) & ((1 << DigitBits) - 1))]++;
                    intoPositions[at] = fromPositions[i];
                    intoOwners[at] = fromOwners[i];
                }

                (fromPositions, intoPositions) = (intoPositions, fromPositions);
                (fromOwners, intoOwners) = (intoOwners, fromOwners);
            }

            if (!ReferenceEquals(fromPositions, positions))
            {
                fromPositions.AsSpan(0, count).CopyTo(positions);
                fromOwners.AsSpan(0, count).CopyTo(owners);
            }
        }
        finally
        {
            // The rented pair is whichever two the passes did not leave the result in.
            QueryArrays.Return(memory, ReferenceEquals(fromPositions, positions) ? intoPositions : fromPositions);
            QueryArrays.Return(memory, ReferenceEquals(fromOwners, owners) ? intoOwners : fromOwners);
        }
    }
}
