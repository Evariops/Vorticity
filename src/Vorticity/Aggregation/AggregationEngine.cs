using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>What an aggregation computes: its aggregates, distinct by identity, and the columns of its group key.</summary>
internal sealed class AggregationPlan
{
    internal AggregationPlan(ReadOnlySpan<SymNode> results, ColumnShape[] keys)
    {
        Keys = keys;
        List<IAggregateNode> aggregates = [];
        foreach (SymNode node in results)
        {
            switch (node)
            {
                case IAggregateNode aggregate:
                    if (!aggregates.Exists(known => ReferenceEquals(known, aggregate)))
                    {
                        aggregates.Add(aggregate);
                    }

                    break;
                case IKeyNode when keys.Length > 0:
                    break;
                case IKeyNode:
                    throw new InvalidOperationException("A group key is a result of a grouped scan only.");
                default:
                    throw new InvalidOperationException($"'{node}' is not an aggregate: a result is an aggregate of the lambda's argument, or the group's key.");
            }
        }

        Aggregates = [.. aggregates];
    }

    internal IAggregateNode[] Aggregates { get; }

    internal ColumnShape[] Keys { get; }

    internal bool Grouped => Keys.Length > 0;

    /// <summary>The result a symbol stands for.</summary>
    /// <exception cref="InvalidOperationException">The symbol is a column, not an aggregate or a key.</exception>
    internal static ResultNode<T> Result<T>(Sym<T> symbol) =>
        symbol.Node as ResultNode<T>
        ?? throw new InvalidOperationException($"'{symbol}' is not an aggregate: a result is an aggregate of the lambda's argument, or the group's key.");

    internal int IndexOf(IAggregateNode node) => Array.FindIndex(Aggregates, known => ReferenceEquals(known, node));

    /// <summary>The index of the groups of one partition.</summary>
    internal GroupKeys CreateKeys(bool sorted)
    {
        if (Keys.Length > 1)
        {
            return new CompositeKeys(Keys);
        }

        ColumnShape key = Keys[0];
        return key.Kind switch
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
            StorageKind.Uuid => new FixedKeys<UInt128>(key, sorted),
            StorageKind.Bool => new BoolKeys(key),
            StorageKind.Bytes => new BytesKeys(key, sorted),
            _ => throw key.Unsupported("a group key"),
        };
    }
}

/// <summary>An aggregation that has run: the merged states and keys, and the order of the groups.</summary>
internal sealed class AggregationOutcome
{
    private readonly AggregationPlan _plan;
    private readonly AggregateSlot[] _slots;

    internal AggregationOutcome(AggregationPlan plan, AggregateSlot[] slots, GroupKeys? keys, int[] order)
    {
        _plan = plan;
        _slots = slots;
        Keys = keys;
        Order = order;
    }

    internal GroupKeys? Keys { get; }

    /// <summary>The groups in delivery order; the one group of a scalar aggregation.</summary>
    internal int[] Order { get; }

    internal AggregateSlot SlotOf(IAggregateNode node)
    {
        int index = _plan.IndexOf(node);
        return index >= 0
            ? _slots[index]
            : throw new InvalidOperationException($"'{node}' belongs to another aggregation.");
    }
}

/// <summary>The aggregates of one range of rows, stepped batch by batch on the thread that reads the range.</summary>
internal sealed class AggregationPartition
{
    private readonly ColumnShape[] _columns;
    private readonly int[] _inputs;
    private readonly int _keyCount;
    private readonly int[] _nodes;
    private readonly GroupRanges _ranges = new GroupRanges();
    private int[] _rowGroups = [];
    private long _batch;

    internal AggregationPartition(AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, bool sorted)
    {
        _columns = columns;
        _inputs = inputs;
        _keyCount = plan.Keys.Length;
        _nodes = new int[columns.Length];
        Slots = new AggregateSlot[settled.Length];
        for (int i = 0; i < settled.Length; i++)
        {
            Slots[i] = settled[i] ?? plan.Aggregates[i].Create();
        }

        Keys = plan.Grouped ? plan.CreateKeys(sorted) : null;
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

    internal void Process(RecordBatch batch)
    {
        int rows = batch.RowCount;
        if (rows == 0 || batch.SelectedRows == 0)
        {
            return;
        }

        long number = ++_batch;
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

        if (Keys is null)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (_inputs[i] != Settled)
                {
                    Slots[i].StepRange(Input(number, arena, i, rows, selection), 0, rows, 0);
                }
            }

            return;
        }

        Scratch.Grow(ref _rowGroups, rows);
        _ranges.Clear();
        bool ranged = Keys.Assign(arena, _nodes.AsSpan(0, _keyCount), rows, selection, _rowGroups, _ranges);
        int groups = Keys.Count;
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i].EnsureGroups(groups);
        }

        if (!ranged)
        {
            ReadOnlySpan<int> rowGroups = _rowGroups.AsSpan(0, rows);
            for (int i = 0; i < Slots.Length; i++)
            {
                Slots[i].StepRows(Input(number, arena, i, rows, selection), rowGroups);
            }

            return;
        }

        for (int i = 0; i < Slots.Length; i++)
        {
            AggregateSlot slot = Slots[i];
            BatchInput input = Input(number, arena, i, rows, selection);
            for (int r = 0; r < _ranges.Count; r++)
            {
                slot.StepRange(input, _ranges.StartAt(r), _ranges.EndAt(r), _ranges.GroupAt(r));
            }
        }
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

    private BatchInput Input(long number, CanonicalArena arena, int slot, int rows, ReadOnlySpan<ulong> selection)
    {
        int column = _inputs[slot];
        return new BatchInput(number, arena, column >= 0 ? _nodes[column] : -1, rows, selection);
    }
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

    internal async ValueTask<AggregationOutcome> RunAsync(AggregationPlan plan, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            return await AggregationEngine.RunAsync(Source, Spec(), Metrics, plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            End();
        }
    }

    /// <summary>The plan of the pass the aggregation would run, before the statistics settle any of it.</summary>
    internal ValueTask<ScanPlan> ExplainAsync(AggregationPlan plan, CancellationToken cancellationToken)
    {
        (ColumnShape[] columns, _) = AggregationEngine.Columns(plan, new AggregateSlot?[plan.Aggregates.Length]);
        return Source.ExplainAsync(AggregationEngine.PassSpec(Spec(), columns), cancellationToken);
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
            // A count reads no column: the terminal answers it from the structures, a pass would decode.
            long count = -1;
            AggregateSlot[] answers = new AggregateSlot[aggregates.Length];
            for (int i = 0; i < aggregates.Length; i++)
            {
                if (settled[i] is { } answer)
                {
                    answers[i] = answer;
                    continue;
                }

                count = count >= 0 ? count : await source.CountAsync(spec, metrics, cancellationToken).ConfigureAwait(false);
                answers[i] = new SettledSlot<long>(count);
            }

            return new AggregationOutcome(plan, answers, null, [0]);
        }

        (ColumnShape[] columns, int[] inputs) = Columns(plan, settled);
        bool sorted = plan.Keys.Length == 1 && IsSorted(source, plan.Keys[0]);
        ScanSpec pass = PassSpec(spec, columns);

        AggregationPartition[] partitions;
        RowRange[]? ranges = Partition(source, pass);
        if (ranges is null)
        {
            AggregationPartition only = new AggregationPartition(plan, settled, columns, inputs, sorted);
            await RunPartitionAsync(source, pass, metrics, only, cancellationToken).ConfigureAwait(false);
            partitions = [only];
        }
        else
        {
            partitions = new AggregationPartition[ranges.Length];
            for (int p = 0; p < partitions.Length; p++)
            {
                partitions[p] = new AggregationPartition(plan, settled, columns, inputs, sorted);
            }

            // One read of the zone maps and indexes for every range, as a single scan would make,
            // rather than one per range.
            if (pass.Filter is { } filter && pass.Options.Pruning && source is FileScanSource file)
            {
                BlockMask? live = await ZonePruningPlan
                    .RefineAsync(file.File, file.File.LayoutTree, filter, cancellationToken, steps: null, metrics, pass.Options.UseIndexes)
                    .ConfigureAwait(false);
                pass = pass with { Pruned = true, Live = live };
            }

            await RunParallelAsync(source, pass, metrics, partitions, ranges, cancellationToken).ConfigureAwait(false);
        }

        AggregationPartition merged = partitions[0];
        for (int p = 1; p < partitions.Length; p++)
        {
            merged.MergeFrom(partitions[p]);
        }

        GroupKeys? keys = merged.Keys;
        int[] order = keys is null ? [0] : keys.Order(sorted || (plan.Keys.Length == 1 && keys.OnlyDictionaries));
        return new AggregationOutcome(plan, merged.Slots, keys, order);
    }

    /// <summary>
    /// The spec of the pass: the columns it reads, their encoded forms kept and counted as decoded
    /// only when an aggregate expands them, whole blocks with their selection, no key order.
    /// </summary>
    internal static ScanSpec PassSpec(ScanSpec spec, ColumnShape[] columns)
    {
        FieldMaskBuilder mask = new FieldMaskBuilder();
        foreach (ColumnShape column in columns)
        {
            mask.Include(column.Column.FieldPath);
        }

        return spec with
        {
            Projection = mask.Build(),
            KeepEncodings = true,
            SinkDecodes = true,
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

    private static bool OnlyCountsLeft(IAggregateNode[] aggregates, AggregateSlot?[] settled)
    {
        for (int i = 0; i < aggregates.Length; i++)
        {
            if (settled[i] is null && aggregates[i].Kind != AggregateKind.Count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the statistics say the key column is sorted, so that its rows come in runs of one key.</summary>
    private static bool IsSorted(ScanSource source, ColumnShape key)
    {
        if (source is not FileScanSource file || !file.File.HasFileStatistics || key.Column.FieldPath.Length != 1 || !file.File.Schema.RootIsStruct)
        {
            return false;
        }

        int index = key.Column.FieldPath[0];
        VortexFileStatistics statistics = file.File.Statistics;
        return index < statistics.Count && statistics[index].TryGetIsSorted(out bool sorted) && sorted;
    }

    /// <summary>
    /// The scan's rows cut at chunk boundaries into as many ranges as its degree of parallelism, or
    /// null when it runs as one: a degree of one, a take by position, or a source that is not a file.
    /// </summary>
    private static RowRange[]? Partition(ScanSource source, ScanSpec spec)
    {
        int degree = spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : source.Session.Options.MaxDegreeOfParallelism;
        if (degree <= 1 || spec.Take is not null || spec.MatchesNothing || source is not FileScanSource file)
        {
            return null;
        }

        LayoutTree tree = file.File.LayoutTree;
        RowRange whole = new RowRange(0, tree.Root.RowCount);
        RowRange rows = spec.Rows is { } asked ? asked.Intersect(whole) : whole;
        if (rows.IsEmpty)
        {
            return null;
        }

        FieldMask mask = spec.Projection ?? FieldMask.All;
        SplitPlan plan = SplitPlan.Compute(tree, rows, in mask, SplitPlan.NaturalBatchRows(tree));
        List<RowRange> parts = [];
        long previous = rows.Start;
        int next = 1;
        for (int k = 1; k < degree; k++)
        {
            long target = rows.Start + (rows.Length * k / degree);
            while (next < plan.BoundaryCount - 1 && plan.BoundaryAt(next) < target)
            {
                next++;
            }

            long cut = plan.BoundaryAt(Math.Min(next, plan.BoundaryCount - 1));
            if (cut > previous && cut < rows.End)
            {
                parts.Add(new RowRange(previous, cut));
                previous = cut;
            }
        }

        parts.Add(new RowRange(previous, rows.End));
        return parts.Count > 1 ? [.. parts] : null;
    }

    private static async Task RunPartitionAsync(ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPartition partition, CancellationToken cancellationToken)
    {
        await foreach (RecordBatch batch in source.BatchesAsync(spec, metrics).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            partition.Process(batch);
        }
    }

    private static async Task RunParallelAsync(
        ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPartition[] partitions, RowRange[] ranges, CancellationToken cancellationToken)
    {
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        Task[] lanes = new Task[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
        {
            AggregationPartition partition = partitions[p];

            // The degree is the parallelism: a partition already runs on the pool, and decoding
            // ahead inside each one would put twice the degree's lanes on it.
            ScanSpec lane = spec with { Rows = ranges[p], Options = spec.Options with { DegreeOfParallelism = 1, Prefetch = 0 } };
            lanes[p] = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunPartitionAsync(source, lane, metrics, partition, token).ConfigureAwait(false);
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
