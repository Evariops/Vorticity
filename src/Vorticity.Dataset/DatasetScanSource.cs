using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// A scan over every object of one version of a dataset, compiled to the dataset's engine: the
/// core's typed and tool scans ask this what they ask a file. Each object is read by its own file
/// scan, its batches count the dataset's rows, and an answer takes the cheapest proof each object
/// offers: its entry, its statistics, then its blocks.
/// </summary>
internal sealed class DatasetScanSource : ScanSource
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;

    internal DatasetScanSource(VortexDataset dataset, DatasetSnapshot version)
    {
        _dataset = dataset;
        _version = version;
    }

    internal override VortexSchema Schema => _version.Schema.Columns;

    internal override VortexSession Session => _dataset.Session;

    /// <summary>The rows of every object of the version, deleted ones counted, as its levels hold them: what bounds a table of a key's groups by value.</summary>
    internal override long RowBound => _version.RowCount;

    /// <summary>A dataset compacted by periods: its sealed objects carry synopses.</summary>
    internal override bool KeepsStates => _dataset.Options.Periods is not null;

    /// <summary>Its objects are read from its store, a request each.</summary>
    internal override bool ReadsRoundTrips => true;

    /// <summary>Whether the synopsis of an object the scan covers answers: its entry's table of contents says, with no read of the object.</summary>
    internal override async ValueTask<bool> TakesStatesAsync(ScanSpec spec, Aggregating.StateRequest states, CancellationToken cancellationToken) =>
        KeepsStates && spec.Options.UseSynopses && !spec.MatchesNothing
        && await Builder(await AlignedAsync(spec with { States = states }, null, cancellationToken).ConfigureAwait(false), null)
            .AnswersAnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The version every sink of the scan reads.</summary>
    internal ulong Version => _version.Version;

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>()
        : spec.Options.AlignWindow ? AlignedBatchesAsync(spec, metrics)
        : Builder(spec, metrics).ExecuteAsync();

    /// <summary>The batches of the scan whose window is aligned to its slices first.</summary>
    private async IAsyncEnumerable<RecordBatch> AlignedBatchesAsync(
        ScanSpec spec, ScanCounters metrics, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ScanSpec aligned = await AlignedAsync(spec, metrics, cancellationToken).ConfigureAwait(false);
        await foreach (RecordBatch batch in Builder(aligned, metrics).ExecuteAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing
            ? 0L
            : await Builder(await AlignedAsync(spec, metrics, cancellationToken).ConfigureAwait(false), metrics).CountAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        !spec.MatchesNothing
        && await Builder(await AlignedAsync(spec, metrics, cancellationToken).ConfigureAwait(false), metrics).AnyAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return FilterLiteral.Null;
        }

        DatasetScanBuilder builder = Builder(await AlignedAsync(spec, metrics, cancellationToken).ConfigureAwait(false), metrics);
        return min
            ? await builder.MinAsync(column.Path, cancellationToken).ConfigureAwait(false)
            : await builder.MaxAsync(column.Path, cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        ScanPlan plan = await Builder(await AlignedAsync(spec, null, cancellationToken).ConfigureAwait(false), null).PlanAsync(cancellationToken).ConfigureAwait(false);
        return spec.MatchesNothing ? plan with { MayMatch = false } : plan;
    }

    /// <summary>The filter and the states <see cref="_aligned"/> was worked out for, and the spec's filter it gave; null before.</summary>
    private (VortexExpr Asked, Aggregating.StateRequest States, (VortexExpr Filter, long Low, long High)? Aligned)? _aligned;

    /// <summary>
    /// The spec of an aggregation with its window grown to the bounds its objects' tables answer at, when its
    /// options ask and its filter bounds the instant (<see cref="WindowAlignment"/>), the window applied recorded
    /// in <paramref name="counters"/>; the spec as it is otherwise. Every operation of the scan applies the same
    /// window, worked out once, whether the objects answer with states or with rows.
    /// </summary>
    private async ValueTask<ScanSpec> AlignedAsync(ScanSpec spec, ScanCounters? counters, CancellationToken cancellationToken)
    {
        if (!spec.Options.AlignWindow || !KeepsStates || spec.Filter is not { } filter || spec.Aligning is not { } states
            || _version.Schema.Key is not { } key)
        {
            return spec;
        }

        if (_aligned is not { } known || !ReferenceEquals(known.Asked, filter) || !ReferenceEquals(known.States, states))
        {
            known = (filter, states, await WindowAlignment.OfAsync(_version, key.InstantPath, filter, states, _version.Header.Identifiers, cancellationToken)
                .ConfigureAwait(false));
            _aligned = known;
        }

        if (known.Aligned is not { } aligned)
        {
            return spec;
        }

        if (counters is not null)
        {
            TimeUnit unit = PeriodOptions.InstantOf(key);
            counters.WindowFrom = aligned.Low == long.MinValue ? null : TimeSlicer.TimeOf(aligned.Low, unit);
            counters.WindowTo = aligned.High == long.MaxValue ? null : TimeSlicer.TimeOf(aligned.High + 1, unit);
        }

        return spec with { Filter = aligned.Filter };
    }

    internal override bool MayMatch(VortexExpr filter) => _version.MayMatch(filter);

    internal override async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await DatasetKeyCursor.OpenAsync(_dataset, _version, path, distinct, indexes, cancellationToken).ConfigureAwait(false);

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        DatasetKeyCursor.ExplainAsync(_version, path, indexes, cancellationToken);

    /// <summary>
    /// On the first column of the clustering key, or a function of it: the key-ordered read opens an
    /// object once it could hold the next row, so a group of it is final once a greater key is read.
    /// Not in a dataset of periods, whose objects sort each slice by its synopsis columns: read in key
    /// order, every slice is sorted again, its instants read to order it and its columns read again in
    /// that order, which made a group by of time over a store 5 ms away 5.6 times slower than the pass
    /// that holds its groups at one lane, reading 14 times its bytes, and 35 times slower at sixteen.
    /// </summary>
    internal override bool OrdersOnAsking(FieldExpr column) =>
        !_version.Levels.ByPeriods && _version.Schema.Key is { } key && key.Paths.Count > 0 && string.Equals(key.Paths[0], column.Path, System.StringComparison.Ordinal);

    /// <summary>
    /// The loosest bounds the summaries of the version's root pages in hand give the column, over
    /// every object: a table of a key's groups by value over the objects,
    /// as over a file, with no read of its own.
    /// </summary>
    internal override Aggregating.KeyBounds? Bounds(Aggregating.ColumnShape key) => _version.Bounds(key.Path);

    /// <summary>The version's roots read first, as its walk reads them: then in hand.</summary>
    internal override async ValueTask<Aggregating.KeyBounds?> BoundsAsync(Aggregating.ColumnShape key, CancellationToken cancellationToken)
    {
        await _version.ReadRootsAsync(cancellationToken).ConfigureAwait(false);
        return _version.Bounds(key.Path);
    }

    /// <summary>
    /// The objects the summaries keep, each a range of the dataset's rows, an object of two shares of
    /// the rows or more cut between its chunks into ranges of a share or more: about four a lane, so
    /// that a lane on a slow core takes fewer and no lane waits on a large object the others finished
    /// beside.
    /// </summary>
    internal override async ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken)
    {
        if (spec.OrderPath is not null || spec.Take is not null)
        {
            return null;
        }

        spec = await AlignedAsync(spec, null, cancellationToken).ConfigureAwait(false);

        SummaryPruner? pruner = spec.Options.UseStatistics && spec.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;

        // An object its synopsis answers counts for none of the rows the lanes share, but for the rows of the tables its
        // answer folds, its states; one whose rows are read is cut as any other, however many tables it holds, which would
        // otherwise leave a lane alone over a period of rows.
        DatasetScanBuilder? answering = spec.States is null ? null : Builder(spec, null);
        List<(PositionedObject Held, RowRange Rows, bool Answered, bool Light, long States)> objects = [];
        long total = 0;
        long totalStates = 0;
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);
            if (end > start)
            {
                bool light = false;
                Indexes.SynopsisContents? contents = null;
                SynopsisAnswer? answer = answering?.AnswerOf(held, out light, out contents);
                long states = answer is not null && Cuttable(answer, held) ? answer.Weight(contents!) : 0;
                objects.Add((held, new RowRange(start, end), answer is not null, light, states));
                total += answer is not null ? 0 : end - start;
                totalStates += states;
            }
        }

        // The objects to cut are opened side by side to read where their chunks end; the scan finds
        // them open after. Each cut gives its object back, whatever another one's open did.
        long share = System.Math.Max(_dataset.Options.LeastPieceRows, total / (degree * 4L));
        Task<RowRange[]>?[] cuts = new Task<RowRange[]>?[objects.Count];
        List<Task> cutting = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Rows.Length >= 2 * share && !objects[o].Answered)
            {
                cuts[o] = CutAsync(objects[o].Held, objects[o].Rows, share, spec.Projection, cancellationToken);
                cutting.Add(cuts[o]!);
            }
        }

        // Objects their tables alone answer, in a row, make one piece, up to a few and while they fold less than a share of
        // states together: such an answer reads a few tables and folds their rows, where a piece costs a walk of the tree
        // to its rows and a lane's turn, which made a warm question over 48 hourly objects at 16 lanes spend more of its
        // lanes' time waiting than answering. An answer that folds two shares or more, its edges' rows counted, is cut
        // between the chunks of its tables, a lane's part of its slices each, its edges read by the part that holds them;
        // one that reads the rows of an identifier left out or of a delete keeps a piece, as one that reads its edges'
        // rows beside a few states does.
        await Task.WhenAll(cutting).ConfigureAwait(false);
        long stateShare = System.Math.Max(_dataset.Options.LeastPieceStates, totalStates / (degree * 4L));
        List<RowRange> pieces = [];
        List<int>? slices = null;
        int answers = 0;
        long folded = 0;
        for (int o = 0; o < objects.Count; o++)
        {
            RowRange rows = objects[o].Rows;
            if (cuts[o] is { } cut)
            {
                pieces.AddRange(await cut.ConfigureAwait(false));
                answers = 0;
            }
            else if (degree > 1 && objects[o].States >= 2 * stateShare && rows.Length == objects[o].Held.Entry.Rows
                && answering!.AnswerOf(objects[o].Held, out _, out Indexes.SynopsisContents? contents) is { } answer
                && CutAnswer(objects[o].Held, rows, answer, contents!, stateShare, slices ??= [], pieces))
            {
                answers = 0;
            }
            else if (objects[o].Light && answers is > 0 && answers < _dataset.Options.AnswersPerPiece
                && folded + objects[o].States <= stateShare && pieces[^1].End == rows.Start)
            {
                pieces[^1] = new RowRange(pieces[^1].Start, rows.End);
                answers++;
                folded += objects[o].States;
            }
            else
            {
                pieces.Add(rows);
                answers = objects[o].Light ? 1 : 0;
                folded = objects[o].States;
            }
        }

        return pieces.Count > 1 ? [.. pieces] : null;
    }

    /// <summary>
    /// Whether the answer of <paramref name="held"/> may be cut into parts its lanes answer apart: one that takes no row of
    /// a delete or of an identifier left out of its tables, which the object's rows and indexes find whole.
    /// </summary>
    private static bool Cuttable(SynopsisAnswer answer, in PositionedObject held) => answer.Excluded.Count == 0 && !held.Entry.HasDeletions;

    /// <summary>
    /// The rows <paramref name="rows"/> of an object its synopsis answers, cut before the slices its answer may be cut at
    /// into pieces of about <paramref name="share"/> states each (<see cref="SynopsisAnswer.Cuts"/>), each piece's lane
    /// answering its part of the slices alone; added to <paramref name="pieces"/>, false when the answer cuts nowhere.
    /// </summary>
    private static bool CutAnswer(
        in PositionedObject held, RowRange rows, SynopsisAnswer answer, Indexes.SynopsisContents contents, long share, List<int> slices, List<RowRange> pieces)
    {
        slices.Clear();
        answer.Cuts(contents, share, slices);
        if (slices.Count == 0)
        {
            return false;
        }

        IReadOnlyList<Indexes.TimeSlice> table = contents.Slices.Slices;
        long start = rows.Start;
        for (int s = 0; s < slices.Count; s++)
        {
            long at = held.FirstRow + table[slices[s]].FirstRow;
            pieces.Add(new RowRange(start, at));
            start = at;
        }

        pieces.Add(new RowRange(start, rows.End));
        return true;
    }

    /// <summary>
    /// The rows <paramref name="rows"/> of an object cut between its chunks, as a file's are, into
    /// ranges of <paramref name="share"/> rows or more: a cut inside a chunk would have both ranges
    /// decode it. A deleted row counts for none, and a chunk of deleted rows alone is in no range.
    /// </summary>
    private async Task<RowRange[]> CutAsync(PositionedObject held, RowRange rows, long share, FieldMask? projection, CancellationToken cancellationToken)
    {
        ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            // The projection names the version's columns, where an object of an earlier schema need
            // not hold them: its chunks are read whole for every column.
            FieldMask mask = projection is { } named && _version.Schema.ColumnsOf(lease.File.DType, held.Entry.Key) is null ? named : FieldMask.All;
            DeletionVector deletions = held.Entry.Deletions;
            long lower = rows.Start - held.FirstRow;
            long upper = rows.End - held.FirstRow;
            RowRange physical = deletions.IsEmpty ? new RowRange(lower, upper) : new RowRange(deletions.Physical(lower), deletions.Physical(upper - 1) + 1);
            LayoutTree tree = lease.File.LayoutTree;
            SplitPlan plan = SplitPlan.Compute(tree, physical, in mask, SplitPlan.NaturalBatchRows(tree));
            long parts = rows.Length / share;
            List<RowRange> pieces = [];
            long start = rows.Start;
            for (int b = 0; b < plan.BoundaryCount && pieces.Count < parts - 1; b++)
            {
                long at = held.FirstRow + deletions.Logical(plan.BoundaryAt(b));
                if (at - start >= share && at < rows.End)
                {
                    pieces.Add(new RowRange(start, at));
                    start = at;
                }
            }

            pieces.Add(new RowRange(start, rows.End));
            return [.. pieces];
        }
    }

    /// <summary>The engine's scan for <paramref name="spec"/>, adding what the objects' scans count to <paramref name="counters"/>.</summary>
    internal DatasetScanBuilder Builder(ScanSpec spec, ScanCounters? counters)
    {
        DatasetScanBuilder builder = new DatasetScanBuilder(_dataset, _version)
            .WithVariantKeys(spec.VariantKeys).WithStates(spec.States).WithReadAhead(spec.ReadAhead);
        if (spec.Projection is { } mask)
        {
            builder.Project(in mask);
        }

        if (spec.Filter is { } filter)
        {
            builder.Where(filter);
        }

        if (spec.Rows is { } rows)
        {
            builder.Rows(rows.Start, rows.End);
        }

        if (spec.Take is { } take)
        {
            builder.Take(take);
        }

        if (spec.OrderPath is { } order)
        {
            builder.InKeyOrder(order, spec.Descending);
        }

        ScanOptions options = spec.Options;
        builder.WithOptions(options, spec.KeepEncodings, spec.SinkDecodes, spec.PositionsUnread).WithIndexes(options.UseIndexes).WithSummaries(options.UseStatistics);
        return counters is null ? builder : builder.WithCounters(counters);
    }
}
