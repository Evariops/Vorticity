using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scanning;

/// <summary>Builds and launches one scan over an open <see cref="VortexFile"/>.</summary>
/// <remarks>
/// <para>
/// A builder is not thread-safe and is meant to be used and discarded. The
/// <see cref="IAsyncEnumerable{T}"/> it produces is independent of it: mutating the builder
/// afterwards does not change an enumeration already handed out.
/// </para>
/// <para>
/// The filter is applied before the projection, so it sees columns the projection does not.
/// Compiling a scan therefore produces two masks -- what the caller projected, and that unioned
/// with every field the filter references -- plans the scan under the wider one, and lets the
/// enumerator trim back down once the filter has run.
/// </para>
/// </remarks>
internal sealed class ScanBuilder
{
    private static int s_defaultDegree = 1;

    private readonly VortexFile _file;
    private FieldMaskBuilder? _fields;
    private VortexExpr? _filter;
    private List<FieldExpr>? _filterPaths;
    private bool _prune = true;
    private bool _indexes = true;
    private RowSelection? _take;
    private RowRange _rows;
    private bool _rowsSet;
    private int _maxBatchRows;
    private int _windowRows = FlatLayoutReader.WindowRows;
    private int _degree = Volatile.Read(ref s_defaultDegree);
    private ScanMetrics? _metrics;
    private TerminalTiers _tiers = TerminalTiers.All;
    private string? _orderPath;
    private string[]? _orderComposite;
    private bool _descending;
    private int _prefetch;
    private bool _compact = true;
    private bool _reverse;

    /// <summary>
    /// Reads the splits last one first, each still in file order, on one lane: what a caller that
    /// wants the rows in the reverse of file order reverses a batch at a time.
    /// </summary>
    internal ScanBuilder InReverse()
    {
        _reverse = true;
        return this;
    }

    /// <summary>Decodes <paramref name="batches"/> ahead of the consumer, so the decode overlaps the caller's work.</summary>
    internal ScanBuilder WithPrefetch(int batches)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batches);
        _prefetch = batches;
        return this;
    }

    /// <summary>Whether a filtered batch is compacted; false delivers whole blocks with the passing rows marked.</summary>
    internal ScanBuilder WithCompaction(bool compact)
    {
        _compact = compact;
        return this;
    }

    private bool _keepEncodings;
    private bool _sinkDecodes;

    /// <summary>Whether the decoders may deliver dictionary and run-end columns encoded.</summary>
    internal bool KeepEncodings => _keepEncodings;

    /// <summary>Lets the decoders deliver dictionary and run-end columns in their encoded form.</summary>
    /// <param name="keep">Whether they may.</param>
    /// <param name="sinkDecodes">Whether the consumer reads the encoded forms itself, so that only the blocks it decodes count as decoded.</param>
    internal ScanBuilder WithEncodings(bool keep, bool sinkDecodes = false)
    {
        _keepEncodings = keep;
        _sinkDecodes = sinkDecodes;
        return this;
    }

    private bool _pruned;
    private BlockMask? _live;

    /// <summary>Hands the scan the filter's mask of live blocks, refined already, so that it reads no structure to refine it again.</summary>
    /// <param name="live">The mask, or null when no structure prunes anything.</param>
    internal ScanBuilder WithPruned(BlockMask? live)
    {
        _pruned = true;
        _live = live;
        return this;
    }

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
    /// <b>A Vortex field name may itself contain a <c>.</c> or be empty</b> -- names such as
    /// <c>"a.b"</c> and <c>""</c> are legal -- so this overload cannot address every column and no
    /// escaping syntax is invented for it. Use
    /// <see cref="ProjectFields(ReadOnlySpan{int})"/> with
    /// <see cref="StructColumn.GetField(int)"/> for those.
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
            ScanProjection.IncludePath(_file.DType, paths[i], Fields(), nameof(paths));
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

        DType schema = _file.DType;
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
    /// <b>It caps; it does not set.</b> A scan with a filter, a take or an order is batched by the
    /// file's zone length, or <c>8192</c> when it has no zone map. Any other scan is batched by the
    /// window: as many zones as fit in the rows of a window, and never past the end of a chunk. A
    /// cap above that changes nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRows"/> is not positive.</exception>
    public ScanBuilder WithMaxBatchRows(int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        _maxBatchRows = maxRows;
        return this;
    }

    /// <summary>
    /// Sets the most rows a window of a chunk holds, <see cref="FlatLayoutReader.WindowRows"/> by
    /// default: a chunk larger than a window is decoded a window of whole batches at a time.
    /// </summary>
    /// <param name="rows">The most rows a window holds.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// A window changes what the scan holds decoded at once and never what it returns, so a small
    /// one is how a file of small chunks reaches the range decode of every encoding it carries.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is not positive.</exception>
    internal ScanBuilder WithWindowRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _windowRows = rows;
        return this;
    }

    /// <summary>
    /// Keeps only the rows <paramref name="filter"/> evaluates to <c>true</c>.
    /// </summary>
    /// <param name="filter">The predicate; build it with <see cref="Expr"/>.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>The filter runs before the projection</b>: a column the
    /// filter names is read even when the caller did not project it, and is dropped from the batch
    /// afterwards. Calling this twice replaces the filter rather than combining the two - use
    /// <see cref="Expr.And"/>, which says what it means.
    /// </para>
    /// <para>
    /// Three-valued logic: a row is kept only when the predicate is <c>true</c>, so a
    /// row whose compared column is null is dropped by <c>x = 1</c> and by <c>x != 1</c> alike.
    /// </para>
    /// <para>
    /// A batch whose rows are all rejected is not produced at all; the scan moves to the next
    /// split. So a filtered scan yields fewer batches than an unfiltered one, not empty ones.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The filter references a field the file's schema does not have, or compares a column against
    /// a constant of a kind no comparison relates to it.
    /// </exception>
    public ScanBuilder Where(VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter = Compute.FunctionFieldExpr.Ranges(filter);

        List<FieldExpr> paths = [];
        FieldsOf(filter, paths);

        // Resolved here rather than per batch, so a typo in a path is an error at build time with
        // the schema in hand, not an exception from inside an enumeration.
        FieldMaskBuilder probe = new FieldMaskBuilder();
        for (int i = 0; i < paths.Count; i++)
        {
            ScanProjection.IncludeField(_file.DType, paths[i], probe, nameof(filter));
        }

        // Same place, same reason, for the constants: a comparison the schema cannot make yields
        // no row, and an empty result reads like an empty file.
        FilterTypeCheck.Check(_file.DType, filter, nameof(filter));

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
    /// The splits the list never touches are skipped before a single segment is registered, so a
    /// thousand scattered rows out of a billion read the splits those rows live in and nothing
    /// else.
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
    /// Delivers the rows in the key order of <paramref name="path"/> instead of file order.
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
    /// key order encodes its keys with nulls last in both directions to match, so a key-ordered
    /// file and a key-ordered merge of files agree.
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
    /// Delivers the rows in the order of a composite key, the tuple of <paramref name="paths"/>.
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
    /// Pruning never changes which rows a scan returns: it may never eliminate a row that full
    /// materialization would have returned, and that is the invariant the whole feature rests on.
    /// Turning it off is therefore a diagnostic, not a semantic: it is how the
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
    /// never changes which rows a scan returns, so a scan with indexes
    /// on and the same scan with them off are the equivalence test every index kind is held to.
    /// </remarks>
    public ScanBuilder WithIndexes(bool enabled)
    {
        _indexes = enabled;
        return this;
    }

    /// <summary>
    /// The degree every scan starts from when it does not ask for one. Defaults to <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a host that wants concurrency has to call
    /// <see cref="WithDegreeOfParallelism"/> at every call site, and nothing obliges it to: one
    /// forgotten site and that scan is sequential, one careless site and it is not. This is the
    /// setting made once, in the place a host configures things — the server-side half of what an
    /// engine spells <c>MAXDOP</c>, where the per-scan call is the query-side half and wins.
    /// </para>
    /// <para>
    /// <b>It is a default and not a total.</b> Ten concurrent scans at a default of four may have
    /// forty splits in flight between them; this bounds one scan, not the process. Bounding the
    /// process would mean scans waiting on each other through a shared gate, which is a different
    /// contract and is not offered here.
    /// </para>
    /// <para>
    /// Read once, when a <see cref="ScanBuilder"/> is constructed, so changing it never disturbs a
    /// builder already made or an enumeration already running. Setting it from one thread while
    /// another builds a scan is safe and leaves that scan with either value.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public static int DefaultDegreeOfParallelism
    {
        get => Volatile.Read(ref s_defaultDegree);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Volatile.Write(ref s_defaultDegree, value);
        }
    }

    /// <summary>Opts in to decoding independent splits concurrently.</summary>
    /// <param name="degree">How many splits may decode at once. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// Overrides <see cref="DefaultDegreeOfParallelism"/>, which is <c>1</c> unless the host has
    /// set it: a library must not appropriate the host's thread pool. Each concurrent split gets
    /// its <b>own</b> <see cref="Vorticity.Arrays.ScanContext"/> with its own arenas; nothing is
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
    /// is immutable after parsing and therefore safe for concurrent scans.
    /// </remarks>
    /// <exception cref="VortexFormatException">The file's layout tree is malformed.</exception>
    public IAsyncEnumerable<RecordBatch> ExecuteAsync() => BatchesAsync(excluded: null);

    /// <summary>
    /// The batches of a key-ordered scan without <paramref name="rows"/>, whose entries it skips as
    /// it walks its key source. The rows whose key is null, which no source holds and a key-ordered
    /// scan otherwise delivers last, are then the caller's to deliver, since only the caller knows
    /// which of them are gone: the rows a dataset deleted from the file without rewriting it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan has no key order.</exception>
    internal IAsyncEnumerable<RecordBatch> ExecuteExcludingAsync(IRowExclusion rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return _orderPath is not null
            ? BatchesAsync(rows)
            : throw new InvalidOperationException(
                "Only a key-ordered scan leaves rows out by itself; a scan in file order delivers every row it reads, and its caller filters them.");
    }

    private IAsyncEnumerable<RecordBatch> BatchesAsync(IRowExclusion? excluded)
    {
        // Parsed at most once per open file rather than once per scan: the tree is a function of
        // the file's bytes and nothing else.
        LayoutTree tree = _file.LayoutTree;
        ScanProjection keep = _fields is null ? ScanProjection.All : ScanProjection.Create(_fields.Build());

        // Filter first, projection second: the scan reads the union so the filter has its columns,
        // and the enumerator trims back down to `keep` once the filter has decided.
        ScanProjection read = _filter is null ? keep : Union(keep, _filterPaths!);
        (RowRange rows, long natural, long cap) = Frame(tree);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap, _windowRows);

        BatchAsyncEnumerable batches = new BatchAsyncEnumerable(
            _file, tree, read, keep, plan, _reverse ? 1 : _degree, _filter, _take, _metrics, _reverse && _orderPath is null)
        {
            Prefetch = _prefetch,
            Compact = _compact,
            KeepEncodings = _keepEncodings,
            SinkDecodes = _sinkDecodes,
            WidenRows = _orderPath is null && !_reverse ? (int)Capped(WindowBatch(natural)) : 0,
        };

        if (_orderPath is not null)
        {
            IAsyncEnumerable<RecordBatch>? nulls = excluded is null && _orderComposite is null && MayBeNull(_orderPath)
                ? NullKeysAsync(tree, rows, keep, cap)
                : null;
            return new KeyOrderedBatches(
                batches, _filter, _orderPath, _orderComposite, _descending, _prune, _indexes, (int)cap, nulls)
            {
                Excluded = excluded,
            };
        }

        // Only a filtered scan pays for the skip-empty wrapper, so an unfiltered one keeps the
        // per-batch allocation figure the allocation tests pin.
        // The wrapper exists for the filter's own two jobs -- prune before reading, skip emptied
        // batches after -- and a take needs the second of them too: a split whose wanted rows are
        // all it holds still produces a batch, but one gathered down to nothing must not.
        if (_filter is null && _take is null)
        {
            // A mask with no filter is an aggregation's: the blocks its zone maps answered, which
            // no split reads.
            return _pruned && _live is not null ? new LiveBatches(batches, _live) : batches;
        }

        return new FilteredBatches(batches, _filter, _prune, _indexes, rows) { Refined = _pruned, Live = _live };
    }

    /// <summary>The batches of an unfiltered scan that reads only the splits a mask keeps.</summary>
    private sealed class LiveBatches(BatchAsyncEnumerable batches, BlockMask live) : IAsyncEnumerable<RecordBatch>
    {
        public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            batches.GetAsyncEnumerator(live, cancellationToken);
    }

    /// <summary>Hands the scan a sink it adds its counters to as it runs.</summary>
    /// <param name="metrics">The caller's sink; a fresh one per fresh count.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Every enumerator started from this builder adds to the same object, so a scan run twice
    /// reports the sum. What it counts is what <see cref="ExplainAsync"/> planned: the segments
    /// and bytes asked of the source, the values the flat reader materialized, the batches and
    /// rows produced -- the same quantities, once the scan has actually run.
    /// </remarks>
    public ScanBuilder WithMetrics(ScanMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
        return this;
    }

    /// <summary>
    /// The plan of this scan without executing it: splits and blocks, what each structure prunes,
    /// the segments and bytes the live splits would read, against the file.
    /// </summary>
    /// <param name="cancellationToken">Cancels the one read this makes.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// The same planning the scan does before its first batch, stopped before any data segment
    /// is read: the layout tree, the split plan under the read projection, the mask of live blocks
    /// refined by every structure the file carries (the zone maps are read for that, one segment
    /// per filtered column, as the scan itself reads them), then a walk of the splits the cursor
    /// would produce, registering the live ones into one request set -- which is how the segments
    /// come out distinct and their bytes summed once. Nothing is decoded. Whether an index earns
    /// its bytes is answered by this and <see cref="WithMetrics"/> together: what it would cost,
    /// and what it did.
    /// </remarks>
    public async System.Threading.Tasks.ValueTask<ScanExplanation> ExplainAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        LayoutTree tree = _file.LayoutTree;
        ScanProjection keep = _fields is null ? ScanProjection.All : ScanProjection.Create(_fields.Build());
        ScanProjection read = _filter is null ? keep : Union(keep, _filterPaths!);
        (RowRange rows, long natural, long cap) = Frame(tree);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap, _windowRows);

        // Each structure is credited with the blocks it pruned among those the scan's rows reach, so
        // that the blocks, the live ones and what each structure pruned add up.
        List<PruningStep> steps = [];
        Compute.ZonePruningPlan.PruningPlan pruning = _filter is not null && _prune
            ? await Compute.ZonePruningPlan
                .PlanAsync(_file, tree, _filter, cancellationToken, steps, metrics: null, _indexes, Scope(tree, rows, natural))
                .ConfigureAwait(false)
            : default;
        Compute.BlockMask? live = pruning.Live;
        Compute.ZonePruner? zones = (_tiers & TerminalTiers.FullBlock) != 0 ? pruning.Zones : null;
        int splitsPruned = 0;
        int splitsProven = 0;

        // What the scan's first step does after the pruning: ask the filter's exact source, when
        // it has one, for the rows it proves, unless the zone maps already show they cannot fit a
        // batch. When they fit, the scan reads the splits holding them and nothing else.
        (RowSelection? proven, string? structure, ScanMetrics? probe) =
            await ProbeAsync(rows, natural, plan, pruning, cancellationToken).ConfigureAwait(false);

        // Counted over the splits the scan's rows touch, as the scan counts what it decodes and
        // prunes: a range or a take covers the blocks it reaches, not the file's, and a block is
        // live by its own verdict, not by that of a split it shares with a live block.
        long blockRows = live?.BlockRows ?? natural;
        BlockTally touched = new BlockTally(blockRows);
        BlockTally zoned = new BlockTally(blockRows);
        BlockTally survived = new BlockTally(blockRows);
        long blocks = 0;
        long zoneLiveBlocks = 0;
        long liveBlocks = 0;

        // A key-ordered scan walks its key source over the key column: a sorted column is decoded
        // zone by zone from the segments the scan itself reads, through what the scan holds, so
        // they are registered with the scan's own; and opening it reads the column's zone map when
        // the filter's pruning did not read it already, which the scan counts as it does.
        Compute.ZoneColumn? keyZones = null;
        ScanMetrics? orderCost = null;
        FieldMask walk = read.RootMask;
        if (_orderPath is not null && _orderComposite is null && Keys.KeyCursorBuilder.StatedSorted(_file, _orderPath))
        {
            walk = Union(read, [Expr.Field(_orderPath)]).RootMask;
            keyZones = pruning.Zones?.Column(_orderPath);
            if (keyZones is null)
            {
                orderCost = new ScanMetrics();
                keyZones = (await Compute.ZonePruningPlan
                    .PlanAsync(_file, tree, Expr.IsNotNull(Expr.Field(_orderPath)), cancellationToken, steps: null, orderCost)
                    .ConfigureAwait(false)).Zones?.Column(_orderPath);
            }
        }

        int splits = 0;
        int zoneLiveSplits = 0;
        int liveSplits = 0;
        IO.SegmentRequestSet segments = new IO.SegmentRequestSet();
        try
        {
            LayoutNode root = tree.Root;
            LayoutReader reader = LayoutReaderTable.Require(in root);
            FieldMask mask = walk;
            SplitCursor cursor = plan.CreateCursor();
            while (cursor.TryNext(out RowRange split))
            {
                splits++;
                if (_take is not null && !_take.Touches(split))
                {
                    continue;
                }

                blocks += touched.Add(split, _take);
                zoneLiveBlocks += zoned.Add(split, _take, live);
                if (live is not null && !live.AnyLive(split))
                {
                    splitsPruned++;
                    continue;
                }

                zoneLiveSplits++;
                if (zones is not null && Proves(zones, split))
                {
                    splitsProven++;
                }

                if (proven is not null && !proven.Touches(split))
                {
                    continue;
                }

                // The rows an exact index proved are read without the mask, as the scan reads them.
                liveSplits++;
                liveBlocks += proven is null ? survived.Add(split, _take, live) : survived.Add(split, _take, proven: proven);
                reader.RegisterSegments(in root, split, in mask, segments);
            }

            if (structure is not null)
            {
                steps.Add(new PruningStep(
                    structure, checked((int)(zoneLiveBlocks - liveBlocks)), checked((int)probe!.SegmentRequests), probe.BytesRequested));
            }

            // The data segments of the live splits, plus what consulting each structure cost:
            // the same asking the metrics count, so that plan and outcome are one quantity. A
            // segment the scan holds is asked for once, so the distinct ones are what it asks for.
            int toRead = ScanMetrics.Unread(segments, _file.Segments, out long bytes);

            for (int i = 0; i < steps.Count; i++)
            {
                toRead += steps[i].SegmentsRead;
                bytes += steps[i].BytesRead;
            }

            if (orderCost is not null)
            {
                toRead += checked((int)orderCost.SegmentRequests);
                bytes += orderCost.BytesRequested;
            }

            // The whole file-level answer: the statistics, then the file filters when indexes are on.
            bool fileMayMatch = _filter is null
                || (_indexes
                    ? await _file.MayMatchAsync(_filter, cancellationToken).ConfigureAwait(false)
                    : Compute.FileStatisticsPruner.MayMatch(_file, _filter));
            // The exact cover, the first tier a count takes: a scan whose cover holds a batch or
            // fewer reads those rows and evaluates nothing.
            CountExplanation? count = null;
            if (_filter is not null)
            {
                (bool exact, long covered) = await ExactAsync(pruning.Zones, cancellationToken).ConfigureAwait(false);
                count = new CountExplanation(exact, covered, splitsPruned, splitsProven, zoneLiveSplits - splitsProven);
            }

            OrderExplanation? order = _orderPath is null ? null : await OrderAsync(keyZones, cancellationToken).ConfigureAwait(false);
            return new ScanExplanation(
                _take?.Count ?? rows.Length, blockRows, checked((int)blocks), checked((int)liveBlocks), steps, splits, liveSplits,
                proven?.Count ?? 0, toRead, bytes, _file.FileLength, fileMayMatch)
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

    /// <summary>
    /// The blocks the scan's rows reach, as a mask over blocks of the natural size: those the range
    /// overlaps, or those holding a taken row; null when the scan covers the whole file.
    /// </summary>
    private BlockMask? Scope(LayoutTree tree, RowRange rows, long natural)
    {
        if (!_rowsSet && _take is null)
        {
            return null;
        }

        BlockMask scope = new BlockMask(tree.Root.RowCount, natural);
        scope.KeepOnly(rows);
        if (_take is not null)
        {
            for (int block = 0; block < scope.BlockCount; block++)
            {
                if (scope.IsLive(block) && !_take.Touches(scope.BlockRange(block)))
                {
                    scope.Kill(block);
                }
            }
        }

        return scope;
    }

    /// <summary>
    /// What the scan's first step asks the filter's exact source, as <c>FilteredBatches</c> asks it:
    /// the rows it proves when they fit a batch, the structure that answered, and what asking cost.
    /// </summary>
    private async System.Threading.Tasks.ValueTask<(RowSelection? Proven, string? Structure, ScanMetrics? Cost)> ProbeAsync(
        RowRange rows, long natural, SplitPlan plan, Compute.ZonePruningPlan.PruningPlan pruning,
        System.Threading.CancellationToken cancellationToken)
    {
        Compute.ZonePruner? zones = pruning.Zones;
        if (_orderPath is not null || !_compact || !_prune || _filter is null || _take is not null || pruning.Located
            || !Keys.ExactCover.MayExist(_file, _filter, _indexes)
            || !FilteredBatches.MayFitBatch(plan, natural, zones, pruning.Live))
        {
            return (null, null, null);
        }

        ScanMetrics cost = new ScanMetrics();
        Keys.ExactCover? cover = await Keys.ExactCover
            .TryCreateAsync(_file, _filter, _indexes, cancellationToken, zones, cost)
            .ConfigureAwait(false);
        if (cover is null)
        {
            return (null, null, null);
        }

        string structure = cover.Kind == KeySourceKind.SortedColumn ? "sorted column" : "sorted runs";
        long[]? proven;
        try
        {
            proven = await cover.RowsAsync(rows, natural, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await cover.DisposeAsync().ConfigureAwait(false);
        }

        return (proven is null ? null : RowSelection.Create(proven, _file.RowCount), structure, cost);
    }

    /// <summary>Whether an exact source covers the filter, and its count.</summary>
    /// <param name="zones">The zone maps the plan read already, handed on so that they are not read again.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private async System.Threading.Tasks.ValueTask<(bool Exact, long Count)> ExactAsync(
        Compute.ZonePruner? zones, System.Threading.CancellationToken cancellationToken)
    {
        if ((_tiers & TerminalTiers.ExactCover) == 0 || !_prune || _filter is null)
        {
            return (false, 0);
        }

        Keys.ExactCover? cover = await Keys.ExactCover
            .TryCreateAsync(_file, _filter, _indexes, cancellationToken, zones)
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

    /// <summary>The key source <c>InKeyOrder</c> would walk, and the range it would walk.</summary>
    /// <param name="keyZones">The key column's zone map, when the plan read it already; null to let the source read it.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private async System.Threading.Tasks.ValueTask<OrderExplanation> OrderAsync(
        Compute.ZoneColumn? keyZones, System.Threading.CancellationToken cancellationToken)
    {
        Keys.KeySource? source;
        KeySourceKind kind;
        string named = _orderComposite is null ? _orderPath! : "(" + string.Join(", ", _orderComposite) + ")";
        if (_orderComposite is not null)
        {
            (source, _) = _indexes
                ? await Keys.SortedRunsSource.OpenCompositeAsync(_file, _orderComposite, cancellationToken).ConfigureAwait(false)
                : (null, null);
            kind = source is null ? KeySourceKind.None : KeySourceKind.SortedRuns;
        }
        else
        {
            (source, kind) = await Keys.KeyCursorBuilder
                .OpenSourceAsync(_file, _orderPath!, _indexes, cancellationToken, keyZones)
                .ConfigureAwait(false);
        }

        if (source is null)
        {
            return new OrderExplanation(named, KeySourceKind.None, 0, 0, null, 0, _descending);
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
            return new OrderExplanation(named, kind, source.Runs, runsInRange, source.EntryCount, entries, _descending);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How many rows the scan would return, exactly, without returning them.
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
    /// <see cref="CountAsync"/> without <paramref name="rows"/>, whatever the filter says of them,
    /// taken out tier by tier in the same pass: arithmetic without a filter or on a sorted column's
    /// slices, a whole verdict of the zone maps on a split that holds some, a decode with them cleared.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scan takes rows, which it would take by their own places.</exception>
    internal System.Threading.Tasks.ValueTask<long> CountExcludingAsync(
        IRowExclusion rows, System.Threading.CancellationToken cancellationToken) =>
        Terminal(excluded: rows ?? throw new ArgumentNullException(nameof(rows))).CountAsync(cancellationToken);

    /// <summary>
    /// Whether the scan would return at least one row.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Whether some row matches.</returns>
    /// <remarks>
    /// <see cref="CountAsync"/> stopped at the first split that counts: an empty mask answers
    /// without a read, a split the zone maps prove answers from memory, and the bad case reads
    /// every live split once and materializes none. It is an exact membership probe, where
    /// <see cref="VortexFilePruningExtensions.MayMatch"/> answers with a superset.
    /// </remarks>
    public System.Threading.Tasks.ValueTask<bool> AnyAsync(
        System.Threading.CancellationToken cancellationToken = default) =>
        Terminal().AnyAsync(cancellationToken);

    /// <summary><see cref="AnyAsync"/> without <paramref name="rows"/>, as <see cref="CountExcludingAsync"/> leaves them out.</summary>
    /// <exception cref="InvalidOperationException">The scan takes rows.</exception>
    internal System.Threading.Tasks.ValueTask<bool> AnyExcludingAsync(
        IRowExclusion rows, System.Threading.CancellationToken cancellationToken) =>
        Terminal(excluded: rows ?? throw new ArgumentNullException(nameof(rows))).AnyAsync(cancellationToken);

    /// <summary>
    /// The smallest non-null value of <paramref name="path"/> among the rows the scan would
    /// return, in the filter's order; <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The minimum.</returns>
    /// <remarks>
    /// Cheapest resolution first: the file's own statistic when the scan is the whole file and
    /// the statistic is <c>Exact</c> (no read); the zone map's bounds for every split they decide
    /// whole, an <c>Inexact</c> bound being a candidate decoded only when it could still win;
    /// the decode of what is left, with a running extreme and one split of memory at a time. A
    /// NaN is never the minimum nor the maximum, as the statistics have it.
    /// <see cref="Where"/>, <see cref="Rows"/>, <see cref="Take"/> and <see cref="WithPruning"/>
    /// are honoured exactly as <see cref="ExecuteAsync"/> honours them.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not resolve against the file's schema.</exception>
    /// <exception cref="NotSupportedException">The column's type is not one the filter supports.</exception>
    public System.Threading.Tasks.ValueTask<FilterLiteral> MinAsync(
        string path, System.Threading.CancellationToken cancellationToken = default) =>
        Terminal(Resolved(path)).ExtremeAsync(path, wantMin: true, cancellationToken);

    /// <summary>
    /// The largest non-null value of <paramref name="path"/> among the rows the scan would
    /// return, in the filter's order; <see cref="FilterLiteral.Null"/> when there is none.
    /// </summary>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The maximum.</returns>
    /// <remarks>See <see cref="MinAsync"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not resolve against the file's schema.</exception>
    /// <exception cref="NotSupportedException">The column's type is not one the filter supports.</exception>
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
        ScanProjection.IncludePath(_file.DType, path, new FieldMaskBuilder(), nameof(path));
        return path;
    }

    /// <summary>
    /// The rows the scan covers and the batch size it runs at -- the same frame for the scan,
    /// its plan and its terminals.
    /// </summary>
    /// <param name="tree">The parsed layout tree.</param>
    /// <returns>The row range, the file's natural batch size, and the cap the scan runs at.</returns>
    /// <remarks>
    /// <para>
    /// A take narrows the planned range to the span its indices cover, which already skips every
    /// split outside it; the ones inside it that hold no wanted row are skipped per split.
    /// </para>
    /// <para>
    /// A scan that only reads is batched by the window, as many zones as a window holds: the fixed
    /// cost of a batch is paid once a window, and a window of one batch is decoded straight into it
    /// rather than held decoded for the batches after it. A filter, a take and an order work zone
    /// by zone -- a zone is what is pruned, proven and kept -- and a batch of several zones would
    /// read the ones they drop, so those scans are batched by the zone; a filtered scan in file
    /// order then reads a run of zones its zone maps prove whole as one batch, up to the window,
    /// since nothing in them is dropped.
    /// </para>
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

        long batch = _filter is null && _take is null && _orderPath is null ? WindowBatch(natural) : natural;
        return (rows, natural, Capped(batch));
    }

    /// <summary>As many of the file's zones as a window holds, and at least one.</summary>
    private long WindowBatch(long natural) => Math.Max(1, _windowRows / natural) * natural;

    /// <summary><paramref name="batch"/> under the caller's cap, which only ever lowers it.</summary>
    private long Capped(long batch) => _maxBatchRows > 0 && _maxBatchRows < batch ? _maxBatchRows : batch;

    /// <summary>
    /// The terminal form of this scan: it reads the filter's columns, and the one an extreme is
    /// asked of, and nothing else, since it has no batch to fill.
    /// </summary>
    /// <param name="extraPath">The column a <c>Min</c> or <c>Max</c> reads, or null for a count.</param>
    /// <param name="excluded">The rows a count or an any leaves out, or null for none.</param>
    private TerminalScan Terminal(string? extraPath = null, IRowExclusion? excluded = null)
    {
        LayoutTree tree = _file.LayoutTree;
        ScanProjection read = _filter is null && extraPath is null
            ? ScanProjection.All
            : Only(_filterPaths, extraPath);
        (RowRange rows, _, long cap) = Frame(tree);
        bool wholeFile = !_rowsSet && _take is null;
        if (excluded is not null && _take is not null)
        {
            throw new InvalidOperationException(
                "Rows left out serve a count of the file's rows, never of rows taken by their places: the caller maps a take past them.");
        }

        return new TerminalScan(
            _file, tree, _filter, rows, wholeFile, cap, read, _take, _prune, _tiers, _metrics, _indexes, excluded);
    }

    /// <summary>The projection of exactly the fields a filter reads, plus one.</summary>
    private ScanProjection Only(List<FieldExpr>? filterPaths, string? extraPath)
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        if (filterPaths is not null)
        {
            for (int i = 0; i < filterPaths.Count; i++)
            {
                ScanProjection.IncludeField(_file.DType, filterPaths[i], builder, "filter");
            }
        }

        if (extraPath is not null)
        {
            ScanProjection.IncludePath(_file.DType, extraPath, builder, nameof(extraPath));
        }

        return ScanProjection.Create(builder.Build());
    }

    /// <summary>Whether the key column, or a struct above it, is nullable: whether rows can have no key.</summary>
    private bool MayBeNull(string path)
    {
        DType current = _file.DType;
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
    /// A filtered scan under <c>IsNull(key)</c> conjoined with the caller's own filter, so the zone
    /// maps' null counts prune every block that holds no null key before a byte of it is read. A
    /// split is at most one batch, so the descending tail reverses one batch at a time and memory
    /// stays one batch.
    /// </remarks>
    private IAsyncEnumerable<RecordBatch> NullKeysAsync(LayoutTree tree, RowRange rows, ScanProjection keep, long cap)
    {
        VortexExpr isNull = Expr.IsNull(Expr.Field(_orderPath!));
        VortexExpr filter = _filter is null ? isNull : Expr.And(isNull, _filter);
        ScanProjection read = Union(keep, [.. _filterPaths ?? [], Expr.Field(_orderPath!)]);
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap, _windowRows);
        return _descending
            ? ReversedAsync(tree, rows, read, keep, plan, filter)
            : new FilteredBatches(
                new BatchAsyncEnumerable(_file, tree, read, keep, plan, _degree, filter, null, _metrics),
                filter, _prune, _indexes, rows);
    }

    /// <summary>The splits of <paramref name="plan"/> last first, each batch's rows reversed.</summary>
    private async IAsyncEnumerable<RecordBatch> ReversedAsync(
        LayoutTree tree, RowRange rows, ScanProjection read, ScanProjection keep, SplitPlan plan, VortexExpr filter)
    {
        // One pipeline for the whole walk, reading the plan's splits last one first. Building one
        // per split would cost a plan, an enumerable and a filter for every batch -- a split is a
        // batch, since both are cut to the same row cap -- which dwarfs the per-batch allocation
        // every other scan is held to.
        IAsyncEnumerable<RecordBatch> scan = new FilteredBatches(
            new BatchAsyncEnumerable(_file, tree, read, keep, plan, 1, filter, null, _metrics, reverse: true),
            filter, _prune, _indexes, rows);

        // The permutation of a batch of n rows is n-1 … 0 and depends on nothing else, so it is
        // shared by every batch of the same length. Kept across the walk and refilled only when the
        // length changes, it costs one array per scan instead of one per batch.
        int[] order = [];
        int ordered = 0;

        // The reversed rows are a view the walk binds again per batch, as the scan binds its own.
        RecordBatch? reversed = null;
        try
        {
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

                // In the batch's own arena: the scan owns it, and resets it at its next batch.
                int root = CanonicalFilter.Apply(batch.Arena, batch.RootIndex, order.AsSpan(0, batch.RowCount));
                reversed?.Dispose();
                reversed = RecordBatch.Over(batch.Arena, root, batch.StartRow, reversed);
                yield return reversed;
            }
        }
        finally
        {
            reversed?.Dispose();
        }
    }

    /// <summary>The projection widened by every field a filter reads.</summary>
    private ScanProjection Union(ScanProjection keep, List<FieldExpr> filterPaths)
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        builder.Include(keep.RootMask);
        for (int i = 0; i < filterPaths.Count; i++)
        {
            ScanProjection.IncludeField(_file.DType, filterPaths[i], builder, "filter");
        }

        return ScanProjection.Create(builder.Build());
    }

    /// <summary>Every column a filter reads, in the order it names them.</summary>
    internal static void FieldsOf(VortexExpr filter, List<FieldExpr> into)
    {
        Collector collector = new Collector(into);
        VisitFields(filter, ref collector);
    }

    /// <summary>What a walk over the columns a filter reads does with each one.</summary>
    internal interface IFieldVisitor
    {
        /// <summary>Takes one column.</summary>
        /// <param name="field">The column, as the filter names it.</param>
        /// <returns>Whether the walk goes on.</returns>
        bool Visit(FieldExpr field);
    }

    /// <summary>Hands every column a filter reads to <paramref name="visitor"/>, in the order it names them.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="visitor">What takes each column.</param>
    /// <returns>Whether the walk went to its end.</returns>
    internal static bool VisitFields<TVisitor>(VortexExpr filter, ref TVisitor visitor)
        where TVisitor : IFieldVisitor, allows ref struct
    {
        switch (filter)
        {
            case FieldExpr field:
                return visitor.Visit(field);
            case ComparisonExpr comparison:
                return visitor.Visit(comparison.Field);
            case ColumnComparisonExpr columns:
                return visitor.Visit(columns.Left) && visitor.Visit(columns.Right);
            case NullCheckExpr check:
                return visitor.Visit(check.Field);
            case InExpr membership:
                return visitor.Visit(membership.Field);
            case StringMatchExpr match:
                return visitor.Visit(match.Field);
            case ListContainsExpr contains:
                return visitor.Visit(contains.Field);
            case NotExpr negation:
                return VisitFields(negation.Operand, ref visitor);
            case LogicalExpr logical:
                return VisitFields(logical.Left, ref visitor) && VisitFields(logical.Right, ref visitor);
            default:
                return true;
        }
    }

    /// <summary>Lists the columns in the order the filter names them.</summary>
    private readonly struct Collector(List<FieldExpr> into) : IFieldVisitor
    {
        public bool Visit(FieldExpr field)
        {
            into.Add(field);
            return true;
        }
    }

    /// <summary>Adds <paramref name="mask"/> to the projection: the columns a record reads, resolved by index.</summary>
    internal ScanBuilder ProjectMask(in FieldMask mask)
    {
        Fields().Include(in mask);
        return this;
    }

    private FieldMaskBuilder Fields() => _fields ??= new FieldMaskBuilder();
}

/// <summary>The scan entry point.</summary>
internal static class VortexFileScanExtensions
{
    /// <summary>Starts building a scan over <paramref name="file"/>.</summary>
    /// <param name="file">An open file.</param>
    /// <returns>A fresh builder.</returns>
    /// <remarks>
    /// An extension rather than a method on <see cref="VortexFile"/> so that file-open does not
    /// depend on scan.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static ScanBuilder ScanBuilder(this VortexFile file) => new ScanBuilder(file);
}
