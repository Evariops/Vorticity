using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// What the scan would cost, at the dataset's profile, before reading anything: each unit through the alternative its
    /// weight chooses (<see cref="UnitsAsync"/>), as its explanation says, a rollup's totals, an object's counts, its blocks'
    /// statistics and the rows of the blocks they are not expected to answer, or its rows,
    /// with its sheets when the filter asks values of a column. An object a sheet or an index would pass over counts whole:
    /// the cost is a bound.
    /// </summary>
    internal async ValueTask<PathCosts.Cost> CostAsync(CancellationToken cancellationToken)
    {
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;
        (_, List<Planned> units) = await UnitsAsync(answer, explain: false, read: false, cancellationToken).ConfigureAwait(false);
        PathCosts.Cost cost = default;
        foreach (Planned unit in units)
        {
            cost += unit.Cost;
        }

        return cost;
    }

    /// <summary>
    /// What the scan would read, without reading a data object. A level 0 that has outgrown its
    /// ceiling is reported as a lag rather than refused, since compaction is the caller's own
    /// background job and the answers stay correct meanwhile.
    /// </summary>
    public async ValueTask<DatasetPlan> ExplainAsync(CancellationToken cancellationToken = default)
    {
        DatasetScanCounters metrics = new DatasetScanCounters();
        long objects = 0;
        long rows = 0;
        List<long> keptByLevel = [];

        // The partitions the kept objects lie in, which the walk gives one after the other by the tree's keys.
        long partitions = 0;
        ReadOnlyMemory<byte> partition = default;
        SortKey? tree = _version.Schema.Partitioned ? _version.Schema.Tree : null;
        await foreach (PositionedObject held in _version
            .WalkAsync(Pruner(), _from, _to, metrics, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            if (tree is not null && VortexDataset.OrderOf(held.TreeKey) is { IsEmpty: false } order
                && tree.PrefixLength(order.Span, 1) is > 0 and int length && (partitions == 0 || !order.Span[..length].SequenceEqual(partition.Span)))
            {
                partitions++;
                partition = order[..length];
            }

            objects++;
            rows += held.Entry.Rows;
            while (keptByLevel.Count <= held.Level)
            {
                keptByLevel.Add(0);
            }

            keptByLevel[held.Level]++;
        }

        DatasetLevels levels = _version.Levels;
        long[] byLevel = new long[Math.Max(levels.Count, 1)];
        for (int level = 0; level < byLevel.Length; level++)
        {
            byLevel[level] = levels[level].Entries;
        }

        return new DatasetPlan(
            _version.Version,
            byLevel,
            levels.LagAtLevelZero(),
            _version.Schema.Layout?.Keyed == true,
            objects,
            rows,
            metrics.ObjectsSkipped,
            metrics.SubtreesSkipped,
            _orderPaths is null ? null : _orderPaths.Length == 1 ? _orderPaths[0] : "(" + string.Join(", ", _orderPaths) + ")",
            CursorBound(objects, keptByLevel, partitions));
    }

    /// <summary>
    /// What the scan will do (spec B, 7): each unit as its weight chooses it (<see cref="UnitsAsync"/>), explained, and the
    /// rows the summaries refute as one step. Without <paramref name="analyze"/>, from the version's entries alone: an object
    /// read as rows weighs what its table of contents says, its blocks all live, nothing read. With it, the units whose choice
    /// reads rows pass through their sheets, and each object read as rows is opened for its own plan, worked out from its
    /// statistics, zone maps and indexes: no data segment is read.
    /// </summary>
    /// <remarks>
    /// An object the summaries refute is never opened, so its blocks are counted at the block size
    /// of the objects that were, or at the dataset's own when none was.
    /// </remarks>
    public async ValueTask<ScanPlan> PlanAsync(bool analyze = true, CancellationToken cancellationToken = default)
    {
        long keptRows = 0;
        long blockRows = 0;
        long blocks = 0;
        long live = 0;
        long segments = 0;
        long bytes = 0;
        bool mayMatch = false;
        List<PruningStep> steps = [];
        Dictionary<string, int> stepAt = new Dictionary<string, int>(StringComparer.Ordinal);
        bool exact = true;
        long exactRows = 0;
        long pruned = 0;
        long proven = 0;
        long decoded = 0;
        List<string> sources = [];
        long runs = 0;
        long entries = 0;
        KeyRange? range = CountRange();
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;
        (_, List<Planned> units) = await UnitsAsync(answer, explain: true, read: analyze, cancellationToken).ConfigureAwait(false);
        List<UnitStep> explained = new List<UnitStep>(units.Count);
        PathCosts.Cost total = default;
        foreach (Planned unit in units)
        {
            total += unit.Cost;
            explained.Add(new UnitStep(
                unit.Name, unit.Level == UnitLevel.Run ? "run" : "object", unit.Chosen.Name, unit.Chosen.Describe(unit, unit.Cost),
                unit.Cost.Requests, unit.Cost.Bytes, unit.Weight, [.. unit.Rejected ?? []]));
            if (unit.Chosen.Path is not (AnswerPath.Rows or AnswerPath.Zones))
            {
                // A unit its synopses answer reads what its alternative says: a rollup the rows of the buckets the filter
                // keeps, an object the rows the scan covers.
                long answered = unit.Run is { } rollup ? RowsOf(rollup, unit.Buckets) : Covered(unit.Held);
                keptRows += answered;
                segments += unit.Cost.Requests;
                bytes += unit.Cost.Bytes;
                mayMatch |= answered > 0;
                continue;
            }

            PositionedObject held = unit.Held;
            long covered = Covered(held);
            if (!analyze)
            {
                // From the entry alone: its rows' blocks, every one live, and what its table of contents says they cost.
                long perObject = Math.Max(_dataset.Options.Write.BlockRows, 1);
                long touched = (covered + perObject - 1) / perObject;
                keptRows += covered;
                blocks += touched;
                live += touched;
                segments += unit.Cost.Requests;
                bytes += unit.Cost.Bytes;
                mayMatch |= covered > 0;
                exact &= _filter is null;
                exactRows += _filter is null ? covered : 0;
                decoded += _filter is null ? 0 : touched;
                continue;
            }

            ScanExplanation plan;
            bool evaluated = false;
            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);

                // An object is planned as its own scan reads it: the filter it takes, or none when the
                // filter is evaluated on its reshaped batches.
                ObjectPart part = PartOf(ColumnsOf(lease, held.Entry), _mask);
                if (part.Fate == FilterFate.None)
                {
                    continue;
                }

                evaluated = part.Fate == FilterFate.Reshaped;
                ScanBuilder scan = _orderPaths is { } paths && part.Mapped is null
                    ? OrderedOf(lease.File, paths, withKey: false)
                    : ScanOf(lease.File, held, part);
                plan = await scan.ExplainAsync(analyze: true, cancellationToken).ConfigureAwait(false);
            }

            keptRows += covered;

            blockRows = blockRows == 0 ? plan.BlockRows : blockRows;
            blocks += plan.Blocks;
            live += plan.LiveBlocks;
            segments += plan.SegmentsToRead;
            bytes += plan.BytesToRead;
            mayMatch |= plan.FileMayMatch;
            foreach (PruningStep step in plan.Pruning)
            {
                if (stepAt.TryGetValue(step.Structure, out int at))
                {
                    PruningStep sum = steps[at];
                    steps[at] = sum with
                    {
                        BlocksPruned = sum.BlocksPruned + step.BlocksPruned,
                        SegmentsRead = sum.SegmentsRead + step.SegmentsRead,
                        BytesRead = sum.BytesRead + step.BytesRead,
                    };
                }
                else
                {
                    stepAt[step.Structure] = steps.Count;
                    steps.Add(step);
                }
            }

            if (evaluated || (held.Entry.HasDeletions && !Settled(held, range)))
            {
                // Its rows are counted by evaluating the filter on them, or its own plan counts rows
                // that are deleted: no plan settles the count.
                exact = false;
            }
            else if (Settled(held, range) || plan.Count is null)
            {
                exactRows += covered;
            }
            else
            {
                CountExplanation count = plan.Count;
                exact &= count.ExactCover;
                exactRows += count.ExactCount;
                pruned += count.SplitsPruned;
                proven += count.SplitsProven;
                decoded += count.SplitsDecoded;
            }

            if (plan.Order is { } order)
            {
                string source = order.Source.ToString();
                if (!sources.Contains(source))
                {
                    sources.Add(source);
                }

                runs += order.RunsInRange;
                entries += order.EntriesInRange;
            }
        }

        long coveredRows = _take is { } take ? take.Length : Math.Max(Math.Min(_to, _version.RowCount) - _from, 0);
        long refuted = Math.Max(coveredRows - keptRows, 0);
        long perBlock = blockRows > 0 ? blockRows : Math.Max(_dataset.Options.Write.BlockRows, 1);
        long refutedBlocks = (refuted + perBlock - 1) / perBlock;
        List<PruningStep> pruning = new List<PruningStep>(steps.Count + 1);
        if (refutedBlocks > 0)
        {
            pruning.Add(new PruningStep("object summaries", Narrow(refutedBlocks), 0, 0));
        }

        pruning.AddRange(steps);
        return new ScanPlan(
            coveredRows,
            Narrow(blocks + refutedBlocks),
            Narrow(live),
            Narrow(segments),
            bytes,
            mayMatch,
            [.. pruning],
            new CountPlan(exact, exactRows, Narrow(pruned), Narrow(proven), Narrow(decoded)),
            _orderPaths is null
                ? null
                : new OrderPlan(sources.Count == 0 ? nameof(KeySourceKind.None) : string.Join('+', sources), Narrow(runs), entries, _descending))
        {
            Units = [.. explained],
            Cost = new PlanCost(total.Requests, total.Bytes, total.Nanoseconds, _version.Costs.Profile.Predict(total.Requests, total.Bytes, total.Nanoseconds)),
        };
    }

    /// <summary>The rows <paramref name="rollup"/> answers for: its objects', or those of the buckets the filter keeps of them under <paramref name="buckets"/>.</summary>
    private static long RowsOf(RollupEntry rollup, ObjectSummaries?[]? buckets)
    {
        if (buckets is null)
        {
            return rollup.Rows;
        }

        long rows = 0;
        for (int b = 0; b < buckets.Length; b++)
        {
            rows += buckets[b] is null ? 0 : rollup.Buckets[b].Rows;
        }

        return rows;
    }
}
