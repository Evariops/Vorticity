// PHASE1-CONTRACTS.md §13.1. The fluent front door of the library.
//
// WHERE `Where` GOES, and why it sits where it does. docs/03-architecture.md §3.4 fixes the order
// of application as WHERE THEN PROJECT: the filter sees columns the projection does not. So
// ExecuteAsync compiles TWO masks - `keep`, what the caller projected, and `read`, that unioned
// with every field the filter references - plans the scan under `read`, and hands both to the
// enumerator, which trims `read` down to `keep` after the filter has run.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>Builds and launches one scan over an open <see cref="VortexFile"/>.</summary>
/// <remarks>
/// A builder is not thread-safe and is meant to be used and discarded. The
/// <see cref="IAsyncEnumerable{T}"/> it produces is independent of it: mutating the builder
/// afterwards does not change an enumeration already handed out.
/// </remarks>
public sealed class ScanBuilder
{
    private readonly VortexFile _file;
    private FieldMaskBuilder? _fields;
    private VortexExpr? _filter;
    private List<string>? _filterPaths;
    private bool _prune = true;
    private bool _indexes = true;
    private RowSelection? _take;
    private RowRange _rows;
    private bool _rowsSet;
    private int _maxBatchRows;
    private int _degree = 1;
    private ScanMetrics? _metrics;
    private TerminalTiers _tiers = TerminalTiers.All;
    private string? _orderPath;
    private string[]? _orderComposite;
    private bool _descending;

    internal ScanBuilder(VortexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>
    /// Adds <paramref name="paths"/> to the projection. Calling it twice unions the projections.
    /// </summary>
    /// <param name="paths">
    /// <c>.</c>-separated field names: <c>"id"</c>, <c>"payload.size"</c>.
    /// <b>A Vortex field name may itself contain a <c>.</c> or be empty</b> - corpus
    /// <c>types/struct_field_names</c> has fields named <c>"a.b"</c> and <c>""</c> - so this
    /// overload cannot address every column and no escaping syntax is invented for it. Use
    /// <see cref="ProjectFields(ReadOnlySpan{int})"/> with
    /// <see cref="StructColumn.GetField(int)"/> for those (§13.2).
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or an element is null.</exception>
    /// <exception cref="ArgumentException">A path does not resolve against the file's schema.</exception>
    public ScanBuilder Project(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Project(new ReadOnlySpan<string>(paths));
    }

    /// <inheritdoc cref="Project(string[])"/>
    public ScanBuilder Project(ReadOnlySpan<string> paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            Projection.IncludePath(_file.Schema, paths[i], Fields(), nameof(paths));
        }

        return this;
    }

    /// <summary>Adds root-level field indices to the projection, for callers that resolved names themselves.</summary>
    /// <param name="fieldIndices">0-based indices into the root struct's fields.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The file's root dtype is not a struct.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the root struct.</exception>
    public ScanBuilder ProjectFields(ReadOnlySpan<int> fieldIndices)
    {
        if (fieldIndices.Length == 0)
        {
            return this;
        }

        DType schema = _file.Schema;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            ScanThrow.NonStructRoot(nameof(fieldIndices));
        }

        int fieldCount = schema.FieldCount;
        FieldMaskBuilder builder = Fields();
        for (int i = 0; i < fieldIndices.Length; i++)
        {
            int field = fieldIndices[i];
            if ((uint)field >= (uint)fieldCount)
            {
                ScanThrow.FieldIndexOutOfRange(field, fieldCount, nameof(fieldIndices));
            }

            builder.IncludeField(field);
        }

        return this;
    }

    /// <summary>Restricts the scan to <paramref name="range"/>.</summary>
    /// <param name="range">The wanted rows, in file coordinates.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The range is <b>intersected</b> with the file's rows rather than rejected: a caller asking
    /// for <c>RowRange.FromLength(0, 1_000_000)</c> of a 4096-row file gets 4096 rows, and one
    /// asking wholly past the end gets no batches. <see cref="RowRange"/>'s own constructor already
    /// rejects a negative or inverted range.
    /// </remarks>
    public ScanBuilder Rows(RowRange range)
    {
        if (_take is not null)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range or by index list, not both.");
        }

        ThrowIfOrdered(nameof(Rows));

        _rows = range;
        _rowsSet = true;
        return this;
    }

    /// <summary>Caps the batch size, so a memory-constrained consumer is not at the writer's mercy.</summary>
    /// <param name="maxRows">The largest batch this scan may produce. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <b>It caps; it does not set.</b> The natural batch size is the file's zone length, or
    /// <c>8192</c> when it has no zone map, and a cap above that changes nothing (§13 traps).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRows"/> is not positive.</exception>
    public ScanBuilder WithMaxBatchRows(int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        _maxBatchRows = maxRows;
        return this;
    }

    /// <summary>
    /// Keeps only the rows <paramref name="filter"/> evaluates to <c>true</c>.
    /// </summary>
    /// <param name="filter">The predicate; build it with <see cref="Expr"/>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>The filter runs before the projection</b> (docs/03-architecture.md §3.4): a column the
    /// filter names is read even when the caller did not project it, and is dropped from the batch
    /// afterwards. Calling this twice replaces the filter rather than combining the two - use
    /// <see cref="Expr.And"/>, which says what it means.
    /// </para>
    /// <para>
    /// Three-valued logic, SQL-style: a row is kept only when the predicate is <c>true</c>, so a
    /// row whose compared column is null is dropped by <c>x = 1</c> AND by <c>x != 1</c> alike
    /// (docs/08-semantics.md §3).
    /// </para>
    /// <para>
    /// A batch whose rows are all rejected is not produced at all; the scan moves to the next
    /// split. So a filtered scan yields fewer batches than an unfiltered one, not empty ones.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The filter references a field the file's schema does not have.
    /// </exception>
    public ScanBuilder Where(VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        List<string> paths = [];
        filter.CollectFields(paths);

        // Resolved here rather than per batch, so a typo in a path is an error at build time with
        // the schema in hand, not an exception from inside an enumeration.
        FieldMaskBuilder probe = new FieldMaskBuilder();
        for (int i = 0; i < paths.Count; i++)
        {
            Projection.IncludePath(_file.Schema, paths[i], probe, nameof(filter));
        }

        _filter = filter;
        _filterPaths = paths;
        return this;
    }

    /// <summary>
    /// Returns only the rows at <paramref name="rowIndices"/>.
    /// </summary>
    /// <param name="rowIndices">
    /// File row indices, in any order. Duplicates are collapsed and the order is not preserved:
    /// batches come out in file order, and reordering rows to match an arbitrary list would mean
    /// buffering the whole result.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// This is F5, Vortex's headline claim over Parquet: the splits the list never touches are
    /// skipped before a single segment is registered, so a thousand scattered rows out of a billion
    /// read the splits those rows live in and nothing else.
    /// </para>
    /// <para>
    /// Mutually exclusive with <see cref="Rows(RowRange)"/>, which selects a contiguous range;
    /// composes with <see cref="Where(VortexExpr)"/>, which is applied to the taken rows.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A row is negative or beyond the file.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Rows(RowRange)"/> was already set.</exception>
    public ScanBuilder Take(ReadOnlySpan<long> rowIndices)
    {
        if (_rowsSet)
        {
            throw new InvalidOperationException(
                "A scan selects rows by range or by index list, not both.");
        }

        ThrowIfOrdered(nameof(Take));
        _take = RowSelection.Create(rowIndices, _file.RowCount);
        return this;
    }

    /// <summary>
    /// Delivers the rows in the key order of <paramref name="path"/> instead of file order
    /// (docs/12-index-reads.md §6).
    /// </summary>
    /// <param name="path">The key column, <c>.</c>-separated for a nested field.</param>
    /// <param name="descending">Whether the order is reversed, ties included.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// The scan is driven by the column's key source -- the column itself when the file
    /// statistics say it is sorted, its <c>sorted.runs</c> index otherwise -- one window of
    /// <see cref="WithMaxBatchRows"/> entries at a time: each batch is in key order, consecutive
    /// batches are, and equal keys come in row order. The filter's conjuncts on the key column
    /// narrow the walk; the others prune as they always do. A source-less column is refused at the
    /// first <c>MoveNextAsync</c>, with a <see cref="VortexUnsupportedException"/> that names the
    /// policy that would have served.
    /// </para>
    /// <para>
    /// <b>A row whose key is null comes last, in both directions</b>: after every keyed row, in row
    /// order ascending and in reverse row order descending. No source holds it, so the scan reads
    /// those rows in file order once the walk is done, under the same filter. A merge of files in
    /// key order (docs/13-dataset.md §6.6) encodes its keys with nulls last in both directions to
    /// match, so a key-ordered file and a key-ordered merge of files agree.
    /// </para>
    /// <para>
    /// <b>What it costs</b> is the key's correlation with file order: a window of a sorted column
    /// is one contiguous read, while a window of runs over an uncorrelated column can touch a
    /// split per row. <see cref="ScanMetrics.WindowSplits"/> reports which. It is the tool for a
    /// selective range or a top-k, not for ordering a whole uncorrelated column.
    /// </para>
    /// <para>
    /// Mutually exclusive with <see cref="Rows(RowRange)"/> and <see cref="Take"/>.
    /// <see cref="WithDegreeOfParallelism"/> applies within a window; windows are sequential.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not resolve against the file's schema.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Rows(RowRange)"/> or <see cref="Take"/> was already set.</exception>
    public ScanBuilder InKeyOrder(string path, bool descending = false)
    {
        Resolved(path);
        if (_rowsSet || _take is not null)
        {
            throw new InvalidOperationException(
                "A key-ordered scan walks its key source; it cannot also select rows by range or by index list.");
        }

        _orderPath = path;
        _orderComposite = null;
        _descending = descending;
        return this;
    }

    /// <summary>
    /// Delivers the rows in the order of a composite key, the tuple of <paramref name="paths"/>
    /// (docs/12-index-reads.md §4.6, §6).
    /// </summary>
    /// <param name="paths">The key's columns, in key order; one path is <see cref="InKeyOrder(string, bool)"/>.</param>
    /// <param name="descending">Whether the order is reversed, ties included.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Driven by the composite key's <c>sorted.runs</c> entry (<c>WritePolicy.ForKey</c>), whose keys
    /// are the row encoding of the tuple and whose order is bytewise: exactly the order a merge across
    /// files compares. The filter prunes and filters as for one column, but does not narrow the walk,
    /// since its conjuncts name columns and the walk is over tuples. <b>A row whose tuple holds a null
    /// is in no entry and is not delivered</b>: the row encoding sorts it inside its leading column's
    /// group, not after every keyed row, so it cannot come last the way a single column's null does
    /// without sorting every such row in memory.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty, or a path does not resolve.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Rows(RowRange)"/> or <see cref="Take"/> was already set.</exception>
    public ScanBuilder InKeyOrder(IReadOnlyList<string> paths, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("A key has at least one column.", nameof(paths));
        }

        if (paths.Count == 1)
        {
            return InKeyOrder(paths[0], descending);
        }

        foreach (string path in paths)
        {
            Resolved(path);
        }

        InKeyOrder(paths[0], descending);
        _orderComposite = [.. paths];
        return this;
    }

    private void ThrowIfOrdered(string method)
    {
        if (_orderPath is not null)
        {
            throw new InvalidOperationException(
                $"A key-ordered scan walks its key source; {method} cannot also select its rows.");
        }
    }

    /// <summary>
    /// Turns zone-map pruning on or off. On by default.
    /// </summary>
    /// <param name="enabled">Whether the scan may skip splits its zone maps rule out.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Pruning never changes which ROWS a scan returns -- docs/08-semantics.md §1 makes "pruning may
    /// never eliminate a row that full materialization would have returned" the invariant the whole
    /// feature rests on. Turning it off is therefore a diagnostic, not a semantic: it is how the
    /// property test compares a pruned scan against an unpruned one, and how a caller who suspects
    /// a file's statistics can check.
    /// </remarks>
    public ScanBuilder WithPruning(bool enabled)
    {
        _prune = enabled;
        return this;
    }

    /// <summary>
    /// Turns the file's index directory on or off, leaving the zone maps alone. On by default.
    /// </summary>
    /// <param name="enabled">Whether the scan may consult the file's indexes.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The same diagnostic as <see cref="WithPruning"/>, one layer down: an index is a hint and
    /// never changes which rows a scan returns (docs/10-indexes.md §6.6), so a scan with indexes
    /// on and the same scan with them off are the equivalence test every index kind is held to.
    /// </remarks>
    public ScanBuilder WithIndexes(bool enabled)
    {
        _indexes = enabled;
        return this;
    }

    /// <summary>Opts in to decoding independent splits concurrently.</summary>
    /// <param name="degree">How many splits may be in flight at once. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// Default <c>1</c>: a library must not appropriate the host's thread pool
    /// (docs/09-contracts.md §2). Each concurrent split gets its <b>own</b>
    /// <see cref="Vorticity.Arrays.ScanContext"/> with its own arenas (contract §2.2); nothing is
    /// shared. Batches are still delivered in row order.
    /// </para>
    /// <para>
    /// I/O concurrency is separate and always on: one <c>ReadManyAsync</c> per batch issues
    /// overlapping reads whatever this is set to. A degree above 1 allocates a task per split,
    /// which the degree-1 path does not.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degree"/> is not positive.</exception>
    public ScanBuilder WithDegreeOfParallelism(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(degree);
        _degree = degree;
        return this;
    }

    /// <summary>Compiles the scan and returns its batches.</summary>
    /// <returns>
    /// A re-enumerable sequence: every <c>GetAsyncEnumerator</c> call starts a fresh scan with its
    /// own <see cref="Vorticity.Arrays.ScanContext"/>.
    /// </returns>
    /// <remarks>
    /// The layout tree is parsed here, once, and shared by every enumerator this call produces: it
    /// is immutable after parsing and therefore safe for concurrent scans (docs/09-contracts.md §1).
    /// </remarks>
    /// <exception cref="VortexFormatException">The file's layout tree is malformed.</exception>
    public IAsyncEnumerable<RecordBatch> ExecuteAsync()
    {
        // Parsed at most once per OPEN FILE rather than once per scan: the tree is a function of
        // the file's bytes and nothing else. See VortexFile.LayoutTree.
        LayoutTree tree = _file.LayoutTree;
        (RowRange rows, _, long cap) = Frame(tree);

        Projection keep = _fields is null ? Projection.All : Projection.Create(_fields.Build());

        // WHERE THEN PROJECT: the scan reads the union so the filter has its columns, and the
        // enumerator trims back down to `keep` once the filter has decided.
        Projection read = _filter is null ? keep : Union(keep, _filterPaths!);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap);

        BatchAsyncEnumerable batches = new BatchAsyncEnumerable(
            _file, tree, read, keep, plan, _degree, _filter, _take, _metrics);

        if (_orderPath is not null)
        {
            IAsyncEnumerable<RecordBatch>? nulls = _orderComposite is null && MayBeNull(_orderPath)
                ? NullKeysAsync(tree, rows, keep, cap)
                : null;
            return new KeyOrderedBatches(
                batches, _filter, _orderPath, _orderComposite, _descending, _prune, _indexes, (int)cap, nulls);
        }

        // Only a filtered scan pays for the skip-empty wrapper; an unfiltered one is the same
        // object graph it has always been, which is what keeps the per-batch allocation figure
        // the allocation tests pin unchanged.
        // The wrapper exists for the filter's own two jobs -- prune before reading, skip emptied
        // batches after -- and a take needs the second of them too: a split whose wanted rows are
        // all it holds still produces a batch, but one gathered down to nothing must not.
        return _filter is null && _take is null
            ? batches
            : new FilteredBatches(batches, _filter, _prune, _indexes, rows);
    }

    /// <summary>Hands the scan a sink it adds its counters to as it runs (docs/11 §6.4).</summary>
    /// <param name="metrics">The caller's sink; a fresh one per fresh count.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Every enumerator started from this builder adds to the same object, so a scan run twice
    /// reports the sum. What it counts is what <see cref="ExplainAsync"/> planned: the segments
    /// and bytes asked of the source, the values the flat reader materialized, the batches and
    /// rows produced -- the same quantities, measured.
    /// </remarks>
    public ScanBuilder WithMetrics(ScanMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
        return this;
    }

    /// <summary>
    /// The plan of this scan without executing it (docs/11 §6.4): splits and blocks, what each
    /// structure prunes, the segments and bytes the live splits would read, against the file.
    /// </summary>
    /// <param name="cancellationToken">Cancels the one read this makes.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// THE SAME PLANNING THE SCAN DOES BEFORE ITS FIRST BATCH, stopped before any data segment
    /// is read: the layout tree, the split plan under the read projection, the mask of live blocks
    /// refined by every structure the file carries (the zone maps are read for that, one segment
    /// per filtered column, as the scan itself reads them), then a walk of the splits the cursor
    /// would produce, registering the live ones into one request set -- which is how the segments
    /// come out distinct and their bytes summed once. Nothing is decoded. Whether an index earns
    /// its bytes is answered by this and <see cref="WithMetrics"/> together: what it would cost,
    /// and what it did.
    /// </remarks>
    public async System.Threading.Tasks.ValueTask<ScanPlan> ExplainAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        LayoutTree tree = _file.LayoutTree;
        (RowRange rows, long natural, long cap) = Frame(tree);
        long rootRows = tree.Root.RowCount;

        Projection keep = _fields is null ? Projection.All : Projection.Create(_fields.Build());
        Projection read = _filter is null ? keep : Union(keep, _filterPaths!);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap);

        List<PruningStep> steps = [];
        Compute.ZonePruningPlan.PruningPlan pruning = _filter is not null && _prune
            ? await Compute.ZonePruningPlan
                .PlanAsync(_file, tree, _filter, cancellationToken, steps, metrics: null, _indexes)
                .ConfigureAwait(false)
            : default;
        Compute.BlockMask? live = pruning.Live;
        Compute.ZonePruner? zones = (_tiers & TerminalTiers.FullBlock) != 0 ? pruning.Zones : null;
        int splitsPruned = 0;
        int splitsProven = 0;

        long blockRows = live?.BlockRows ?? natural;
        int blocks = live?.BlockCount ?? checked((int)((rootRows + blockRows - 1) / blockRows));
        int liveBlocks = live?.LiveCount ?? blocks;

        int splits = 0;
        int liveSplits = 0;
        IO.SegmentRequestSet segments = new IO.SegmentRequestSet();
        try
        {
            LayoutNode root = tree.Root;
            LayoutReader reader = LayoutReaderTable.Require(in root);
            FieldMask mask = read.RootMask;
            SplitCursor cursor = plan.CreateCursor();
            while (cursor.TryNext(out RowRange split))
            {
                splits++;
                if (_take is not null && !_take.Touches(split))
                {
                    continue;
                }

                if (live is not null && !live.AnyLive(split))
                {
                    splitsPruned++;
                    continue;
                }

                liveSplits++;
                reader.RegisterSegments(in root, split, in mask, segments);
                if (zones is not null && Proves(zones, split))
                {
                    splitsProven++;
                }
            }

            // The data segments of the live splits, plus what consulting each structure cost:
            // the same asking the metrics count, so that plan and measurement are one quantity.
            int toRead = segments.Count;
            long bytes = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                bytes += segments.GetSpec(i).Length;
            }

            for (int i = 0; i < steps.Count; i++)
            {
                toRead += steps[i].SegmentsRead;
                bytes += steps[i].BytesRead;
            }

            // The whole file-level answer: the statistics, then the file filters when indexes are on.
            bool fileMayMatch = _filter is null
                || (_indexes
                    ? await _file.MayMatchAsync(_filter, cancellationToken).ConfigureAwait(false)
                    : Compute.FileStatisticsPruner.MayMatch(_file, _filter));
            // The exact cover (docs/12 §5.2's first tier, 10 §6.6's row selection): a scan whose
            // cover holds a batch or fewer reads those rows and evaluates nothing.
            CountPlan? count = null;
            long selected = 0;
            if (_filter is not null)
            {
                (bool exact, long covered) = await ExactAsync(cancellationToken).ConfigureAwait(false);
                if (exact && !_rowsSet && _take is null && covered <= natural)
                {
                    selected = covered;
                }

                count = new CountPlan(exact, covered, splitsPruned, splitsProven, liveSplits - splitsProven);
            }

            OrderPlan? order = _orderPath is null ? null : await OrderAsync(cancellationToken).ConfigureAwait(false);
            return new ScanPlan(
                rows.Length, blockRows, blocks, liveBlocks, steps, splits, liveSplits,
                selected, toRead, bytes, _file.FileLength, fileMayMatch)
            {
                Count = count,
                Order = order,
            };
        }
        finally
        {
            segments.Dispose();
        }
    }

    /// <summary>What <c>TerminalScan</c>'s full-block proof would decide of a split.</summary>
    private bool Proves(Compute.ZonePruner zones, RowRange split)
    {
        if (_take is null)
        {
            return zones.TryCount(split, out _);
        }

        Compute.RangeVerdict verdict = zones.Verdict(split);
        return verdict.IsAllTrue || verdict.IsNoneTrue;
    }

    /// <summary>Whether an exact source covers the filter, and its count.</summary>
    private async System.Threading.Tasks.ValueTask<(bool Exact, long Count)> ExactAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        if ((_tiers & TerminalTiers.ExactCover) == 0 || !_prune || _filter is null)
        {
            return (false, 0);
        }

        Keys.ExactCover? cover = await Keys.ExactCover
            .TryCreateAsync(_file, _filter, _indexes, cancellationToken)
            .ConfigureAwait(false);
        if (cover is null)
        {
            return (false, 0);
        }

        try
        {
            return (true, cover.Count);
        }
        finally
        {
            await cover.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The key source <c>InKeyOrder</c> would walk, and the range it would walk; §6.</summary>
    private async System.Threading.Tasks.ValueTask<OrderPlan> OrderAsync(System.Threading.CancellationToken cancellationToken)
    {
        Keys.KeySource? source;
        Keys.KeySourceKind kind;
        string named = _orderComposite is null ? _orderPath! : "(" + string.Join(", ", _orderComposite) + ")";
        if (_orderComposite is not null)
        {
            (source, _) = _indexes
                ? await Keys.SortedRunsSource.OpenCompositeAsync(_file, _orderComposite, cancellationToken).ConfigureAwait(false)
                : (null, null);
            kind = source is null ? Keys.KeySourceKind.None : Keys.KeySourceKind.SortedRuns;
        }
        else
        {
            (source, kind) = await Keys.KeyCursorBuilder
                .OpenSourceAsync(_file, _orderPath!, _indexes, cancellationToken)
                .ConfigureAwait(false);
        }

        if (source is null)
        {
            return new OrderPlan(named, Keys.KeySourceKind.None, 0, 0, null, 0, _descending);
        }

        try
        {
            // A composite walk is not narrowed by the filter (InKeyOrder(paths)).
            List<(long Low, long High)> slices = await Keys.ExactCover
                .RangeAsync(_orderComposite is null ? _filter : null, source, _orderPath!, cancellationToken)
                .ConfigureAwait(false);
            long entries = 0;
            foreach ((long low, long high) in slices)
            {
                entries += high - low;
            }

            int runsInRange = await source.RunsOverlappingAsync(slices, cancellationToken).ConfigureAwait(false);
            return new OrderPlan(named, kind, source.Runs, runsInRange, source.EntryCount, entries, _descending);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How many rows the scan would return, exactly, without returning them
    /// (docs/12-index-reads.md §5.2).
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    /// The count is pushed into the structures, split by split, cheapest proof first: a split the
    /// zone maps rule out counts nothing and reads nothing; one they decide whole counts from the
    /// bounds already in memory; the rest are decoded and the filter evaluated, with no batch
    /// built, no rows gathered, and one split of memory at a time. <see cref="Where"/>,
    /// <see cref="Rows"/>, <see cref="Take"/> and <see cref="WithPruning"/> are honoured exactly
    /// as <see cref="ExecuteAsync"/> honours them: a terminal is the scan with a different output,
    /// never a different scan. Without a filter it is arithmetic and reads nothing.
    /// </remarks>
    public System.Threading.Tasks.ValueTask<long> CountAsync(
        System.Threading.CancellationToken cancellationToken = default) =>
        Terminal().CountAsync(cancellationToken);

    /// <summary>
    /// Whether the scan would return at least one row (docs/12-index-reads.md §5.1).
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether some row matches.</returns>
    /// <remarks>
    /// <see cref="CountAsync"/> stopped at the first split that counts: an empty mask answers
    /// without a read, a split the zone maps prove answers from memory, and the bad case reads
    /// every live split once and materializes none. This is the membership probe of docs/12 §1,
    /// exact where <see cref="VortexFilePruningExtensions.MayMatch"/> is a superset.
    /// </remarks>
    public System.Threading.Tasks.ValueTask<bool> AnyAsync(
        System.Threading.CancellationToken cancellationToken = default) =>
        Terminal().AnyAsync(cancellationToken);

    /// <summary>
    /// The smallest non-null value of <paramref name="path"/> among the rows the scan would
    /// return, in the filter's order (docs/12-index-reads.md §5.3); <see cref="FilterLiteral.Null"/>
    /// when there is none.
    /// </summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The minimum.</returns>
    /// <remarks>
    /// Cheapest resolution first: the file's own statistic when the scan is the whole file and
    /// the statistic is <c>Exact</c> (no read); the zone map's bounds for every split they decide
    /// whole, an <c>Inexact</c> bound being a candidate decoded only when it could still win;
    /// the decode of what is left, with a running extreme and one split of memory at a time. A
    /// NaN is never the minimum nor the maximum, as the statistics have it (docs/08 §2).
    /// <see cref="Where"/>, <see cref="Rows"/>, <see cref="Take"/> and <see cref="WithPruning"/>
    /// are honoured exactly as <see cref="ExecuteAsync"/> honours them.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not resolve against the file's schema.</exception>
    /// <exception cref="NotSupportedException">The column's type is outside the 1.0 filter scope.</exception>
    public System.Threading.Tasks.ValueTask<FilterLiteral> MinAsync(
        string path, System.Threading.CancellationToken cancellationToken = default) =>
        Terminal(Resolved(path)).ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>
    /// The largest non-null value of <paramref name="path"/> among the rows the scan would
    /// return, in the filter's order (docs/12-index-reads.md §5.3); <see cref="FilterLiteral.Null"/>
    /// when there is none.
    /// </summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The maximum.</returns>
    /// <remarks>See <see cref="MinAsync"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not resolve against the file's schema.</exception>
    /// <exception cref="NotSupportedException">The column's type is outside the 1.0 filter scope.</exception>
    public System.Threading.Tasks.ValueTask<FilterLiteral> MaxAsync(
        string path, System.Threading.CancellationToken cancellationToken = default) =>
        Terminal(Resolved(path)).ExtremeAsync(path, wantMin: false, cancellationToken);

    /// <summary>Forces terminal tiers off, for the tests that hold every tier to the decode.</summary>
    /// <param name="tiers">The tiers a terminal may take; <see cref="TerminalTiers.Decode"/> is always taken.</param>
    /// <returns>This builder.</returns>
    internal ScanBuilder WithTiers(TerminalTiers tiers)
    {
        _tiers = tiers;
        return this;
    }

    /// <summary>A path checked against the schema now, so a typo is an error with the schema in hand.</summary>
    private string Resolved(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Projection.IncludePath(_file.Schema, path, new FieldMaskBuilder(), nameof(path));
        return path;
    }

    /// <summary>
    /// The rows the scan covers and the batch size it runs at -- the same frame for the scan,
    /// its plan and its terminals.
    /// </summary>
    /// <param name="tree">The parsed layout tree.</param>
    /// <returns>The row range, the file's natural batch size, and the cap the scan runs at.</returns>
    /// <remarks>
    /// A take narrows the planned range to the span its indices cover, which already skips every
    /// split outside it; the ones inside it that hold no wanted row are skipped per split.
    /// </remarks>
    private (RowRange Rows, long Natural, long Cap) Frame(LayoutTree tree)
    {
        RowRange whole = new RowRange(0, tree.Root.RowCount);
        RowRange rows = _rowsSet ? _rows.Intersect(whole) : whole;
        if (_take is not null)
        {
            rows = _take.Bounds.Intersect(whole);
        }

        long natural = SplitPlan.NaturalBatchRows(tree);
        if (natural > int.MaxValue)
        {
            natural = int.MaxValue;
        }

        long cap = _maxBatchRows > 0 && _maxBatchRows < natural ? _maxBatchRows : natural;
        return (rows, natural, cap);
    }

    /// <summary>
    /// The terminal form of this scan: it reads the filter's columns, and the one an extreme is
    /// asked of, and nothing else, since it has no batch to fill.
    /// </summary>
    /// <param name="extraPath">The column a <c>Min</c> or <c>Max</c> reads, or null for a count.</param>
    private TerminalScan Terminal(string? extraPath = null)
    {
        LayoutTree tree = _file.LayoutTree;
        (RowRange rows, _, long cap) = Frame(tree);
        Projection read = _filter is null && extraPath is null
            ? Projection.All
            : Only(_filterPaths, extraPath);
        bool wholeFile = !_rowsSet && _take is null;
        return new TerminalScan(
            _file, tree, _filter, rows, wholeFile, cap, read, _take, _prune, _tiers, _metrics, _indexes);
    }

    /// <summary>The projection of exactly the fields a filter reads, plus one.</summary>
    private Projection Only(List<string>? filterPaths, string? extraPath)
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        if (filterPaths is not null)
        {
            for (int i = 0; i < filterPaths.Count; i++)
            {
                Projection.IncludePath(_file.Schema, filterPaths[i], builder, "filter");
            }
        }

        if (extraPath is not null)
        {
            Projection.IncludePath(_file.Schema, extraPath, builder, nameof(extraPath));
        }

        return Projection.Create(builder.Build());
    }

    /// <summary>Whether the key column, or a struct above it, is nullable: whether rows can have no key.</summary>
    private bool MayBeNull(string path)
    {
        DType current = _file.Schema;
        foreach (string segment in path.Split('.'))
        {
            int field = current.IndexOfField(System.Text.Encoding.UTF8.GetBytes(segment));
            if (field < 0)
            {
                // A field whose name holds a dot: the projection resolved it, the walk here cannot,
                // and assuming it nullable costs one pruned scan at most.
                return true;
            }

            current = current.GetField(field);
            if (current.IsNullable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The rows a key-ordered scan delivers after its walk: those whose key is null, which no source
    /// holds, in file order -- reversed, split by split, when the scan is descending.
    /// </summary>
    /// <remarks>
    /// A filtered scan under <c>IsNull(key) AND filter</c>, so the zone maps' null counts prune every
    /// block that holds no null key before a byte of it is read. A split is at most one batch, so the
    /// descending tail reverses one batch at a time and memory stays one batch.
    /// </remarks>
    private IAsyncEnumerable<RecordBatch> NullKeysAsync(LayoutTree tree, RowRange rows, Projection keep, long cap)
    {
        VortexExpr isNull = Expr.IsNull(Expr.Field(_orderPath!));
        VortexExpr filter = _filter is null ? isNull : Expr.And(isNull, _filter);
        Projection read = Union(keep, [.. _filterPaths ?? [], _orderPath!]);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap);
        return _descending
            ? ReversedAsync(tree, rows, read, keep, plan, filter, cap)
            : new FilteredBatches(
                new BatchAsyncEnumerable(_file, tree, read, keep, plan, _degree, filter, null, _metrics),
                filter, _prune, _indexes, rows);
    }

    /// <summary>The splits of <paramref name="plan"/> last first, each batch's rows reversed.</summary>
    private async IAsyncEnumerable<RecordBatch> ReversedAsync(
        LayoutTree tree, RowRange rows, Projection read, Projection keep, SplitPlan plan, VortexExpr filter, long cap)
    {
        // One pipeline for the whole walk, reading the plan's splits last one first. Building one
        // per split cost a plan, an enumerable and a filter for every batch -- a split is a batch,
        // since both are cut to the same row cap -- which is 40 kB a batch against the 80 of the
        // RecordBatch every other scan is held to.
        IAsyncEnumerable<RecordBatch> scan = new FilteredBatches(
            new BatchAsyncEnumerable(_file, tree, read, keep, plan, 1, filter, null, _metrics, reverse: true),
            filter, _prune, _indexes, rows);

        // The permutation of a batch of n rows is n-1 … 0 and depends on nothing else, so it is
        // shared by every batch of the same length. Kept across the walk and refilled only when the
        // length changes, it costs one array per scan instead of one per batch.
        int[] order = [];
        int ordered = 0;

        await foreach (RecordBatch batch in scan.ConfigureAwait(false))
        {
            if (batch.RowCount > order.Length)
            {
                order = new int[batch.RowCount];
                ordered = 0;
            }

            if (ordered != batch.RowCount)
            {
                for (int row = 0; row < batch.RowCount; row++)
                {
                    order[row] = batch.RowCount - 1 - row;
                }

                ordered = batch.RowCount;
            }

            // In the batch's own arena: the scan owns it, and disposes it at its next batch.
            int root = CanonicalFilter.Apply(batch.Arena, batch.RootIndex, order.AsSpan(0, batch.RowCount));
            yield return new RecordBatch(batch.Arena, root, batch.StartRow);
        }
    }

    /// <summary>The projection widened by every field a filter reads.</summary>
    private Projection Union(Projection keep, List<string> filterPaths)
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        builder.Include(keep.RootMask);
        for (int i = 0; i < filterPaths.Count; i++)
        {
            Projection.IncludePath(_file.Schema, filterPaths[i], builder, "filter");
        }

        return Projection.Create(builder.Build());
    }

    private FieldMaskBuilder Fields() => _fields ??= new FieldMaskBuilder();
}

/// <summary>The scan entry point.</summary>
public static class VortexFileScanExtensions
{
    /// <summary>Starts building a scan over <paramref name="file"/>.</summary>
    /// <param name="file">An open file.</param>
    /// <returns>A fresh builder.</returns>
    /// <remarks>
    /// An extension rather than a method on <see cref="VortexFile"/> so that file-open does not
    /// depend on scan (contract §13.1).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static ScanBuilder Scan(this VortexFile file) => new ScanBuilder(file);
}
