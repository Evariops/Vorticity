using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity;

/// <summary>
/// What one scan asks of its source: the columns, the filter, the rows, the order, the options; derived from the question it
/// serves (<see cref="Query"/>), then narrowed by the engine for each of its passes and parts.
/// </summary>
internal sealed record ScanSpec
{
    /// <summary>The question the scan serves, as the caller asked it; null for a read of the engine's own.</summary>
    internal LogicalQuery? Query { get; init; }

    /// <summary>The columns to deliver, or null for every column.</summary>
    internal FieldMask? Projection { get; init; }

    /// <summary>The filter, or null for every row.</summary>
    internal VortexExpr? Filter { get; init; }

    /// <summary>Whether the filter is known to reject every row, so that nothing is read.</summary>
    internal bool MatchesNothing { get; init; }

    internal RowRange? Rows { get; init; }

    /// <summary>
    /// The rows asked for by position, or null for every row: held unchanged by whoever made the spec until its scans end,
    /// so that a source reads a list that ascends without a copy of it.
    /// </summary>
    internal ReadOnlyMemory<long>? Take { get; init; }

    internal string? OrderPath { get; init; }

    internal bool Descending { get; init; }

    /// <summary>
    /// Whether the splits come last one first, each still in file order, on one lane: a stream on a
    /// sorted column read from its greatest values down, which needs the order of the splits and
    /// not that of the rows inside one.
    /// </summary>
    internal bool Backward { get; init; }

    internal ScanOptions Options { get; init; } = ScanOptions.Default;

    /// <summary>Whether the decoders may deliver dictionary and run-end columns in their encoded form, for a consumer that reads it.</summary>
    internal bool KeepEncodings { get; init; }

    /// <summary>
    /// The variant columns, by name, the consumer reads only through these top-level keys, which a
    /// file's path map lets a scan read alone; null when it reads every variant whole.
    /// </summary>
    internal IReadOnlyDictionary<string, string[]>? VariantKeys { get; init; }

    /// <summary>
    /// The top-level columns, by name, the consumer reads only through these functions of them, a key's truncation say:
    /// a split whose zones send each function to one value takes the zones' values in place of the column
    /// (<see cref="ZoneKeyReads"/>); null for none.
    /// </summary>
    internal ZoneKeyReads.Request? ZoneKeys { get; init; }

    /// <summary>
    /// The spec of a terminal that delivers no column but <paramref name="uses"/>: a variant column those and the filter
    /// read through top-level keys is read by those keys alone (<see cref="VariantKeys"/>), where a count over a filter on
    /// one key parsed every row's variant.
    /// </summary>
    internal ScanSpec Reading(IEnumerable<FieldExpr> uses) => this with { VariantKeys = Indexes.VariantKeyReads.Of(uses, Filter) };

    /// <summary>
    /// Whether the consumer reads the encoded forms itself, as an aggregation does, so that a block
    /// counts as decoded only when a column of it reached canonical form.
    /// </summary>
    internal bool SinkDecodes { get; init; }

    /// <summary>
    /// The splits a scan of one lane reads ahead while it decodes one, over a source whose read is a
    /// round trip, 0 for none: an aggregation's lanes, which decode nothing ahead and read every split they
    /// are given, so that no read ahead is wasted, as many as their admission reserved.
    /// </summary>
    internal int ReadAhead { get; init; }

    /// <summary>
    /// Whether the consumer reads no row's place, as an aggregation folds values without asking
    /// where they lie, so that a source may leave a row out of a batch by its selection alone rather
    /// than gather the others: the rows kept are right, their numbering from the batch's start is not.
    /// </summary>
    internal bool PositionsUnread { get; init; }

    /// <summary>
    /// What the analysis of the scan's structures found (spec B, 5), refined already: the ranges of one aggregation share one
    /// read of the structures instead of each reading them again; null when the read analyses for itself.
    /// </summary>
    internal ScanAnalysis? Analysis { get; init; }

    /// <summary>
    /// What an aggregation asks of a source that answers some of its units in place of rows (spec B, 8): what its states
    /// capability reads, and its rows capability does not; null for rows alone.
    /// </summary>
    internal StatesAsked? Answering { get; init; }
}

/// <summary>What the analysis of a scan's structures found (spec B, 5), shared by the ranges of one read.</summary>
/// <param name="Live">The filter's mask of live blocks; null when no structure prunes anything.</param>
internal sealed record ScanAnalysis(BlockMask? Live);

/// <summary>What an aggregation asks of a source that answers some of its units in place of rows (spec B, 8).</summary>
/// <param name="States">
/// The states it would take in place of rows (<see cref="Aggregating.StateRequest"/>): a source with a states capability may
/// answer those with batches of states; null for none.
/// </param>
/// <param name="Settles">
/// The aggregation whose blocks a source of several files, a dataset, settles by their zone maps as it reads each file, in
/// place of their rows (<see cref="Aggregating.ZoneSettling.PlanObjectAsync"/>): set when the zone maps answer its every
/// aggregate and key, a source that reads one file being settled by the aggregation itself; null for none.
/// </param>
internal sealed record StatesAsked(Aggregating.StateRequest? States = null, Aggregating.AggregationPlan? Settles = null);

/// <summary>
/// Where a scan's batches and answers come from: a file, a stream, or every object of a dataset's version. The engine sees a
/// source through its capabilities alone (spec B, 8), each null where the source has none: the typed and the tool scans
/// compile to a <see cref="ScanSpec"/> and a form of their question, which its rows answer.
/// </summary>
internal abstract class ScanSource
{
    /// <summary>The columns the source holds.</summary>
    internal abstract VortexSchema Schema { get; }

    /// <summary>The session whose pool, cache and parallelism the scan uses.</summary>
    internal abstract VortexSession Session { get; }

    /// <summary>What the source is to a question: a file, a version of a dataset, a stream.</summary>
    internal abstract LogicalSource Logical { get; }

    /// <summary>Its rows, which every source gives.</summary>
    internal abstract IRowsCapability Rows { get; }

    /// <summary>What it knows of its rows before reading them; null when it knows nothing.</summary>
    internal virtual IStatisticsCapability? Statistics => null;

    /// <summary>The walks of its columns' keys; null when it has none.</summary>
    internal virtual IKeysCapability? Keys => null;

    /// <summary>The states of an aggregation it gives in place of rows; null for a source of rows alone.</summary>
    internal virtual IStatesCapability? States => null;

    /// <summary>Its units as tasks read side by side; null when the engine cuts them.</summary>
    internal virtual ITasksCapability? Tasks => null;

    /// <summary>What reading it costs; null for a source that cannot say, priced as a remote store.</summary>
    internal virtual ICostCapability? Cost => null;

    /// <summary>
    /// The plan of <paramref name="spec"/> (<see cref="IRowsCapability.ExplainAsync"/>), reading the structures it would
    /// consult with <paramref name="analyze"/>, with the question it serves in words.
    /// </summary>
    internal async ValueTask<ScanPlan> PlanAsync(ScanSpec spec, bool analyze, CancellationToken cancellationToken)
    {
        ScanPlan plan = await Rows.ExplainAsync(spec, analyze, cancellationToken).ConfigureAwait(false);
        return spec.Query is { } query ? plan with { Logical = query.Text } : plan;
    }
}

/// <summary>A scan over one open file, compiled to the engine's builder.</summary>
internal sealed class FileScanSource : ScanSource, IRowsCapability, IStatisticsCapability, IKeysCapability, ITasksCapability, ICostCapability
{
    private readonly VortexFile _file;

    internal FileScanSource(VortexFile file) => _file = file;

    internal VortexFile File => _file;

    internal override LogicalSource Logical => new LogicalSource(LogicalSourceKind.File);

    internal override VortexSchema Schema => _file.Schema;

    internal override IRowsCapability Rows => this;

    internal override IStatisticsCapability Statistics => this;

    internal override IKeysCapability Keys => this;

    internal override ITasksCapability Tasks => this;

    internal override ICostCapability Cost => this;

    /// <summary>The file statistics' extremes of the column, when they hold them exactly.</summary>
    public Aggregating.KeyBounds? Bounds(Aggregating.ColumnShape key) => Aggregating.AggregationEngine.FileBounds(_file, key);

    internal override VortexSession Session => _file.Session;

    /// <summary>Its source's: a remote store's for a file read from an object store, a local disk's otherwise.</summary>
    public IO.CostProfile Profile => SessionReader.Unwrap(_file.Source) is IO.ObjectStoreSource store ? store.Profile : IO.CostProfile.LocalDisk;

    public IAsyncEnumerable<RecordBatch> BatchesAsync(ScanSpec spec, ScanCounters metrics) =>
        spec.MatchesNothing ? System.Linq.AsyncEnumerable.Empty<RecordBatch>()
        : Refuted(spec) ? RefusedAsync(spec, metrics)
        : Builder(spec, metrics).ExecuteAsync();

    /// <summary>The file's rows the pass reads as the ranges of one list; none when its statistics refute the filter.</summary>
    public ValueTask<RowRange[][]?> StrataAsync(ScanSpec pass, long rangeBytes, CancellationToken cancellationToken)
    {
        RowRange whole = new RowRange(0, _file.RowCount);
        RowRange rows = pass.Rows is { } asked ? asked.Intersect(whole) : whole;
        if (pass.MatchesNothing || rows.IsEmpty || Refuted(pass))
        {
            return ValueTask.FromResult<RowRange[][]?>([]);
        }

        FieldMask mask = pass.Projection ?? FieldMask.All;
        return ValueTask.FromResult<RowRange[][]?>([[.. Aggregating.EstimateStrata.Clusters(_file.LayoutTree, _file.SegmentSpecs, rows, in mask, rangeBytes)]]);
    }

    /// <summary>A form of the question, answered by the count, the proof of a row, or the extreme the source works out.</summary>
    public async ValueTask<FormAnswer> AnswerAsync(ScanSpec spec, LogicalShape form, ScanCounters metrics, CancellationToken cancellationToken) => form switch
    {
        ExtremeShape extreme => new FormAnswer(0, await ExtremeAsync(spec, extreme.Column, extreme.Min, metrics, cancellationToken).ConfigureAwait(false)),
        _ when ReferenceEquals(form, LogicalShape.Any) => new FormAnswer(await AnyAsync(spec, metrics, cancellationToken).ConfigureAwait(false) ? 1 : 0, FilterLiteral.Null),
        _ => new FormAnswer(await CountAsync(spec, metrics, cancellationToken).ConfigureAwait(false), FilterLiteral.Null),
    };

    private async ValueTask<long> CountAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing || await RefuseAsync(spec, metrics, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return await Builder(spec, metrics).CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The paths of variant columns among the rows the scan keeps: none when the statistics refute it, its file's answer otherwise.</summary>
    public async ValueTask PathsAsync(ScanSpec spec, PathsQuestion question, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing || await RefuseAsync(spec, metrics, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The directories first; the columns they leave counted from the rows kept, read once.
        ScanBuilder builder = Builder(spec, metrics);
        if (await builder.TallyDirectoriesAsync(question, excluded: null, cancellationToken).ConfigureAwait(false) is { } left)
        {
            await PathQuestions.CountAsync(builder.ExecuteAsync(), question, left, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> AnyAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken) =>
        !spec.MatchesNothing
        && !await RefuseAsync(spec, metrics, cancellationToken).ConfigureAwait(false)
        && await Builder(spec, metrics).AnyAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask<FilterLiteral> ExtremeAsync(ScanSpec spec, FieldExpr column, bool min, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (spec.MatchesNothing || await RefuseAsync(spec, metrics, cancellationToken).ConfigureAwait(false))
        {
            return FilterLiteral.Null;
        }

        ScanBuilder builder = Builder(spec, metrics);
        return min
            ? await builder.MinAsync(column.Path, cancellationToken).ConfigureAwait(false)
            : await builder.MaxAsync(column.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The batches of a scan the file statistics refute: none, and its blocks counted pruned.</summary>
    private async IAsyncEnumerable<RecordBatch> RefusedAsync(
        ScanSpec spec, ScanCounters metrics, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await RefuseAsync(spec, metrics, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    /// <summary>
    /// Whether the file statistics refute the scan; when they do, its blocks are counted pruned, as
    /// its plan counts them, and nothing is read.
    /// </summary>
    private async ValueTask<bool> RefuseAsync(ScanSpec spec, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (!Refuted(spec))
        {
            return false;
        }

        metrics.AddBlocksPruned((await WholeAsync(spec, cancellationToken).ConfigureAwait(false)).Blocks);
        return true;
    }

    /// <summary>
    /// The plan of the scan without its filter: the blocks its rows touch, which the layout alone
    /// gives. It consults no structure, and so reads nothing.
    /// </summary>
    private async ValueTask<ScanPlan> WholeAsync(ScanSpec spec, CancellationToken cancellationToken) =>
        ScanPlan.From(await Builder(spec with { Filter = null, OrderPath = null }, new ScanCounters())
            .ExplainAsync(analyze: false, cancellationToken).ConfigureAwait(false));

    public async ValueTask<ScanPlan> ExplainAsync(ScanSpec spec, bool analyze, CancellationToken cancellationToken)
    {
        if (Refuted(spec) && !spec.MatchesNothing)
        {
            // The plan of a scan that reads nothing: the blocks its rows touch, every one of them
            // pruned by the file statistics.
            ScanPlan whole = await WholeAsync(spec, cancellationToken).ConfigureAwait(false);
            return whole with
            {
                MayMatch = false,
                LiveBlocks = 0,
                Segments = 0,
                BytesToRead = 0,
                Pruning = [new PruningStep("file statistics", whole.Blocks, 0, 0)],
                Count = new CountPlan(true, 0, 0, 0, 0),
            };
        }

        ScanExplanation plan = await Builder(spec, new ScanCounters()).ExplainAsync(analyze, cancellationToken).ConfigureAwait(false);
        ScanPlan result = ScanPlan.From(plan);

        // A filter known to match nothing runs no scan at all: it reads nothing and counts zero.
        return spec.MatchesNothing
            ? result with { MayMatch = false, LiveBlocks = 0, Segments = 0, BytesToRead = 0, Pruning = [], Count = new CountPlan(true, 0, 0, 0, 0) }
            : result;
    }

    /// <summary>
    /// Whether the file statistics prove the filter false for every row: they are in memory once
    /// the file is open, so a scan they rule out reads nothing, not even a zone map.
    /// </summary>
    private bool Refuted(ScanSpec spec) => spec.Filter is { } filter && !MayMatch(filter);

    public bool MayMatch(VortexExpr filter) => Compute.FileStatisticsPruner.MayMatch(_file, filter);

    /// <remarks>The file statistics are one entry per top-level column.</remarks>
    public bool TryBounds(int[] path, out FilterLiteral min, out FilterLiteral max)
    {
        min = FilterLiteral.Null;
        max = FilterLiteral.Null;
        if (!_file.HasFileStatistics || path.Length != 1 || !_file.Schema.RootIsStruct || path[0] >= _file.Statistics.Count)
        {
            return false;
        }

        FieldStatistics statistics = _file.Statistics[path[0]];
        return statistics.HasMin && statistics.HasMax
            && FileStatisticsPruner.TryLiteral(statistics.Min, out min) && FileStatisticsPruner.TryLiteral(statistics.Max, out max);
    }

    public long RowBound => _file.RowCount;

    /// <summary>A file read from an object store, a request a read; not one read from the local disk or memory.</summary>
    public bool ReadsRoundTrips => SessionReader.Unwrap(_file.Source) is IO.ObjectStoreSource;

    /// <summary>The segments the largest split of the scan asks for, over a source that copies them: the layout walked once, nothing read.</summary>
    public long ReadAheadBytes(ScanSpec spec) =>
        _file.Source.ReadsInPlace || spec.MatchesNothing ? 0 : Builder(spec, new ScanCounters()).LargestSplitBytes();

    public async ValueTask<IKeyWalker> OpenKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        await KeysOf(path, distinct, indexes).OpenAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask<KeyPlan> ExplainKeysAsync(string path, bool distinct, bool indexes, CancellationToken cancellationToken) =>
        KeysOf(path, distinct, indexes).ExplainAsync(cancellationToken);

    private KeyCursorBuilder KeysOf(string path, bool distinct, bool indexes)
    {
        KeyCursorBuilder builder = _file.Keys(path);
        if (distinct)
        {
            builder.Distinct();
        }

        return indexes ? builder : builder.WithSource(KeySourceKind.SortedColumn);
    }

    /// <summary>The engine's builder for <paramref name="spec"/>.</summary>
    internal ScanBuilder Builder(ScanSpec spec, ScanCounters metrics)
    {
        ScanBuilder builder = _file.ScanBuilder().WithMetrics(metrics).WithVariantKeys(spec.VariantKeys).WithZoneKeys(spec.ZoneKeys);
        if (spec.Projection is { } mask)
        {
            builder.ProjectMask(in mask);
        }

        if (spec.Filter is { } filter)
        {
            builder.Where(filter);
        }

        if (spec.Rows is { } rows)
        {
            builder.Rows(rows);
        }

        if (spec.Take is { } take)
        {
            builder.TakeHeld(take);
        }

        if (spec.OrderPath is { } order)
        {
            builder.InKeyOrder(order, spec.Descending);
        }
        else if (spec.Backward)
        {
            builder.InReverse();
        }

        ScanOptions options = spec.Options;
        if (options.BatchRows > 0)
        {
            builder.WithMaxBatchRows(options.BatchRows);
        }

        builder.WithPruning(options.UseStatistics).WithIndexes(options.UseIndexes);
        int degree = options.DegreeOfParallelism > 0 ? options.DegreeOfParallelism : Session.Options.MaxDegreeOfParallelism;
        builder.WithDegreeOfParallelism(Math.Max(degree, 1));
        builder.WithPrefetch(options.Prefetch).WithCompaction(options.Compact).WithEncodings(spec.KeepEncodings, spec.SinkDecodes);
        if (spec.ReadAhead > 0)
        {
            builder.WithReadAhead(spec.ReadAhead);
        }

        if (spec.Analysis is { } analysis)
        {
            builder.WithPruned(analysis.Live);
        }

        return builder;
    }
}
