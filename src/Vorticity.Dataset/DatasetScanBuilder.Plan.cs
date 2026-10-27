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
    /// What the scan would cost, by the formula, before reading anything: each object it covers through the path its read
    /// takes, as its explanation says: its column's counts, its blocks' statistics and the rows of the blocks they are not
    /// expected to answer, the partial its family learned, or the rows of the columns it reads and filters by
    /// (<see cref="PathCosts"/>). An object a sheet or an index would pass over counts whole: the cost is a bound.
    /// </summary>
    internal async ValueTask<PathCosts.Cost> CostAsync(CancellationToken cancellationToken)
    {
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;
        int[] fields = FieldsRead();
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        List<int>? sheetColumns = _summaries && _filter is { } filter && SheetPruning.Asks(filter, _version.Schema.DType)
            ? SheetPruning.Columns(filter, _version.Schema.DType)
            : null;
        long requests = 0;
        long bytes = 0;
        await foreach (PositionedObject held in _version.WalkAsync(Pruner(), _from, _to, null, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            PathCosts.Cost cost;
            if (answer is not null && AnsweredBy(answer, held) is int field)
            {
                cost = _version.Costs.Regions(held.Entry.Uid, [held.Entry.Contents!.CountsOf(field), answer.AsksSketches ? held.Entry.Contents.SketchesOf(field) : null]);
            }
            else if (answer is not null && BlocksAnswer(held))
            {
                (PathCosts.Cost statistics, PathCosts.Cost unanswered, _) = BlockAnswer()!.Costs(held);
                cost = new PathCosts.Cost(statistics.Requests + unanswered.Requests, statistics.Bytes + unanswered.Bytes);
            }
            else if (answer is not null && Learns(held))
            {
                // A partial learned, or the partials of the objects a compaction merged into it, read nothing; else its rows.
                cost = _dataset.Partials.Holds(held.Entry.Uid, FamilyOf(held)!.Value.Key, cache) || _dataset.Partials.MergedInto(held.Entry.Uid, cache) is not null
                    ? default
                    : _version.Costs.Rows(held.Entry, FieldsOf(_family!));
            }
            else
            {
                cost = _version.Costs.Rows(held.Entry, fields);
            }

            // An object its counts do not answer is passed through its sheets first, or where the asked values live, when
            // the filter asks a column for values.
            if (answer is null || AnsweredBy(answer, held) is null)
            {
                PathCosts.Cost sheets = sheetColumns is { } columns ? SheetPruning.Cost(held.Entry, _dataset.Options, _version.Schema, columns, _version.Costs) : default;
                cost = new PathCosts.Cost(cost.Requests + sheets.Requests, cost.Bytes + sheets.Bytes);
            }

            requests += cost.Requests;
            bytes += cost.Bytes;
        }

        return new PathCosts.Cost((int)Math.Min(requests, int.MaxValue), bytes);
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
        await foreach (PositionedObject held in _version
            .WalkAsync(Pruner(), _from, _to, metrics, cancellationToken).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
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
            CursorBound(objects, keptByLevel));
    }

    /// <summary>
    /// What the scan will do as the core's plan, summed over the objects it would open: each one's
    /// own plan, worked out from its statistics, zone maps and indexes, and before them the rows the
    /// summaries refute as one step. No data segment is read, but every kept object is opened.
    /// </summary>
    /// <remarks>
    /// An object the summaries refute is never opened, so its blocks are counted at the block size
    /// of the objects that were, or at the dataset's own when none was.
    /// </remarks>
    public async ValueTask<ScanPlan> PlanAsync(CancellationToken cancellationToken = default)
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
        List<StatesStep> states = [];
        KeyRange? range = CountRange();
        CountsAnswer? answer = Answer() is { } asked && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false) ? asked : null;

        // A rollup the scan takes reads its column's totals, and their sketches beside them for a quantile, in a request,
        // for the objects it covers, which the walk passes over.
        RollupChoice? rollups = answer is null ? null : await RollupsAsync(answer, cancellationToken).ConfigureAwait(false);
        foreach ((RollupEntry rollup, int field, ObjectSummaries?[]? buckets) in rollups?.Chosen ?? [])
        {
            // Under values asked, large counts read their header, then the chunks of the values, a chunk each at most. A
            // rollup taken by bucket reads its counts by bucket, and answers for the rows of the buckets the filter keeps.
            RollupColumn kept = rollup.ColumnOf(field)!.Value;
            (Indexes.IndexSegment? counted, Indexes.IndexSegment? sketches, int countsHead, int sketchesHead) = kept.Regions(buckets is not null);
            Indexes.IndexSegment? sketched = answer!.AsksSketches ? sketches : null;
            string column = _version.Schema.Columns[field].Name;
            bool inPart = VortexDataset.ReadsInPart(kept, answer.AsksSketches, answer.Values, buckets is not null);
            int regions = (sketched is null ? 1 : 2) * (inPart ? 2 : 1);
            string what = buckets is null ? $"totals of {column}" : $"counts by bucket of {column}";
            string read = inPart ? $"the header of the {what} and the chunks of {answer.Values!.Length} values" : $"the {what}";
            long rows = buckets is null ? rollup.Rows : 0;
            int answered = 0;
            for (int b = 0; b < (buckets?.Length ?? 0); b++)
            {
                answered += buckets![b] is null ? 0 : 1;
                rows += buckets[b] is null ? 0 : rollup.Buckets[b].Rows;
            }

            states.Add(new StatesStep(
                rollup.Key,
                "rollup",
                $"{read}{(sketched is null ? string.Empty : " and their sketches")} over {rollup.Objects} objects, rank {rollup.Rank}"
                    + (buckets is null ? string.Empty : $", {answered} of its {buckets.Length} buckets"),
                regions));
            keptRows += rows;
            segments += regions;
            bytes += inPart
                ? InPart(counted!.Value, countsHead, answer.Values!.Length) + (sketched is { } some ? InPart(some, sketchesHead, answer.Values.Length) : 0)
                : counted!.Value.Length + (sketched?.Length ?? 0);
            mayMatch |= rows > 0;
        }

        await foreach (PositionedObject held in WalkAsync(cancellationToken, answer, rollups).ConfigureAwait(false))
        {
            long covered = Covered(held);
            if (covered == 0 || rollups?.CoverOf(held) >= 0)
            {
                continue;
            }

            // An object whose counts answer reads its column's counts, and their sketches beside them for a quantile, in a
            // request.
            if (answer is not null && AnsweredBy(answer, held) is null && BlocksAnswer(held))
            {
                // An object whose blocks may answer reads its columns' statistics over them and their indexes, in as many
                // requests as runs of them lie apart; the blocks they do not answer are read as rows. The formula's costs of
                // both paths say why.
                (PathCosts.Cost statistics, PathCosts.Cost unanswered, PathCosts.Cost read) = BlockAnswer()!.Costs(held);
                states.Add(new StatesStep(
                    held.Entry.Key,
                    "blocks",
                    $"the statistics over the blocks of {BlockAnswer()!.Fields.Length} columns, {statistics.Bytes} bytes in {statistics.Requests} requests where the rows would take {read.Bytes} in {read.Requests}, and the rows of the blocks they do not answer, about {unanswered.Bytes} bytes in {unanswered.Requests}",
                    statistics.Requests + unanswered.Requests));
                keptRows += covered;
                segments += statistics.Requests + unanswered.Requests;
                bytes += statistics.Bytes + unanswered.Bytes;
                mayMatch = true;
                continue;
            }

            if (answer is not null && AnsweredBy(answer, held) is int field)
            {
                // An object a delete marked reads the counts of its deleted rows too, from the commit object they lie in.
                Indexes.ObjectContents contents = held.Entry.Contents!;
                Indexes.IndexSegment? sketched = answer.AsksSketches ? contents.SketchesOf(field) : null;
                string column = _version.Schema.Columns[field].Name;
                // Under a value of an identifier left out, the rows its postings find, in three round trips cold: the index
                // entries and the chunk directories, the runs, the chunks.
                int less = (held.Entry.HasDeletions ? 1 : 0) + (answer.Exclusion is null ? 0 : 3);
                states.Add(new StatesStep(
                    held.Entry.Key,
                    "states",
                    (sketched is null ? $"the counts of {column}" : $"the counts of {column} and their sketches")
                        + (held.Entry.HasDeletions ? ", less those of its deleted rows" : string.Empty)
                        + (answer.Exclusion is { } exclusion ? $", less those of the rows of {exclusion.Path} its postings find" : string.Empty),
                    (sketched is null ? 1 : 2) + less));
                keptRows += covered;
                segments += (sketched is null ? 1 : 2) + less;
                bytes += contents.CountsOf(field)!.Value.Length + (sketched?.Length ?? 0) + (less > 0 ? held.Entry.NegativesAt.Length : 0);
                mayMatch = true;
                continue;
            }

            // An object of the question's family answers from the partial it learned, with no request, or learns it from its
            // rows, which it reads under the family's filter.
            if (answer is not null && Learns(held))
            {
                // An object a compaction merged others into alone sums their partials when each has one, else learns its own.
                SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
                bool learned = _dataset.Partials.Holds(held.Entry.Uid, FamilyOf(held)!.Value.Key, cache);
                bool merged = !learned && _dataset.Partials.MergedInto(held.Entry.Uid, cache) is not null;
                PathCosts.Cost rows = learned || merged ? default : _version.Costs.Rows(held.Entry, FieldsOf(_family!));
                states.Add(new StatesStep(
                    held.Entry.Key,
                    "partial",
                    learned ? "the partial of the question's family it learned"
                        : merged ? "the partials of the question's family of the objects a compaction merged into it, summed, or else its rows"
                        : $"its rows, {rows.Bytes} bytes in {rows.Requests} requests, the partial of the question's family learned from them",
                    rows.Requests));
                keptRows += covered;
                segments += rows.Requests;
                bytes += rows.Bytes;
                mayMatch = true;
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
                plan = await scan.ExplainAsync(cancellationToken).ConfigureAwait(false);
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
            States = [.. states],
        };
    }
}
