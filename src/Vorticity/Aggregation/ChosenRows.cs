using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>The values of one column of the chosen rows, by group: fetched after the pass, read as results.</summary>
internal abstract class ChosenValues
{
    /// <summary>
    /// Makes room for groups up to <paramref name="groups"/>, its arrays held under the result's
    /// <paramref name="memory"/> until it is delivered (PLAN-HIGH-CARDINALITY, H2), the ones they
    /// replace let go.
    /// </summary>
    internal abstract void EnsureGroups(int groups, QueryMemory? memory);

    /// <summary>Leaves <paramref name="group"/> without a row: its value null, or a value type's default.</summary>
    internal abstract void Clear(int group);

    /// <summary>
    /// Reads column <paramref name="node"/> of a fetched batch whose first row is the take's row
    /// <paramref name="first"/>: the take's row <c>rows[i]</c> goes to group <c>groups[i]</c>.
    /// </summary>
    internal abstract void Read(RecordBatch batch, int node, int first, ReadOnlySpan<int> rows, ReadOnlySpan<int> groups);
}

/// <summary>The values of a column of chosen rows as <typeparamref name="T"/>, and the groups that have no row.</summary>
internal sealed class ChosenValues<T>(ChosenColumnNode<T> column) : ChosenValues
{
    private T[] _values = [];
    private bool[] _missing = [];
    private T[] _scratch = [];

    /// <summary>The value of <paramref name="group"/>'s chosen row; the default when it has none.</summary>
    internal T At(int group) => _values[group];

    /// <summary>Whether <paramref name="group"/> has no chosen row: no candidate, or no row its filter keeps.</summary>
    internal bool Missing(int group) => _missing[group];

    internal override void EnsureGroups(int groups, QueryMemory? memory)
    {
        if (groups > _values.Length)
        {
            int length = Scratch.Capacity(groups, _values.Length);
            long element = Unsafe.SizeOf<T>() + sizeof(bool);
            memory?.Hold(length * element, "fetch of the chosen rows");
            memory?.LetGo(_values.Length * element);
            Array.Resize(ref _values, length);
            Array.Resize(ref _missing, length);
        }
    }

    internal override void Clear(int group)
    {
        _values[group] = default!;
        _missing[group] = true;
    }

    internal override void Read(RecordBatch batch, int node, int first, ReadOnlySpan<int> rows, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _scratch, batch.RowCount);
        ResultValues.Copy(batch, batch.Arena, node, column.Read.Type, column.Read.Extensions, _scratch, null);
        for (int i = 0; i < groups.Length; i++)
        {
            _values[groups[i]] = _scratch[rows[i] - first];
            _missing[groups[i]] = false;
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
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanMetrics metrics, ReadOnlyMemory<int> groups, CancellationToken cancellationToken)
    {
        // What the fetch sorts and reads by, an entry a group and choice — its position, its owner,
        // its row and group, the take's position — reserved under the result's memory while it runs
        // (PLAN-HIGH-CARDINALITY, H2, decision 13).
        QueryMemory? memory = outcome.Memory;
        long scratch = (long)groups.Length * outcome.Plan.ChosenRows.Length * ((3 * sizeof(long)) + (2 * sizeof(int)));
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
        AggregationOutcome outcome, ScanSource source, ScanSpec spec, ScanMetrics metrics, ReadOnlyMemory<int> groups, QueryMemory? memory, CancellationToken cancellationToken)
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
            outcome.ChosenOf(chosen[c]).EnsureGroups(most, memory);
        }

        // Each row a group chose, with the choice and the group it serves; a group without a row
        // for a choice has the choice's columns left empty.
        long[] positions = new long[groups.Length * choices.Length];
        long[] owners = new long[positions.Length];
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
        int[] rowOf = new int[count];
        int[] groupOf = new int[count];
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
            Take = positions.AsSpan(0, distinct).ToArray(),
            Projection = mask.Build(),
            OrderPath = null,
            Descending = false,
            KeepEncodings = false,
            SinkDecodes = false,
            PositionsUnread = false,
            Pruned = false,
            Live = null,
            Options = spec.Options with { Compact = true },
        };

        // Each batch of the take: for each choice, its rows among the batch's, read into its columns.
        int first = 0;
        int[] read = ofChoice[..^1];
        await foreach (RecordBatch batch in source.BatchesAsync(take, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
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
