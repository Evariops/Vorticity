using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>What an aggregation computes: its aggregates, one of each (<see cref="AggregateIdentity"/>), and the columns of its group key.</summary>
internal sealed class AggregationPlan
{
    internal AggregationPlan(ReadOnlySpan<SymNode> results, ColumnShape[] keys)
    {
        Keys = keys;
        List<IAggregateNode> aggregates = [];
        List<IChosenColumn> chosen = [];
        foreach (SymNode node in results)
        {
            switch (node)
            {
                case IAggregateNode aggregate:
                    Add(aggregates, aggregate);
                    break;
                case IKeyNode when keys.Length > 0:
                    break;
                case IKeyNode:
                    throw new InvalidOperationException("A group key is a result of a grouped scan only.");
                case IChosenColumn column:
                    // The row is an aggregate, its position; the column is read from it after the pass.
                    Add(aggregates, column.Row);
                    if (!chosen.Exists(known => string.Equals(known.Key, column.Key, StringComparison.Ordinal)))
                    {
                        chosen.Add(column);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"'{node}' is not an aggregate: a result is an aggregate of the lambda's argument, or the group's key.");
            }
        }

        // A mean of a group by reads the slot of the sum of its column and its rows, when the
        // selection holds one: the sum's states keep the total and the count both need.
        List<(AggregateIdentity Mean, IAggregateNode Sum)> shares = [];
        if (keys.Length > 0)
        {
            for (int i = aggregates.Count - 1; i >= 0; i--)
            {
                IAggregateNode mean = aggregates[i];
                if (mean.Kind != AggregateKind.Average || mean.Input is not { Kind: StorageKind.Primitive } input)
                {
                    continue;
                }

                IAggregateNode? sum = aggregates.Find(known =>
                    known.Kind == AggregateKind.Sum && known.Input is { } summed && summed.Is(input)
                    && string.Equals(known.Filter?.Key, mean.Filter?.Key, StringComparison.Ordinal));
                if (sum is not null)
                {
                    shares.Add((mean.Identity, sum));
                    aggregates.RemoveAt(i);
                }
            }
        }

        Aggregates = [.. aggregates];
        Shares = new (AggregateIdentity, int)[shares.Count];
        for (int s = 0; s < shares.Count; s++)
        {
            Shares[s] = (shares[s].Mean, IndexOf(shares[s].Sum));
        }

        Chosen = [.. chosen];
        ChosenRows = Rows(Chosen);
        Filters = new FilterPlan(Aggregates);
    }

    /// <summary>The means that read the slot of a sum, and that slot.</summary>
    internal (AggregateIdentity Mean, int Sum)[] Shares { get; }

    /// <summary>The columns read from chosen rows, each once: fetched after the pass by the rows' positions.</summary>
    internal IChosenColumn[] Chosen { get; }

    /// <summary>The chosen rows, each once, and the columns of <see cref="Chosen"/> read from each.</summary>
    internal (IAggregateNode Row, int[] Columns)[] ChosenRows { get; }

    internal IAggregateNode[] Aggregates { get; }

    /// <summary>The filters of the aggregates of filtered groups, and the predicates they read.</summary>
    internal FilterPlan Filters { get; }

    internal ColumnShape[] Keys { get; }

    internal bool Grouped => Keys.Length > 0;

    /// <summary>
    /// Whether a key that streams is grouped as one that does not all the same: the switch the
    /// bench and the tests compare the two paths with. Every query made from the plan sees it.
    /// </summary>
    internal bool Blocking { get; set; }

    /// <summary>What the plan's last run did, lane by lane, and its merge; null before one.</summary>
    internal AggregationRun? LastRun { get; set; }

    /// <summary>The result a symbol stands for.</summary>
    /// <exception cref="InvalidOperationException">The symbol is a column, not an aggregate or a key.</exception>
    internal static ResultNode<T> Result<T>(Sym<T> symbol) => Result(symbol, []);

    /// <summary>The result a symbol of a grouped scan's lambda stands for: an aggregate, or a component of the key, known by identity.</summary>
    /// <exception cref="InvalidOperationException">The symbol is neither.</exception>
    internal static ResultNode<T> Result<T>(Sym<T> symbol, SymNode[] components)
    {
        if (symbol.Node is ResultNode<T> result)
        {
            return result;
        }

        int component = Array.IndexOf(components, symbol.Node);
        return component >= 0
            ? new KeyNode<T>(component, symbol.Node.ToString() ?? string.Empty)
            : throw new InvalidOperationException(
                $"'{symbol}' is neither an aggregate nor a component of the key: a result is an aggregate of the group, or g.Key.");
    }

    /// <summary>The slot of <paramref name="node"/>: the one aggregate of the plan it is the same as.</summary>
    internal int IndexOf(IAggregateNode node) => Array.FindIndex(Aggregates, known => known.Identity.Equals(node.Identity));

    private static void Add(List<IAggregateNode> aggregates, IAggregateNode aggregate)
    {
        if (!aggregates.Exists(known => known.Identity.Equals(aggregate.Identity)))
        {
            aggregates.Add(aggregate);
        }
    }

    /// <summary>The rows <paramref name="chosen"/> are read from, in the order first met, with the columns read from each.</summary>
    private static (IAggregateNode Row, int[] Columns)[] Rows(IChosenColumn[] chosen)
    {
        List<(IAggregateNode Row, List<int> Columns)> rows = [];
        for (int c = 0; c < chosen.Length; c++)
        {
            int row = rows.FindIndex(known => known.Row.Identity.Equals(chosen[c].Row.Identity));
            if (row < 0)
            {
                rows.Add((chosen[c].Row, []));
                row = rows.Count - 1;
            }

            rows[row].Columns.Add(c);
        }

        return [.. rows.ConvertAll(row => (row.Row, row.Columns.ToArray()))];
    }

    /// <summary>The index of the groups of one partition.</summary>
    internal GroupKeys CreateKeys(bool sorted) => Keys.Length > 1 ? new CompositeKeys(Keys) : Single(Keys[0], sorted);

    /// <summary>The index of a key of one column.</summary>
    internal static GroupKeys Single(ColumnShape key, bool sorted) =>
        key.Kind switch
        {
            StorageKind.Primitive => key.PType switch
            {
                PType.I8 => new FixedKeys<sbyte>(key, sorted),
                PType.I16 => new FixedKeys<short>(key, sorted),
                PType.I32 => new FixedKeys<int>(key, sorted),
                PType.I64 => new FixedKeys<long>(key, sorted),
                PType.U8 => new FixedKeys<byte>(key, sorted),
                PType.U16 => new FixedKeys<ushort>(key, sorted),
                PType.U32 => new FixedKeys<uint>(key, sorted),
                PType.U64 => new FixedKeys<ulong>(key, sorted),
                PType.F16 => new FixedKeys<Half>(key, sorted),
                PType.F32 => new FixedKeys<float>(key, sorted),
                _ => new FixedKeys<double>(key, sorted),
            },
            StorageKind.Decimal => new FixedKeys<Int128>(key, sorted),
            StorageKind.Decimal256 => new FixedKeys<Vorticity.Types.Numerics.Int256>(key, sorted),
            StorageKind.Uuid => new FixedKeys<UInt128>(key, sorted),
            StorageKind.Bool => new BoolKeys(key),
            StorageKind.Bytes => new BytesKeys(key, sorted),
            _ => throw key.Unsupported("a group key"),
        };
}

/// <summary>
/// What a run of an aggregation did: each lane's active time, the ranges it took and its groups at
/// the end of its pass, then the merge of the lanes' groups. Each lane counts on its own, in its
/// partition, and the counts are gathered at the end, so that no counter is shared between lanes.
/// </summary>
/// <param name="Lanes">Each lane's counts, in the order of its rows.</param>
/// <param name="MergeTicks">The merge's time, in <see cref="Stopwatch"/> ticks.</param>
/// <param name="MergeParts">The partitions merged into the first.</param>
internal sealed record AggregationRun(AggregationRun.Lane[] Lanes, long MergeTicks, int MergeParts)
{
    /// <summary>One lane's counts.</summary>
    /// <param name="ActiveTicks">The time its passes took, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="Ranges">The ranges of rows it read.</param>
    /// <param name="Groups">Its groups at the end of its pass, before the merge.</param>
    internal readonly record struct Lane(long ActiveTicks, int Ranges, int Groups);
}

/// <summary>An aggregation that has run: the merged states and keys, and the order of the groups.</summary>
internal sealed class AggregationOutcome
{
    private readonly AggregationPlan _plan;
    private readonly AggregateSlot[] _slots;
    private readonly ChosenValues[] _chosen;

    // The means read from the slots of sums, by the sum's slot, made on the first read.
    private AggregateSlot?[]? _views;

    internal AggregationOutcome(AggregationPlan plan, AggregateSlot[] slots, GroupKeys? keys, int[] order)
    {
        _plan = plan;
        _slots = slots;
        Keys = keys;
        Order = order;
        _chosen = new ChosenValues[plan.Chosen.Length];
        for (int c = 0; c < _chosen.Length; c++)
        {
            _chosen[c] = plan.Chosen[c].CreateValues();
        }
    }

    internal AggregationPlan Plan => _plan;

    internal GroupKeys? Keys { get; }

    /// <summary>The groups in delivery order; the one group of a scalar aggregation.</summary>
    internal int[] Order { get; }

    internal AggregateSlot SlotOf(IAggregateNode node)
    {
        int index = _plan.IndexOf(node);
        if (index >= 0)
        {
            return _slots[index];
        }

        foreach ((AggregateIdentity mean, int sum) in _plan.Shares)
        {
            if (mean.Equals(node.Identity))
            {
                _views ??= new AggregateSlot?[_slots.Length];
                return _views[sum] ??= new MeanView((IMeanSlot)_slots[sum]);
            }
        }

        throw new InvalidOperationException($"'{node}' belongs to another aggregation.");
    }

    /// <summary>The values of a column of chosen rows, as the last fetch left them.</summary>
    internal ChosenValues ChosenOf(IChosenColumn column)
    {
        IChosenColumn[] chosen = _plan.Chosen;
        for (int c = 0; c < chosen.Length; c++)
        {
            if (string.Equals(chosen[c].Key, column.Key, StringComparison.Ordinal))
            {
                return _chosen[c];
            }
        }

        throw new InvalidOperationException($"'{column}' belongs to another aggregation.");
    }
}

/// <summary>The aggregates of one range of rows, stepped batch by batch on the thread that reads the range.</summary>
internal sealed class AggregationPartition
{
    private readonly ColumnShape[] _columns;
    private readonly int[] _inputs;
    private readonly int _keyCount;
    private readonly int[] _nodes;

    // The filter of each aggregate, or -1, and the selection of each filter over the batch.
    private readonly int[] _filterOf;
    private readonly FilterMasks? _masks;
    private readonly GroupRanges _ranges = new GroupRanges();
    private int[] _rowGroups = [];
    private long _batch;

    // The source's row the current batch starts at, which a chosen row keeps.
    private long _startRow;

    // The component of a composite key that streams, its own index, the component group of each
    // group and of each row of the batch: what tells a group a later row may still join from one
    // no row to come can.
    private readonly int _streaming = -1;
    private readonly GroupKeys? _componentKeys;
    private readonly GroupRanges _componentRanges = new GroupRanges();
    private int[] _componentOf = [];
    private int[] _componentRows = [];

    // What following the partition of the next range maps its groups and its component's values with.
    private int[] _followMap = [];
    private int[] _followComponents = [];

    // The blocks the zone maps settled in the partition's rows, the next to fold and the end.
    private ZoneSettling? _settling;
    private int _settledNext;
    private int _settledEnd;
    private ZoneSettling.Scratch? _settledScratch;

    internal AggregationPartition(AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, bool sorted, int streaming = -1, ScanSource? source = null)
    {
        _columns = columns;
        _inputs = inputs;
        _keyCount = plan.Keys.Length;
        _nodes = new int[columns.Length];
        _filterOf = plan.Filters.FilterOf;
        _masks = plan.Filters.Filters.Length > 0 ? new FilterMasks(plan.Filters) : null;
        Slots = new AggregateSlot[settled.Length];
        for (int i = 0; i < settled.Length; i++)
        {
            Slots[i] = settled[i] ?? plan.Aggregates[i].Create(source);
        }

        Keys = plan.Grouped ? plan.CreateKeys(sorted) : null;
        if (streaming >= 0 && _keyCount > 1)
        {
            _streaming = streaming;
            _componentKeys = AggregationPlan.Single(plan.Keys[streaming], sorted: true);
        }

        if (Keys is null)
        {
            foreach (AggregateSlot slot in Slots)
            {
                slot.EnsureGroups(1);
            }
        }
    }

    internal AggregateSlot[] Slots { get; }

    internal GroupKeys? Keys { get; }

    /// <summary>The time the partition's passes took, in <see cref="Stopwatch"/> ticks: the lane's active time.</summary>
    internal long ActiveTicks { get; set; }

    /// <summary>The ranges of rows the partition read.</summary>
    internal int Ranges { get; set; }

    /// <summary>The partition's groups when its last pass ended, before any merge.</summary>
    internal int GroupsAtEnd { get; set; }

    /// <summary>
    /// The group of the last row read whose key is not null, or -1: the group a key that streams
    /// keeps open; on a composite key, the group of the streaming component's value.
    /// </summary>
    internal int LastValueGroup { get; private set; } = -1;

    /// <summary>The value of the streaming component a group holds, as the number <see cref="LastValueGroup"/> is: the group itself on a key of one column.</summary>
    internal int ComponentOf(int group) => _componentKeys is null ? group : _componentOf[group];

    /// <summary>The number of the streaming component's null, or -1.</summary>
    internal int ComponentNull => (_componentKeys ?? Keys!).NullNumber;

    /// <summary>
    /// Keeps the groups <paramref name="groups"/> alone, keys and states, numbered again from 0 in
    /// their order: what a streaming group by does once it has delivered the groups it closed.
    /// </summary>
    internal void Keep(ReadOnlySpan<int> groups)
    {
        if (_componentKeys is not null)
        {
            // The values of the component the kept groups hold, kept too and numbered again.
            Span<int> components = stackalloc int[2];
            int count = 0;
            foreach (int group in groups)
            {
                int component = _componentOf[group];
                if (components[..count].IndexOf(component) < 0)
                {
                    if (count == components.Length)
                    {
                        throw new InvalidOperationException("A streaming group by keeps the groups of one value of its component and of its null.");
                    }

                    components[count++] = component;
                }
            }

            components[..count].Sort();
            _componentKeys.Keep(components[..count]);
            for (int i = 0; i < groups.Length; i++)
            {
                _componentOf[i] = components[..count].IndexOf(_componentOf[groups[i]]);
            }

            LastValueGroup = components[..count].IndexOf(LastValueGroup);
        }
        else
        {
            LastValueGroup = groups.IndexOf(LastValueGroup);
        }

        Keys!.Keep(groups);
        foreach (AggregateSlot slot in Slots)
        {
            slot.Keep(groups);
        }
    }

    /// <summary>
    /// Hands the partition the blocks the zone maps settled within <paramref name="rows"/>, its
    /// own: each is folded when the rows before it are, before the batch after it, or at the end.
    /// </summary>
    internal void Settle(ZoneSettling settling, RowRange rows)
    {
        _settling = settling;
        (_settledNext, _settledEnd) = settling.Within(rows);
    }

    /// <summary>Folds the settled blocks no batch came after: what the end of the rows leaves.</summary>
    internal void Finish() => FoldSettled(long.MaxValue);

    internal void Process(RecordBatch batch)
    {
        if (_settling is not null)
        {
            FoldSettled(batch.StartRow);
            if (_settledNext < _settledEnd && _settling.Start(_settledNext) < batch.StartRow + batch.RowCount)
            {
                throw new InvalidOperationException("A block the zone maps settled lies in a batch the scan read.");
            }
        }

        Fold(batch);
    }

    /// <summary>Folds the settled blocks that start before row <paramref name="before"/>, in their order.</summary>
    private void FoldSettled(long before)
    {
        ZoneSettling? settling = _settling;
        while (settling is not null && _settledNext < _settledEnd && settling.Start(_settledNext) < before)
        {
            _settledScratch ??= settling.NewScratch();
            Fold(settling.Batch(_settledNext++, _settledScratch));
        }
    }

    private void Fold(RecordBatch batch)
    {
        int rows = batch.RowCount;
        if (rows == 0 || batch.SelectedRows == 0)
        {
            return;
        }

        long number = ++_batch;
        _startRow = batch.StartRow;
        CanonicalArena arena = batch.Arena;
        int root = batch.RootIndex;
        ReadOnlySpan<ulong> selection = batch.SelectionWords;
        for (int c = 0; c < _columns.Length; c++)
        {
            int node = FilterEvaluator.Resolve(arena, root, _columns[c].Field, rows);
            while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
            {
                node = arena.GetNode(node).StorageIndex;
            }

            _nodes[c] = node;
        }

        _masks?.Evaluate(arena, root, rows, selection);
        if (Keys is null)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (_inputs[i] != Settled && Folds(i))
                {
                    Slots[i].StepRange(Input(number, arena, i, rows, selection), 0, rows, 0);
                }
            }

            return;
        }

        Scratch.Grow(ref _rowGroups, rows);
        _ranges.Clear();
        int before = Keys.Count;
        bool ranged = Keys.Assign(arena, _nodes.AsSpan(0, _keyCount), rows, selection, _rowGroups, _ranges);
        int groups = Keys.Count;
        LastValueGroup = _componentKeys is null
            ? LastValue(ranged, rows, selection, LastValueGroup)
            : Components(arena, rows, selection, before, groups);
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i].EnsureGroups(groups);
        }

        if (!ranged)
        {
            ReadOnlySpan<int> rowGroups = _rowGroups.AsSpan(0, rows);
            for (int i = 0; i < Slots.Length; i++)
            {
                if (Folds(i))
                {
                    Slots[i].StepRows(Input(number, arena, i, rows, selection), rowGroups);
                }
            }

            return;
        }

        // An aggregate folds a batch's ranges in one call. Ranges this short, a key in runs of
        // seven rows, are folded row by row by an aggregate a range costs more than its rows.
        bool shortRanges = (long)_ranges.Count * ShortRange > batch.SelectedRows;
        bool filled = false;
        for (int i = 0; i < Slots.Length; i++)
        {
            if (!Folds(i))
            {
                continue;
            }

            AggregateSlot slot = Slots[i];
            BatchInput input = Input(number, arena, i, rows, selection);
            if (!shortRanges || slot.FoldsRanges(input))
            {
                slot.StepRanges(input, _ranges);
                continue;
            }

            if (!filled)
            {
                Span<int> fill = _rowGroups.AsSpan(0, rows);
                for (int r = 0; r < _ranges.Count; r++)
                {
                    fill[_ranges.StartAt(r).._ranges.EndAt(r)].Fill(_ranges.GroupAt(r));
                }

                filled = true;
            }

            slot.StepRows(input, _rowGroups.AsSpan(0, rows));
        }
    }

    /// <summary>
    /// The value of the streaming component each new group holds, and the last one read that is
    /// not null: the component's own index assigns every row, and a group takes the value of the
    /// row that made it.
    /// </summary>
    private int Components(CanonicalArena arena, int rows, ReadOnlySpan<ulong> selection, int before, int groups)
    {
        GroupKeys components = _componentKeys!;
        Scratch.Grow(ref _componentRows, rows);
        _componentRanges.Clear();
        if (components.Assign(arena, _nodes.AsSpan(_streaming, 1), rows, selection, _componentRows, _componentRanges))
        {
            Span<int> rowComponents = _componentRows.AsSpan(0, rows);
            for (int r = 0; r < _componentRanges.Count; r++)
            {
                rowComponents[_componentRanges.StartAt(r).._componentRanges.EndAt(r)].Fill(_componentRanges.GroupAt(r));
            }
        }

        Scratch.Grow(ref _componentOf, groups);
        _componentOf.AsSpan(before, groups - before).Fill(-1);
        int nullComponent = components.NullNumber;
        int last = LastValueGroup;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int group = _rowGroups[row];
            int component = _componentRows[row];
            if (_componentOf[group] < 0)
            {
                _componentOf[group] = component;
            }

            last = component != components.NullNumber ? component : last;
        }

        return nullComponent == last ? LastValueGroup : last;
    }

    /// <summary>The group of the batch's last selected row whose key is not null; <paramref name="previous"/> when every one is null.</summary>
    private int LastValue(bool ranged, int rows, ReadOnlySpan<ulong> selection, int previous)
    {
        int nullGroup = Keys!.NullNumber;
        if (ranged)
        {
            for (int r = _ranges.Count - 1; r >= 0; r--)
            {
                if (_ranges.GroupAt(r) != nullGroup)
                {
                    return _ranges.GroupAt(r);
                }
            }

            return previous;
        }

        for (int row = rows - 1; row >= 0; row--)
        {
            if ((selection.IsEmpty || ((selection[row >> 6] >> (row & 63)) & 1) != 0) && _rowGroups[row] != nullGroup)
            {
                return _rowGroups[row];
            }
        }

        return previous;
    }

    /// <summary>
    /// Folds in the partition of the range of rows after this one's, as its batches would have been
    /// folded: its groups mapped onto this one's by key, the new ones numbered after, in the order
    /// they were met; the streaming component of each, and the last value met, carried over.
    /// </summary>
    internal void Follow(AggregationPartition next)
    {
        int before = Keys!.Count;
        Scratch.Grow(ref _followMap, next.Keys!.Count);
        Span<int> map = _followMap.AsSpan(0, next.Keys.Count);
        next.Keys.MergeInto(Keys, map);
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i].EnsureGroups(Keys.Count);
            if (_inputs[i] != Settled)
            {
                Slots[i].MergeFrom(next.Slots[i], map);
            }
        }

        if (_componentKeys is null)
        {
            LastValueGroup = next.LastValueGroup >= 0 ? map[next.LastValueGroup] : LastValueGroup;
            return;
        }

        // The values of the streaming component the new groups hold, in this partition's numbers.
        Scratch.Grow(ref _followComponents, next._componentKeys!.Count);
        Span<int> components = _followComponents.AsSpan(0, next._componentKeys.Count);
        next._componentKeys.MergeInto(_componentKeys, components);
        Scratch.Grow(ref _componentOf, Keys.Count);
        for (int g = 0; g < map.Length; g++)
        {
            if (map[g] >= before)
            {
                _componentOf[map[g]] = components[next._componentOf[g]];
            }
        }

        LastValueGroup = next.LastValueGroup >= 0 ? components[next.LastValueGroup] : LastValueGroup;
    }

    /// <summary>Folds another partition into this one, its groups mapped onto this one's by key.</summary>
    internal void MergeFrom(AggregationPartition other)
    {
        int[] map;
        if (Keys is null)
        {
            map = [0];
        }
        else
        {
            map = new int[other.Keys!.Count];
            other.Keys.MergeInto(Keys, map);
            foreach (AggregateSlot slot in Slots)
            {
                slot.EnsureGroups(Keys.Count);
            }
        }

        for (int i = 0; i < Slots.Length; i++)
        {
            if (_inputs[i] != Settled)
            {
                Slots[i].MergeFrom(other.Slots[i], map);
            }
        }
    }

    /// <summary>The input of an aggregate settled before the scan, which is never stepped.</summary>
    internal const int Settled = -2;

    /// <summary>
    /// The rows a range of one group must average for its batch to be folded a range at a time:
    /// below, the call per aggregate and per range outweighs the rows it folds, and the batch is
    /// folded row by row instead.
    /// </summary>
    internal const int ShortRange = 64;

    /// <summary>The aggregate's input over the batch: its column, and the rows the scan kept, or those its filter keeps of them.</summary>
    private BatchInput Input(long number, CanonicalArena arena, int slot, int rows, ReadOnlySpan<ulong> selection)
    {
        int column = _inputs[slot];
        int filter = _filterOf[slot];
        return new BatchInput(number, arena, column >= 0 ? _nodes[column] : -1, rows, filter < 0 ? selection : _masks!.Selection(filter), _startRow);
    }

    /// <summary>Whether the aggregate has rows of the batch to fold: none when its filter keeps none.</summary>
    private bool Folds(int slot) => _filterOf[slot] < 0 || !_masks!.KeepsNone(_filterOf[slot]);
}

/// <summary>The scan an aggregation runs on, whatever its record type.</summary>
internal abstract class AggregationHost
{
    internal abstract ScanSource Source { get; }

    internal abstract ScanMetrics Metrics { get; }

    internal abstract ScanStatistics Statistics { get; }

    internal abstract ScanSpec Spec();

    /// <summary>Claims the scan for its one sink.</summary>
    internal abstract void Begin();

    internal abstract void End();

    internal async ValueTask<AggregationOutcome> RunAsync(AggregationPlan plan, CancellationToken cancellationToken, VortexExpr? rows = null)
    {
        Begin();
        try
        {
            ScanSpec spec = Spec(rows);
            AggregationOutcome outcome = await AggregationEngine.RunAsync(Source, spec, Metrics, plan, cancellationToken).ConfigureAwait(false);
            if (plan.Chosen.Length > 0)
            {
                // The columns of the chosen rows, read once every group's row is known.
                int[] groups = new int[outcome.Keys?.Count ?? 1];
                for (int g = 0; g < groups.Length; g++)
                {
                    groups[g] = g;
                }

                await ChosenFetch.FetchAsync(outcome, Source, spec, Metrics, groups, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Runs a grouped query that does not stream: its pass, then its operators on groups, the chosen
    /// rows fetched for the groups the first operator that reads one is given, or else for the
    /// groups delivered (<see cref="GroupSelection.ApplyAsync"/>).
    /// </summary>
    /// <returns>The merged states and keys, and the groups delivered, in order, before the result's window.</returns>
    internal async ValueTask<(AggregationOutcome Outcome, int[] Groups, int Count)> RunAsync(AggregationQuery query, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            ScanSpec spec = Spec(query.RowFilter);
            AggregationOutcome outcome = await AggregationEngine.RunAsync(Source, spec, Metrics, query.Plan, cancellationToken).ConfigureAwait(false);
            query.PeakGroups = outcome.Keys?.Count ?? 1;
            (int[] groups, int count) = await GroupSelection.ApplyAsync(query, outcome, spec, cancellationToken).ConfigureAwait(false);
            return (outcome, groups, count);
        }
        finally
        {
            End();
        }
    }

    /// <summary>The plan of the pass the aggregation would run, before the statistics settle any of it.</summary>
    internal ValueTask<ScanPlan> ExplainAsync(AggregationPlan plan, CancellationToken cancellationToken, VortexExpr? rows = null)
    {
        (ColumnShape[] columns, _) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        return Source.ExplainAsync(AggregationEngine.PassSpec(Spec(rows), columns, plan, Source.Schema), cancellationToken);
    }

    /// <summary>The scan's spec, its filter joined with <paramref name="rows"/>: the conjuncts on a group's key, which filter rows.</summary>
    internal ScanSpec Spec(VortexExpr? rows)
    {
        ScanSpec spec = Spec();
        return rows is null ? spec : spec with { Filter = spec.Filter is { } filter ? Expr.Logical(true, filter, rows) : rows };
    }
}

/// <summary>
/// Runs an aggregation: the file statistics when they are the answer, then one pass over the blocks
/// in their encoded form, split into chunk-aligned ranges aggregated concurrently when the scan's
/// degree of parallelism allows, and merged at the end.
/// </summary>
internal static class AggregationEngine
{
    internal static async ValueTask<AggregationOutcome> RunAsync(
        ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPlan plan, CancellationToken cancellationToken)
    {
        IAggregateNode[] aggregates = plan.Aggregates;
        AggregateSlot?[] settled = new AggregateSlot?[aggregates.Length];
        if (!plan.Grouped && StatisticsView.For(source, spec) is { } statistics)
        {
            for (int i = 0; i < aggregates.Length; i++)
            {
                settled[i] = aggregates[i].Settle(statistics);
            }
        }

        if (!plan.Grouped && OnlyCountsLeft(aggregates, settled))
        {
            // A count reads no column: the terminal answers it from the structures, a pass would
            // decode. A count or an any of filtered rows is the scan's, its filter joined.
            long count = -1;
            AggregateSlot[] answers = new AggregateSlot[aggregates.Length];
            for (int i = 0; i < aggregates.Length; i++)
            {
                if (settled[i] is { } answer)
                {
                    answers[i] = answer;
                    continue;
                }

                RowFilter? filter = aggregates[i].Filter;
                if (filter is null)
                {
                    count = count >= 0 ? count : await source.CountAsync(spec, metrics, cancellationToken).ConfigureAwait(false);
                    answers[i] = new SettledSlot<long>(count);
                    continue;
                }

                ScanSpec rows = filter.IsNothing ? spec with { MatchesNothing = true } : spec with { Filter = Joined(spec.Filter, filter) };
                answers[i] = aggregates[i].Kind == AggregateKind.Any
                    ? new SettledSlot<bool>(await source.AnyAsync(rows, metrics, cancellationToken).ConfigureAwait(false))
                    : new SettledSlot<long>(await source.CountAsync(rows, metrics, cancellationToken).ConfigureAwait(false));
            }

            return new AggregationOutcome(plan, answers, null, [0]);
        }

        (ColumnShape[] columns, int[] inputs) = Columns(plan, settled);
        bool sorted = plan.Keys.Length == 1 && IsSorted(source, plan.Keys[0]);
        ScanSpec pass = PassSpec(spec, columns, plan, source.Schema);

        // The blocks the zone maps answer are not read: the pass's mask has them dead, and each
        // partition folds them as its rows reach them.
        ZoneSettling? settling = await ZoneSettling.PlanAsync(source, pass, plan, metrics, cancellationToken).ConfigureAwait(false);
        if (settling is not null)
        {
            pass = settling.Pass(pass);
        }

        // On several lanes the structures are read once, before the rows are cut, so that the cut
        // shares the live blocks, not the file's: a filter that keeps a few contiguous blocks keeps
        // every lane busy, rather than the one whose rows hold them. The settling read them already.
        int degree = Degree(source, pass);
        if (settling is null && degree > 1 && pass.Take is null && pass.Filter is { } kept && pass.Options.Pruning && source is FileScanSource file)
        {
            BlockMask? live = await ZonePruningPlan
                .RefineAsync(file.File, file.File.LayoutTree, FunctionFieldExpr.Ranges(kept), cancellationToken, steps: null, metrics, pass.Options.UseIndexes)
                .ConfigureAwait(false);
            pass = pass with { Pruned = true, Live = live };
        }

        AggregationPartition[] partitions;
        RowRange[]? ranges = Ranges(source, pass, degree);
        if (ranges is null)
        {
            AggregationPartition only = new AggregationPartition(plan, settled, columns, inputs, sorted, source: source);
            if (settling is not null)
            {
                only.Settle(settling, pass.Rows ?? new RowRange(0, long.MaxValue));
            }

            await RunPartitionAsync(source, pass, metrics, only, cancellationToken).ConfigureAwait(false);
            partitions = [only];
        }
        else
        {
            // A worker per lane, each with its partition from one range to the next: the merge
            // stays in as many parts as lanes, however many ranges the queue holds.
            partitions = new AggregationPartition[Math.Min(degree, ranges.Length)];
            for (int p = 0; p < partitions.Length; p++)
            {
                partitions[p] = new AggregationPartition(plan, settled, columns, inputs, sorted, source: source);
            }

            await RunQueueAsync(source, pass, metrics, partitions, ranges, settling, cancellationToken).ConfigureAwait(false);
        }

        AggregationPartition merged = partitions[0];
        long merging = Stopwatch.GetTimestamp();
        for (int p = 1; p < partitions.Length; p++)
        {
            merged.MergeFrom(partitions[p]);
        }

        plan.LastRun = Gathered(partitions, Stopwatch.GetTimestamp() - merging);
        GroupKeys? keys = merged.Keys;
        // Groups as they were first met: an order is asked for, with OrderBy.
        int[] order = keys is null ? [0] : keys.Order(sorted: false);
        return new AggregationOutcome(plan, merged.Slots, keys, order);
    }

    /// <summary>
    /// The spec of the pass: the columns it reads, their encoded forms kept and counted as decoded
    /// only when an aggregate expands them, whole blocks with their selection, no key order, and no
    /// row's place read.
    /// </summary>
    internal static ScanSpec PassSpec(ScanSpec spec, ColumnShape[] columns, AggregationPlan plan, VortexSchema schema)
    {
        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (ColumnShape column in columns)
        {
            mask.Include(column.Column.FieldPath);
        }

        // The columns the filters of filtered groups read, evaluated on each batch of the pass.
        foreach (FieldExpr field in plan.Filters.Fields())
        {
            mask.Include(ToolPaths.Resolve(schema, field.Segments ?? field.Path.Split('.'), field.Path));
        }

        return spec with
        {
            Projection = mask.Build(),
            KeepEncodings = true,
            SinkDecodes = true,

            // A chosen row is kept as its place in the source, which the batches then have to number.
            PositionsUnread = plan.Chosen.Length == 0,
            OrderPath = null,
            Descending = false,
            Options = spec.Options with { Compact = false },
        };
    }

    /// <summary>The columns an aggregation reads: the key's, then the inputs of the aggregates left after the statistics.</summary>
    internal static (ColumnShape[] Columns, int[] Inputs) Columns(AggregationPlan plan, AggregateSlot?[] settled)
    {
        List<ColumnShape> columns = [.. plan.Keys];
        int[] inputs = new int[plan.Aggregates.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            if (settled[i] is not null)
            {
                inputs[i] = AggregationPartition.Settled;
                continue;
            }

            ColumnShape? input = plan.Aggregates[i].Input;
            if (input is null)
            {
                inputs[i] = -1;
                continue;
            }

            int found = columns.FindIndex(known => known.Is(input));
            if (found < 0)
            {
                found = columns.Count;
                columns.Add(input);
            }

            inputs[i] = found;
        }

        return ([.. columns], inputs);
    }

    /// <summary>
    /// Whether every answer left is a count the scan's structures give: of every row, and of the rows
    /// of one filter, a count or an any whose conditions are each a predicate true. Several filters
    /// are one pass, rather than a scan each.
    /// </summary>
    private static bool OnlyCountsLeft(IAggregateNode[] aggregates, AggregateSlot?[] settled)
    {
        string? filtered = null;
        for (int i = 0; i < aggregates.Length; i++)
        {
            if (settled[i] is not null)
            {
                continue;
            }

            IAggregateNode aggregate = aggregates[i];
            RowFilter? filter = aggregate.Filter;
            if (filter is null ? aggregate.Kind != AggregateKind.Count : aggregate.Kind is not (AggregateKind.Count or AggregateKind.Any))
            {
                return false;
            }

            if (filter is null || filter.IsNothing)
            {
                continue;
            }

            if (Array.Exists(filter.Conditions, condition => !condition.Holds) || (filtered is not null && !string.Equals(filtered, filter.Key, StringComparison.Ordinal)))
            {
                return false;
            }

            filtered = filter.Key;
        }

        return true;
    }

    /// <summary>The scan's filter and the conditions of a filter that are each a predicate true, joined.</summary>
    private static VortexExpr Joined(VortexExpr? scan, RowFilter filter)
    {
        VortexExpr? joined = scan;
        foreach (RowCondition condition in filter.Conditions)
        {
            joined = joined is null ? condition.Predicate : Expr.Logical(true, joined, condition.Predicate);
        }

        return joined!;
    }

    /// <summary>Whether the statistics say the key column is sorted, so that its rows come in runs of one key.</summary>
    internal static bool IsSorted(ScanSource source, ColumnShape key)
    {
        if (source is not FileScanSource file || !file.File.HasFileStatistics || key.Column.FieldPath.Length != 1 || !file.File.Schema.RootIsStruct)
        {
            return false;
        }

        int index = key.Column.FieldPath[0];
        VortexFileStatistics statistics = file.File.Statistics;
        return index < statistics.Count && statistics[index].TryGetIsSorted(out bool sorted) && sorted;
    }

    /// <summary>The lanes a pass may take: the scan's own degree, or its session's.</summary>
    private static int Degree(ScanSource source, ScanSpec spec) =>
        spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : source.Session.Options.MaxDegreeOfParallelism;

    /// <summary>
    /// The ranges of rows a queue hands its lanes: about <see cref="RangesPerLane"/> a lane, of as
    /// many live rows each, cut at the plan's boundaries, a dead block joining the range before it;
    /// null when the pass runs as one range: a degree of one, a take by position, a source that is
    /// not a file, or live rows too few for two ranges of a block each.
    /// </summary>
    /// <remarks>
    /// The live rows are the mask's when the pass has one, so that a filter keeping a few contiguous
    /// blocks shares them among the lanes rather than leaving them to the one whose rows hold them.
    /// </remarks>
    private static RowRange[]? Ranges(ScanSource source, ScanSpec spec, int degree)
    {
        if (degree <= 1 || spec.Take is not null || spec.MatchesNothing || source is not FileScanSource file)
        {
            return null;
        }

        LayoutTree tree = file.File.LayoutTree;
        RowRange whole = new RowRange(0, tree.Root.RowCount);
        RowRange rows = spec.Rows is { } asked ? asked.Intersect(whole) : whole;
        long blockRows = SplitPlan.NaturalBatchRows(tree);
        if (rows.IsEmpty || blockRows <= 0)
        {
            return null;
        }

        FieldMask mask = spec.Projection ?? FieldMask.All;
        SplitPlan plan = SplitPlan.Compute(tree, rows, in mask, blockRows);
        BlockMask? live = spec.Pruned ? spec.Live : null;
        long alive = Live(live, rows);
        long count = Math.Min(degree * (long)RangesPerLane, alive / blockRows);
        if (count < 2)
        {
            return null;
        }

        long share = alive / count;
        List<RowRange> parts = [];
        long start = rows.Start;
        long held = 0;
        long previous = rows.Start;
        for (int b = 0; b < plan.BoundaryCount; b++)
        {
            long at = plan.BoundaryAt(b);
            if (at <= previous || at >= rows.End)
            {
                continue;
            }

            held += Live(live, new RowRange(previous, at));
            previous = at;
            if (held >= share && parts.Count < count - 1)
            {
                parts.Add(new RowRange(start, at));
                start = at;
                held = 0;
            }
        }

        parts.Add(new RowRange(start, rows.End));
        return parts.Count > 1 ? [.. parts] : null;
    }

    /// <summary>The ranges a queue holds per lane: enough for a lane on a slow core to leave its last one to the others.</summary>
    private const int RangesPerLane = 4;

    /// <summary>The live rows of <paramref name="rows"/>: every one without a mask.</summary>
    private static long Live(BlockMask? live, RowRange rows)
    {
        if (live is null)
        {
            return rows.Length;
        }

        long count = 0;
        int first = (int)(rows.Start / live.BlockRows);
        int last = (int)((rows.End - 1) / live.BlockRows);
        for (int block = first; block <= last && block < live.BlockCount; block++)
        {
            if (live.IsLive(block))
            {
                count += live.BlockRange(block).Intersect(rows).Length;
            }
        }

        return count;
    }

    /// <summary>
    /// The rows of a pass that streams cut at the plan's boundaries into ranges that double from a
    /// block up to a quarter of a lane's share, or null when it runs as one stream: a degree of
    /// one, a take by position, a source that is not a file, rows of one range. The first range
    /// answers after a block; the later ones are long enough for their merge to cost little against
    /// their rows.
    /// </summary>
    internal static RowRange[]? StreamingRanges(ScanSource source, ScanSpec spec, int degree)
    {
        if (degree <= 1 || spec.Take is not null || spec.MatchesNothing || source is not FileScanSource file)
        {
            return null;
        }

        LayoutTree tree = file.File.LayoutTree;
        RowRange whole = new RowRange(0, tree.Root.RowCount);
        RowRange rows = spec.Rows is { } asked ? asked.Intersect(whole) : whole;
        long blockRows = SplitPlan.NaturalBatchRows(tree);
        if (rows.IsEmpty || blockRows <= 0)
        {
            return null;
        }

        FieldMask mask = spec.Projection ?? FieldMask.All;
        SplitPlan plan = SplitPlan.Compute(tree, rows, in mask, blockRows);
        long most = Math.Max(blockRows, rows.Length / (degree * 4L));
        List<RowRange> parts = [];
        long start = rows.Start;
        long target = blockRows;
        for (int b = 0; b < plan.BoundaryCount; b++)
        {
            long at = plan.BoundaryAt(b);
            if (at > start && at < rows.End && at - start >= target)
            {
                parts.Add(new RowRange(start, at));
                start = at;
                target = Math.Min(target * 2, most);
            }
        }

        parts.Add(new RowRange(start, rows.End));
        return parts.Count > 1 ? [.. parts] : null;
    }

    internal static async Task RunPartitionAsync(ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPartition partition, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        await foreach (RecordBatch batch in source.BatchesAsync(spec, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            partition.Process(batch);
        }

        partition.Finish();
        partition.ActiveTicks += Stopwatch.GetTimestamp() - start;
        partition.Ranges++;
        partition.GroupsAtEnd = partition.Keys?.Count ?? 1;
    }

    /// <summary>What the partitions did, gathered once they have merged into the first: the plan's last run.</summary>
    private static AggregationRun Gathered(AggregationPartition[] partitions, long mergeTicks)
    {
        AggregationRun.Lane[] lanes = new AggregationRun.Lane[partitions.Length];
        for (int p = 0; p < lanes.Length; p++)
        {
            lanes[p] = new AggregationRun.Lane(partitions[p].ActiveTicks, partitions[p].Ranges, partitions[p].GroupsAtEnd);
        }

        return new AggregationRun(lanes, mergeTicks, partitions.Length - 1);
    }

    /// <summary>
    /// Runs the ranges on a worker per partition, each taking the next range of the queue as it
    /// finishes one and folding it into its partition: a lane on a slow core takes fewer, and no
    /// lane waits on the others while ranges are left.
    /// </summary>
    private static async Task RunQueueAsync(
        ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPartition[] partitions, RowRange[] ranges, ZoneSettling? settling,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;

        // The degree is the parallelism: a partition already runs on the pool, and decoding ahead
        // inside each one would put twice the degree's lanes on it.
        ScanSpec lane = spec with { Options = spec.Options with { DegreeOfParallelism = 1, Prefetch = 0 } };
        int[] next = [-1];
        Task[] lanes = new Task[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
        {
            AggregationPartition partition = partitions[p];
            lanes[p] = Task.Run(
                async () =>
                {
                    try
                    {
                        int at;
                        while ((at = Interlocked.Increment(ref next[0])) < ranges.Length)
                        {
                            if (settling is not null)
                            {
                                partition.Settle(settling, ranges[at]);
                            }

                            await RunPartitionAsync(source, lane with { Rows = ranges[at] }, metrics, partition, token).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // One lane's failure stops the others rather than letting them read to the end.
                        await failed.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                },
                token);
        }

        await Task.WhenAll(lanes).ConfigureAwait(false);
    }
}
