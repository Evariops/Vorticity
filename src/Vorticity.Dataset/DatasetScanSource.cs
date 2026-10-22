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
        // The objects of a pass that takes states are walked as its pieces' scans walk them, which then take them as they
        // are (Planned).
        DatasetScanBuilder? answering = spec.States is null ? null : Builder(spec, null);
        List<(PositionedObject Held, RowRange Rows, bool Answered, bool Light, long States, SynopsisAnswer? Answer)> objects = [];
        long total = 0;
        long totalStates = 0;
        IAsyncEnumerable<PositionedObject> walked = answering?.ObjectsAsync(cancellationToken) ?? _version.WalkAsync(pruner, from, to, null, cancellationToken);
        long low = long.MinValue;
        long high = long.MaxValue;
        bool windowed = spec.Filter is { } bounded && _dataset.Key is { } instant && DatasetScanBuilder.Bound(bounded, instant.InstantPath, ref low, ref high);
        await foreach (PositionedObject held in walked.ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);
            if (end <= start)
            {
                continue;
            }

            bool light = false;
            SynopsisAnswer? answer = answering?.AnswerOf(held, out light, out _);

            // An object read as rows reads the slices its window reaches alone, as its scan does (DatasetScanBuilder.Of):
            // those are the rows its lanes share, where a day's object cut at its every row left the last 15 minutes,
            // 45 M rows, to two of its 128 pieces, every other piece reading nothing.
            if (windowed && answer is null && held.Entry.Deletions.IsEmpty && held.Entry.Contents is { } sliced
                && DatasetScanBuilder.WindowRows(sliced.Slices.Slices, low, high) is { } window)
            {
                start = System.Math.Max(start, held.FirstRow + window.Start);
                end = System.Math.Min(end, held.FirstRow + window.End);
            }

            if (end > start)
            {
                objects.Add((held, new RowRange(start, end), answer is not null, light, 0, answer));
                total += answer is not null ? 0 : end - start;
            }
        }

        // An answer is weighed by the chunks of its tables, whose lists an entry names apart: those no question loaded yet,
        // loaded over the objects at once, a round trip, before the weights. Lists another question loaded since the answer
        // was made prune it now.
        List<(ObjectEntry, Indexes.SynopsisContents, Indexes.ISynopsisReads?)>? lists = null;
        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Answer is { } listing && objects[o].Held.Entry.Contents is { } listed)
            {
                if (listed.HasUnloaded(listing))
                {
                    (lists ??= []).Add((objects[o].Held.Entry, listed, listing));
                }
                else
                {
                    listing.Prune(listed);
                }
            }
        }

        if (lists is not null)
        {
            await _dataset.LoadSynopsisListsAsync(lists, cancellationToken).ConfigureAwait(false);
        }

        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Answer is { } weighed && Cuttable(weighed, objects[o].Held))
            {
                long states = weighed.Weight(objects[o].Held.Entry.Contents!);
                objects[o] = objects[o] with { States = states };
                totalStates += states;
            }
        }

        Planned? plan = null;
        if (answering is not null && spec.States is { } asked)
        {
            PositionedObject[] planned = new PositionedObject[objects.Count];
            for (int o = 0; o < planned.Length; o++)
            {
                planned[o] = objects[o].Held;
            }

            plan = new Planned(spec.Filter, asked, planned, answering.Question);
        }

        // The objects to cut are opened side by side to read where their chunks end; the scan finds
        // them open after. Each cut gives its object back, whatever another one's open did. A lookup of an identifier
        // cuts none: it reads an object's rows by the identifier's index with no open of it, where a cut opens it, and
        // each piece would look the whole object up again.
        long share = System.Math.Max(_dataset.Options.LeastPieceRows, total / (degree * 4L));
        bool lookingUp = spec.States is null && spec.Options.UseIndexes && DatasetScanBuilder.LookupOf(spec.Filter, _version.Header.Identifiers) is not null;
        Task<RowRange[]>?[] cuts = new Task<RowRange[]>?[objects.Count];
        List<Task> cutting = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Rows.Length >= 2 * share && !objects[o].Answered && !(lookingUp && DatasetScanBuilder.LooksUp(objects[o].Held.Entry, _dataset.Options)))
            {
                cuts[o] = CutAsync(objects[o].Held, objects[o].Rows, share, spec.Projection, cancellationToken);
                cutting.Add(cuts[o]!);
            }
        }

        // The edges of an answer, the slices its window cuts, are read as rows by the piece that holds them, a lane's: a
        // window that cuts a minute of 3 M rows decoded it in 38.7 ms at sixteen lanes as at one. Over several lanes, an
        // edge of two edge shares or more is cut between its chunks into pieces of its rows alone, which read them as the
        // edge was read, the window's filter over them; the pieces answered around it end and begin with its neighbours'
        // slices, and so answer no row of it.
        Task<RowRange[]>[]?[] edges = new Task<RowRange[]>[]?[objects.Count];
        for (int o = 0; o < objects.Count && degree > 1; o++)
        {
            if (objects[o].Answer is not { Edges.Count: > 0 } answer || !Cuttable(answer, objects[o].Held) || objects[o].Rows.Length != objects[o].Held.Entry.Rows)
            {
                continue;
            }

            List<Task<RowRange[]>>? cut = null;
            foreach (Indexes.TimeSlice edge in answer.Edges)
            {
                long edgeShare = System.Math.Max(_dataset.Options.LeastEdgePieceRows, edge.Rows / degree);
                if (edge.Rows >= 2 * edgeShare)
                {
                    long first = objects[o].Held.FirstRow + edge.FirstRow;
                    (cut ??= []).Add(CutAsync(objects[o].Held, new RowRange(first, first + edge.Rows), edgeShare, spec.Projection, cancellationToken));
                    cutting.Add(cut[^1]);
                }
            }

            edges[o] = cut is null ? null : [.. cut];
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
        int reading = 0;
        for (int o = 0; o < objects.Count; o++)
        {
            RowRange rows = objects[o].Rows;
            if (cuts[o] is { } cut)
            {
                RowRange[] parts = await cut.ConfigureAwait(false);
                pieces.AddRange(parts);
                reading += parts.Length;
                answers = 0;
            }
            else if (edges[o] is { } cutEdges
                && answering!.AnswerOf(objects[o].Held, out _, out Indexes.SynopsisContents? edgeContents) is { } edged)
            {
                // The answered parts between the edges, cut at the slices the answer may be cut at that fall inside them.
                slices ??= [];
                slices.Clear();
                edged.Cuts(edgeContents!, stateShare, slices);
                IReadOnlyList<Indexes.TimeSlice> table = edgeContents!.Slices.Slices;
                long start = rows.Start;
                foreach (Task<RowRange[]> edge in cutEdges)
                {
                    RowRange[] parts = await edge.ConfigureAwait(false);
                    Answered(objects[o].Held, table, slices, start, parts[0].Start, pieces);
                    pieces.AddRange(parts);
                    reading += parts.Length;
                    start = parts[^1].End;
                }

                Answered(objects[o].Held, table, slices, start, rows.End, pieces);
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
                reading += objects[o].Answered ? 0 : 1;
            }
        }

        RowRange[]? result = pieces.Count > 1 ? [.. pieces] : null;
        if (plan is not null)
        {
            System.Threading.Volatile.Write(ref _planned, plan with { Pieces = result, ReadingRows = reading });
        }

        return result;
    }

    /// <summary>The pieces of the last plan that read rows from the store, the objects no synopsis answers; every one of other pieces.</summary>
    internal override int RangesReadingRows(RowRange[] ranges) =>
        System.Threading.Volatile.Read(ref _planned) is { } planned && ReferenceEquals(planned.Pieces, ranges) ? planned.ReadingRows : ranges.Length;

    /// <summary>
    /// Whether the answer of <paramref name="held"/> may be cut into parts its lanes answer apart: one that takes no row of
    /// a delete or of an identifier left out of its tables, which the object's rows and indexes find whole.
    /// </summary>
    private static bool Cuttable(SynopsisAnswer answer, in PositionedObject held) => answer.Excluded.Count == 0 && !held.Entry.HasDeletions;

    /// <summary>
    /// The rows from <paramref name="start"/> to <paramref name="end"/> of an object its synopsis answers, slices whole,
    /// cut before those of <paramref name="cuts"/> that fall inside them; added to <paramref name="pieces"/>, nothing when
    /// they are none.
    /// </summary>
    private static void Answered(
        in PositionedObject held, IReadOnlyList<Indexes.TimeSlice> table, List<int> cuts, long start, long end, List<RowRange> pieces)
    {
        if (end <= start)
        {
            return;
        }

        foreach (int slice in cuts)
        {
            long at = held.FirstRow + table[slice].FirstRow;
            if (at > start && at < end)
            {
                pieces.Add(new RowRange(start, at));
                start = at;
            }
        }

        pieces.Add(new RowRange(start, end));
    }

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

        // A piece of the pass the last plan of pieces cut takes the objects it walked and the question it worked out.
        if (spec.States is { } states && System.Threading.Volatile.Read(ref _planned) is { } planned
            && ReferenceEquals(planned.States, states) && ReferenceEquals(planned.Filter, spec.Filter))
        {
            builder.WithPlanned(planned.Objects, planned.Question);
        }

        return counters is null ? builder : builder.WithCounters(counters);
    }

    /// <summary>
    /// The objects the last plan of pieces of a pass that takes states walked, in their order, and the question their
    /// answers share, with the spec's filter and states they were worked out for; null before. Every piece's scan
    /// walked the tree to its rows again and worked the question out again, its pruner, its walk's pages and their
    /// summaries made a piece: at sixteen lanes, a window cut in 55 pieces walked the tree 55 times a question.
    /// </summary>
    private Planned? _planned;

    /// <summary>What a plan of pieces walked and worked out, for the pieces' scans to take as they are.</summary>
    private sealed record Planned(VortexExpr? Filter, Aggregating.StateRequest States, PositionedObject[] Objects, SynopsisQuestion? Question)
    {
        /// <summary>The pieces the plan cut, which the engine runs on its lanes.</summary>
        internal RowRange[]? Pieces { get; init; }

        /// <summary>How many of them read rows from the store: those of the objects no synopsis answers.</summary>
        internal int ReadingRows { get; init; }
    }
}
