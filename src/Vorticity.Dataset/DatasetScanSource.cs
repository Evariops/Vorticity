using System;
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

    /// <summary>Its objects are read from its store, a request each.</summary>
    internal override bool ReadsRoundTrips => true;

    /// <summary>It settles each object's blocks as it reads it, those no delete marked a row of (<see cref="ScanSpec.Settles"/>).</summary>
    internal override bool SettlesBlocks => true;

    /// <summary>The version every sink of the scan reads.</summary>
    internal ulong Version => _version.Version;

    /// <summary>A dataset whose jobs' objects keep counts: those answer some aggregations as states (<see cref="CountsAnswer"/>).</summary>
    internal override bool KeepsStates => _version.MayKeepCounts;

    internal override async ValueTask<bool> TakesStatesAsync(ScanSpec spec, Aggregating.StateRequest states, CancellationToken cancellationToken) =>
        KeepsStates && spec.Options.UseSynopses && !spec.MatchesNothing
        && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false)
        && await Builder(spec with { States = states }, null).AnswersAnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The filter grown so that its range on the key's first column covers whole the objects it cuts whose counts would
    /// answer them whole, as <see cref="CountsAnswer"/> judges an object by the filter's other conjuncts: each bound inside
    /// such objects moves out to the furthest of their own bounds on the column, compared row-encoded as the key orders the
    /// column, whatever its type. A bound stays where it was when another such object would hold values on both sides of
    /// the new one, which growing on would cut in turn: objects cut by size that share a value of a composite key's first
    /// column, which a clustering value never leaves. Two walks of the version's entries. Null when the range cuts no such
    /// object or stays, the filter bounds no range of the column, or the counts answer no object of the question.
    /// </summary>
    internal override async ValueTask<(VortexExpr Filter, VortexExpr Window)?> AlignAsync(
        ScanSpec spec, Aggregating.StateRequest states, CancellationToken cancellationToken)
    {
        if (!KeepsStates || spec.Filter is not { } filter || _version.Schema.Key is not { } key
            || !await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The range: the conjuncts that compare the column with a value, the tightest bound on each side row-encoded; and
        // the rest, by which the counts judge an object.
        List<VortexExpr> conjuncts = [];
        Aggregating.GroupPredicates.Conjuncts(filter, conjuncts);
        List<ComparisonExpr> range = [];
        List<VortexExpr> rest = [];
        (byte[]? Bytes, bool Strict) low = default;
        (byte[]? Bytes, bool Strict) high = default;
        foreach (VortexExpr conjunct in conjuncts)
        {
            if (conjunct is not ComparisonExpr { Field: ColumnFieldExpr field } comparison
                || comparison.Op is not (ComparisonOp.Greater or ComparisonOp.GreaterOrEqual or ComparisonOp.Less or ComparisonOp.LessOrEqual)
                || !string.Equals(field.Path, key.InstantPath, System.StringComparison.Ordinal) || Encoded(key, comparison.Value) is not { } bound)
            {
                rest.Add(conjunct);
                continue;
            }

            range.Add(comparison);
            bool strict = comparison.Op is ComparisonOp.Greater or ComparisonOp.Less;
            if (comparison.Op is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual)
            {
                int order = low.Bytes is null ? 1 : bound.AsSpan().SequenceCompareTo(low.Bytes);
                low = order > 0 ? (bound, strict) : order == 0 ? (bound, strict || low.Strict) : low;
            }
            else
            {
                int order = high.Bytes is null ? -1 : bound.AsSpan().SequenceCompareTo(high.Bytes);
                high = order < 0 ? (bound, strict) : order == 0 ? (bound, strict || high.Strict) : high;
            }
        }

        VortexExpr? others = Aggregating.GroupPredicates.And(null, rest);
        if (range.Count == 0 || CountsAnswer.Of(states, others, _version) is not { } answer)
        {
            return null;
        }

        // Whether a value lies past the range's low bound, or its high one.
        static bool Below(byte[] value, (byte[]? Bytes, bool Strict) bound) =>
            bound.Bytes is { } at && value.AsSpan().SequenceCompareTo(at) is var order && (bound.Strict ? order <= 0 : order < 0);

        static bool Above(byte[] value, (byte[]? Bytes, bool Strict) bound) =>
            bound.Bytes is { } at && value.AsSpan().SequenceCompareTo(at) is var order && (bound.Strict ? order >= 0 : order > 0);

        // The objects the range reaches and cuts, which the counts would answer whole: the furthest of their bounds.
        (byte[] Bytes, FilterLiteral Value)? lowest = null;
        (byte[] Bytes, FilterLiteral Value)? highest = null;
        await foreach (PositionedObject held in WalkAsync(spec, others, Window(range, null, null), cancellationToken).ConfigureAwait(false))
        {
            if (Bounds(held, key) is not { } of || Above(of.Low.Bytes, high) || Below(of.High.Bytes, low) || answer.FieldOf(held) is null)
            {
                continue;
            }

            if (Below(of.Low.Bytes, low) && (lowest is not { } least || of.Low.Bytes.AsSpan().SequenceCompareTo(least.Bytes) < 0))
            {
                lowest = of.Low;
            }

            if (Above(of.High.Bytes, high) && (highest is not { } most || of.High.Bytes.AsSpan().SequenceCompareTo(most.Bytes) > 0))
            {
                highest = of.High;
            }
        }

        // Each new bound, unless another such object holds values on both sides of it.
        if (lowest is not null || highest is not null)
        {
            await foreach (PositionedObject held in WalkAsync(spec, others, Window(range, lowest?.Value, highest?.Value), cancellationToken).ConfigureAwait(false))
            {
                if (Bounds(held, key) is not { } of || answer.FieldOf(held) is null)
                {
                    continue;
                }

                if (lowest is { } least && of.Low.Bytes.AsSpan().SequenceCompareTo(least.Bytes) < 0 && of.High.Bytes.AsSpan().SequenceCompareTo(least.Bytes) >= 0)
                {
                    lowest = null;
                }

                if (highest is { } most && of.Low.Bytes.AsSpan().SequenceCompareTo(most.Bytes) <= 0 && of.High.Bytes.AsSpan().SequenceCompareTo(most.Bytes) > 0)
                {
                    highest = null;
                }
            }
        }

        if (lowest is null && highest is null)
        {
            return null;
        }

        VortexExpr window = Window(range, lowest?.Value, highest?.Value);
        return (others is null ? window : Expr.And(others, window), window);
    }

    /// <summary>The objects of the version that may hold rows of <paramref name="window"/> and <paramref name="others"/>, by their summaries.</summary>
    private IAsyncEnumerable<PositionedObject> WalkAsync(ScanSpec spec, VortexExpr? others, VortexExpr window, CancellationToken cancellationToken)
    {
        SummaryPruner? pruner = spec.Options.UseStatistics
            ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(others is null ? window : Expr.And(others, window)))
            : null;
        return _version.WalkAsync(pruner, 0, long.MaxValue, null, cancellationToken);
    }

    /// <summary>
    /// The range of <paramref name="range"/>'s conjuncts, its low bound <paramref name="lowest"/> when it grew, and its high
    /// one <paramref name="highest"/>, both inclusive.
    /// </summary>
    private static VortexExpr Window(List<ComparisonExpr> range, FilterLiteral? lowest, FilterLiteral? highest)
    {
        List<VortexExpr> bounds = [];
        FieldExpr column = range[0].Field;
        foreach (ComparisonExpr bound in range)
        {
            bool lower = bound.Op is ComparisonOp.Greater or ComparisonOp.GreaterOrEqual;
            if ((lower && lowest is null) || (!lower && highest is null))
            {
                bounds.Add(bound);
            }
        }

        if (lowest is { } low)
        {
            bounds.Add(Expr.Ge(column, Expr.Literal(low)));
        }

        if (highest is { } high)
        {
            bounds.Add(Expr.Le(column, Expr.Literal(high)));
        }

        return Aggregating.GroupPredicates.And(null, bounds)!;
    }

    /// <summary>
    /// The least and the greatest value of the key's first column in the object <paramref name="held"/>, each with its bytes
    /// row-encoded as the key orders the column: by its summaries, its keys bounding a text (<see cref="PositionedObject.Summaries"/>);
    /// null when they do not bound it, or prove it holds no null.
    /// </summary>
    private static ((byte[] Bytes, FilterLiteral Value) Low, (byte[] Bytes, FilterLiteral Value) High)? Bounds(in PositionedObject held, ClusteringKey key) =>
        held.Summaries(key).TryGet(key.InstantPath, out ColumnSummary column) && column.HasMin && column.HasMax
        && column.HasNullCount && column.NullCount == 0 && Encoded(key, column.Min) is { } low && Encoded(key, column.Max) is { } high
            ? ((low, column.Min), (high, column.Max))
            : null;

    /// <summary><paramref name="value"/> row-encoded as the key's first column, as a key of it alone; null for a value the column's type does not take.</summary>
    private static byte[]? Encoded(ClusteringKey key, FilterLiteral value)
    {
        if (value.Kind == FilterLiteralKind.Null)
        {
            return null;
        }

        try
        {
            return key.Encode([value]);
        }
        catch (System.ArgumentException)
        {
            return null;
        }
    }

    internal override IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>() : Builder(spec, metrics).ExecuteAsync();

    internal override async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing ? 0L : await Builder(spec, metrics).CountAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        !spec.MatchesNothing && await Builder(spec, metrics).AnyAsync(cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing)
        {
            return FilterLiteral.Null;
        }

        DatasetScanBuilder builder = Builder(spec, metrics);
        return min
            ? await builder.MinAsync(column.Path, cancellationToken).ConfigureAwait(false)
            : await builder.MaxAsync(column.Path, cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        ScanPlan plan = await Builder(spec, null).PlanAsync(cancellationToken).ConfigureAwait(false);
        return spec.MatchesNothing ? plan with { MayMatch = false } : plan;
    }

    internal override bool MayMatch(VortexExpr filter) => _version.MayMatch(filter);

    internal override async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await DatasetKeyCursor.OpenAsync(_dataset, _version, path, distinct, indexes, cancellationToken).ConfigureAwait(false);

    internal override ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        DatasetKeyCursor.ExplainAsync(_version, path, indexes, cancellationToken);

    /// <summary>
    /// On the first column of the clustering key, or a function of it: the key-ordered read opens an
    /// object once it could hold the next row, so a group of it is final once a greater key is read.
    /// Not in a dataset of periods, whose objects may order each slice by another column: read in key
    /// order, such a slice is sorted again, its instants read to order it and its columns read again in
    /// that order, which made a group by of time over a store 5 ms away 5.6 times slower than the pass
    /// that holds its groups at one lane, reading 14 times its bytes, and 35 times slower at sixteen. And only when
    /// <paramref name="stopsEarly"/>, a take or a window stopping the read, which reading in order is for: the read opens
    /// the objects one after the other, each through its own key-ordered scan, on one lane, where the pass that holds the
    /// groups reads them on every lane and spills what does not fit. A group by of the hour over objects of an hour each
    /// read on one lane of eight, where the pass read them on four, and at one lane allocated three times what the pass
    /// did, in 1.5 times its time.
    /// </summary>
    internal override bool OrdersOnAsking(FieldExpr column, bool stopsEarly) =>
        stopsEarly && !_version.Levels.ByPeriods && _version.Schema.Key is { } key && key.Paths.Count > 0
        && string.Equals(key.Paths[0], column.Path, System.StringComparison.Ordinal);

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

        SummaryPruner? pruner = spec.Options.UseStatistics && spec.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        List<(PositionedObject Held, RowRange Rows)> objects = [];
        long total = 0;
        long low = long.MinValue;
        long high = long.MaxValue;
        bool windowed = spec.Filter is { } bounded && _dataset.Key is { } instant && DatasetScanBuilder.Bound(bounded, instant.InstantPath, ref low, ref high);
        CountsAnswer? answer = spec.States is { } request && spec.Options.UseSynopses
            && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false)
            ? CountsAnswer.Of(request, spec.Filter, _version)
            : null;
        // A scan that rollups answer reads on one lane: the rows of their objects lie in no piece, and a piece of a scan
        // takes no rollup (RollupChoice).
        if (answer is not null && spec.Rows is null && await RollupChoice.OfAsync(_version, answer, pruner, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        HashSet<long> answered = [];
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);
            if (end <= start)
            {
                continue;
            }

            // An object its counts answer is read whole, as states: a piece of its own, never cut.
            if (answer is not null && start == held.FirstRow && end == held.FirstRow + held.Entry.Rows && answer.FieldOf(held) is not null)
            {
                objects.Add((held, new RowRange(start, end)));
                answered.Add(held.FirstRow);
                continue;
            }

            // An object reads the slices its window reaches alone, as its scan does (DatasetScanBuilder.Of): those are the
            // rows its lanes share, where a day's object cut at its every row left the last 15 minutes, 45 M rows, to two
            // of its 128 pieces, every other piece reading nothing.
            if (windowed && held.Entry.Deletions.IsEmpty && held.Entry.Contents is { } sliced
                && DatasetScanBuilder.WindowRows(sliced.Slices.Slices, low, high) is { } window)
            {
                start = System.Math.Max(start, held.FirstRow + window.Start);
                end = System.Math.Min(end, held.FirstRow + window.End);
            }

            if (end > start)
            {
                objects.Add((held, new RowRange(start, end)));
                total += end - start;
            }
        }

        // An object whose column sheets prove a value the filter asks for absent is no range: the sheets of the objects
        // read side by side, and the ranges' own walks find them read. An object its counts answer reads none: they say
        // which values it holds.
        if (spec.Options.UseStatistics && spec.Filter is { } asked && SheetPruning.Asks(asked, _version.Schema.DType) && ReadsSheets(objects))
        {
            List<PositionedObject> held = objects.ConvertAll(static o => o.Held);
            List<int> columns = SheetPruning.Columns(asked, _version.Schema.DType);
            List<PositionedObject> kept = await SheetPruning.KeptAsync(
                _dataset, _version.Schema, held, asked, columns, null, answered.Count == 0 ? null : o => answered.Contains(o.FirstRow), cancellationToken)
                .ConfigureAwait(false);
            if (kept.Count < held.Count)
            {
                HashSet<long> starts = [.. kept.ConvertAll(static o => o.FirstRow)];
                objects.RemoveAll(o => !starts.Contains(o.Held.FirstRow));
                total = 0;
                foreach ((PositionedObject _, RowRange rows) in objects)
                {
                    total += rows.Length;
                }
            }
        }

        // The objects to cut are opened side by side to read where their chunks end; the scan finds
        // them open after. Each cut gives its object back, whatever another one's open did. A lookup of an identifier
        // cuts none: it reads an object's rows by the identifier's index with no open of it, where a cut opens it, and
        // each piece would look the whole object up again.
        long share = System.Math.Max(_dataset.Options.LeastPieceRows, total / (degree * 4L));
        bool lookingUp = spec.Options.UseIndexes && DatasetScanBuilder.LookupOf(spec.Filter, _version.Header.Identifiers) is not null;
        Task<RowRange[]>?[] cuts = new Task<RowRange[]>?[objects.Count];
        List<Task> cutting = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (objects[o].Rows.Length >= 2 * share && !answered.Contains(objects[o].Held.FirstRow)
                && !(lookingUp && DatasetScanBuilder.LooksUp(objects[o].Held.Entry, _dataset.Options)))
            {
                cuts[o] = CutAsync(objects[o].Held, objects[o].Rows, share, spec.Projection, cancellationToken);
                cutting.Add(cuts[o]!);
            }
        }

        await Task.WhenAll(cutting).ConfigureAwait(false);
        List<RowRange> pieces = [];
        for (int o = 0; o < objects.Count; o++)
        {
            if (cuts[o] is { } cut)
            {
                pieces.AddRange(await cut.ConfigureAwait(false));
            }
            else
            {
                pieces.Add(objects[o].Rows);
            }
        }

        return pieces.Count > 1 ? [.. pieces] : null;
    }

    /// <summary>
    /// The rows of the pass as each object's ranges an estimate draws from, the objects its filter keeps by their summaries and
    /// their slices, in the order of their rows: each object cut where the chunks of the columns read end together, as a
    /// lane's piece is, into ranges of <paramref name="rangeBytes"/> of the narrowest column at least. The objects are opened
    /// side by side; the scans of the ranges find them open.
    /// </summary>
    internal override async ValueTask<RowRange[][]?> StrataAsync(ScanSpec pass, long rangeBytes, CancellationToken cancellationToken)
    {
        if (pass.MatchesNothing)
        {
            return [];
        }

        SummaryPruner? pruner = pass.Options.UseStatistics && pass.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = pass.Rows?.Start ?? 0;
        long to = pass.Rows?.End ?? long.MaxValue;
        long low = long.MinValue;
        long high = long.MaxValue;
        bool windowed = pass.Filter is { } bounded && _dataset.Key is { } instant && DatasetScanBuilder.Bound(bounded, instant.InstantPath, ref low, ref high);
        List<Task<RowRange[]>> objects = [];
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);

            // An object reads the slices its window reaches alone, as its scan does.
            if (windowed && held.Entry.Deletions.IsEmpty && held.Entry.Contents is { } sliced
                && DatasetScanBuilder.WindowRows(sliced.Slices.Slices, low, high) is { } window)
            {
                start = System.Math.Max(start, held.FirstRow + window.Start);
                end = System.Math.Min(end, held.FirstRow + window.End);
            }

            if (end > start)
            {
                objects.Add(ClustersAsync(held, new RowRange(start, end), pass.Projection, rangeBytes, cancellationToken));
            }
        }

        return await Task.WhenAll(objects).ConfigureAwait(false);
    }

    /// <summary>The rows <paramref name="rows"/> of an object as an estimate's ranges, between its chunks: a deleted row counts for none.</summary>
    private async Task<RowRange[]> ClustersAsync(PositionedObject held, RowRange rows, FieldMask? projection, long rangeBytes, CancellationToken cancellationToken)
    {
        ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            FieldMask mask = projection is { } named && _version.Schema.ColumnsOf(lease.File.DType, held.Entry.Key) is null ? named : FieldMask.All;
            DeletionVector deletions = held.Entry.Deletions;
            long lower = rows.Start - held.FirstRow;
            long upper = rows.End - held.FirstRow;
            RowRange physical = deletions.IsEmpty ? new RowRange(lower, upper) : new RowRange(deletions.Physical(lower), deletions.Physical(upper - 1) + 1);
            List<RowRange> clusters = Aggregating.EstimateStrata.Clusters(lease.File.LayoutTree, lease.File.SegmentSpecs, physical, in mask, rangeBytes);
            List<RowRange> logical = new List<RowRange>(clusters.Count);
            for (int c = 0; c < clusters.Count; c++)
            {
                long start = c == 0 ? rows.Start : held.FirstRow + deletions.Logical(clusters[c].Start);
                long end = c == clusters.Count - 1 ? rows.End : held.FirstRow + deletions.Logical(clusters[c].End);
                if (end > start)
                {
                    logical.Add(new RowRange(start, end));
                }
            }

            return [.. logical];
        }
    }

    /// <summary>Whether a scan reads the sheets of one of <paramref name="objects"/> before it (<see cref="SheetPruning.Reads"/>).</summary>
    private bool ReadsSheets(List<(PositionedObject Held, RowRange Rows)> objects)
    {
        foreach ((PositionedObject held, RowRange _) in objects)
        {
            if (SheetPruning.Reads(held.Entry, _dataset.Options, _version.Schema))
            {
                return true;
            }
        }

        return false;
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
            long[] starts = SplitPlan.CommonStarts(lease.File.LayoutTree, physical, in mask);
            long parts = rows.Length / share;
            List<RowRange> pieces = [];
            long start = rows.Start;
            for (int b = 0; b < starts.Length && pieces.Count < parts - 1; b++)
            {
                long at = held.FirstRow + deletions.Logical(starts[b]);
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
            .WithVariantKeys(spec.VariantKeys).WithReadAhead(spec.ReadAhead);
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
        if (spec.States is { } states && options.UseSynopses)
        {
            builder.WithStates(states);
        }

        if (spec.Settles is { } plan)
        {
            builder.WithSettles(plan);
        }

        return counters is null ? builder : builder.WithCounters(counters);
    }
}
