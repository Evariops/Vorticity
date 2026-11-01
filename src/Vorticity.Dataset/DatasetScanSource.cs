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
internal sealed partial class DatasetScanSource
    : ScanSource, IRowsCapability, IStatisticsCapability, IKeysCapability, IStatesCapability, ITasksCapability, ICostCapability
{
    private readonly VortexDataset _dataset;
    private readonly DatasetSnapshot _version;

    internal DatasetScanSource(VortexDataset dataset, DatasetSnapshot version)
    {
        _dataset = dataset;
        _version = version;
    }

    // The lookup of the partitions' indexes for the filter the question's lanes ask it with, made by the first that asks.
    private Locates? _locates;

    /// <summary>
    /// The lookup of the values <paramref name="filter"/> seeks of <paramref name="columns"/> over the version's partition
    /// indexes (<see cref="PartitionLocates.Of"/>), one for every lane of the question, which asks it with the question's
    /// filter: each partition's index read once for the question. Each lane's own, QP-2's pods of a route at 32 lanes read
    /// the same three regions of their partition's index 36 times, 37 MB of the 220 the question read.
    /// </summary>
    internal PartitionLocates? LocatesOf(VortexExpr filter, List<int> columns)
    {
        if (Volatile.Read(ref _locates) is { } held && ReferenceEquals(held.Filter, filter))
        {
            return held.Lookup;
        }

        Locates made = new Locates(filter, PartitionLocates.Of(_dataset, _version, filter, columns));
        return Interlocked.CompareExchange(ref _locates, made, null) is { } raced && ReferenceEquals(raced.Filter, filter) ? raced.Lookup : made.Lookup;
    }

    /// <summary>The lookup of the partitions' indexes made for <see cref="Filter"/>; null where none answers it.</summary>
    private sealed record Locates(VortexExpr Filter, PartitionLocates? Lookup);

    internal override VortexSchema Schema => _version.Schema.Columns;

    internal override VortexSession Session => _dataset.Session;

    internal override LogicalSource Logical => new LogicalSource(LogicalSourceKind.Dataset, _version.Version);

    internal override IRowsCapability Rows => this;

    internal override IStatisticsCapability Statistics => this;

    internal override IKeysCapability Keys => this;

    /// <summary>Its, when its jobs' objects keep counts: those answer some aggregations as states (<see cref="CountsAnswer"/>).</summary>
    internal override IStatesCapability? States => _version.MayKeepCounts ? this : null;

    internal override ITasksCapability Tasks => this;

    internal override ICostCapability Cost => this;

    /// <summary>The dataset's: its options' profile, or its store's.</summary>
    public IO.CostProfile Profile => _dataset.Profile;

    /// <summary>The rows of every object of the version, deleted ones counted, as its levels hold them: what bounds a table of a key's groups by value.</summary>
    public long RowBound => _version.RowCount;

    /// <summary>Its objects are read from its store, a request each.</summary>
    public bool ReadsRoundTrips => true;

    /// <summary>It settles each object's blocks as it reads it, those no delete marked a row of (<see cref="StatesAsked.Settles"/>).</summary>
    public bool SettlesBlocks => true;

    public async ValueTask<bool> TakesStatesAsync(ScanSpec spec, Aggregating.StateRequest states, CancellationToken cancellationToken) =>
        _version.MayKeepCounts && spec.Options.UseSynopses && !spec.MatchesNothing
        && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false)
        && await Builder(spec with { Answering = (spec.Answering ?? new StatesAsked()) with { States = states } }, null).AnswersAnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The filter grown so that its range on the key's first column covers whole the objects it cuts whose counts would
    /// answer them whole, as <see cref="CountsAnswer"/> judges an object by the filter's other conjuncts: each bound inside
    /// such objects moves out to the furthest of their own bounds on the column, compared row-encoded as the key orders the
    /// column, whatever its type. A bound stays where it was when another such object would hold values on both sides of
    /// the new one, which growing on would cut in turn: objects cut by size that share a value of a composite key's first
    /// column, which a partition value never leaves. Two walks of the version's entries. Null when the range cuts no such
    /// object or stays, the filter bounds no range of the column, or the counts answer no object of the question.
    /// </summary>
    public async ValueTask<(VortexExpr Filter, VortexExpr Window)?> AlignAsync(
        ScanSpec spec, Aggregating.StateRequest states, CancellationToken cancellationToken)
    {
        // The column a window is aligned on: the key's first, or under a partition the column it reads, objects holding one
        // partition each.
        if (!_version.MayKeepCounts || spec.Filter is not { } filter || AlignedOn() is not { } key
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
                || !string.Equals(field.Path, key.LeadingPath, System.StringComparison.Ordinal) || Encoded(key, comparison.Value) is not { } bound)
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
            if (Bounds(held, key, _version.Schema.Key is not null) is not { } of || Above(of.Low.Bytes, high) || Below(of.High.Bytes, low) || answer.FieldOf(held) is null)
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
                if (Bounds(held, key, _version.Schema.Key is not null) is not { } of || answer.FieldOf(held) is null)
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
    /// The column a window is aligned on, as a key of it alone: the layout key's first column, or the column the partition
    /// reads when the layout has a partition; null for neither.
    /// </summary>
    private DatasetKey? AlignedOn() =>
        _version.Schema.Key ?? (_version.Schema.Cut is { Path: { } path } ? DatasetKey.For([path], _version.Schema.DType) : null);

    /// <summary>
    /// The least and the greatest value of the aligned column in the object <paramref name="held"/>, each with its bytes
    /// row-encoded as the key orders the column: by its summaries, its keys bounding a text when the column leads the
    /// layout's key (<paramref name="ownKey"/>, <see cref="PositionedObject.Summaries(DatasetKey)"/>); null when they do not bound it, or
    /// prove it holds no null.
    /// </summary>
    private static ((byte[] Bytes, FilterLiteral Value) Low, (byte[] Bytes, FilterLiteral Value) High)? Bounds(in PositionedObject held, DatasetKey key, bool ownKey) =>
        (ownKey ? held.Summaries(key) : held.Entry.Summaries).TryGet(key.LeadingPath, out ColumnSummary column) && column.HasMin && column.HasMax
        && column.HasNullCount && column.NullCount == 0 && Encoded(key, column.Min) is { } low && Encoded(key, column.Max) is { } high
            ? ((low, column.Min), (high, column.Max))
            : null;

    /// <summary><paramref name="value"/> row-encoded as the key's first column, as a key of it alone; null for a value the column's type does not take.</summary>
    private static byte[]? Encoded(DatasetKey key, FilterLiteral value)
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

    public IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>() : Builder(spec, metrics).ExecuteAsync();

    /// <summary>A form of the question, answered by the count, the proof of a row, or the extreme the version works out.</summary>
    public async ValueTask<FormAnswer> AnswerAsync(ScanSpec spec, LogicalShape form, ScanCounters metrics, CancellationToken cancellationToken) => form switch
    {
        ExtremeShape extreme => new FormAnswer(0, await ExtremeAsync(spec, extreme.Column, extreme.Min, metrics, cancellationToken).ConfigureAwait(false)),
        _ when ReferenceEquals(form, LogicalShape.Any) => new FormAnswer(await AnyAsync(spec, metrics, cancellationToken).ConfigureAwait(false) ? 1 : 0, FilterLiteral.Null),
        _ => new FormAnswer(await CountAsync(spec, metrics, cancellationToken).ConfigureAwait(false), FilterLiteral.Null),
    };

    private async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        spec.MatchesNothing ? 0L : await Builder(spec, metrics).CountAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        !spec.MatchesNothing && await Builder(spec, metrics).AnyAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
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

    public async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, bool analyze, CancellationToken cancellationToken)
    {
        ScanPlan plan = await Builder(spec, null).PlanAsync(analyze, cancellationToken).ConfigureAwait(false);
        return spec.MatchesNothing ? plan with { MayMatch = false } : plan;
    }

    public async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await DatasetKeyCursor.OpenAsync(_version, path, distinct, indexes, cancellationToken).ConfigureAwait(false);

    public ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        DatasetKeyCursor.ExplainAsync(_version, path, indexes, cancellationToken);

    /// <summary>
    /// On the first column of the sort key, or a function of it: the key-ordered read opens an
    /// object once it could hold the next row, so a group of it is final once a greater key is read.
    /// Only when <paramref name="stopsEarly"/>, a take or a window stopping the read, which reading in order is for: the read opens
    /// the objects one after the other, each through its own key-ordered scan, on one lane, where the pass that holds the
    /// groups reads them on every lane and spills what does not fit. A group by of the hour over objects of an hour each
    /// read on one lane of eight, where the pass read them on four, and at one lane allocated three times what the pass
    /// did, in 1.5 times its time.
    /// </summary>
    public bool OrdersOnAsking(FieldExpr column, bool stopsEarly) =>
        stopsEarly && _version.Schema.Key is { } key && key.Paths.Count > 0
        && string.Equals(key.Paths[0], column.Path, System.StringComparison.Ordinal);

    /// <summary>Any column but the first of the sort key, which the objects lie in the order of (spec G, 2).</summary>
    public bool ReadsTopLate(FieldExpr column) => !OrdersOnAsking(column, stopsEarly: true);

    /// <summary>
    /// The loosest bounds the summaries of the version's root pages in hand give the column, over
    /// every object: a table of a key's groups by value over the objects,
    /// as over a file, with no read of its own.
    /// </summary>
    public Aggregating.KeyBounds? Bounds(Aggregating.ColumnShape key) => _version.Bounds(key.Path);

    /// <summary>The version's roots read first, as its walk reads them: then in hand.</summary>
    public async ValueTask<Aggregating.KeyBounds?> BoundsAsync(Aggregating.ColumnShape key, CancellationToken cancellationToken)
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
    public async ValueTask<RowRange[]?> PiecesAsync(ScanSpec spec, int degree, CancellationToken cancellationToken)
    {
        if (spec.OrderPath is not null || spec.Take is not null)
        {
            return null;
        }

        SummaryPruner? pruner = spec.Options.UseStatistics && spec.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = spec.Rows?.Start ?? 0;
        long to = spec.Rows?.End ?? long.MaxValue;
        DatasetScanBuilder planner = Builder(spec, null);
        List<(PositionedObject Held, RowRange Rows)> objects = [];
        long total = 0;
        KeyPruning? pruning = spec.Options.UseStatistics && spec.Options.UseIndexes ? KeyPruning.Of(_version.Schema, spec.Filter) : null;
        CountsAnswer? answer = spec.Answering?.States is { } request && spec.Options.UseSynopses
            && await _version.KeepsCountsAsync(cancellationToken).ConfigureAwait(false)
            ? CountsAnswer.Of(request, spec.Filter, _version)
            : null;
        // A scan that rollups answer reads on one lane: the rows of their objects lie in no piece, and a piece of a scan
        // takes no rollup (RollupChoice).
        if (answer is not null && spec.Rows is null && await RollupChoice.OfAsync(_version, answer, pruner, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        // The objects answered whole, those their granules answer with the rows they leave, and those whose choice waits on
        // their index, made as each is met.
        HashSet<long>? answered = null;
        Dictionary<long, long>? granular = null;
        HashSet<long>? undecided = null;
        List<(PositionedObject Held, RowRange Rows, List<RowRange>? Kept, Task<List<RowRange>?>? Reading)> walked = [];
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);
            if (end <= start)
            {
                continue;
            }

            // An object its counts answer is read whole, as states: a piece of its own, never cut.
            bool whole = start == held.FirstRow && end == held.FirstRow + held.Entry.Rows;
            if (whole && answer is not null && answer.FieldOf(held) is not null)
            {
                walked.Add((held, new RowRange(start, end), null, null));
                (answered ??= []).Add(held.FirstRow);
                continue;
            }

            // An object one lane's plan answers through its granules' statistics is answered so too, the rows they leave
            // weighed for its pieces: cut as rows, each piece read all of its own. Its key's index is read first where the
            // share it keeps could weigh less than its granules.
            bool deciding = false;
            if (whole && answer is not null)
            {
                await planner.PlacedAsync(held, cancellationToken).ConfigureAwait(false);
                (AnswerPath path, deciding) = planner.ChosenOnOneLane(held, kept: 1);
                if (!deciding && path == AnswerPath.Granules)
                {
                    walked.Add((held, new RowRange(start, end), null, null));
                    (granular ??= [])[held.FirstRow] = planner.GranulesLeave(held);
                    continue;
                }
            }

            // An object reads the granules the key's pruning keeps alone, by its sparse index: those are the rows its lanes
            // share, where pieces cut over every row of it would leave most lanes reading nothing. The indexes are asked as
            // the walk finds their objects and read side by side, a wave of requests, where one awaited at a time cost a
            // round trip each: fourteen for a time window over seven objects. An object kept whole is marked so, no list
            // made for it.
            ValueTask<List<RowRange>?> keeping = KeptAsync(held, pruning, planner, new RowRange(start, end), cancellationToken);
            walked.Add(keeping.IsCompletedSuccessfully
                ? (held, new RowRange(start, end), await keeping.ConfigureAwait(false) ?? AllKept, null)
                : (held, new RowRange(start, end), null, keeping.AsTask()));
            if (deciding)
            {
                (undecided ??= []).Add(held.FirstRow);
            }
        }

        foreach ((PositionedObject held, RowRange rows, List<RowRange>? done, Task<List<RowRange>?>? reading) in walked)
        {
            if (done is null && reading is null)
            {
                // An object answered whole reads no rows; one its granules answer, the rows they leave.
                objects.Add((held, rows));
                total += Weight(held, rows);
                continue;
            }

            // The read was asked by this walk, above.
#pragma warning disable VSTHRD003
            List<RowRange> read = done ?? await reading!.ConfigureAwait(false) ?? AllKept;
#pragma warning restore VSTHRD003
            bool whole = ReferenceEquals(read, AllKept);

            // An object its granules may answer, judged once its index said the share of its rows the question would read
            // otherwise.
            AnswerPath chosen = undecided?.Contains(held.FirstRow) == true
                ? planner.ChosenOnOneLane(held, whole ? (double)rows.Length / System.Math.Max(held.Entry.Rows, 1) : Share(held, read)).Path
                : AnswerPath.Rows;
            if (chosen == AnswerPath.Granules)
            {
                objects.Add((held, rows));
                (granular ??= [])[held.FirstRow] = planner.GranulesLeave(held);
                total += Weight(held, rows);
                continue;
            }

            if (whole)
            {
                objects.Add((held, rows));
                total += rows.Length;
                continue;
            }

            foreach (RowRange range in read)
            {
                objects.Add((held, range));
                total += range.Length;
            }
        }

        // An object whose column sheets prove a value the filter asks for absent is no range: the sheets of the objects
        // read side by side, and the ranges' own walks find them read. An object its counts answer reads none: they say
        // which values it holds.
        if (spec.Options.UseStatistics && spec.Filter is { } asked && SheetPruning.Asks(asked, _version.Schema.DType)
            && objects.Exists(o => planner.ReadsSheets(o.Held)))
        {
            List<PositionedObject> held = objects.ConvertAll(static o => o.Held);
            List<int> columns = SheetPruning.Columns(asked, _version.Schema.DType);
            List<PositionedObject> kept = await SheetPruning.KeptAsync(
                _dataset, _version.Schema, held, asked, columns, null, o => answered?.Contains(o.FirstRow) != true && planner.ReadsSheets(o), cancellationToken)
                .ConfigureAwait(false);
            if (kept.Count < held.Count)
            {
                HashSet<long> starts = [.. kept.ConvertAll(static o => o.FirstRow)];
                objects.RemoveAll(o => !starts.Contains(o.Held.FirstRow));
                total = 0;
                foreach ((PositionedObject each, RowRange rows) in objects)
                {
                    total += Weight(each, rows);
                }
            }
        }

        // The objects to cut are opened side by side to read where their chunks end; the scan finds
        // them open after. Each cut gives its object back, whatever another one's open did. A lookup of an identifier
        // cuts none: it reads an object's rows by the identifier's index with no open of it, where a cut opens it, and
        // each piece would look the whole object up again. An object its granules answer is cut where the rows they leave
        // weigh two shares or more, into pieces of about a share of those rows, each of which settles the granules it holds,
        // their statistics read ahead once for all of them; kept whole otherwise, on one lane, which reads its statistics and
        // the rows they leave, where cut as rows its pieces read all of its rows.
        long share = System.Math.Max(_dataset.Options.LeastPieceRows, total / (degree * 4L));
        bool lookingUp = spec.Options.UseIndexes && DatasetScanBuilder.LookupOf(spec.Filter, _version.Header.Identifiers) is not null;
        Task<RowRange[]>?[] cuts = new Task<RowRange[]>?[objects.Count];
        List<Task> cutting = [];
        for (int o = 0; o < objects.Count; o++)
        {
            (PositionedObject held, RowRange rows) = objects[o];
            long weight = Weight(held, rows);
            if (weight >= 2 * share && answered?.Contains(held.FirstRow) != true && !(lookingUp && planner.LooksUp(held)))
            {
                bool settles = granular?.ContainsKey(held.FirstRow) == true;
                cuts[o] = CutAsync(held, rows, settles ? (long)((double)share * rows.Length / weight) : share, planner.ReadMask, spec.Projection, cancellationToken);
                cutting.Add(cuts[o]!);
                if (settles)
                {
                    cutting.Add(planner.GranuleStatsAheadAsync(held, cancellationToken));
                }
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

        // What a range of an object reads: none of one answered whole, the rows its granules leave of one they answer, its
        // rows otherwise.
        long Weight(PositionedObject held, RowRange rows) =>
            answered?.Contains(held.FirstRow) == true ? 0
            : granular is not null && granular.TryGetValue(held.FirstRow, out long leave) ? leave
            : rows.Length;
    }

    public async ValueTask<(long Requests, long Bytes)?> CostAsync(ScanSpec spec, CancellationToken cancellationToken)
    {
        PathCosts.Cost cost = await Builder(spec, null).CostAsync(cancellationToken).ConfigureAwait(false);
        return (cost.Requests, cost.Bytes);
    }

    /// <summary>
    /// The rows of the pass as each object's ranges an estimate draws from, the objects its filter keeps by their summaries and
    /// their sparse indexes, in the order of their rows: each object cut where the chunks of the columns read end together, as a
    /// lane's piece is, into ranges of <paramref name="rangeBytes"/> of the narrowest column at least. The objects are opened
    /// side by side; the scans of the ranges find them open.
    /// </summary>
    public async ValueTask<RowRange[][]?> StrataAsync(ScanSpec pass, long rangeBytes, CancellationToken cancellationToken)
    {
        if (pass.MatchesNothing)
        {
            return [];
        }

        SummaryPruner? pruner = pass.Options.UseStatistics && pass.Filter is { } filter ? new SummaryPruner(Compute.FunctionFieldExpr.Ranges(filter)) : null;
        long from = pass.Rows?.Start ?? 0;
        long to = pass.Rows?.End ?? long.MaxValue;
        KeyPruning? pruning = pass.Options.UseStatistics && pass.Options.UseIndexes ? KeyPruning.Of(_version.Schema, pass.Filter) : null;
        DatasetScanBuilder planner = Builder(pass, null);
        List<Task<RowRange[]>> objects = [];
        await foreach (PositionedObject held in _version.WalkAsync(pruner, from, to, null, cancellationToken).ConfigureAwait(false))
        {
            long start = System.Math.Max(from, held.FirstRow);
            long end = System.Math.Min(to, held.FirstRow + held.Entry.Rows);

            // An object reads the granules the key's pruning keeps alone, by its sparse index.
            if (await KeptAsync(held, pruning, planner, new RowRange(start, end), cancellationToken).ConfigureAwait(false) is not { } ranges)
            {
                objects.Add(ClustersAsync(held, new RowRange(start, end), pass.Projection, rangeBytes, cancellationToken));
                continue;
            }

            foreach (RowRange kept in ranges)
            {
                objects.Add(ClustersAsync(held, kept, pass.Projection, rangeBytes, cancellationToken));
            }
        }

        return await Task.WhenAll(objects).ConfigureAwait(false);
    }

    /// <summary>The ranges of an object that keeps none of the rows asked: one list for every such object, never added to.</summary>
    private static readonly List<RowRange> NoneKept = [];

    /// <summary>The mark of an object a walk keeps whole, the rows it asked: one list, never read nor added to.</summary>
    private static readonly List<RowRange> AllKept = [];

    /// <summary>
    /// The parts of <paramref name="rows"/>, rows of the dataset inside an object's, that the key's pruning keeps of it
    /// (<see cref="KeyPruning"/>): none when the filter refutes its key range, the ranges of the granules it may keep a row of
    /// by its sparse index when <paramref name="planner"/>'s rule reads it, and the rows whole otherwise, null then, no list
    /// made, or for an object with marked rows, whose index counts the rows before the marks.
    /// </summary>
    private async ValueTask<List<RowRange>?> KeptAsync(PositionedObject held, KeyPruning? pruning, DatasetScanBuilder planner, RowRange rows, CancellationToken cancellationToken)
    {
        if (rows.IsEmpty)
        {
            return NoneKept;
        }

        ReadOnlyMemory<byte> first = held.TreeKey.Length > VortexDataset.UidBytes ? VortexDataset.OrderOf(held.TreeKey) : default;
        if (pruning is null || first.IsEmpty)
        {
            return null;
        }

        if (!pruning.MayHold(first.Span, held.Entry.LastKey.Span))
        {
            return NoneKept;
        }

        if (await planner.KeptGranulesAsync(held, pruning, first, read: true, cancellationToken).ConfigureAwait(false) is not ({ } ranges, { } keys))
        {
            return null;
        }

        List<RowRange> kept = [];
        foreach ((int from, int end) in ranges)
        {
            long start = System.Math.Max(rows.Start, held.FirstRow + ((long)from * keys.GranuleRows));
            long stop = System.Math.Min(rows.End, held.FirstRow + System.Math.Min((long)end * keys.GranuleRows, keys.Rows));
            if (stop > start)
            {
                kept.Add(new RowRange(start, stop));
            }
        }

        return kept;
    }

    /// <summary>The share of the rows of the object <paramref name="held"/> that <paramref name="kept"/>, ranges of the dataset's rows inside it, hold.</summary>
    private static double Share(in PositionedObject held, List<RowRange> kept)
    {
        long rows = 0;
        foreach (RowRange range in kept)
        {
            rows += range.Length;
        }

        return (double)rows / System.Math.Max(held.Entry.Rows, 1);
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

    /// <summary>
    /// The rows <paramref name="rows"/> of an object cut between its chunks, as a file's are, into
    /// ranges of <paramref name="share"/> rows or more: a cut inside a chunk would have both ranges
    /// decode it. A deleted row counts for none, and a chunk of deleted rows alone is in no range.
    /// </summary>
    /// <remarks>
    /// The cuts fall where the chunks of every column the scan reads start together, <paramref name="read"/>, its filter's
    /// with its projection's: a chunk of a column the filter alone reads, cut, was read by both pieces, 16.0 MB twice of
    /// the 183.2 the pods of one route read at 32 lanes over S1's spans (QP-2), its routes' chunks of a million rows cut
    /// where its pods' start. An object's columns' chunks end together at its slices and its groups of rows; where those
    /// leave fewer than three quarters of the pieces the chunks of the projection (<paramref name="projection"/>) alone
    /// place, those place them: a filter's chunks of few values hold millions of rows in few bytes, and their starts left
    /// the 19.2 M rows of an object of the routes under 5xx of a day of S2's logs (T11) six pieces where its projection
    /// cuts sixteen, twice as long at the S3 profile, to spare 1 MB of a route's pods (QP-2) read twice.
    /// </remarks>
    private async Task<RowRange[]> CutAsync(PositionedObject held, RowRange rows, long share, FieldMask read, FieldMask? projection, CancellationToken cancellationToken)
    {
        ObjectLease lease = await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            // The masks name the version's columns, where an object of an earlier schema need not hold them: its chunks are
            // read whole for every column.
            bool own = _version.Schema.ColumnsOf(lease.File.DType, held.Entry.Key) is null;
            DeletionVector deletions = held.Entry.Deletions;
            long lower = rows.Start - held.FirstRow;
            long upper = rows.End - held.FirstRow;
            RowRange physical = deletions.IsEmpty ? new RowRange(lower, upper) : new RowRange(deletions.Physical(lower), deletions.Physical(upper - 1) + 1);
            FieldMask mask = own ? read : FieldMask.All;
            List<RowRange> pieces = Cut(SplitPlan.CommonStarts(lease.File.LayoutTree, physical, in mask));
            if (own && projection is { } named)
            {
                List<RowRange> projected = Cut(SplitPlan.CommonStarts(lease.File.LayoutTree, physical, in named));
                if (pieces.Count * 4 < projected.Count * 3)
                {
                    pieces = projected;
                }
            }

            return [.. pieces];
        }

        List<RowRange> Cut(long[] starts)
        {
            long parts = rows.Length / share;
            List<RowRange> pieces = [];
            long start = rows.Start;
            for (int b = 0; b < starts.Length && pieces.Count < parts - 1; b++)
            {
                long at = held.FirstRow + held.Entry.Deletions.Logical(starts[b]);
                if (at - start >= share && at < rows.End)
                {
                    pieces.Add(new RowRange(start, at));
                    start = at;
                }
            }

            pieces.Add(new RowRange(start, rows.End));
            return pieces;
        }
    }

    /// <summary>The engine's scan for <paramref name="spec"/>, adding what the objects' scans count to <paramref name="counters"/>.</summary>
    internal DatasetScanBuilder Builder(ScanSpec spec, ScanCounters? counters)
    {
        DatasetScanBuilder builder = new DatasetScanBuilder(_dataset, _version)
            .WithVariantKeys(spec.VariantKeys).WithZoneKeys(spec.ZoneKeys).WithReadAhead(spec.ReadAhead).WithShape(spec.Query?.Shape).OfQuestion(this);
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
        if (spec.Answering?.States is { } states && options.UseSynopses)
        {
            builder.WithStates(states);
        }

        if (spec.Answering?.Settles is { } plan)
        {
            builder.WithSettles(plan);
        }

        return counters is null ? builder : builder.WithCounters(counters);
    }
}
