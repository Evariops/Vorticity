using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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
        MeanRead = new bool[Aggregates.Length];
        for (int s = 0; s < shares.Count; s++)
        {
            Shares[s] = (shares[s].Mean, IndexOf(shares[s].Sum));
            MeanRead[Shares[s].Sum] = true;
        }

        Chosen = [.. chosen];
        ChosenRows = Rows(Chosen);
        Filters = new FilterPlan(Aggregates);
    }

    /// <summary>The means that read the slot of a sum, and that slot.</summary>
    internal (AggregateIdentity Mean, int Sum)[] Shares { get; }

    /// <summary>
    /// Whether a mean reads the slot of each aggregate (<see cref="Shares"/>): a sum that none reads
    /// keeps its total alone, without the count a mean divides by.
    /// </summary>
    internal bool[] MeanRead { get; }

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

    /// <summary>
    /// Whether a key its zones may prove final streams on several lanes all the same, where its pass blocks
    /// by default there (<see cref="ZoneFinality.Candidate"/>): the switch the bench and the tests compare
    /// the two paths with.
    /// </summary>
    internal bool ZonesAtEveryDegree { get; set; }

    /// <summary>What the plan's last run did, lane by lane, and its merge; null before one.</summary>
    internal AggregationRun? LastRun { get; set; }

    /// <summary>The groups the plan's last run found, before any operator on them.</summary>
    internal long LastGroups { get; set; }

    /// <summary>The most bytes the plan's last run held of its memory budget at once.</summary>
    internal long LastPeakBytes { get; set; }

    /// <summary>The key blocks the plan's last run grouped, by how.</summary>
    internal (long ByRange, long ByCode, long Hashed) LastKeyBlocks { get; set; }

    /// <summary>The time from the first move of the plan's last result to its first batch, in <see cref="Stopwatch"/> ticks.</summary>
    internal long LastFirstBatchTicks { get; set; }

    /// <summary>The chunks the last run's top-k ranked on tasks of their own; one when it ranked its groups at once.</summary>
    internal int LastTopChunks { get; set; }

    /// <summary>What the plan's last run did, as the query's statistics say it (docs/design/16-queries.md §10); null before one.</summary>
    internal GroupStatistics? Statistics()
    {
        if (LastRun is not { } run)
        {
            return null;
        }

        CoreRun? core = run.Core;
        return new GroupStatistics(
            LastGroups,
            PeakGroups,
            LastPeakBytes,
            run.Lanes.Length,
            run.MergeParts,
            core is not null,
            core?.Flushes ?? 0,
            core?.BypassedRows ?? 0,
            core?.Bursts ?? 0,
            core?.PendingPeakBytes ?? 0,
            core?.ReloadedBytes ?? 0,
            core?.Tables ?? 0,
            core?.Splits ?? 0,
            core?.SpilledParts ?? 0,
            (core?.SpilledBytes ?? 0) + run.SpilledBytes,
            LastKeyBlocks.ByRange,
            LastKeyBlocks.ByCode,
            LastKeyBlocks.Hashed,
            Stopwatch.GetElapsedTime(0, LastFirstBatchTicks))
        {
            CoreReason = core?.Reason switch
            {
                null => GroupCoreReason.None,
                CoreReason.Plan => GroupCoreReason.Asked,
                CoreReason.FirstRows => GroupCoreReason.FirstRows,
                CoreReason.Spread => GroupCoreReason.Spread,
                CoreReason.Projection => GroupCoreReason.Projection,
                _ => GroupCoreReason.Pressure,
            },
            TurnedAfterRows = core?.TurnedAfterRows ?? -1,
            SpilledRuns = run.SpilledRuns,
        };
    }

    /// <summary>Called with the lanes' partitions once made, before the pass: what a test watches their tables by. Null but in tests.</summary>
    internal Action<AggregationPartition[]>? Watch { get; set; }

    /// <summary>
    /// Whether the lanes' groups merge in parts (true) or in series (false), or as the merge weighs
    /// them (null, the default): the switch the tests force each merge with, whichever lane the
    /// queue gave most ranges.
    /// </summary>
    internal bool? MergeInParts { get; set; }

    /// <summary>The parts a merge in parts cuts the key space into, a power of two up to 256, or null for the merge's own count: the switch the bench sweeps them with.</summary>
    internal int? MergeParts { get; set; }

    /// <summary>
    /// The rows each slot folds before the next slot folds them, on the hashed path: a window whose
    /// groups, values and records stay in the first level of cache from one slot to the next
    /// (<see cref="DefaultFoldWindow"/>); 0 for the whole batch, the switch the bench compares them in.
    /// </summary>
    internal int? FoldWindow { get; set; } = DefaultFoldWindow;

    /// <summary>The window of <see cref="FoldWindow"/>: 2 048 rows, their groups, a column of values and the records they touch within the first level of cache of current cores.</summary>
    internal const int DefaultFoldWindow = 2_048;

    /// <summary>
    /// How a key of one fixed-width column finds its rows' groups on the hashed path: -1 a row at a
    /// time; 0 in two passes, each row's home slot with no branch on the keys, then the rows left in
    /// their order; more, in two passes whose first reads the slot that many rows on ahead of each row.
    /// The switch the bench sweeps the distance with. A key numbered by value goes by two passes too
    /// from 0, its pages read with no branch, and a row at a time at -1.
    /// </summary>
    internal int ProbeAhead { get; set; } = DefaultProbeAhead;

    /// <summary>
    /// The setting of <see cref="ProbeAhead"/>: two passes, no slot read ahead. Measured on 2026-10-06
    /// at degree 1, a read 16 to 64 rows ahead took 6 to 10 % off ten million keys in no order and off
    /// a key a row, nothing off a million, and added 2 to 8 % to keys in a row, at a stride, hot keys
    /// and ten rows a key.
    /// </summary>
    internal const int DefaultProbeAhead = 0;

    /// <summary>The most groups the plan's last run held at once (<see cref="AggregationQuery.PeakGroups"/>).</summary>
    internal long PeakGroups { get; set; }

    /// <summary>
    /// Whether a blocking group by holds its groups once, in the parts of <see cref="GroupCore"/>,
    /// where its key travels in batches and its states lie in records: the switch that asks for the
    /// core whatever the lanes, which the tests and the bench measure it by.
    /// </summary>
    internal bool Core { get; set; }

    /// <summary>
    /// Whether a lane whose budget cannot let its table grow turns to the core rather than failing,
    /// where the core can hold the query's groups: on by default, off to measure or test the lanes'
    /// tables alone under a budget.
    /// </summary>
    internal bool CoreUnderPressure { get; set; } = true;

    /// <summary>
    /// Whether the groups of a query come in an order shuffled the same way for a process: on in the
    /// repository's tests, so that none relies on the order the engine happens to give groups no
    /// <c>OrderBy</c> sorts.
    /// </summary>
    internal static bool ShuffledOrder { get; set; }

    /// <summary>
    /// The batches a lane folds into its own table before it turns to the core, as if its budget could
    /// not let the table grow, or null to turn under pressure alone: the tests' way to turn every lane
    /// at a chosen point of the pass.
    /// </summary>
    internal int? CoreTurnAt { get; set; }

    /// <summary>
    /// Whether an integer key numbered by value over <see cref="ScatteredSpan"/> values or more, which a
    /// lane's first batch shows scattered over its span, takes the core on the core's lanes before a row
    /// is folded (<see cref="AggregationPartition.Judge"/>): its lanes' tables would each hold most of
    /// its groups, out of the cache. A file's zones, which bound each zone's values by their least and
    /// their greatest, read a key in the order of the rows with a sentinel, an anonymous 0 among growing
    /// identifiers, as covering its span; and a dataset reads none before its pass. False to leave the
    /// core to <see cref="Core"/> and to pressure alone.
    /// </summary>
    internal bool CoreScattered { get; set; } = true;

    /// <summary>
    /// Whether a hashed key, which nothing bounds before the pass, takes the core on its lanes once a
    /// lane's first <see cref="AggregationPartition.JudgedRows"/> rows were nearly all new groups: a key
    /// of a million values or more, which the lanes' tables would each hold. False to leave the core to
    /// <see cref="Core"/>, the zones and pressure.
    /// </summary>
    internal bool CoreOnNew { get; set; } = true;

    /// <summary>
    /// Whether the lanes that judge their key on their first rows start across the source, lane i at
    /// i / N of the queue, rather than all at its start: on by default, off for the tests and the bench
    /// to weigh it against the queue's order alone.
    /// </summary>
    internal bool Fan { get; set; } = true;

    /// <summary>
    /// The span of values from which a key its first batch shows scattered takes the core: measured on
    /// 2026-10-07 at fourteen lanes, the core ×0.72 at 10⁶ random keys and ×0.52 at 10⁷, ×2.47 at 10⁵.
    /// </summary>
    internal const long ScatteredSpan = 1_000_000;

    /// <summary>
    /// The lanes from which the core holds the groups, or null for the core's own: below, each lane's
    /// table and the merge cost less (<see cref="GroupCore.Of"/>); 1 in the tests and the bench, which
    /// run the core at every degree.
    /// </summary>
    internal int? CoreLanes { get; set; }

    /// <summary>The groups of a lane's cache in the core, or null for the core's own: tiny in the tests, so that every batch copies the cache.</summary>
    internal int? CoreCapacity { get; set; }

    /// <summary>The core's α forced (1, 2, 4, 8), or null for the one it derives from the lanes.</summary>
    internal int? CoreAlpha { get; set; }

    /// <summary>The entries a part holds pending at least before a burst, or null for the core's own.</summary>
    internal int? CoreFloor { get; set; }

    /// <summary>The groups past which a sub-table splits, or null for the core's own bound.</summary>
    internal int? CoreTableGroups { get; set; }

    /// <summary>The entries of a part's batch, or null for the core's own.</summary>
    internal int? CoreBatchEntries { get; set; }

    /// <summary>The spins a burst waits once it holds its part, or null: the tests' slowed holder, which the lanes deposit past.</summary>
    internal int? CoreBurstSpin { get; set; }

    /// <summary>
    /// Whether the core under the governor spills its largest part to the query's scratch when its
    /// budget holds no more: on by default, off to test the refusal.
    /// </summary>
    internal bool CoreSpills { get; set; } = true;

    /// <summary>
    /// Whether the lanes write what they hold to the query's scratch when its budget holds it no more and
    /// the core cannot take it: on by default, off to test the refusal.
    /// </summary>
    internal bool LanesSpill { get; set; } = true;

    /// <summary>
    /// Whether a lane whose table the budget holds no more, while other lanes run, merges it into the table
    /// the retired lanes share and retires at its range's end, rather than write it to the scratch: on by
    /// default, off to weigh the two (<see cref="LaneRetirement"/>).
    /// </summary>
    internal bool LanesRetire { get; set; } = true;

    /// <summary>
    /// Whether a core, or a merge in parts, with no order nor window over its groups delivers them part
    /// by part, its first batch once its first part is applied or merged: on by default, off for the
    /// bench to weigh it against the delivery whole.
    /// </summary>
    internal bool CoreParted { get; set; } = true;

    /// <summary>
    /// Whether a top-k of many groups ranks them in chunks at once, then the chunks' candidates: on by
    /// default, off for the bench to weigh it against one ranking.
    /// </summary>
    internal bool TopInChunks { get; set; } = true;

    /// <summary>
    /// Whether a top-k on a column's largest or smallest value of each group keeps the lanes' best
    /// groups alone, dropping the rows past their frontier: on by default, off for the bench to weigh
    /// it against the group by of every group.
    /// </summary>
    internal bool TopOnExtremes { get; set; } = true;

    /// <summary>What the core tells of each group it makes as it makes it, a <c>Distinct</c>'s reader; null for none.</summary>
    internal CoreEmitter? Emitter { get; set; }

    /// <summary>
    /// Whether a <c>Distinct</c> of this plan, on several lanes, takes the core's path, each value told
    /// as it enters its part's set: on by default, off for the tests and the bench to weigh it against
    /// the values taken on the reader's thread.
    /// </summary>
    internal bool CoreDistinct { get; set; } = true;

    /// <summary>
    /// Whether the core asked for (<see cref="Core"/>) is the lean one lanes turn to under pressure: α at 1,
    /// a part applied from 256 entries, batches of a kilobyte, its caches and batches sized on the
    /// budget: a <c>Distinct</c>'s, which tells its values as they enter their sets and spills under its
    /// budget.
    /// </summary>
    internal bool CoreLean { get; set; }

    /// <summary>The share of its rows a lane's cache finds below which the lane bypasses it, ε, or null for the core's own: 1 bypasses it always once it has filled, 0 never.</summary>
    internal double? CoreBypass { get; set; }

    /// <summary>The rows a lane's cache is judged over against ε, in capacities of the cache, or null for the core's own: ε's period.</summary>
    internal int? CoreBypassPeriod { get; set; }

    /// <summary>
    /// Whether the merge in parts of partitions that number one span of values by value cuts it by
    /// value: false to hash them as any other key, the switch the bench compares them with.
    /// </summary>
    internal bool MergeByValue { get; set; } = true;

    /// <summary>
    /// Whether a lane's key numbered by value over a span whose groups fit the private cache numbers it
    /// whole (<see cref="GroupKeys.NumberWhole"/>): false to number values as they first come, the switch
    /// the tests compare them with.
    /// </summary>
    internal bool NumberWhole { get; set; } = true;

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

    /// <summary>
    /// The index of the groups of one partition: a key of one column by its own index, of two to
    /// eight by their indexes' numbers packed into a word, of more by their values encoded into bytes.
    /// </summary>
    /// <param name="sorted">Whether the statistics say the key of one column is sorted.</param>
    /// <param name="facts">What the statistics say of each column, which a composite's parts and a bounded integer read.</param>
    /// <param name="shelf">The lane's shelf under its query's memory, which the key's tables grow from; null for tables nothing counts.</param>
    internal GroupKeys CreateKeys(bool sorted, KeyFacts? facts = null, ArrayShelf? shelf = null) => Keys.Length switch
    {
        1 => Single(Keys[0], sorted, sorted ? null : facts?.Bounds[0], ProbeAhead, facts?.Rows ?? -1, shelf),
        2 or 3 or 4 when Raw(facts) is { } layout => layout.Bits <= 64 ? new RawKeys<ulong>(layout, shelf: shelf) : new RawKeys<UInt128>(layout, shelf: shelf),
        2 => new PackedKeys<ulong>(Keys, facts, shelf: shelf),
        3 or 4 => new PackedKeys<UInt128>(Keys, facts, shelf: shelf),
        <= 8 => new PackedKeys<PackedTuple>(Keys, facts, shelf: shelf),
        _ => new CompositeKeys(Keys, shelf),
    };

    /// <summary>
    /// The layout of the key as the tuple of its values in one word, unless the statistics say a
    /// column is sorted, which hands its rows by range, or bound the
    /// columns' values to a product a table of groups holds: both <see cref="PackedKeys{TKey}"/>'s.
    /// </summary>
    private RawLayout? Raw(KeyFacts? facts)
    {
        if (RawLayout.Of(Keys) is not { } layout)
        {
            return null;
        }

        if (facts is not { } known)
        {
            return layout;
        }

        long product = 1;
        for (int p = 0; p < Keys.Length; p++)
        {
            if (known.Sorted[p])
            {
                return null;
            }

            if (product <= FixedKeys<int>.DirectValues)
            {
                product = known.Bounds[p] is { } bounds && bounds.Max >= bounds.Min && (ulong)(bounds.Max - bounds.Min) < (ulong)FixedKeys<int>.DirectValues
                    ? product * (bounds.Max - bounds.Min + 1)
                    : long.MaxValue;
            }
        }

        return product <= FixedKeys<int>.DirectValues ? null : layout;
    }

    /// <summary>The index of a key of one column.</summary>
    internal static GroupKeys Single(ColumnShape key, bool sorted, KeyBounds? bounds = null, int probeAhead = DefaultProbeAhead, long rows = -1, ArrayShelf? shelf = null) =>
        key.Kind switch
        {
            StorageKind.Primitive => key.PType switch
            {
                PType.I8 => new FixedKeys<sbyte>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.I16 => new FixedKeys<short>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.I32 => new FixedKeys<int>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.I64 => new FixedKeys<long>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.U8 => new FixedKeys<byte>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.U16 => new FixedKeys<ushort>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.U32 => new FixedKeys<uint>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.U64 => new FixedKeys<ulong>(key, sorted, bounds, probeAhead, rows, shelf: shelf),
                PType.F16 => new FixedKeys<Half>(key, sorted, probeAhead: probeAhead, shelf: shelf),
                PType.F32 => new FixedKeys<float>(key, sorted, probeAhead: probeAhead, shelf: shelf),
                _ => new FixedKeys<double>(key, sorted, probeAhead: probeAhead, shelf: shelf),
            },
            StorageKind.Decimal => new FixedKeys<Int128>(key, sorted, probeAhead: probeAhead, shelf: shelf),
            StorageKind.Decimal256 => new FixedKeys<Vorticity.Types.Numerics.Int256>(key, sorted, probeAhead: probeAhead, shelf: shelf),
            StorageKind.Uuid => new FixedKeys<UInt128>(key, sorted, probeAhead: probeAhead, shelf: shelf),
            StorageKind.Bool => new BoolKeys(key),
            StorageKind.Bytes => new ShortTextKeys(key, sorted, probeAhead, shelf),
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
/// <param name="StateBytes">
/// The bytes the groups held at the end of the merge, every lane's and the merge's own: their peak in
/// a run that keeps them all until then (<see cref="AggregationPartition.Footprint"/>). Under the core,
/// its sub-tables, the lanes' caches and every batch it made.
/// </param>
/// <param name="Core">What the core did, when it held the groups; null otherwise.</param>
internal sealed record AggregationRun(AggregationRun.Lane[] Lanes, long MergeTicks, int MergeParts, long StateBytes, CoreRun? Core = null)
{
    /// <summary>The groups of the lanes' tables the merge put into another table: every lane's but the largest's in series, every lane's by parts.</summary>
    internal long MergeEntries { get; init; }

    /// <summary>The runs the lanes wrote to their scratch when the budget held their tables no more and the core could not take them.</summary>
    internal int SpilledRuns { get; init; }

    /// <summary>The bytes of those runs.</summary>
    internal long SpilledBytes { get; init; }

    /// <summary>The bytes read back from them.</summary>
    internal long SpillReadBytes { get; init; }

    /// <summary>What the query held of its budget's ceiling when the first run was written; -1 without one.</summary>
    internal double FirstSpillShare { get; init; } = -1;

    /// <summary>The run's counts with what <paramref name="spill"/> wrote and read, when it wrote anything.</summary>
    internal AggregationRun Spilled(SpillScope? spill) => spill is not { Runs: > 0 } ? this : this with
    {
        SpilledRuns = spill.Runs,
        SpilledBytes = spill.WrittenBytes,
        SpillReadBytes = spill.ReadBytes,
        FirstSpillShare = spill.FirstShare,
    };

    /// <summary>One lane's counts.</summary>
    /// <param name="ActiveTicks">The time its passes took, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="Ranges">The ranges of rows it read.</param>
    /// <param name="Groups">Its groups at the end of its pass, before the merge.</param>
    /// <param name="Rows">The rows it folded.</param>
    /// <param name="Arrays">The arrays its shelf handed out as its tables grew.</param>
    /// <param name="ArrayBytes">Their bytes.</param>
    /// <param name="CopiedBytes">The bytes its tables copied from an array into the one that replaced it.</param>
    internal readonly record struct Lane(long ActiveTicks, int Ranges, int Groups, long Rows = 0, long Arrays = 0, long ArrayBytes = 0, long CopiedBytes = 0);
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

    /// <summary>What the result holds of its query's memory budget, until it is delivered; null for a result nobody counts.</summary>
    internal QueryMemory? Memory { get; set; }

    /// <summary>Gives back what the result held of its query's budget: delivered, it is the caller's.</summary>
    internal void Delivered()
    {
        Parts?.Close();
        if (Memory is { } memory)
        {
            _plan.LastPeakBytes = Math.Max(_plan.LastPeakBytes, memory.Peak);
            memory.Dispose();
        }
    }

    /// <summary>
    /// The parts of the result delivered after these groups, one at a time: a core's, applied in the
    /// background or written to the scratch, or a merge's in parts; null when the result is whole.
    /// </summary>
    internal ResultParts? Parts { get; set; }

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

        // Made on the first read, which the chunks of a top-k may make at once.
        foreach ((AggregateIdentity mean, int sum) in _plan.Shares)
        {
            if (mean.Equals(node.Identity))
            {
                AggregateSlot?[] views = Volatile.Read(ref _views) ?? Interlocked.CompareExchange(ref _views, new AggregateSlot?[_slots.Length], null) ?? _views!;
                return Volatile.Read(ref views[sum]) ?? Interlocked.CompareExchange(ref views[sum], new MeanView((IMeanSlot)_slots[sum]), null) ?? views[sum]!;
            }
        }

        throw new InvalidOperationException($"'{node}' belongs to another aggregation.");
    }

    /// <summary>
    /// The values of the chosen rows let go, and what they held of the query's memory: a part of a result
    /// delivered part by part, once its batches are built.
    /// </summary>
    internal void LetChosen()
    {
        foreach (ChosenValues chosen in _chosen)
        {
            chosen.Release(Memory);
        }
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

    // The rows the slots fold together before the next window (AggregationPlan.FoldWindow).
    private readonly int _window;

    /// <summary>
    /// The bytes of records past which the slots fold a batch a window at a time: 1 MiB, the private
    /// second level of cache of a current core. Under it the
    /// records of every group stay in cache from one slot to the next anyway: in one process, windows
    /// cost a count and a float mean on ten thousand groups 3 %, records of 640 KiB, and won nothing
    /// before a hundred thousand.
    /// </summary>
    private const long WindowedBytes = 1024 * 1024;

    /// <summary>
    /// The bytes of records past which a state of more than 16 bytes no longer carries the count in its
    /// pass: the count then folds first, alone, and the heavy pass finds its records in cache. Carried,
    /// the heavy pass reaches each record first, a miss to memory at the head of a long chain, and holds
    /// few rows in flight. Measured on 2026-10-07, 2e7 rows at one lane: a count and a float's mean ×0.78
    /// at 10⁶ groups (records of 40 MB) and ×0.84 at 10⁷, a count and its deviation ×0.68 at 10⁶; but
    /// ×1.07 to ×1.09 to 10⁵ (4 to 7 MB of records), which a large last level of cache holds.
    /// </summary>
    private const long CarriedBytes = 16L * 1024 * 1024;

    // The source's row the current batch starts at, which a chosen row keeps.
    private long _startRow;

    // What the partition makes its slots from: made again empty once it wrote them to a spill.
    private readonly AggregationPlan _plan;
    private readonly AggregateSlot?[] _settledSlots;
    private readonly ScanSource? _source;

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

    // The rows the partition folded, and whether its groups had seen SettledRows each at the batch folded.
    private long _rowsFolded;
    private bool _settled = true;

    /// <summary>
    /// The rows a group has seen, on the mean, past which its extremes rarely move: the k-th value is a
    /// new maximum one time in k, which a branch predicts past a few dozen.
    /// </summary>
    private const int SettledRows = 32;

    // The blocks the zone maps settled in the partition's rows, the next to fold and the end.
    private ZoneSettling? _settling;
    private int _settledNext;
    private int _settledEnd;
    private ZoneSettling.Scratch? _settledScratch;

    internal AggregationPartition(
        AggregationPlan plan, AggregateSlot?[] settled, ColumnShape[] columns, int[] inputs, bool sorted, int streaming = -1, ScanSource? source = null, KeyFacts? facts = null,
        GroupKeys? keys = null, QueryMemory? memory = null)
    {
        Memory = memory;
        _arrays = memory is null ? null : new ArrayShelf(memory);
        _turnAt = plan.CoreTurnAt;
        _plan = plan;
        _settledSlots = settled;
        _source = source;
        _columns = columns;
        _inputs = inputs;
        _keyCount = plan.Keys.Length;
        _nodes = new int[columns.Length];
        _filterOf = plan.Filters.FilterOf;
        _masks = plan.Filters.Filters.Length > 0 ? new FilterMasks(plan.Filters) : null;
        _window = plan.FoldWindow is int window and > 0 ? window : int.MaxValue;

        // Under the query's memory, the records and a key of one fixed column grow from the lane's
        // shelf, which reserves each array before it comes.
        Slots = NewSlots(plan, settled, source, out GroupRecords? records, _arrays);
        Records = records;
        if (plan.Grouped && keys is null && streaming >= 0 && Few(facts))
        {
            foreach (AggregateSlot slot in Slots)
            {
                slot.FewGroups();
            }
        }

        // The plan's index of the groups, unless the caller brings another: a core's lane bypassing its cache.
        Keys = keys ?? (plan.Grouped ? plan.CreateKeys(sorted, facts, _arrays) : null);
        if (streaming >= 0 && _keyCount > 1)
        {
            _streaming = streaming;
            _componentKeys = AggregationPlan.Single(plan.Keys[streaming], sorted: true);
        }

        if (Keys is null)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (_inputs[i] != Settled)
                {
                    Slots[i].Ungrouped();
                }

                Slots[i].EnsureGroups(1);
            }
        }
    }

    internal AggregateSlot[] Slots { get; private set; }

    /// <summary>The records the slots whose states hold no reference share, a record a group; null when no slot has one.</summary>
    internal GroupRecords? Records { get; private set; }

    /// <summary>
    /// The lane's side of the query's core, which copies the partition into its batches when it fills;
    /// null without a core, or until the lane turns to it under pressure.
    /// </summary>
    internal LaneCore? Core
    {
        get => _core;
        init => _core = value;
    }

    /// <summary>
    /// The core the query turns to when its budget cannot let a lane's table grow; null for a partition
    /// that never turns to one.
    /// </summary>
    internal CorePressure? Pressure
    {
        get => _pressure;
        init
        {
            // A lane that can turn to the core takes past its budget in the middle of a batch, and turns at the next.
            _pressure = value;
            if (_arrays is not null && value is not null)
            {
                _arrays.Overdraws = true;
            }
        }
    }

    /// <summary>
    /// What the lane writes to the query's scratch when its budget holds its groups no more and the core
    /// cannot take them; null for a lane that never spills. Over the whole scan, a slot that holds a set
    /// of values spills alone (<see cref="AggregateSlot.SpillsAlone"/>), and its arrays may take past the
    /// budget within a batch, the lane spilling before the next.
    /// </summary>
    internal SpillScope? Spill
    {
        get => _spill;
        init
        {
            _spill = value;
            _spillsAlone = value is not null && Keys is null && Array.Exists(Slots, slot => slot.SpillsAlone);
            if (_spillsAlone && _arrays is not null)
            {
                _arrays.Overdraws = true;
            }
        }
    }

    private readonly SpillScope? _spill;
    private readonly bool _spillsAlone;

    /// <summary>The lanes the pass runs on, which grow their tables together: what a lane asks the budget for before a batch is that many times its own growth.</summary>
    internal int Lanes { get; init; } = 1;

    /// <summary>
    /// Whether the lane, over the whole scan, writes its slots' sets to the scratch before its next batch
    /// of <paramref name="rows"/> rows: an array it took past the budget in the batch before, or what its
    /// sets may take folding the batch, a doubling, that the budget would not grant on every lane at once.
    /// </summary>
    internal bool MustSpill(int rows)
    {
        if (!_spillsAlone || Memory is not { } memory || _arrays is not { } arrays)
        {
            return false;
        }

        if (arrays.Overdrawn)
        {
            return true;
        }

        long ahead = 0;
        foreach (AggregateSlot slot in Slots)
        {
            ahead += slot.SpillsAlone ? slot.GrowthAhead(rows) : 0;
        }

        return ahead > 0 && !memory.CanGrow(ahead * Lanes);
    }

    /// <summary>The slots that spill alone write their sets to the lane's scratch, each emptied, its arrays given back.</summary>
    internal async ValueTask SpillAloneAsync(CancellationToken cancellationToken)
    {
        SpillBuffer buffer = new SpillBuffer(Memory, Lanes);
        try
        {
            foreach (AggregateSlot slot in Slots)
            {
                if (slot.SpillsAlone)
                {
                    await slot.SpillAsync(_spill!, buffer, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            buffer.Release();
        }

        _arrays!.Relieved();
    }

    // The runs the lane wrote its table to, the file they lie in, and what the tables it emptied held:
    // their key blocks, their groups and their bytes.
    private List<SpillRun>? _runs;
    private SpillFile? _file;
    private (long ByRange, long ByCode, long Hashed) _evictedBlocks;
    private long _evictedGroups;
    private long _evictedBytes;

    // Whether the lane found that the core cannot hold the query, its table written to the scratch instead
    // under pressure; and whether its keys are hashed since, as every table it makes again is.
    private bool _spilling;
    private bool _hashed;

    /// <summary>
    /// Whether the lane knows from its first batch that the core cannot hold the query, which a budget
    /// that cuts its batches finds out before the pass: it asks the budget for what its table would take
    /// folding a batch from then on. Without it, a lane asks for its arrays' doubling until it first
    /// turns, which a key numbered by value outgrows by the pages a batch meets: fourteen lanes of a
    /// distinct count by 10⁶ keys held twice a budget of 14 MiB before the first wrote its table.
    /// </summary>
    internal bool Spilling
    {
        init => _spilling = value;
    }

    /// <summary>The runs the lane wrote its table to; null when it wrote none.</summary>
    internal List<SpillRun>? Runs => _runs;

    /// <summary>Whether the lane wrote its table to the scratch: the query's groups come back from the runs at the end.</summary>
    internal bool Evicted => _runs is not null;

    /// <summary>The groups and the bytes of the tables the lane emptied into its runs: what a group costs a table, which a part read back reserves by.</summary>
    internal (long Groups, long Bytes) EvictedTables => (_evictedGroups, _evictedBytes);

    /// <summary>The key blocks the lane grouped, by how: its table's, and those of the tables it emptied.</summary>
    internal (long ByRange, long ByCode, long Hashed) Blocks
    {
        get
        {
            (long range, long code, long hashed) = Keys?.Blocks ?? default;
            return (range + _evictedBlocks.ByRange, code + _evictedBlocks.ByCode, hashed + _evictedBlocks.Hashed);
        }
    }

    /// <summary>Whether the lane's table can go to a spill: a table that grows, its keys and every state of its slots in bytes.</summary>
    internal bool CanEvict
    {
        get
        {
            if (_spill is null || !_plan.LanesSpill || Keys is not { Spills: true } || _streaming >= 0 || Top is not null || _core is not null)
            {
                return false;
            }

            for (int i = 0; i < Slots.Length; i++)
            {
                if (_inputs[i] != Settled && !Slots[i].SpillsStates)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The lane's table written to its scratch as a run, in sections, a section the groups whose key's
    /// hash has its top byte, each group with its key, its record and the states its slots keep apart;
    /// then let go, what it held of the budget given back, and made again empty from the lane's shelf.
    /// What moves is states: a group goes once, whatever its rows.
    /// </summary>
    internal async ValueTask EvictAsync(CancellationToken cancellationToken)
    {
        DropUnmet();
        if (Keys is not { Count: > 0 } keys)
        {
            return;
        }

        QueryMemory memory = Memory!;
        SpillFile file = _file ??= _spill!.NewFile();
        int count = keys.Count;

        // One lane writes at a time: what writing takes past the budget, a byte and a number a group
        // and a page, is one lane's, each given back with its table before the next lane's.
        SemaphoreSlim gate = _spill!.Writing;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A byte a group for its section, then the groups placed section by section: taken past the
            // budget, which the table they empty gives back more of right after.
            long scratch = (long)count * (sizeof(byte) + sizeof(int));
            memory.Force(scratch);
            memory.Measure(scratch);
            SpillRun run = new SpillRun(file);
            SpillBuffer buffer = new SpillBuffer(memory, Lanes);
            try
            {
                (int[] placed, int[] starts) = Placed(keys, count);
                for (int section = 0; section < SpillRun.Sections; section++)
                {
                    run.Starts[section] = file.Length + buffer.Length;
                    run.Counts[section] = starts[section + 1] - starts[section];
                    WriteSection(placed, starts[section], run.Counts[section], buffer);
                    if (buffer.Full)
                    {
                        await file.AppendAsync(buffer.Written, cancellationToken).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }

                run.Starts[SpillRun.Sections] = file.Length + buffer.Length;
                if (buffer.Length > 0)
                {
                    await file.AppendAsync(buffer.Written, cancellationToken).ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            finally
            {
                buffer.Release();
                memory.LetGo(scratch);
            }

            (_runs ??= []).Add(run);
            _spill.Ran(run.Bytes);
            _evictedGroups += count;
            _evictedBytes += Footprint;
            Emptied();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Whether the partition on the reader's thread of a <c>Distinct</c>, with no core to turn to, writes
    /// its index to the scratch before its next batch of <paramref name="rows"/> rows: an array it took
    /// past the budget in the batch before, or what its index would take were the batch all new values,
    /// which the budget would not grant. From the first call on, its arrays take past the budget within a
    /// batch rather than fail.
    /// </summary>
    internal bool MustEvict(int rows)
    {
        if (!CanEvict || Memory is not { } memory || _arrays is not { } arrays)
        {
            return false;
        }

        arrays.Overdraws = true;
        _spilling = true;
        _coming = rows;
        return MemoryPressed();
    }

    /// <summary>The groups of <paramref name="keys"/> placed section by section, and where each section's start, then the end.</summary>
    private static (int[] Placed, int[] Starts) Placed(GroupKeys keys, int count)
    {
        byte[] sections = new byte[count];
        keys.Sections(sections);
        int[] starts = new int[SpillRun.Sections + 1];
        foreach (byte section in sections)
        {
            starts[section + 1]++;
        }

        for (int section = 0; section < SpillRun.Sections; section++)
        {
            starts[section + 1] += starts[section];
        }

        int[] next = starts[..^1];
        int[] placed = new int[count];
        for (int g = 0; g < count; g++)
        {
            placed[next[sections[g]]++] = g;
        }

        return (placed, starts);
    }

    /// <summary>A section's groups: their keys, then their records, then the states each slot keeps apart, slot after slot.</summary>
    private void WriteSection(int[] placed, int start, int count, SpillBuffer buffer)
    {
        if (count == 0)
        {
            return;
        }

        ReadOnlySpan<int> groups = placed.AsSpan(start, count);
        Keys!.WriteKeys(groups, buffer);
        Records?.Write(groups, buffer);
        for (int i = 0; i < Slots.Length; i++)
        {
            if (_inputs[i] != Settled && Slots[i].StateBytes == 0)
            {
                Slots[i].WriteStates(groups, buffer);
            }
        }
    }

    /// <summary>Whether the lane retires at its range's end, its table merged into the retired lanes', and takes no more ranges.</summary>
    internal bool Retiring { get; private set; }

    /// <summary>
    /// The lane's table, which the budget holds no more while other lanes run: merged into the table the
    /// retired lanes share, the lane going on with an empty one to its range's end, then retiring
    /// (<see cref="LaneRetirement"/>); or, when that does not pay or the shared table cannot take it,
    /// written to the scratch.
    /// </summary>
    private async ValueTask RelieveAsync(CancellationToken cancellationToken)
    {
        if (_plan.LanesRetire)
        {
            (bool merged, bool retires) = await _spill!.Retirement.TakeAsync(this, Retiring, cancellationToken).ConfigureAwait(false);
            if (merged)
            {
                Retiring = retires;
                return;
            }
        }

        await EvictAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The retiring lane at its range's end: what its table holds since merged into the retired lanes', or written to the scratch.</summary>
    internal async ValueTask RetireAsync(CancellationToken cancellationToken)
    {
        if (Keys is { Count: > 0 } && !(await _spill!.Retirement.TakeAsync(this, retiring: true, cancellationToken).ConfigureAwait(false)).Merged)
        {
            await EvictAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A partition of the lane's plan and memory that no lane folds into: the table retired lanes share.</summary>
    internal AggregationPartition Sibling() =>
        new AggregationPartition(_plan, _settledSlots, _columns, _inputs, sorted: false, source: _source, memory: Memory) { Spill = _spill, Lanes = Lanes };

    /// <summary>
    /// The lane's tables handed to <paramref name="retired"/> as they are, with the shelf they grew from
    /// and what they hold of the query's memory; the lane goes on with empty ones from a shelf of its own.
    /// </summary>
    internal void HandTo(AggregationPartition retired)
    {
        // What the tables it replaces held, little or nothing, given back first.
        retired.GiveBack();
        retired.Keys = Keys;
        retired.Slots = Slots;
        retired.Records = Records;
        retired._arrays = _arrays;
        retired.Accounted = Accounted;
        retired.Measured = Measured;
        retired._hashed = _hashed;
        _arrays = Memory is { } memory ? new ArrayShelf(memory) { Overdraws = true } : null;
        Accounted = 0;
        Measured = 0;
        Renew();
    }

    /// <summary>
    /// The lane's table emptied once another holds its groups, or the scratch: in place, its arrays kept
    /// at their length and still counted, once its keys are hashed, so that the next table fills them
    /// without growing; made again otherwise, hashed, what it held given back.
    /// </summary>
    internal void Emptied()
    {
        if (_hashed)
        {
            Keys!.Keep([]);
            Records?.Keep([]);
            foreach (AggregateSlot slot in Slots)
            {
                slot.Clear();
            }
        }
        else
        {
            // The key blocks the table counted, kept: its keys go.
            (long range, long code, long hashed) = Keys!.Blocks;
            _evictedBlocks = (_evictedBlocks.ByRange + range, _evictedBlocks.ByCode + code, _evictedBlocks.Hashed + hashed);
            GiveBack();
            Renew();
        }

        _arrays?.Relieved();
    }

    /// <summary>
    /// Empty tables made again from the lane's shelf: the slots as the partition first made them, the keys
    /// hashed. A key numbered by value would take the pages of its span again after every run.
    /// </summary>
    private void Renew()
    {
        Slots = NewSlots(_plan, _settledSlots, _source, out GroupRecords? records, _arrays);
        Records = records;
        Keys = Keys!.ForSpill(_arrays);
        _hashed = true;
    }

    /// <summary>
    /// Whether the lane, one of the core's lanes on a hashed key, turns to the core when the groups its
    /// first <see cref="JudgedRows"/> rows made say a key of <see cref="TurnValues"/> values or more.
    /// </summary>
    internal bool TurnOnNew { get; init; }

    /// <summary>The rows after which a lane judges its key by the groups they made: a batch of the scan's at least.</summary>
    internal const long JudgedRows = 65_536;

    /// <summary>
    /// Whether the lane, one of the core's lanes on an integer key numbered by value over
    /// <see cref="AggregationPlan.ScatteredSpan"/> values or more, turns to the core when its first batch
    /// spreads over that span as a key in no order spreads (<see cref="Judge"/>): what the zones say
    /// before the pass, where a file has no zones that say it and a dataset none it reads.
    /// </summary>
    internal bool TurnOnSpread { get; init; }

    /// <summary>
    /// The share of the bins over its span that values drawn at random would fall in, from which a
    /// batch's values say the key lies scattered: a key in the order of the rows falls in a few bins, its
    /// own and the sentinels'; one in no order in about all of them.
    /// </summary>
    internal const double SpreadShare = 0.75;

    // Whether the lane's first batch was judged (Judge).
    private bool _spreadJudged;

    /// <summary>
    /// The lane's first range ended before <see cref="JudgedRows"/> rows: it judges its key on that range's
    /// rows alone, rather than on rows the queue hands it next, which come from elsewhere in the source
    /// and differ from one run to the next. The ranges near the queue's end are the shortest, and a fan
    /// starts a lane there.
    /// </summary>
    internal void RangeEnded()
    {
        if (TurnOnNew && !_judged && Ranges == 1 && _rowsFolded > 0 && Pressure is { } pressure && Keys is { NumberedByValue: false } keys)
        {
            _judged = true;
            if (EstimatedValues(_rowsFolded, keys.Count) >= TurnValues)
            {
                pressure.Outgrew(CoreReason.FirstRows, _rowsFolded);
            }
        }
    }

    // The map of the first batch's values over the span, 4 096 bits.
    private ulong[]? _spreadMap;

    /// <summary>
    /// Judges the key on the lane's first batch, before a row of it is folded: its selected values
    /// placed in 4 096 bins over the span, a bit each, and compared to the B (1 − e^(−n/B)) of B bins n
    /// values drawn at random would fall in. Past <see cref="SpreadShare"/> of them, the lane turns to the
    /// core with its table empty. Once a lane, a read of its key column and a bit a row; the bins, not the
    /// values' least and greatest, so that a sentinel adds a bin and no more. Numbered by value, the
    /// lane's table would otherwise allocate a page of its span for nearly every value of that batch,
    /// ten million values a span of 2 442 pages, and lay them all out only to empty them.
    /// </summary>
    internal void Judge(RecordBatch batch)
    {
        if (!TurnOnSpread || _spreadJudged || Keys is not { NumberedByValue: true } keys || batch.RowCount == 0 || batch.SelectedRows == 0)
        {
            return;
        }

        _spreadJudged = true;
        CanonicalArena arena = batch.Arena;
        int node = FilterEvaluator.Resolve(arena, batch.RootIndex, _columns[0].Field, batch.RowCount);
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        _spreadMap ??= new ulong[4096 / 64];
        if (keys.Spread(arena, [node], batch.RowCount, batch.SelectionWords, AggregationPlan.ScatteredSpan, _spreadMap) is not { } spread || spread.Values == 0)
        {
            return;
        }

        double drawn = spread.Bins * -double.ExpM1(-(double)spread.Values / spread.Bins);
        if (spread.Set >= SpreadShare * drawn)
        {
            Pressure!.Outgrew(CoreReason.Spread, _rowsFolded);
        }
    }

    /// <summary>
    /// The groups, nulls included, the statistics bound the key of a streaming group by to at most for
    /// its slots to take the shape of few groups (<see cref="AggregateSlot.FewGroups"/>): a distinct
    /// count's set by group, which costs an object and 32 slots a group where pairs cost none, and which
    /// the groups a batch closes leave to those it opens. Measured on 2026-10-08, the distinct users of
    /// each of 365 days over 20M visits took 155 ms in sets at one lane, 221 in pairs. A group by that
    /// waits for the end of its pass keeps pairs: on a key of 10³ values over 4M rows, a thousand sets a
    /// lane, each doubling from 32 slots, took ×1.05 the pairs' time at one lane and ×2.2 at fourteen,
    /// their arrays a third of a gigabyte a query.
    /// </summary>
    internal const long FewGroups = 4_096;

    /// <summary>Whether the statistics bound every column of the key, and their product, nulls included, to <see cref="FewGroups"/> at most.</summary>
    private static bool Few(KeyFacts? facts)
    {
        if (facts is not { } known || known.Bounds.Length == 0)
        {
            return false;
        }

        long groups = 1;
        foreach (KeyBounds? bounds in known.Bounds)
        {
            if (bounds is not { } span || span.Max < span.Min || (ulong)(span.Max - span.Min) >= FewGroups)
            {
                return false;
            }

            groups *= span.Max - span.Min + 2;
            if (groups > FewGroups)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The rows a lane must have in hand, the source's shared among the lanes, for its key to be judged:
    /// with fewer, its table stays small and its merge cheap, which the core's fixed costs do not repay.
    /// Measured on 2026-10-07 at fourteen lanes: 286 000 rows a lane of a million uuids took the core
    /// 0.55 times the lanes' tables, 143 000 of 1.8M pairs of integers 2.97 times.
    /// </summary>
    internal const long LaneRows = 200_000;

    /// <summary>
    /// The values of a key from which a lane turns: between 10⁵, where the core took 2.5 times the
    /// lanes' tables at fourteen lanes, and 10⁶, where it took 0.42 to 0.72 of them.
    /// </summary>
    internal const double TurnValues = 500_000;

    /// <summary>
    /// The values of a uniform key whose <paramref name="rows"/> draws made <paramref name="groups"/>
    /// distinct ones: the <c>K</c> of <c>groups = K (1 − e^(−rows / K))</c>, found by halving its range;
    /// infinite when every row made a group. A key whose rows favour some values reads as fewer.
    /// </summary>
    internal static double EstimatedValues(long rows, long groups)
    {
        if (groups >= rows)
        {
            return double.PositiveInfinity;
        }

        double low = groups;
        double high = (double)rows * rows;
        for (int step = 0; step < 64 && high - low > 1; step++)
        {
            double middle = Math.Sqrt(low * high);
            if (middle * -double.ExpM1(-rows / middle) < groups)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // Whether the lane judged its first rows (TurnOnNew).
    private bool _judged;

    // Whether the partition reserved its table for the groups its first rows foretold (Foretell).
    private bool _foretold;

    /// <summary>The rows the partition expects to fold, the source's shared among the lanes; -1 when unknown.</summary>
    internal long ExpectedRows { get; init; } = -1;

    /// <summary>
    /// Once <see cref="JudgedRows"/> rows are folded, a table of a hashed key reserves the groups its first
    /// rows foretell for the rows the partition expects: those a uniform key of <see cref="EstimatedValues"/>
    /// values makes from them, as its budget lets it. Grown by doubling, the table placed every group again
    /// at each step, a sixth of a group by of 1.8M pairs of integers. First rows all new reserve the index
    /// for every row new and the keys and their states for half (<see cref="GroupKeys.ReserveAllNew"/>):
    /// at one lane, 10⁶ names over 2M rows, each once then all again, held 66.6 MB rather than 101.9, the
    /// bytes' 66.3, and db-benchmark's q10, every row new, 899 MB rather than 989, both in the same time
    /// (2026-10-09). With no key, each slot foretells its own (<see cref="AggregateSlot.Foretell"/>).
    /// </summary>
    private void Foretell()
    {
        _foretold = true;
        if (Keys is null && ExpectedRows > _rowsFolded)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (_inputs[i] != Settled)
                {
                    Slots[i].Foretell(_rowsFolded, ExpectedRows, Memory, Lanes);
                }
            }

            return;
        }

        if (Keys is not { NumberedByValue: false } keys || keys.Count == 0 || ExpectedRows <= _rowsFolded)
        {
            return;
        }

        // A lane its first rows turn to the core reserves nothing: its table goes.
        double values = EstimatedValues(_rowsFolded, keys.Count);
        if (TurnOnNew && values >= TurnValues)
        {
            return;
        }

        double expected = double.IsPositiveInfinity(values) ? ExpectedRows : values * -double.ExpM1(-ExpectedRows / values);
        long groups = (long)Math.Min(expected, ExpectedRows);
        if (groups < 2L * keys.Count || groups > int.MaxValue / 4)
        {
            return;
        }

        // What the partition takes a group now, its slack included, times the groups foretold and an eighth,
        // on every lane: a few groups past an estimate that held them all doubled the keys and their states
        // at the end of the pass. Without the eighth when the budget does not hold it on every lane, and
        // past what it grants them all, the table grows as before: four lanes that each checked their own
        // room took it together, and the last overdrew the budget.
        long perGroup = Footprint / keys.Count;
        long room = groups + (groups / 8);

        // First rows all new are every row new to the end, or a key whose values come back later, at most
        // half as many groups as rows: the same first rows. A lane alone folds the source's rows and holds
        // no more groups than rows, where the eighth would go past them.
        bool allNew = double.IsPositiveInfinity(values);
        if (allNew && Lanes == 1)
        {
            room = Math.Min(room, ExpectedRows);
        }

        if (Memory is { } memory)
        {
            if (!memory.CanGrow(perGroup * room * Lanes))
            {
                room = groups;
            }

            if (!memory.CanGrow(perGroup * room * Lanes))
            {
                return;
            }
        }

        // The index, which places every group again as it grows, takes the room of every row new; the keys
        // and their states half, which double once if every row stays new. 10⁶ names over 2M rows, each once
        // then all again, foretold as 2M at one lane, held 2.25M keys and states.
        if (allNew)
        {
            keys.ReserveAllNew((int)room);
            Records?.ReserveAllNew((int)room);
        }
        else
        {
            keys.Reserve((int)room);
            Records?.Reserve((int)room);
        }
    }

    private readonly CorePressure? _pressure;

    /// <summary>The query's memory, in which the partition reserves what its groups hold; null for a partition that does not count them.</summary>
    internal QueryMemory? Memory { get; }

    private LaneCore? _core;

    // The plan's batch to turn to the core at, in the tests, and the batches folded so far.
    private readonly int? _turnAt;
    private int _folded;

    // The lane's shelf under the query's memory, which the arrays of its tables come from, each
    // reserved before it is allocated; null without the query's memory. A lane turning to the core
    // takes its cache's.
    private ArrayShelf? _arrays;

    // What the growths of the tables of the shelf the partition let go of, turning to the core, cost.
    private long _handed;
    private long _handedBytes;
    private long _copiedBytes;

    /// <summary>The rows the partition folded.</summary>
    internal long RowsFolded => _rowsFolded;

    /// <summary>What its tables' growths cost: the arrays its shelves handed out, their bytes, and the bytes copied from an array into the one that replaced it.</summary>
    internal (long Arrays, long Bytes, long Copied) Growth =>
        (_handed + (_arrays?.Handed ?? 0), _handedBytes + (_arrays?.HandedBytes ?? 0), _copiedBytes + (_arrays?.CopiedBytes ?? 0));

    /// <summary>The bytes the partition reserved in the query's memory for the arrays its shelf does not hand out, read once a batch.</summary>
    internal long Accounted { get; private set; }

    /// <summary>The bytes of those arrays at the last reading, which the query's memory measures.</summary>
    internal long Measured { get; private set; }

    /// <summary>What the partition's groups hold past the arrays its shelf handed out and counted as they came.</summary>
    private long Unshelved => Math.Max(0, Footprint - (_arrays?.Out ?? 0));

    internal GroupKeys? Keys { get; private set; }

    /// <summary>
    /// The bytes the partition's groups hold, at the capacity of their arrays: the keys' index, the
    /// records, and what each slot holds apart from them.
    /// </summary>
    internal long Footprint => (Keys?.Footprint ?? 0) + (_componentKeys?.Footprint ?? 0) + AggregateSlot.FootprintOf(Slots);

    /// <summary>
    /// Drops the partition's groups, its keys, states and scratch, once another holds them: a lane's
    /// tables die with the merge, whatever still holds the partition. Frames of the pass do: a
    /// blocking group by goes out in continuations of the lane or the merge worker that finished
    /// last, on its stack, and the compiler clears an async method's hoisted locals as their scope
    /// ends, never its parameters (the partitions of <c>MergeAsync</c>, <c>RunQueueAsync</c> and
    /// <c>RunPartitionAsync</c>) nor what a lane's closure captured.
    /// </summary>
    internal void Release()
    {
        Keys = null;
        Slots = [];
        Records = null;
        _rowGroups = [];
        _narrowed = [];
        _componentOf = [];
        _componentRows = [];
        _followMap = [];
        _followComponents = [];
    }

    /// <summary>
    /// Lets the partition's tables go and gives back what it held of its query's memory, the arrays
    /// left to the next collection: a range a stream followed into the
    /// partition before it, whose groups now live there.
    /// </summary>
    internal void LetGo()
    {
        GiveBack();
        Release();
    }

    /// <summary>What the partition's tables held of its query's memory given back, the tables themselves left to the next collection.</summary>
    private void GiveBack()
    {
        if (Memory is { } memory)
        {
            memory.Shrink(Accounted);
            memory.Measure(-Measured);
            _arrays?.LetGo();
            Accounted = 0;
            Measured = 0;
        }
    }

    /// <summary>
    /// The cache of a lane of a lean core, from its first batch: its arrays reserved each alone, nothing
    /// ahead, and taken past the budget when they must, as a lane turned to the core reserves its cache.
    /// Bounded by the core's capacity, the caches are the least the query holds; a quarter
    /// of a megabyte ahead on every lane outweighed the budget of a small <c>Distinct</c>, which failed
    /// where its core would spill.
    /// </summary>
    internal void ReserveExactly()
    {
        if (_arrays is { } arrays)
        {
            arrays.Overdraws = true;
            arrays.Exact = true;
        }
    }

    /// <summary>
    /// The lane turning to <paramref name="core"/>: its table,
    /// which its budget could not let grow once more, emptied into the core's batches, then let go with
    /// what it held; the lane goes on with a cache of the core's size, which takes the null group, the
    /// one group no batch carries.
    /// </summary>
    internal void TurnTo(GroupCore core)
    {
        // Emptying the table takes memory before the table can be given back: the lane's batches take it
        // past the budget meanwhile, and the lane's deposits burst nothing. Its cache takes what it needs
        // past the budget too, whenever it must: bounded by the core's capacity, it is the least the
        // query holds once it turned.
        LaneCore lane = core.Lane();
        _core = lane;
        core.TurnedAfter(_rowsFolded);
        lane.Pressed = true;
        lane.Turning = true;
        lane.Empty(this);
        AggregationPartition cache = core.Cache(Memory);
        if (cache._arrays is { } arrays)
        {
            arrays.Overdraws = true;
            arrays.Exact = true;
        }
        int[]? numbers = null;
        cache.MergeFrom(this, ref numbers, apart: null);
        GiveBack();
        lane.Turning = false;
        Keys = cache.Keys;
        Slots = cache.Slots;
        Records = cache.Records;
        (_handed, _handedBytes, _copiedBytes) = Growth;
        _arrays = cache._arrays;
        Accounted = cache.Accounted;
        Measured = cache.Measured;
    }

    /// <summary>A lane that never turned to the core, given its side of it at the end, which empties its table into the core's batches as a cache's.</summary>
    internal void Join(GroupCore core) => _core ??= core.Lane();

    /// <summary>
    /// The lanes that did not turn to the core during the pass merged into this one, under pressure, at
    /// its end: its table emptied into the core and let go, then every part's stack applied, with no
    /// table left to give back.
    /// </summary>
    internal void TurnAtEnd(GroupCore core)
    {
        if (_core is null && Keys is { Count: > 0 })
        {
            TurnTo(core);
            core.Pressure?.Settle();
            core.ApplyAll(_core!);
        }
    }

    /// <summary>
    /// Whether the lane turns to the core before its next batch: its budget could not
    /// let its table grow once more, or another lane turned.
    /// </summary>
    internal bool MustTurn(int rows)
    {
        _coming = rows;
        return _core is null && Pressure is { } pressure && (pressure.Turned || Pressed());
    }

    // The rows of the batch the lane folds next: as many new groups at most.
    private int _coming;

    /// <summary>
    /// The lane turning to the core, those whose table holds more than the core would cost them, one at a
    /// time when the table takes memory to empty: the others wait for their turn on a task, folding
    /// nothing. One the core cannot take, or that holds too little, keeps its table; one that took past
    /// its budget and cannot turn fails here, between two batches.
    /// </summary>
    internal async ValueTask TurnAsync(CancellationToken cancellationToken)
    {
        // A lane whose table holds less than the core would cost it keeps its table, but for one already
        // past its budget when the core spills, which a lane's table cannot; and but when a lane turned
        // on what its rows showed, where every lane turns while its table is small.
        DropUnmet();
        CorePressure pressure = Pressure!;

        // The core cannot hold the query's groups: what the lane's rows show changes nothing, and its
        // table goes to the scratch once the budget holds it no more, the lane going on with an empty one.
        if (pressure.Core is null && CanEvict)
        {
            _stayed = true;
            _spilling = true;
            if (!MemoryPressed())
            {
                return;
            }

            // A table numbered by value takes the pages of its span as its values come, whatever the
            // budget: one that holds no group yet is hashed from now on, as each run's next is.
            if (!_hashed && Keys!.Count == 0)
            {
                Keys = Keys.ForSpill(_arrays);
                _hashed = true;
            }

            await RelieveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pressure.Core is not { } core || (_turnAt is null && !pressure.Outgrown && _arrays!.Out < core.LaneBytes && !(core.Spills && _arrays.Overdrawn)))
        {
            if (_arrays is { Overdrawn: true })
            {
                throw Memory!.Exceeded("group by", Keys?.Count ?? 1, _arrays.Out);
            }

            _stayed = true;
            Gave();
            await LeaveAsync(pressure.Core, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_arrays!.Out < QueryMemory.Chunk)
        {
            TurnTo(core);
        }
        else
        {
            await pressure.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TurnTo(core);
            }
            finally
            {
                pressure.Exit();
            }
        }

        Gave();
        await LeaveAsync(core, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A lane on the core whose rows the core could not take yet: it waits for the next table given back, or spills a part.</summary>
    internal async ValueTask RoomAsync(CancellationToken cancellationToken)
    {
        LaneCore lane = _core!;
        lane.Starved = false;

        // A table still to come back is waited for; with none left, the largest part goes to the
        // scratch. Otherwise the stack waits on its part, applied at a later burst or at the end.
        if (Pressure is { } pressure && pressure.Running > 0)
        {
            await pressure.RoomAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (lane.Core.CanSpill)
        {
            await lane.Core.SpillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The lane ran out of rows. Under pressure, once the core is made, one that did not turn merges its
    /// table with those of the lanes that ran out before it, in series into the largest, each let go
    /// once merged: they give back memory now, not at the pass's end, to the lanes that still run.
    /// </summary>
    internal async ValueTask EndedAsync(CancellationToken cancellationToken)
    {
        if (Pressure is not { } pressure || _core is not null)
        {
            return;
        }

        Volatile.Write(ref _ended, true);
        GroupCore? core = pressure.Turned ? pressure.Core : null;
        if (core is not null)
        {
            await pressure.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AggregationEngine.MergeUnturned(pressure.Lanes, ended: true);
            }
            finally
            {
                pressure.Exit();
            }
        }

        await LeaveAsync(core, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The lane leaves the lanes that run with a table they may give back, once. The last to leave, once
    /// the core is made, empties the table the lanes that ran out of rows merged into: no table is left
    /// that the stacks waiting for room would wait for.
    /// </summary>
    private async ValueTask LeaveAsync(GroupCore? core, CancellationToken cancellationToken)
    {
        if (_left)
        {
            return;
        }

        _left = true;
        CorePressure pressure = Pressure!;
        if (pressure.Leave() == 0 && core is not null)
        {
            await pressure.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (AggregationEngine.MergeUnturned(pressure.Lanes, ended: true) is { } largest)
                {
                    largest.TurnTo(core);
                    largest.Gave();
                }
            }
            finally
            {
                pressure.Exit();
            }
        }
    }

    /// <summary>The lane's table given back to its pressure's count, once: emptied into the core, merged into another, or too small to count.</summary>
    internal void Gave()
    {
        if (Pressure is { } pressure && !_given)
        {
            _given = true;
            pressure.GaveBack();
        }
    }

    /// <summary>Whether the lane ran out of rows without turning to the core, under pressure.</summary>
    internal bool HasEnded => Volatile.Read(ref _ended);

    // Whether the lane kept its table when it was to turn, the core unable to hold the query or its table
    // too small to repay emptying it.
    private bool _stayed;

    // Whether the lane left the lanes that run with a table they may give back, gave its table back to
    // the count, and ran out of rows.
    private bool _left;
    private bool _given;
    private bool _ended;

    /// <summary>
    /// Whether the lane's budget could not let its table grow once more: its arrays doubled, the entries
    /// the table would empty into, and a megabyte past them, on every lane that still grows one, asked
    /// without reserving; or an array it took past the budget in the batch before; or the plan's batch to
    /// turn at, in the tests. The table keeps everything its budget lets it hold: the lanes' tables stay
    /// the way the query runs while they fit.
    /// </summary>
    private bool Pressed()
    {
        if (_turnAt is int turnAt)
        {
            return _folded++ >= turnAt;
        }

        // A lane's first rows turned the query to the core: a key its first batch showed scattered over a
        // wide span (Judge), the lane turning before it folds a row; or the first rows below. Once,
        // unless the lane kept its table, the core unable to hold the query.
        if (Pressure!.Outgrown && !_stayed)
        {
            return true;
        }

        // Nearly every one of its first rows a new group, a hashed key of a million values or more: the
        // lane turns to the core while its table is a batch's, which emptying costs
        // little, where turning once the table outgrew the cache paid for it twice.
        if (TurnOnNew && !_judged && _rowsFolded >= JudgedRows)
        {
            _judged = true;
            if (Keys is { NumberedByValue: false } keys && EstimatedValues(_rowsFolded, keys.Count) >= TurnValues)
            {
                Pressure.Outgrew(CoreReason.FirstRows, _rowsFolded);
                return true;
            }

            _windowStart = _rowsFolded;
            _windowGroups = Keys?.Count ?? 0;
            _rateNewest = _rowsFolded > 0 ? (double)_windowGroups / _rowsFolded : 0;
            _windows = 1;
        }

        if (_judged && !_projected && _rowsFolded - _windowStart >= JudgedRows && Projects())
        {
            Pressure.Outgrew(CoreReason.Projection, _rowsFolded);
            return true;
        }

        return MemoryPressed();
    }

    // The projection past the first rows (Projects): where the window of rows it counts began, the groups then,
    // the rate of new groups of the last three windows, newest first, the windows counted, and whether the
    // lane judged it for good.
    private long _windowStart;
    private int _windowGroups;
    private double _rateNewest;
    private double _rateBefore;
    private double _rateOldest;
    private int _windows;
    private bool _projected;

    /// <summary>
    /// Whether the lane's groups at the end, projected from the rate of its new groups, take the query to the
    /// core: counted window after window of <see cref="JudgedRows"/> rows, never row by row. A key drawn from a
    /// fixed set of values brings new groups at a falling rate, e^(−r/K), which the uniform model of its
    /// first rows already judged (<see cref="EstimatedValues"/>); a key of sessions, a few rows each, at a rate
    /// that does not fall, which that model takes for a few tens of thousands of values (65 536 rows, 32 768
    /// groups: 4·10⁴). Past three windows, a rate that has kept four fifths of itself over two windows makes
    /// the groups held plus the rate times the rows left; past <see cref="TurnValues"/>, with rows left to repay
    /// emptying the table, twice its groups, the lane turns, once. A key in the order of the rows, every batch
    /// new groups the core takes no better, never turns (<see cref="GroupKeys.Ascending"/>).
    /// </summary>
    private bool Projects()
    {
        GroupKeys? keys = Keys;
        if (keys is not { NumberedByValue: false } || ExpectedRows <= 0)
        {
            _projected = true;
            return false;
        }

        long rows = _rowsFolded - _windowStart;
        int added = keys.Count - _windowGroups;
        _rateOldest = _rateBefore;
        _rateBefore = _rateNewest;
        _rateNewest = (double)added / rows;
        _windowStart = _rowsFolded;
        _windowGroups = keys.Count;
        if (++_windows < 3)
        {
            return false;
        }

        // A falling rate is a key of fixed values, which the first rows judged: the lane projects no more.
        if (_rateNewest < 0.8 * _rateOldest)
        {
            _projected = true;
            return false;
        }

        long left = ExpectedRows - _rowsFolded;
        if (keys.Count + (_rateNewest * left) < TurnValues || left < 2L * keys.Count)
        {
            return false;
        }

        _projected = true;
        return !keys.Ascending(keys.Count - added, keys.Count);
    }

    /// <summary>
    /// Whether the budget holds the lane's table no more: an array it took past the budget in the batch
    /// before, or a doubling and the entries its table empties into the core, on every lane that still
    /// grows one, that the budget would not grant. The lanes grow together, and each one's doubling past
    /// the budget would come at once. A lane whose table spills empties it into its scratch a page at a
    /// time instead, and asks only when its next batch may make its table grow: a table at its size for
    /// good would otherwise go to the scratch under a budget that holds it twice.
    /// </summary>
    private bool MemoryPressed()
    {
        if (_arrays is { Overdrawn: true })
        {
            return true;
        }

        if (Memory is not { } memory)
        {
            return false;
        }

        int running = Math.Max(1, Pressure?.Running ?? 1);
        if (!_spilling)
        {
            return !memory.CanGrow(((2 * _arrays!.Out) + QueryMemory.Chunk) * running);
        }

        long growth = GrowthFor(_coming);
        return growth > 0 && !memory.CanGrow((growth + SpillBuffer.PageOf(memory, Lanes)) * running);
    }

    /// <summary>
    /// The bytes the table would take more merging <paramref name="lane"/>'s, <paramref name="groups"/> of
    /// its groups new here: the table the retired lanes share, before it takes a lane's.
    /// </summary>
    internal long GrowthFor(AggregationPartition lane, int groups)
    {
        long bytes = (Keys?.GrowthFor(groups) ?? 0) + (Records?.GrowthFor(groups) ?? 0);
        for (int i = 0; i < Slots.Length; i++)
        {
            bytes += _inputs[i] == Settled ? 0 : Slots[i].GrowthFor(lane.Slots[i], groups);
        }

        return bytes;
    }

    /// <summary>The bytes the lane's table would take more were <paramref name="rows"/> rows to come, as many new groups and values at most.</summary>
    internal long GrowthFor(int rows)
    {
        long bytes = (Keys?.GrowthFor(rows) ?? 0) + (Records?.GrowthFor(rows) ?? 0);
        foreach (AggregateSlot slot in Slots)
        {
            bytes += slot.GrowthFor(rows);
        }

        return bytes;
    }

    /// <summary>A slot for each aggregate of the plan: the settled one, or a new one.</summary>
    internal static AggregateSlot[] NewSlots(AggregationPlan plan, AggregateSlot?[] settled, ScanSource? source) =>
        NewSlots(plan, settled, source, out _);

    /// <summary>
    /// A slot for each aggregate of the plan, the settled one or a new one, the new ones whose states
    /// hold no reference sharing <paramref name="records"/>, a record a group.
    /// </summary>
    internal static AggregateSlot[] NewSlots(AggregationPlan plan, AggregateSlot?[] settled, ScanSource? source, out GroupRecords? records, ArrayShelf? shelf = null)
    {
        AggregateSlot[] slots = new AggregateSlot[settled.Length];
        for (int i = 0; i < settled.Length; i++)
        {
            if (settled[i] is { } answer)
            {
                slots[i] = answer;
                continue;
            }

            slots[i] = plan.Aggregates[i].Create(source, plan.MeanRead[i]);
            if (shelf is not null)
            {
                slots[i].Govern(shelf);
            }
        }

        records = null;
        if (RecordLayout.Of(slots) is { } layout)
        {
            records = new GroupRecords(layout, shelf);
            for (int i = 0; i < slots.Length; i++)
            {
                if (layout.Offsets[i] >= 0)
                {
                    slots[i].Bind(records, layout.Offsets[i]);
                }
            }
        }

        return slots;
    }

    /// <summary>The time the partition's passes took, in <see cref="Stopwatch"/> ticks: the lane's active time.</summary>
    internal long ActiveTicks { get; set; }

    /// <summary>The ranges of rows the partition read.</summary>
    internal int Ranges { get; set; }

    /// <summary>The partition's groups when its last pass ended, before any merge.</summary>
    internal int GroupsAtEnd { get; set; }

    /// <summary>
    /// The group of the last row read whose key is not null, or -1: the group a key that streams
    /// keeps open; on a composite key, the group of the streaming component's value. Read
    /// <see cref="Backward"/>, the group of the batch's first such row, its smallest key.
    /// </summary>
    internal int LastValueGroup { get; private set; } = -1;

    /// <summary>
    /// Whether the batches come last one first, each in file order, on a key sorted ascending: the
    /// group a batch leaves open is then that of its first row, whose key the batches before it in
    /// the file may still hold.
    /// </summary>
    internal bool Backward { get; init; }

    /// <summary>The value of the streaming component a group holds, as the number <see cref="LastValueGroup"/> is: the group itself on a key of one column.</summary>
    internal int ComponentOf(int group) => _componentKeys is null ? group : _componentOf[group];

    /// <summary>The number of the streaming component's null, or -1.</summary>
    internal int ComponentNull => (_componentKeys ?? Keys!).NullNumber;

    /// <summary>
    /// Keeps the groups <paramref name="groups"/> alone, keys and states, numbered again from 0 in
    /// their order, the indexes keeping their size: a top's best groups.
    /// </summary>
    internal void Keep(ReadOnlySpan<int> groups) => Keep(groups, carry: false);

    /// <summary>
    /// <see cref="Keep(ReadOnlySpan{int})"/> for a stream once it has delivered the groups it closed, the
    /// indexes making room for as many groups as those brought (<see cref="GroupKeys.Carry"/>).
    /// </summary>
    internal void Carry(ReadOnlySpan<int> groups) => Keep(groups, carry: true);

    private void Keep(ReadOnlySpan<int> groups, bool carry)
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
            if (carry)
            {
                _componentKeys.Carry(components[..count]);
            }
            else
            {
                _componentKeys.Keep(components[..count]);
            }
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

        if (carry)
        {
            Keys!.Carry(groups);
        }
        else
        {
            Keys!.Keep(groups);
        }

        Records?.Keep(groups);
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

    /// <summary>The first groups of an order on the key the query takes, which the partition keeps alone as it goes; null to keep every group.</summary>
    internal KeyTop? Top { get; init; }

    /// <summary>
    /// Whether a key numbered by value may number its span whole (<see cref="GroupKeys.NumberWhole"/>): a
    /// lane of a group by's pass, whose groups no one reads before its rows are folded but through
    /// <see cref="DropUnmet"/>; never with a top, the core or a key that streams.
    /// </summary>
    internal bool NumbersWhole { get; init; }

    // Whether the partition judged at its first batch whether its key numbers its span whole.
    private bool _wholeJudged;

    /// <summary>
    /// The bytes of a group's record, or -1 when a slot keeps its states apart. The groups no row met are
    /// dropped by a keep, which moves the records within their array, but a set of values a group to
    /// arrays beside the old: a distinct count by a thousand values numbered whole, three in ten never
    /// met, held 47 MB at its peak at one lane rather than 29.
    /// </summary>
    private long RecordBytes()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            if (_inputs[i] != Settled && Slots[i].StateBytes == 0)
            {
                return -1;
            }
        }

        return (long)(Records?.Layout.Stride ?? 0) * sizeof(ulong);
    }

    /// <summary>
    /// The groups of a span numbered whole that no row met, dropped, keys and states: before anyone else
    /// reads the partition's groups, at the end of its lane, or when it turns to the core or writes its
    /// table to the scratch. Its key numbers values as they first come from then on.
    /// </summary>
    internal void DropUnmet()
    {
        if (Keys?.Met() is { } met)
        {
            Keep(met);
            GroupsAtEnd = met.Length;
        }
    }

    /// <summary>The groups the partition held at most since its top last counted them.</summary>
    internal int PeakGroups { get; set; }

    /// <summary>The worst of the groups the top kept at its last trim, which no row past it joins; -1 before.</summary>
    internal int TopFrontier { get; set; } = -1;

    /// <summary>
    /// The key of the worst value a top on a column's extreme kept at its last trim, which a row's value
    /// must reach to join it (<see cref="ValueFrontier"/>); null before.
    /// </summary>
    internal long? TopEdge { get; set; }

    // The batch's selection less the rows past the top's frontier.
    private ulong[] _narrowed = [];

    /// <summary>Folds the settled blocks no batch came after: what the end of the rows leaves.</summary>
    internal void Finish()
    {
        FoldSettled(long.MaxValue);
        Top?.Trim(this, final: true);
    }

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

        // Under the core, a lane whose cache finds too few of its keys folds its rows apart, a group
        // each.
        if (Core is { } core && core.Bypasses(batch))
        {
            return;
        }

        int before = Keys?.Count ?? 0;
        Fold(batch);
        Top?.Trim(this, final: false);
        Core?.Fold(this, batch.SelectedRows, before);
        if (Memory is { } memory)
        {
            Account(memory);
        }
    }

    /// <summary>Reserves what the partition's groups grew to through a merge into it.</summary>
    internal void Recount()
    {
        if (Memory is { } memory)
        {
            Account(memory);
        }
    }

    /// <summary>Gives back what the partition reserved past what its groups hold: the room for a doubling, once its table grows no more.</summary>
    internal void Trim()
    {
        long unshelved = Math.Max(0, Unshelved - AggregationEngine.LaneTables);
        if (Memory is { } memory && Accounted > unshelved)
        {
            memory.Shrink(Accounted - unshelved);
            Accounted = unshelved;
        }

        _arrays?.GiveBackAhead();
    }

    /// <summary>
    /// Reserves twice what the partition's groups hold past the arrays its shelf hands out, a megabyte
    /// ahead at least: a reading of their lengths, once a batch. Those the shelf hands out, it reserves
    /// as they come; the others are read here, until every
    /// table grows from a shelf. Twice, because a table doubles within a batch, before the next
    /// reading: its new arrays are reserved before they come, the old ones beside them only for the
    /// copy, a moment no reading sees. What the groups hold is measured too: grown, the arrays they
    /// replaced stay in the heap until a collection, as much as they grew by when they doubled. Past
    /// the budget, the query fails, its memory given back as it unwinds.
    /// </summary>
    private void Account(QueryMemory memory)
    {
        long footprint = Unshelved;
        if (footprint != Measured)
        {
            if (Measured > 0 && footprint > Measured)
            {
                memory.Discard(Math.Min(Measured, footprint - Measured));
            }

            memory.Measure(footprint - Measured);
            Measured = footprint;
        }

        // The lane's first tables come out of the working memory it was admitted with.
        long need = (2 * Math.Max(0, footprint - AggregationEngine.LaneTables)) - Accounted;
        if (need <= 0)
        {
            return;
        }

        long grow = Math.Max(need, QueryMemory.Chunk);
        if (!memory.TryGrow(grow))
        {
            // The megabyte ahead may be what does not fit: the need alone, before failing.
            if (grow == need || !memory.TryGrow(need))
            {
                // A lane on the core folds a batch into its cache before it measures it: a batch of new
                // groups past the cache's capacity, at most, which the next flush empties. It is counted
                // past the budget, as the core then spills what it must; a lane whose table spills, as it
                // writes its table before its next batch.
                if (_core is null && !CanEvict)
                {
                    throw memory.Exceeded("group by", Keys?.Count ?? 1, need);
                }

                memory.Force(need);
                _arrays?.Overdrew();
            }

            grow = need;
        }

        Accounted += grow;
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

        // A row whose key lies past the worst of the top's groups is not grouped, nor folded.
        if (TopFrontier >= 0)
        {
            int words = (rows + 63) >> 6;
            Scratch.Grow(ref _narrowed, words);
            if (Keys!.Narrow(arena, _nodes.AsSpan(0, _keyCount), rows, selection, TopFrontier, Top!.Descending, _narrowed))
            {
                selection = _narrowed.AsSpan(0, words);
                if (selection.IndexOfAnyExcept(0UL) < 0)
                {
                    return;
                }
            }
        }

        // On a column's extreme, a row whose value falls short of the worst the top kept changes none
        // of its groups, nor makes one: the extreme is the query's one aggregate.
        if (TopEdge is long edge)
        {
            int words = (rows + 63) >> 6;
            Scratch.Grow(ref _narrowed, words);
            int input = _inputs[0];
            if (ValueFrontier.Narrow(arena, _nodes[input], _columns[input], rows, selection, edge, Top!.Descending, _narrowed))
            {
                selection = _narrowed.AsSpan(0, words);
                if (selection.IndexOfAnyExcept(0UL) < 0)
                {
                    return;
                }
            }
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

            _rowsFolded += rows;
            if (!_foretold && _rowsFolded >= JudgedRows)
            {
                Foretell();
            }

            return;
        }

        // A key numbered by value over a span whose records fit the private cache numbers it whole from
        // the first row: a row's group is its value less the least once every value is met.
        if (NumbersWhole && !_wholeJudged)
        {
            _wholeJudged = true;
            if (Top is null && _core is null && _componentKeys is null && RecordBytes() is long bytes and >= 0)
            {
                Keys.NumberWhole(bytes);
            }
        }

        Scratch.Grow(ref _rowGroups, rows);
        _ranges.Clear();
        int before = Keys.Count;
        bool ranged = Keys.Assign(arena, _nodes.AsSpan(0, _keyCount), rows, selection, _rowGroups, _ranges);
        int groups = Keys.Count;
        if (ranged && _componentKeys is not null)
        {
            // The component each group holds is read row by row: a composite key's ranges spread first.
            Span<int> fill = _rowGroups.AsSpan(0, rows);
            for (int r = 0; r < _ranges.Count; r++)
            {
                fill[_ranges.StartAt(r).._ranges.EndAt(r)].Fill(_ranges.GroupAt(r));
            }
        }

        LastValueGroup = _componentKeys is null
            ? LastValue(ranged, rows, selection, LastValueGroup)
            : Components(arena, rows, selection, before, groups);

        // Groups that have each seen many rows hold extremes that rarely move, which a branch predicts:
        // before, the slots fold without one.
        _rowsFolded += rows;
        _settled = _rowsFolded >= SettledRows * (long)groups;
        if (!_foretold && _rowsFolded >= JudgedRows)
        {
            Foretell();
        }

        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i].EnsureGroups(groups);
        }

        if (!ranged)
        {
            // The slots fold the batch a window at a time: the window's groups and values, and the
            // records of its groups, stay in the first level of cache from one slot to the next.
            // Records that all fit a private cache stay there whatever the slot, and the batch is
            // folded whole: a window would cost its calls for nothing.
            ReadOnlySpan<int> rowGroups = _rowGroups.AsSpan(0, rows);
            long recordBytes = Records is { } records ? (long)groups * records.Layout.Stride * sizeof(ulong) : 0;
            int window = recordBytes > WindowedBytes ? _window : rows;
            (int count, int carrier) = CountCarrier(recordBytes > CarriedBytes);
            for (int start = 0; start < rows; start += window)
            {
                int end = Math.Min(rows, start + window);

                // The count rides on a fixed slot's pass when both fold the window's every row into one
                // record: a row's record reached once for both. With no slot
                // to carry it, it folds first, alone: past the cache, its short pass brings the window's
                // records in, many rows in flight, for the heavy passes after it.
                bool carried = carrier >= 0 && Folds(count)
                    && Slots[carrier].StepRowsCounted(Input(number, arena, carrier, rows, selection).Window(start, end), rowGroups, Slots[count]);
                int first = carrier < 0 && count >= 0 && Folds(count) ? count : -1;
                if (first >= 0)
                {
                    Slots[first].StepRows(Input(number, arena, first, rows, selection).Window(start, end), rowGroups);
                }

                for (int i = 0; i < Slots.Length; i++)
                {
                    if (i != first && Folds(i) && !(carried && (i == count || i == carrier)))
                    {
                        Slots[i].StepRows(Input(number, arena, i, rows, selection).Window(start, end), rowGroups);
                    }
                }
            }

            return;
        }

        // An aggregate folds a batch's ranges in one call. Ranges this short, a key in runs of
        // seven rows, are folded row by row by an aggregate a range costs more than its rows.
        bool shortRanges = (long)_ranges.Count * ShortRange > batch.SelectedRows;
        bool filled = _componentKeys is not null;
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
        int first = -1;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int group = _rowGroups[row];
            int component = _componentRows[row];
            if (_componentOf[group] < 0)
            {
                _componentOf[group] = component;
            }

            if (component != nullComponent)
            {
                last = component;
                first = first < 0 ? component : first;
            }
        }

        int open = Backward ? first : last;
        return open < 0 || nullComponent == open ? LastValueGroup : open;
    }

    /// <summary>The group of the batch's last selected row whose key is not null, its first when read <see cref="Backward"/>; <paramref name="previous"/> when every one is null.</summary>
    private int LastValue(bool ranged, int rows, ReadOnlySpan<ulong> selection, int previous)
    {
        int nullGroup = Keys!.NullNumber;
        if (ranged)
        {
            for (int at = 0; at < _ranges.Count; at++)
            {
                int r = Backward ? at : _ranges.Count - 1 - at;
                if (_ranges.GroupAt(r) != nullGroup)
                {
                    return _ranges.GroupAt(r);
                }
            }

            return previous;
        }

        for (int at = 0; at < rows; at++)
        {
            int row = Backward ? at : rows - 1 - at;
            if ((selection.IsEmpty || ((selection[row >> 6] >> (row & 63)) & 1) != 0) && _rowGroups[row] != nullGroup)
            {
                return _rowGroups[row];
            }
        }

        return previous;
    }

    /// <summary>
    /// <see cref="Follow"/>, its arrays taken past the budget when it refuses them rather than the merge
    /// failing halfway; whether they were, the partition then relieved.
    /// </summary>
    /// <param name="next">The partition of the next range.</param>
    /// <param name="numbers">The numbers of the groups of a partition past those every thread shares, the caller's.</param>
    internal bool FollowPast(AggregationPartition next, ref int[]? numbers)
    {
        bool overdraws = OverdrawFrom();
        try
        {
            Follow(next, ref numbers);
        }
        finally
        {
            OverdrawTo(overdraws);
        }

        return Overdrew();
    }

    /// <summary>
    /// Folds in <paramref name="earlier"/>, the open groups of the rows before this partition's, which
    /// stands for both from then on: the few groups those rows left open where following this partition
    /// would put all of its own into them. Taken past the budget as <see cref="FollowPast"/> takes them;
    /// whether they were.
    /// </summary>
    /// <param name="earlier">The partition of the rows before.</param>
    /// <param name="numbers">The numbers of the groups of a partition past those every thread shares, the caller's.</param>
    internal bool AbsorbPast(AggregationPartition earlier, ref int[]? numbers)
    {
        bool overdraws = OverdrawFrom();
        try
        {
            MergeFrom(earlier, ref numbers);
        }
        finally
        {
            OverdrawTo(overdraws);
        }

        return Overdrew();
    }

    /// <summary>The partition's arrays taken past the budget from now on; whether they were before.</summary>
    private bool OverdrawFrom()
    {
        bool overdraws = _arrays?.Overdraws ?? false;
        if (_arrays is { } arrays)
        {
            arrays.Overdraws = true;
        }

        return overdraws;
    }

    private void OverdrawTo(bool overdraws)
    {
        if (_arrays is { } arrays)
        {
            arrays.Overdraws = overdraws;
        }
    }

    /// <summary>Whether the arrays were taken past the budget, the partition relieved.</summary>
    private bool Overdrew()
    {
        bool overdrawn = _arrays is { Overdrawn: true };
        _arrays?.Relieved();
        return overdrawn;
    }

    /// <summary>
    /// Folds in the partition of the range of rows after this one's, as its batches would have been
    /// folded: its groups mapped onto this one's by key, the new ones numbered after, in the order
    /// they were met; the streaming component of each, and the last value met, carried over.
    /// </summary>
    /// <param name="next">The partition of the next range.</param>
    /// <param name="numbers">The numbers of the groups of a partition past those every thread shares (<see cref="Numbers"/>), the caller's.</param>
    internal void Follow(AggregationPartition next, ref int[]? numbers)
    {
        int before = Keys!.Count;
        Scratch.Grow(ref _followMap, next.Keys!.Count);
        Span<int> map = _followMap.AsSpan(0, next.Keys.Count);
        ReadOnlySpan<int> all = Numbers.Upto(map.Length, ref numbers);
        next.Keys.MergeInto(Keys, all, map);
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i].EnsureGroups(Keys.Count);
            if (_inputs[i] != Settled)
            {
                Slots[i].MergeFrom(next.Slots[i], all, map);
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
        next._componentKeys.MergeInto(_componentKeys, Numbers.Upto(components.Length, ref numbers), components);
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
    /// <param name="other">The partition folded in.</param>
    /// <param name="numbers">The numbers of the groups of a partition past those every thread shares (<see cref="Numbers"/>), the caller's.</param>
    /// <param name="apart">The slots left to merge apart, by parts of their pairs (<see cref="IPairedSlot"/>); null for none.</param>
    /// <returns>The map of the other's groups onto this one's.</returns>
    internal int[] MergeFrom(AggregationPartition other, ref int[]? numbers, bool[]? apart = null)
    {
        int[] map;
        if (Keys is null)
        {
            map = [0];
        }
        else
        {
            map = new int[other.Keys!.Count];
            other.Keys.MergeInto(Keys, Numbers.Upto(map.Length, ref numbers), map);
            foreach (AggregateSlot slot in Slots)
            {
                slot.EnsureGroups(Keys.Count);
            }
        }

        ReadOnlySpan<int> all = Numbers.Upto(map.Length, ref numbers);
        for (int i = 0; i < Slots.Length; i++)
        {
            if (_inputs[i] != Settled && (apart is null || !apart[i]))
            {
                Slots[i].MergeFrom(other.Slots[i], all, map);
            }
        }

        return map;
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
        return new BatchInput(number, arena, column >= 0 ? _nodes[column] : -1, rows, filter < 0 ? selection : _masks!.Selection(filter), _startRow, _settled);
    }

    /// <summary>Whether the aggregate has rows of the batch to fold: none when its filter keeps none.</summary>
    private bool Folds(int slot) => _filterOf[slot] < 0 || !_masks!.KeepsNone(_filterOf[slot]);

    /// <summary>
    /// The slot of a count of rows in the records, unfiltered, and the fixed slot that can carry it in its
    /// own pass: unfiltered too, its state in the same record, and when <paramref name="large"/> records
    /// pass the cache, a state of 16 bytes at most (<see cref="CarriedBytes"/>). The carrier is -1 when no
    /// slot can; both are when there is no such count.
    /// </summary>
    private (int Count, int Carrier) CountCarrier(bool large)
    {
        int count = Array.FindIndex(Slots, slot => slot is CountSlot<uint> or CountSlot<long>);
        if (count < 0 || _filterOf[count] >= 0 || Slots[count].Bound is not { } records)
        {
            return (-1, -1);
        }

        for (int i = 0; i < Slots.Length; i++)
        {
            if (i != count && _filterOf[i] < 0 && Slots[i].CarriesCount && ReferenceEquals(Slots[i].Bound, records)
                && (!large || Slots[i].StateBytes <= 16))
            {
                return (count, i);
            }
        }

        return (count, -1);
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

                try
                {
                    await ChosenFetch.FetchAsync(outcome, Source, spec, Metrics, groups, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    outcome.Delivered();
                    throw;
                }
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
    /// <param name="query">The query.</param>
    /// <param name="builder">What builds the batches of the parts a core delivers one at a time.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The merged states and keys, and the groups delivered, in order, before the result's window.</returns>
    internal async ValueTask<(AggregationOutcome Outcome, int[] Groups, int Count)> RunAsync(AggregationQuery query, PartBuilder builder, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            ScanSpec spec = Spec(query.RowFilter);

            // The first groups of an order on the key: each lane keeps the best it has met alone; with
            // no order, one lane keeps the first it met.
            KeyTop? top = KeyTop.Of(query) ?? KeyTop.FirstOf(query);

            // With no order nor window over the groups, a core delivers them part by part.
            bool parted = query.Plan.CoreParted && top is null && !Array.Exists(query.Operators, op => op is GroupOrder or GroupWindow);
            AggregationOutcome outcome = await AggregationEngine.RunAsync(Source, spec, Metrics, query.Plan, cancellationToken, top, parted, builder).ConfigureAwait(false);
            query.PeakGroups = Math.Max(top?.Peak ?? 0, outcome.Keys?.Count ?? 1);
            if (outcome.Parts is not null && Array.Exists(query.Operators, op => op is GroupOrder))
            {
                // Groups spilled under an order: the reader sorts them in runs, each part through the
                // operators before the order as it comes.
                return (outcome, [], 0);
            }

            try
            {
                (int[] groups, int count) = await GroupSelection.ApplyAsync(query, outcome, spec, cancellationToken).ConfigureAwait(false);
                return (outcome, groups, count);
            }
            catch
            {
                outcome.Delivered();
                throw;
            }
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
        ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPlan plan, CancellationToken cancellationToken, KeyTop? top = null, bool parted = false,
        PartBuilder? builder = null)
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
        KeyFacts? facts = plan.Grouped ? await FactsAsync(source, plan.Keys, cancellationToken).ConfigureAwait(false) : null;
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

        // A file's rows are cut here, at its chunks; another source cuts its own, a dataset at its objects.
        AggregationPartition[] partitions;
        RowRange[]? ranges = Ranges(source, pass, degree)
            ?? (degree > 1 && pass.Take is null && !pass.MatchesNothing && source is not FileScanSource
                ? await source.PiecesAsync(pass, degree, cancellationToken).ConfigureAwait(false)
                : null);
        // What the lanes' tables and the merge hold, reserved in the session's budget as they grow,
        // brought down to the result once the groups are merged and given back when it is delivered;
        // all of it as soon as the query fails.
        QueryMemory memory = new QueryMemory(source.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        GroupCore? core = null;
        CorePressure? pressure = null;

        // What the lanes write to the scratch when the budget holds them no more and the core cannot take
        // them: no file until one does.
        SpillScope? spill = plan.LanesSpill ? new SpillScope(source.Session.Options, memory) : null;
        try
        {
            // The lanes' batches under the budget, then each lane's working memory admitted: on fewer
            // lanes when its budget is short.
            int asked = ranges is null ? 1 : Math.Min(degree, ranges.Length);

            // A query whose groups the lanes' tables alone hold, which spill under pressure: its batches of
            // new groups, on every lane at once, a sixteenth of the ceiling at most, which is what a lane
            // takes past the budget before it writes its table. Asked only of a budget that would cut them.
            int spilledRow = spill is not null && plan.Grouped && !sorted && top is null
                && memory.Ceiling / 16 / asked / LaneRowBytes < GroupBatches.BatchRows && !GroupCore.Holds(plan, settled, source, facts) ? LaneRowBytes : 0;
            pass = Batched(pass, memory, asked, spilledRow);

            // Lanes that read ahead over a source that copies its reads hold their splits in flight beside
            // their working memory, admitted with it: the largest split of the layout, so many a lane.
            long splitBytes = ranges is null ? 0 : source.ReadAheadBytes(pass);
            int lanes = Admit(memory, asked, pass.Options.BatchRows, spilledRow, splitBytes, out int ahead);

            // The core holds the groups once a lane's cache fills: each lane's
            // partition is then its cache, which no table of groups sized on the source's rows fills.
            core = GroupCore.Of(plan, settled, columns, inputs, source, facts, sorted, top, lanes, memory);
            if (core is not null)
            {
                facts = GroupCore.CacheFacts(facts);
            }

            // Without it, the core a lane turns to when its budget cannot let its table grow: made then,
            // never before, for a query it can hold, on two lanes or more, where the lanes' tables hold a
            // group once each and the core once; on one, where the core spills its parts to the scratch,
            // which a lane's table cannot.
            KeyFacts? tableFacts = facts;
            pressure = core is null && plan.CoreUnderPressure && plan.Grouped && !sorted && top is null && (lanes > 1 || plan.CoreSpills || plan.CoreTurnAt is not null)
                ? new CorePressure(lean => GroupCore.Holding(plan, settled, columns, inputs, source, tableFacts, sorted, top: null, lanes, memory, lean), lanes)
                : null;

            if (ranges is null)
            {
                AggregationPartition only = new AggregationPartition(plan, settled, columns, inputs, sorted, source: source, facts: facts, memory: memory)
                {
                    Top = top,
                    NumbersWhole = plan.NumberWhole,
                    Core = core?.Lane(),
                    Pressure = pressure,
                    Spill = spill,
                    Spilling = spilledRow > 0,
                    ExpectedRows = core is null && top is null ? source.RowBound : -1,
                };
                if (core is { Lean: true })
                {
                    only.ReserveExactly();
                }

                if (settling is not null)
                {
                    only.Settle(settling, pass.Rows ?? new RowRange(0, long.MaxValue));
                }

                partitions = [only];
                plan.Watch?.Invoke(partitions);
                await RunPartitionAsync(source, pass, metrics, only, cancellationToken).ConfigureAwait(false);
                only.DropUnmet();
            }
            else
            {
                // A worker per lane, each with its partition from one range to the next: the merge
                // stays in as many parts as lanes, however many ranges the queue holds.
                partitions = new AggregationPartition[lanes];
                for (int p = 0; p < partitions.Length; p++)
                {
                    partitions[p] = new AggregationPartition(plan, settled, columns, inputs, sorted, source: source, facts: facts, memory: memory)
                    {
                        Top = top is { FirstMet: true } ? null : top,
                        NumbersWhole = plan.NumberWhole,
                        Core = core?.Lane(),
                        Pressure = pressure,
                        TurnOnNew = pressure is not null && plan.CoreOnNew && lanes >= (plan.CoreLanes ?? GroupCore.DefaultLanes)
                            && facts?.Rows is long sourceRows && sourceRows >= lanes * AggregationPartition.LaneRows,
                        TurnOnSpread = pressure is not null && plan.CoreScattered && lanes >= (plan.CoreLanes ?? GroupCore.DefaultLanes),
                        ExpectedRows = core is null && top is null && source.RowBound >= 0 ? source.RowBound / lanes : -1,
                        Spill = spill,
                        Spilling = spilledRow > 0,
                        Lanes = lanes,
                    };
                    if (core is { Lean: true })
                    {
                        partitions[p].ReserveExactly();
                    }
                }

                if (pressure is not null)
                {
                    pressure.Lanes = partitions;
                }

                spill?.Retirement.Begin(lanes);

                plan.Watch?.Invoke(partitions);

                // Lanes that judge their key on their first rows start across the source, unless the
                // zones settle blocks in the order of the rows.
                bool fan = settling is null && plan.Fan && Array.Exists(partitions, partition => partition.TurnOnNew || partition.TurnOnSpread);
                await RunQueueAsync(source, pass, metrics, partitions, ranges, settling, fan, ahead, cancellationToken).ConfigureAwait(false);

                // The reads in flight end with the pass.
                if (splitBytes > 0)
                {
                    memory.LetGo(lanes * ahead * splitBytes);
                }
            }

            long merging = Stopwatch.GetTimestamp();

            // The lanes that retired under pressure left their groups in a table of their own, merged with
            // the lanes' at the end.
            AggregationPartition[] tables = spill?.Retirement.Retired is { } retired ? [.. partitions, retired] : partitions;

            // A lane wrote its table to the scratch, the core unable to hold the groups: every lane's goes
            // there too, and the result comes back from the runs part by part.
            if (Array.Exists(tables, table => table.Evicted))
            {
                return await SpilledAsync(partitions, tables, plan, settled, inputs, lanes, source, memory, spill!, builder, pass, merging, cancellationToken).ConfigureAwait(false);
            }

            plan.LastKeyBlocks = KeyBlocks(tables);

            // Under pressure: a lane turned during the pass, or the merge of the lanes'
            // tables in parts would not fit twice over, its estimate falling a few hundred kilobytes short
            // at times, which no merge in parts can take back. The lanes that did not turn merge in series into the largest
            // of them, each let go once merged, which takes no more than the largest's growth. Then, if
            // lanes turned, the largest empties its table into the core with the memory the others gave
            // back, and the core ends the query; else the largest holds the result.
            AggregationPartition[] merged = tables;
            if (pressure is not null && tables.Length > 1 && (pressure.Turned || !memory.CanGrow(2 * MergeAhead(tables))))
            {
                AggregationPartition? largest = MergeUnturned(tables);
                if (pressure.Made is { Engaged: true } pressed)
                {
                    largest?.TurnAtEnd(pressed);
                }
                else if (largest is not null)
                {
                    merged = [largest];
                }
            }

            pressure?.Settle();
            if ((core ?? pressure?.Made) is { Engaged: true } engaged)
            {
                // The sub-tables are the result; the lanes' caches die with the pass. A lane that never
                // turned to the core under pressure empties its table into it as a cache; one merged into
                // another is gone.
                AggregationPartition[] joined = Array.FindAll(partitions, partition => partition.Keys is not null);
                foreach (AggregationPartition partition in joined)
                {
                    partition.Join(engaged);
                }

                if (parted && builder is not null)
                {
                    // Part by part: each part's batches built as it is applied, while the others apply,
                    // the part let go then; the first batch once the first part is; the result starts with no
                    // group. What the lanes held is given back, the core's shelf counting the rest exactly.
                    CoreParts applying = await engaged.FinishPartedAsync(joined, lanes, plan, builder, cancellationToken).ConfigureAwait(false);
                    plan.LastRun = Gathered(partitions, Stopwatch.GetTimestamp() - merging, 0, 0) with { Core = engaged.Run() };
                    plan.LastGroups = 0;
                    foreach (AggregationPartition partition in partitions)
                    {
                        partition.LetGo();
                    }

                    memory.LetGo(partitions.Length * Working(pass.Options.BatchRows));
                    (GroupKeys none, AggregateSlot[] noSlots) = engaged.Empty();
                    AggregationOutcome first = new AggregationOutcome(plan, noSlots, none, []) { Parts = applying };
                    return Counted(first, memory, memory.Held);
                }

                (GroupKeys held, AggregateSlot[] heldSlots, long heldBytes, CorePart[] spilled) = await engaged.FinishAsync(joined, lanes, cancellationToken).ConfigureAwait(false);
                plan.LastRun = Gathered(partitions, Stopwatch.GetTimestamp() - merging, 0, heldBytes) with { Core = engaged.Run() };
                AggregationOutcome outcome = new AggregationOutcome(plan, heldSlots, held, Shuffled(held.Order(sorted: false)));
                if (spilled.Length == 0)
                {
                    foreach (AggregationPartition partition in partitions)
                    {
                        partition.Release();
                    }

                    return Counted(outcome, memory, heldBytes);
                }

                // Parts spilled: the groups held in memory first, then each part brought back alone.
                // The core's shelf counts what it holds exactly, which the result keeps; what the lanes
                // held, their caches and their admission, is given back.
                foreach (AggregationPartition partition in partitions)
                {
                    partition.LetGo();
                }

                memory.LetGo(partitions.Length * Working(pass.Options.BatchRows));
                outcome.Parts = new CoreParts(engaged, spilled, plan, builder ?? (plan.Emitter is not null ? null : throw new InvalidOperationException("A group by whose core spills delivers its parts through a builder.")));
                return Counted(outcome, memory, memory.Held);
            }

            (GroupKeys? keys, AggregateSlot[] slots, int parts, long mergedBytes, MergedParts? delivery, long entries) =
                await MergeAsync(merged, plan, settled, inputs, lanes, source, memory, spill, parted ? builder : null, merging, cancellationToken).ConfigureAwait(false);
            if (delivery is not null)
            {
                // Part by part: the result starts with no group, the parts come as they are merged
                // and built, the lanes' tables let go once the last is merged. What the lanes held to
                // fold their batches is given back now.
                plan.LastGroups = 0;
                memory.LetGo(partitions.Length * Working(pass.Options.BatchRows));
                AggregationOutcome first = new AggregationOutcome(plan, slots, keys, []) { Parts = delivery };
                return Counted(first, memory, memory.Held);
            }

            plan.LastRun = (Gathered(partitions, Stopwatch.GetTimestamp() - merging, parts, mergedBytes) with { MergeEntries = entries }).Spilled(spill);
            spill?.Dispose();

            // The lanes' tables die with the merge, but the one a merge in series kept as the result.
            long result = mergedBytes;
            foreach (AggregationPartition partition in tables)
            {
                if (ReferenceEquals(partition.Slots, slots))
                {
                    result = partition.Footprint;
                }
                else
                {
                    partition.Release();
                }
            }

            // Groups as they were first met, or part after part: an order is asked for, with OrderBy.
            int[] order = keys is null ? [0] : Shuffled(keys.Order(sorted: false));
            return Counted(new AggregationOutcome(plan, slots, keys, order), memory, result);
        }
        catch
        {
            // A spill's scratch closes with the query that failed: its file goes now, not at a collection.
            (core ?? pressure?.Made)?.CloseSpill();
            spill?.Dispose();
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The result of a group by whose lanes wrote their tables to the scratch: every lane's table written
    /// there too, its memory and the lanes' given back, then the parts read back from the runs one at a
    /// time, the builder making each one's batches. The parts are a power of two of the runs' 256
    /// sections, as few as keep each part's table and the workers that build ahead within half the room
    /// the budget leaves; the workers, as many as the lanes but one at most.
    /// </summary>
    private static async ValueTask<AggregationOutcome> SpilledAsync(
        AggregationPartition[] partitions, AggregationPartition[] tables, AggregationPlan plan, AggregateSlot?[] settled, int[] inputs, int lanes, ScanSource source,
        QueryMemory memory, SpillScope spill, PartBuilder? builder, ScanSpec pass, long merging, CancellationToken cancellationToken)
    {
        if (builder is null)
        {
            throw new VortexUnsupportedException(
                "a spilled group by read whole",
                ComponentKind.Feature,
                "This group by's groups went to the scratch, and come back a part at a time; read its result as batches or records rather than whole, or give its session a larger QueryMemoryBudget.");
        }

        // An index of the lanes' kind, empty: what the parts' tables are made from (GroupKeys.ForSpill).
        GroupKeys kind = partitions[0].Keys!.Fresh();

        // One table after the other, the lanes' and the retired lanes': each gives back its table before
        // the next takes what writing its own asks past the budget.
        foreach (AggregationPartition table in tables)
        {
            await table.EvictAsync(cancellationToken).ConfigureAwait(false);
        }

        plan.LastKeyBlocks = KeyBlocks(tables);
        long bytes = 0;
        List<SpillRun> runs = [];
        foreach (AggregationPartition table in tables)
        {
            bytes += table.EvictedTables.Bytes;
            runs.AddRange(table.Runs ?? []);
        }

        long entries = 0;
        foreach (SpillRun run in runs)
        {
            entries += run.Entries(0, SpillRun.Sections);
        }

        AggregationRun gathered = Gathered(partitions, 0, 0, 0) with { MergeEntries = entries };
        foreach (AggregationPartition table in tables)
        {
            table.LetGo();
        }

        memory.LetGo(partitions.Length * Working(pass.Options.BatchRows));

        // What an entry of a run costs a part's table: its bytes in the runs, times what the lanes' tables
        // held over what they wrote, their slack and their pages, a fourth at most. Twice that a part, its
        // table and its builder's batches, the runs' entries counting a key once a run. As few parts as
        // keep one under half the room the budget leaves, the estimate erring; then as many workers as
        // that half holds parts at once beside the reader's, each one building and one built and waiting,
        // none when a part takes it all: the reader merges each part itself.
        long written = 0;
        foreach (SpillRun run in runs)
        {
            written += run.Bytes;
        }

        double slack = Math.Clamp((double)bytes / Math.Max(1, written), 1, 4);
        long perEntry = Math.Max(1, (long)(slack * written / Math.Max(1, entries)));
        long room = Math.Max(memory.Ceiling / 16, memory.Ceiling - memory.Held);
        int bits = 0;
        while (bits < SpillRun.SectionBits && 2 * (entries >> bits) * perEntry > room / 2)
        {
            bits++;
        }

        long part = Math.Max(1, 2 * (entries >> bits) * perEntry);
        int workers = (int)Math.Clamp(((room / 2 / part) - 1) / 2, 0, Math.Max(0, lanes - 1));
        int readBytes = (int)Math.Clamp(room / 16 / ((2 * workers) + 1), SpillBuffer.PageOf(memory, workers + 1), 64L << 20);
        RunMerge merge = new RunMerge([.. runs], kind, plan, settled, inputs, source, memory, spill, bits, readBytes);
        plan.LastRun = (gathered with { MergeParts = merge.Parts }).Spilled(spill);
        MergedParts delivery = new MergedParts(merge, builder, Math.Min(workers, merge.Parts), plan, memory, 0, merging, cancellationToken);
        (GroupKeys none, AggregateSlot[] noSlots) = merge.Empty();
        plan.LastGroups = 0;
        AggregationOutcome first = new AggregationOutcome(plan, noSlots, none, []) { Parts = delivery };
        return Counted(first, memory, memory.Held);
    }

    /// <summary>
    /// The groups' order as the merge gives it, or, under <see cref="AggregationPlan.ShuffledOrder"/>, a
    /// copy shuffled by a draw the process's seed and the groups' count make: the same for two identical
    /// queries of a process, and unlike the merge's.
    /// </summary>
    internal static int[] Shuffled(int[] order)
    {
        if (!AggregationPlan.ShuffledOrder || order.Length < 2)
        {
            return order;
        }

        int[] shuffled = [.. order];
        new Random(unchecked((int)(MergeHash.Seed ^ (ulong)order.Length))).Shuffle(shuffled);
        return shuffled;
    }

    /// <summary>
    /// The result keeps <paramref name="bytes"/> of its query's memory, what its groups hold, and the
    /// order they are delivered in, until it is delivered; the rest is given back.
    /// </summary>
    private static AggregationOutcome Counted(AggregationOutcome outcome, QueryMemory memory, long bytes)
    {
        memory.Keep(bytes + ((long)outcome.Order.Length * sizeof(int)));
        outcome.Memory = memory;
        outcome.Plan.LastGroups = outcome.Order.Length;
        outcome.Plan.LastPeakBytes = memory.Peak;
        return outcome;
    }

    /// <summary>The key blocks the lanes grouped, by how, summed over their tables and caches.</summary>
    private static (long ByRange, long ByCode, long Hashed) KeyBlocks(AggregationPartition[] partitions)
    {
        (long range, long code, long hashed) = (0, 0, 0);
        foreach (AggregationPartition partition in partitions)
        {
            (long byRange, long byCode, long byHash) = partition.Blocks;
            range += byRange;
            code += byCode;
            hashed += byHash;
        }

        return (range, code, hashed);
    }

    /// <summary>
    /// What a lane holds besides its tables, reserved for it before the pass: a batch's scratch, its
    /// keys' homes, the decoding of its columns. A quarter of a megabyte a lane on the bench's files,
    /// measured; four times that, for wider rows.
    /// </summary>
    private const long LaneBytes = 1 << 20;

    /// <summary>The least working memory a lane is admitted with, whatever its batches.</summary>
    private const long LeastLaneBytes = 64 * 1024;

    /// <summary>The bytes a row of a lane's batch takes in its scratch: its group, its key's home, its values.</summary>
    private const long RowBytes = 16;

    /// <summary>
    /// The tables a lane holds within the working memory it was admitted with, a quarter of a megabyte:
    /// a query of few groups reserves nothing past its admission, where a megabyte ahead a lane would
    /// take fourteen for tables of a few kilobytes.
    /// </summary>
    internal const long LaneTables = LaneBytes / 4;

    /// <summary>
    /// The lanes a query starts on: each lane's working memory reserved, <paramref name="lanes"/> of
    /// them if its budget grants it, else half as many, down to one, before it fails.
    /// A lane's working memory follows its batches: <paramref name="batchRows"/> rows of scratch, when
    /// the scan sets them, between a sixteenth of a megabyte and a megabyte; a megabyte when the scan
    /// decides.
    /// </summary>
    internal static int Admit(QueryMemory memory, int lanes, int batchRows = 0, int spilledRow = 0) =>
        Admit(memory, lanes, batchRows, spilledRow, splitBytes: 0, out _);

    /// <summary>
    /// <see cref="Admit(QueryMemory, int, int, int)"/>, each lane reading <paramref name="ahead"/> splits
    /// ahead of <paramref name="splitBytes"/> bytes at most, over a source that copies its reads: two,
    /// reserved with its working memory; under a budget short of them, one, then none, before the lanes
    /// are halved, the lanes left reading two again as far as they fit. A lane without reads ahead waits
    /// on each of its reads. Nothing is reserved for a source that reads in place, whose lanes read two
    /// ahead.
    /// </summary>
    internal static int Admit(QueryMemory memory, int lanes, int batchRows, int spilledRow, long splitBytes, out int ahead)
    {
        long working = Working(batchRows);
        ahead = BatchAsyncEnumerator.ReadAheadSplits;
        long laneBytes = working + (splitBytes * ahead);

        // Their working memory a quarter of the ceiling at most: past it, their tables share the rest so
        // thinly that each one's batch of new groups outgrows its share, and lanes that spill would
        // write a run a batch. Lanes whose tables spill, their batches of new groups a sixteenth of it:
        // what they take past the budget before they write their tables (Batched).
        while (lanes > 1 && (4 * lanes * laneBytes > memory.Ceiling || 16L * lanes * Math.Max(batchRows, 1_024) * spilledRow > memory.Ceiling))
        {
            if (splitBytes > 0 && ahead > 0 && 4 * lanes * laneBytes > memory.Ceiling)
            {
                laneBytes = working + (splitBytes * --ahead);
                continue;
            }

            lanes = Math.Max(1, lanes / 2);
            ahead = BatchAsyncEnumerator.ReadAheadSplits;
            laneBytes = working + (splitBytes * ahead);
        }

        while (!memory.TryGrow(lanes * laneBytes))
        {
            if (splitBytes > 0 && ahead > 0)
            {
                laneBytes = working + (splitBytes * --ahead);
                continue;
            }

            if (lanes == 1)
            {
                throw memory.Exceeded("group by", 0, laneBytes);
            }

            lanes = Math.Max(1, lanes / 2);
            ahead = BatchAsyncEnumerator.ReadAheadSplits;
            laneBytes = working + (splitBytes * ahead);
        }

        memory.Measure(lanes * laneBytes);
        return lanes;
    }

    /// <summary>Gives back the working memory of <paramref name="lanes"/> lanes <see cref="Admit(QueryMemory, int, int, int)"/> admitted, their batches of <paramref name="batchRows"/> rows.</summary>
    internal static void Dismiss(QueryMemory memory, int lanes, int batchRows) => memory.LetGo(lanes * Working(batchRows));

    /// <summary>The working memory a lane is admitted with, its batches of <paramref name="batchRows"/> rows, or the scan's.</summary>
    private static long Working(int batchRows) => batchRows > 0 ? Math.Clamp(batchRows * RowBytes, LeastLaneBytes, LaneBytes) : LaneBytes;

    /// <summary>
    /// The bytes a row of a lane's batch may come to hold under the core, its budget short: its scratch,
    /// its entry, and the group it may add to the lane's cache before the cache is measured, past its
    /// capacity.
    /// </summary>
    private const int CoreRowBytes = 72;

    /// <summary>
    /// The bytes a row of a lane's batch may come to hold in a table that spills: its scratch, and a new
    /// group's key, record and states, a text key of a few dozen bytes or a text's extremes. A guess, not a
    /// measure: the batch is cut before its rows are read.
    /// </summary>
    internal const int LaneRowBytes = 128;

    /// <summary>
    /// The pass with its lanes' batches under <paramref name="memory"/>'s budget: what a batch may come to
    /// hold on every one of <paramref name="lanes"/> lanes, a quarter of the ceiling at most, in batches
    /// of a power of two from 1 024 rows; the scan's own when the budget leaves them room, as the
    /// process's does. Lanes whose tables spill, at <paramref name="spilledRow"/> bytes a row, a sixteenth
    /// of it: a lane folds its batch before it writes its table, and the peak passes the ceiling by that.
    /// </summary>
    internal static ScanSpec Batched(ScanSpec pass, QueryMemory memory, int lanes, int spilledRow = 0)
    {
        int asked = pass.Options.BatchRows > 0 ? pass.Options.BatchRows : GroupBatches.BatchRows;
        long room = spilledRow > 0 ? memory.Ceiling / 16 / Math.Max(1, lanes) / spilledRow : memory.Ceiling / 4 / Math.Max(1, lanes) / CoreRowBytes;
        if (room >= asked)
        {
            return pass;
        }

        int rows = (int)Math.Max(1_024, BitOperations.RoundUpToPowerOf2((ulong)Math.Max(1, room)) / 2);
        return rows >= asked ? pass : pass with { Options = pass.Options with { BatchRows = rows } };
    }

    /// <summary>
    /// What a merge of <paramref name="partitions"/> reserves at least: the places it cuts their groups by,
    /// and twice the largest partition's groups at what a group costs the lanes, the first parts' tables.
    /// Past what its budget grants, the core ends the query instead.
    /// </summary>
    private static long MergeAhead(AggregationPartition[] partitions)
    {
        long groups = 0;
        long most = 0;
        long bytes = 0;
        foreach (AggregationPartition partition in partitions)
        {
            int count = partition.Keys?.Count ?? 0;
            groups += count;
            most = Math.Max(most, count);
            bytes += partition.Footprint;
        }

        long perGroup = groups > 0 ? Math.Max(1, bytes / groups) : 0;
        return (groups * (sizeof(int) + sizeof(byte))) + (2 * most * perGroup);
    }

    /// <summary>
    /// Under pressure: the lanes that did not turn to the core, at the end of the pass,
    /// or those of them that ran out of rows, <paramref name="ended"/>, during it, merged in series into
    /// the largest of them, each let go once merged. The merge holds the tables it has not reached and
    /// the largest's growth, never a table of its own beside them: memory falls as it goes.
    /// </summary>
    /// <returns>The largest, which holds their groups; null when every lane turned.</returns>
    internal static AggregationPartition? MergeUnturned(AggregationPartition[] partitions, bool ended = false)
    {
        AggregationPartition? largest = null;
        foreach (AggregationPartition partition in partitions)
        {
            if (partition.Core is null && partition.Keys is { } keys && (!ended || partition.HasEnded))
            {
                partition.Trim();
                if (largest is null || keys.Count > largest.Keys!.Count)
                {
                    largest = partition;
                }
            }
        }

        int[]? numbers = null;
        foreach (AggregationPartition partition in partitions)
        {
            if (partition.Core is null && partition.Keys is not null && (!ended || partition.HasEnded) && !ReferenceEquals(partition, largest))
            {
                largest!.MergeFrom(partition, ref numbers, apart: null);
                largest.Recount();
                partition.LetGo();
                partition.Gave();
            }
        }

        return largest;
    }

    /// <summary>The parts a parallel merge cuts the key space into, at most.</summary>
    private const int MostParts = 256;

    /// <summary>The groups of the largest partition a part takes at least: with fewer, handing out a part costs more than merging it.</summary>
    private const int PartGroups = 512;

    /// <summary>
    /// The partitions' groups merged into one outcome. With few groups, in series, into the partition
    /// that holds the most, whose cells stay where they are. With many, the key space cut into parts
    /// by the top bits of a hash of the keys seeded for the merge, a power of two of them, at most
    /// twice the degree (<see cref="Parts"/>): each part merged from every partition by a task the
    /// merge's workers take from a queue, and the parts read as one, without a copy. With
    /// <paramref name="builder"/>, the parts delivered one at a time instead, each built into its
    /// batches by whoever merged it (<see cref="MergedParts"/>).
    /// </summary>
    /// <returns>The merged keys and slots, and the parts merged: the partitions merged into the largest, in series; or, part by part, no group and the parts to come.</returns>
    private static async ValueTask<(GroupKeys? Keys, AggregateSlot[] Slots, int Parts, long Merged, MergedParts? Delivery, long Entries)> MergeAsync(
        AggregationPartition[] partitions, AggregationPlan plan, AggregateSlot?[] settled, int[] inputs, int degree, ScanSource source, QueryMemory memory,
        SpillScope? spill, PartBuilder? builder, long started, CancellationToken cancellationToken)
    {
        int largest = 0;
        for (int p = 1; p < partitions.Length; p++)
        {
            largest = (partitions[p].Keys?.Count ?? 0) > (partitions[largest].Keys?.Count ?? 0) ? p : largest;
        }

        // In parts, every entry of every partition goes into a fresh table, at twice what an entry
        // costs the merge in series once its part is cut and its table made: they pay when a worker's
        // share of that is below the entries the series would merge into the largest. Measured, two
        // lanes merge faster in series, eight in parts, four about alike.
        AggregationPartition biggest = partitions[largest];
        int parts = biggest.Keys is null ? 1 : plan.MergeParts ?? Parts(degree, biggest.Keys.Count);
        long entries = 0;
        foreach (AggregationPartition partition in partitions)
        {
            entries += partition.Keys?.Count ?? 0;
        }

        long serial = entries - (biggest.Keys?.Count ?? 0);
        bool inParts = plan.MergeInParts ?? (parts > 1 && 2 * entries < Math.Min(degree, parts) * serial);

        // The pass done, a lane's table grows no more but by a merge into it: the room it kept for a
        // doubling goes back to the budget, for the merge's own.
        for (int p = 0; p < partitions.Length; p++)
        {
            if (p != largest)
            {
                partitions[p].Trim();
            }
        }

        if (partitions.Length == 1 || biggest.Keys is null || !inParts)
        {
            // Over the whole scan, a slot one lane spilled merges every lane's runs and values apart,
            // part by part, its answer in the largest's slot and the others' emptied: the merge in
            // series adds nothing more.
            if (biggest.Keys is null && Array.Exists(partitions, partition => Array.Exists(partition.Slots, slot => slot.Spilled)))
            {
                biggest.Trim();
                for (int s = 0; s < biggest.Slots.Length; s++)
                {
                    if (inputs[s] != AggregationPartition.Settled && Array.Exists(partitions, partition => partition.Slots[s].Spilled))
                    {
                        AggregateSlot[] lanes = Array.ConvertAll(partitions, partition => partition.Slots[s]);
                        await biggest.Slots[s].MergeSpilledAsync(lanes, spill!, degree, memory, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // The keys and the states in series, into the largest; a distinct count's pairs, when they
            // are many, apart, by parts of the pairs taken side by side.
            int[]? numbers = null;
            bool[]? apart = partitions.Length > 1 && degree > 1 ? Paired(partitions, inputs) : null;
            int[][] maps = new int[partitions.Length][];
            for (int p = 0; p < partitions.Length; p++)
            {
                if (p != largest)
                {
                    maps[p] = biggest.MergeFrom(partitions[p], ref numbers, apart);
                    biggest.Recount();
                }
            }

            if (apart is not null)
            {
                await MergePairedAsync(partitions, largest, maps, apart, degree, memory, cancellationToken).ConfigureAwait(false);
            }

            return (biggest.Keys, biggest.Slots, partitions.Length - 1, 0, null, serial);
        }

        parts = Math.Max(parts, 2);
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;
        GroupKeys[] keysOf = new GroupKeys[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
        {
            keysOf[p] = partitions[p].Keys!;
        }

        // What the partitions share is merged once: a composite's indexes of its columns.
        await keysOf[0].RebaseAsync(keysOf, token).ConfigureAwait(false);

        biggest.Trim();
        long laneBytes = 0;
        long laneGroups = 0;
        foreach (AggregationPartition partition in partitions)
        {
            laneBytes += partition.Footprint;
            laneGroups += partition.Keys!.Count;
        }

        // Each partition's groups by part, a task each: hashed, counted, placed. The places, an int a
        // group, and its part, a byte, reserved before.
        long cut = laneGroups * (sizeof(int) + sizeof(byte));
        if (!memory.TryGrow(cut))
        {
            throw memory.Exceeded("merge of a group by", laneGroups, cut);
        }

        memory.Measure(cut);

        // Partitions that number one span of values by value are cut by value, each part numbered by value
        // over its run of the span: no hash, no probe, no table that grows.
        (long Least, ulong Span)? values = keysOf[0].ValueSpan;
        for (int p = 1; p < keysOf.Length && values is not null; p++)
        {
            values = keysOf[p].ValueSpan == values ? values : null;
        }

        bool byValue = values is not null && plan.MergeByValue;
        ulong seed = MergeHash.Seed;
        int shift = 64 - BitOperations.Log2((uint)parts);
        int[][] placed = new int[partitions.Length][];
        int[][] starts = new int[partitions.Length][];
        Task[] cutting = new Task[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
        {
            int partition = p;
            cutting[p] = Task.Run(() => (placed[partition], starts[partition]) = Cut(keysOf[partition], seed, shift, parts, byValue), token);
        }

        await GuardedAsync(cutting, failed).ConfigureAwait(false);

        // Each part merged from every partition, the parts taken from a queue. A part's groups are
        // reserved before they come (PartMerge.Merge); what a group costs, the lanes' tables tell.
        long perGroup = laneGroups > 0 ? Math.Max(1, laneBytes / laneGroups) : 64;
        PartMerge merge = new PartMerge(partitions, keysOf, placed, starts, parts, plan, settled, inputs, source, memory, byValue, perGroup);
        if (builder is not null)
        {
            // Part by part: each part built into its batches by the worker
            // that merged it, or by the reader, which merges the next rather than wait; the lanes'
            // tables and the places of their groups go once the last part is merged.
            (GroupKeys none, AggregateSlot[] noSlots) = merge.Empty();
            plan.LastRun = Gathered(partitions, Stopwatch.GetTimestamp() - started, parts, 0) with { MergeEntries = laneGroups };
            MergedParts delivery = new MergedParts(merge, builder, Math.Clamp(degree - 1, 1, parts), plan, memory, cut, started, cancellationToken);
            return (none, noSlots, parts, 0, delivery, laneGroups);
        }

        GroupKeys[] partKeys = new GroupKeys[parts];
        AggregateSlot[][] partSlots = new AggregateSlot[parts][];
        Task[] workers = new Task[Math.Min(degree, parts)];
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                () =>
                {
                    int part;
                    while ((part = merge.Take()) < parts)
                    {
                        token.ThrowIfCancellationRequested();
                        (partKeys[part], partSlots[part], _, _) = merge.Merge(part);
                    }
                },
                token);
        }

        await GuardedAsync(workers, failed).ConfigureAwait(false);
        long merged = 0;
        for (int part = 0; part < parts; part++)
        {
            merged += partKeys[part].Footprint + AggregateSlot.FootprintOf(partSlots[part]);
        }

        (GroupKeys joinedKeys, AggregateSlot[] joinedSlots, int joinedParts) = Joined(partKeys, partSlots, parts);
        return (joinedKeys, joinedSlots, joinedParts, merged, null, laneGroups);
    }

    /// <summary>The pairs past which a distinct count's merge in series goes by parts instead: below, handing the parts out costs more than the pairs.</summary>
    private const long PairedPairs = 1 << 16;

    /// <summary>Which slots of the partitions hold pairs enough to merge apart, by parts of their pairs; null when none does.</summary>
    private static bool[]? Paired(AggregationPartition[] partitions, int[] inputs)
    {
        AggregateSlot[] first = partitions[0].Slots;
        bool[]? apart = null;
        for (int s = 0; s < first.Length; s++)
        {
            if (inputs[s] == AggregationPartition.Settled || first[s] is not IPairedSlot)
            {
                continue;
            }

            long pairs = 0;
            foreach (AggregationPartition partition in partitions)
            {
                pairs += ((IPairedSlot)partition.Slots[s]).Pairs;
            }

            if (pairs >= PairedPairs)
            {
                apart ??= new bool[first.Length];
                apart[s] = true;
            }
        }

        return apart;
    }

    /// <summary>
    /// The slots <paramref name="apart"/> of every partition merged into the largest's, by parts of
    /// their pairs taken side by side: the largest's own groups as they are, every other partition's
    /// by its map onto the largest's, its keys merged before.
    /// </summary>
    private static async Task MergePairedAsync(
        AggregationPartition[] partitions, int largest, int[][] maps, bool[] apart, int degree, QueryMemory memory, CancellationToken cancellationToken)
    {
        AggregationPartition biggest = partitions[largest];
        int groups = biggest.Keys?.Count ?? 1;
        int[] own = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            own[g] = g;
        }

        // The largest first, whose slot takes the counts.
        AggregationPartition[] lanes = new AggregationPartition[partitions.Length];
        int[][] ordered = new int[partitions.Length][];
        lanes[0] = biggest;
        ordered[0] = own;
        for (int p = 0, at = 1; p < partitions.Length; p++)
        {
            if (p != largest)
            {
                lanes[at] = partitions[p];
                ordered[at++] = maps[p];
            }
        }

        // Four parts a worker: a worker that took one part too many waits a quarter of a part's time.
        // Measured at fourteen lanes, 64 parts against 16 took a count over 10^7 users from 200 to 176 ms.
        int parts = Math.Max(2, (int)BitOperations.RoundUpToPowerOf2((uint)(4 * degree)));
        for (int s = 0; s < apart.Length; s++)
        {
            if (!apart[s])
            {
                continue;
            }

            AggregateSlot[] slots = new AggregateSlot[lanes.Length];
            for (int p = 0; p < lanes.Length; p++)
            {
                slots[p] = lanes[p].Slots[s];
            }

            await ((IPairedSlot)slots[0]).MergeInPartsAsync(slots, ordered, groups, parts, degree, memory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The parts a merge cuts its keys into: a power of two, at most twice the degree, and a part of <see cref="PartGroups"/> groups of the largest partition at least.</summary>
    /// <remarks>
    /// A part gathers its entries from every partition, every <c>parts</c>-th group of each in
    /// effect: past a stride the prefetchers follow, each entry costs a miss of its own. Measured at
    /// fourteen lanes, 32, 64, 128 and 256 parts each merged slower than 16, by 10 to 250 %, though
    /// they balance the workers and fit a part's table in a core's private cache.
    /// </remarks>
    private static int Parts(int degree, int largest)
    {
        int most = Math.Min(MostParts, Math.Min(2 * degree, largest / PartGroups));
        return most < 2 ? 1 : 1 << BitOperations.Log2((uint)most);
    }

    /// <summary>A partition's groups placed by part, the top bits of their keys' hashes, or of their numbers when <paramref name="byValue"/>; where each part's begin, and the end.</summary>
    private static (int[] Placed, int[] Starts) Cut(GroupKeys keys, ulong seed, int shift, int parts, bool byValue)
    {
        int count = keys.Count;
        byte[] partOf = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            if (byValue)
            {
                keys.PartsByValue(64 - shift, partOf.AsSpan(0, count));
            }
            else
            {
                keys.Parts(seed, shift, partOf.AsSpan(0, count));
            }
            int[] starts = new int[parts + 1];
            for (int g = 0; g < count; g++)
            {
                starts[partOf[g] + 1]++;
            }

            for (int part = 0; part < parts; part++)
            {
                starts[part + 1] += starts[part];
            }

            int[] next = starts[..^1];
            int[] placed = new int[count];
            for (int g = 0; g < count; g++)
            {
                placed[next[partOf[g]]++] = g;
            }

            return (placed, starts);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(partOf);
        }
    }

    /// <summary>The parts read as one, the empty ones left out.</summary>
    internal static (GroupKeys Keys, AggregateSlot[] Slots, int Parts) Joined(GroupKeys[] partKeys, AggregateSlot[][] partSlots, int parts)
    {
        List<int> kept = [];
        for (int part = 0; part < parts; part++)
        {
            if (partKeys[part].Count > 0)
            {
                kept.Add(part);
            }
        }

        if (kept.Count == 0)
        {
            kept.Add(0);
        }

        GroupKeys[] keys = new GroupKeys[kept.Count];
        int[] offsets = new int[kept.Count];
        int total = 0;
        for (int k = 0; k < kept.Count; k++)
        {
            keys[k] = partKeys[kept[k]];
            offsets[k] = total;
            total += keys[k].Count;
        }

        AggregateSlot[] slots = new AggregateSlot[partSlots[0].Length];
        for (int s = 0; s < slots.Length; s++)
        {
            AggregateSlot[] ofSlot = new AggregateSlot[kept.Count];
            for (int k = 0; k < kept.Count; k++)
            {
                ofSlot[k] = partSlots[kept[k]][s];
            }

            slots[s] = ofSlot[0].Joined(ofSlot, offsets);
        }

        return (new JoinedKeys(keys, offsets, total), slots, parts);
    }

    /// <summary>Awaits <paramref name="tasks"/>, cancelling the others through <paramref name="failed"/> once one fails.</summary>
    internal static async Task GuardedAsync(Task[] tasks, CancellationTokenSource failed)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
        }
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

    /// <summary><see cref="Facts"/>, the bounds of a source that reads its structures first, a dataset's.</summary>
    internal static async ValueTask<KeyFacts> FactsAsync(ScanSource source, ColumnShape[] keys, CancellationToken cancellationToken)
    {
        bool[] sorted = new bool[keys.Length];
        KeyBounds?[] bounds = new KeyBounds?[keys.Length];
        for (int k = 0; k < keys.Length; k++)
        {
            sorted[k] = IsSorted(source, keys[k]);
            ColumnShape key = keys[k];
            bounds[k] = key.Kind != StorageKind.Primitive || !key.PType.IsInteger() || key.Column.FieldPath.Length != 1
                ? null
                : await source.BoundsAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return new KeyFacts(sorted, bounds, source.RowBound);
    }

    /// <summary>What the statistics say of each column of a key: whether it is sorted, and what bounds it.</summary>
    internal static KeyFacts Facts(ScanSource source, ColumnShape[] keys)
    {
        bool[] sorted = new bool[keys.Length];
        KeyBounds?[] bounds = new KeyBounds?[keys.Length];
        for (int k = 0; k < keys.Length; k++)
        {
            sorted[k] = IsSorted(source, keys[k]);
            bounds[k] = Bounds(source, keys[k]);
        }

        return new KeyFacts(sorted, bounds, source.RowBound);
    }

    /// <summary>
    /// The smallest and the largest value of an integer column of a key, as the source bounds them
    /// without a read: a file's statistics, a dataset's summaries in hand; what bounds a table of its
    /// groups (<see cref="FixedKeys{TValue}"/>).
    /// </summary>
    internal static KeyBounds? Bounds(ScanSource source, ColumnShape key) =>
        key.Kind != StorageKind.Primitive || !key.PType.IsInteger() || key.Column.FieldPath.Length != 1 ? null : source.Bounds(key);

    /// <summary>The smallest and the largest value of an integer column of a key, when <paramref name="file"/>'s statistics hold them exactly.</summary>
    internal static KeyBounds? FileBounds(VortexFile file, ColumnShape key)
    {
        if (key.Kind != StorageKind.Primitive || !key.PType.IsInteger() || !file.HasFileStatistics
            || key.Column.FieldPath.Length != 1 || !file.Schema.RootIsStruct || key.Column.FieldPath[0] >= file.Statistics.Count)
        {
            return null;
        }

        FieldStatistics statistics = file.Statistics[key.Column.FieldPath[0]];
        return key.PType switch
        {
            PType.I8 => Bounds<sbyte>(statistics),
            PType.I16 => Bounds<short>(statistics),
            PType.I32 => Bounds<int>(statistics),
            PType.I64 => Bounds<long>(statistics),
            PType.U8 => Bounds<byte>(statistics),
            PType.U16 => Bounds<ushort>(statistics),
            PType.U32 => Bounds<uint>(statistics),
            _ => Bounds<ulong>(statistics),
        };
    }

    /// <summary>The statistics' extremes, read as the column's own type and widened; an unsigned one past the longs saturated, which no table takes.</summary>
    private static KeyBounds? Bounds<T>(FieldStatistics statistics)
        where T : System.Numerics.IBinaryInteger<T> =>
        statistics.TryGetMin(out T min) && statistics.TryGetMax(out T max) ? new KeyBounds(long.CreateSaturating(min), long.CreateSaturating(max)) : null;

    /// <summary>The lanes a pass may take: the scan's own degree, or its session's.</summary>
    private static int Degree(ScanSource source, ScanSpec spec) =>
        spec.Options.DegreeOfParallelism > 0 ? spec.Options.DegreeOfParallelism : source.Session.Options.MaxDegreeOfParallelism;

    /// <summary>
    /// The ranges of rows a queue hands its lanes, the largest first: a quarter of a lane's share of
    /// the live rows each, then, once half of them are handed out, a share of what is left that
    /// shrinks down to a block, each cut at the plan's boundaries, a dead block joining the range
    /// before it. Null when the pass runs as one range: a degree of one, a take by position, a
    /// source that is not a file, or live rows too few for two blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lanes do not all go at one pace: one runs on a slower core, waits on its reads, or shares its
    /// core with another process. Whatever the cause, the queue does not ask: each range is at most
    /// a share of what is left of the rows (<see cref="ShrinkingShares"/>), so the lane that takes
    /// the last ones finishes soon after the others, down to a lane half as fast as the mean.
    /// </para>
    /// <para>
    /// A range ends where a chunk does, so that no chunk is read twice, and the last ranges shrink
    /// down to a chunk. Cut at a block's end instead, a file read in place balanced its lanes to
    /// within 2 to 15 %, but each range a lane took cost it 0.1 to 0.6 ms more than its rows at
    /// fourteen lanes, for reasons not isolated: no faster on a group by of a million keys, slower
    /// by up to a fifth at a thousand.
    /// </para>
    /// <para>
    /// The live rows are the mask's when the pass has one, so that a filter keeping a few contiguous
    /// blocks shares them among the lanes rather than leaving them to the one whose rows hold them.
    /// </para>
    /// </remarks>
    internal static RowRange[]? Ranges(ScanSource source, ScanSpec spec, int degree)
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
        if (alive < 2 * blockRows)
        {
            return null;
        }

        long first = Math.Max(blockRows, alive / (degree * (long)RangesPerLane));
        long left = alive;
        long target = ShrinkingShares(first, left, degree, blockRows);
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
            if (held >= target)
            {
                parts.Add(new RowRange(start, at));
                left -= held;
                target = ShrinkingShares(first, left, degree, blockRows);
                start = at;
                held = 0;
            }
        }

        parts.Add(new RowRange(start, rows.End));
        return parts.Count > 1 ? [.. parts] : null;
    }

    /// <summary>The ranges a queue holds per lane at first, before they shrink: a quarter of a lane's share each.</summary>
    private const int RangesPerLane = 4;

    /// <summary>
    /// The live rows of the next range: <paramref name="first"/>, until what is left is half of the
    /// rows; then a share of what is left, <paramref name="left"/> over twice the degree, down to a
    /// block. A lane that takes a range when <paramref name="left"/> rows remain then finishes it
    /// no later than the others finish theirs and the rest, as long as it goes at least half as fast
    /// as the mean lane: guided self-scheduling, halved.
    /// </summary>
    private static long ShrinkingShares(long first, long left, int degree, long blockRows) =>
        Math.Max(blockRows, Math.Min(first, left / (2L * degree)));

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
            // Under pressure, a lane turns to the core between two batches, and one whose
            // rows the core could not take waits for the next table given back: on tasks, never a thread.
            // Its first batch may show the key scattered, before it is folded.
            partition.Judge(batch);
            if (partition.MustTurn(batch.SelectedRows))
            {
                await partition.TurnAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (partition.MustSpill(batch.RowCount))
            {
                await partition.SpillAloneAsync(cancellationToken).ConfigureAwait(false);
            }

            partition.Process(batch);
            if (partition.Core is { Starved: true })
            {
                await partition.RoomAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        partition.Finish();
        partition.ActiveTicks += Stopwatch.GetTimestamp() - start;
        partition.Ranges++;
        partition.GroupsAtEnd = partition.Keys?.Count ?? 1;
        partition.RangeEnded();
    }

    /// <summary>What the partitions did, gathered once they have merged into the first: the plan's last run.</summary>
    /// <summary>The run's counts, gathered once the merge is done: the lanes' groups are all alive then, with the merge's own.</summary>
    private static AggregationRun Gathered(AggregationPartition[] partitions, long mergeTicks, int mergeParts, long merged)
    {
        AggregationRun.Lane[] lanes = new AggregationRun.Lane[partitions.Length];
        long state = merged;
        for (int p = 0; p < lanes.Length; p++)
        {
            AggregationPartition partition = partitions[p];
            (long arrays, long bytes, long copied) = partition.Growth;
            lanes[p] = new AggregationRun.Lane(partition.ActiveTicks, partition.Ranges, partition.GroupsAtEnd, partition.RowsFolded, arrays, bytes, copied);
            state += partition.Footprint;
        }

        return new AggregationRun(lanes, mergeTicks, mergeParts, state);
    }

    /// <summary>
    /// Runs the ranges on a worker per partition, each taking the next range of the queue as it
    /// finishes one and folding it into its partition: a lane on a slow core takes fewer, and no
    /// lane waits on the others while ranges are left. With <paramref name="fan"/>, lane i takes its
    /// first range i / N of the way through the queue, then the queue's next ones as they come, those a
    /// lane took already left out: the lanes judge their key on rows from across the source rather than
    /// all on its first rows, which a key whose cardinality changes as the rows go misleads together.
    /// </summary>
    private static async Task RunQueueAsync(
        ScanSource source, ScanSpec spec, ScanMetrics metrics, AggregationPartition[] partitions, RowRange[] ranges, ZoneSettling? settling,
        bool fan, int ahead, CancellationToken cancellationToken)
    {
        using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = failed.Token;

        // The degree is the parallelism: a partition already runs on the pool, and decoding ahead
        // inside each one would put twice the degree's lanes on it. Its reads go ahead of its decode
        // instead, on a source whose read is a round trip: the splits its admission granted.
        ScanSpec lane = spec with { Options = spec.Options with { DegreeOfParallelism = 1, Prefetch = 0 }, ReadAhead = ahead };
        int[] next = [-1];

        // The ranges the fan's first ranges took, each its lane's before any lane starts, which the queue
        // passes over: a lane that started first would take another's from the queue. Null without a fan.
        int[]? taken = fan ? new int[ranges.Length] : null;
        for (int p = 0; taken is not null && p < partitions.Length; p++)
        {
            taken[(int)((long)p * ranges.Length / partitions.Length)] = 1;
        }

        Task[] lanes = new Task[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
        {
            AggregationPartition partition = partitions[p];
            int first = (int)((long)p * ranges.Length / partitions.Length);
            lanes[p] = Task.Run(
                async () =>
                {
                    try
                    {
                        if (taken is not null)
                        {
                            await RunPartitionAsync(source, lane with { Rows = ranges[first] }, metrics, partition, token).ConfigureAwait(false);
                        }

                        // A lane that retired under pressure takes no more ranges past its own, the others
                        // taking them with the memory its table gave back.
                        int at;
                        while (!partition.Retiring && (at = Interlocked.Increment(ref next[0])) < ranges.Length)
                        {
                            if (taken is not null && Interlocked.Exchange(ref taken[at], 1) != 0)
                            {
                                continue;
                            }

                            if (settling is not null)
                            {
                                partition.Settle(settling, ranges[at]);
                            }

                            await RunPartitionAsync(source, lane with { Rows = ranges[at] }, metrics, partition, token).ConfigureAwait(false);
                        }

                        partition.DropUnmet();
                        if (partition.Retiring)
                        {
                            await partition.RetireAsync(token).ConfigureAwait(false);
                        }

                        await partition.EndedAsync(token).ConfigureAwait(false);
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
