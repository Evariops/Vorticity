// PHASE1-CONTRACTS.md §13.1. The fluent front door of the library.
//
// WHERE `Where` GOES, and why it sits where it does. docs/03-architecture.md §3.4 fixes the order
// of application as WHERE THEN PROJECT: the filter sees columns the projection does not. So
// ExecuteAsync compiles TWO masks - `keep`, what the caller projected, and `read`, that unioned
// with every field the filter references - plans the scan under `read`, and hands both to the
// enumerator, which trims `read` down to `keep` after the filter has run.
using System;
using System.Collections.Generic;

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
    private RowSelection? _take;
    private RowRange _rows;
    private bool _rowsSet;
    private int _maxBatchRows;
    private int _degree = 1;
    private ScanMetrics? _metrics;

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

        _take = RowSelection.Create(rowIndices, _file.RowCount);
        return this;
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

        long rootRows = tree.Root.RowCount;
        RowRange whole = new RowRange(0, rootRows);
        RowRange rows = _rowsSet ? _rows.Intersect(whole) : whole;

        // A take narrows the planned range to the span its indices cover, which already skips every
        // split outside it; the ones inside it that hold no wanted row are skipped per split.
        if (_take is not null)
        {
            rows = _take.Bounds.Intersect(whole);
        }

        Projection keep = _fields is null ? Projection.All : Projection.Create(_fields.Build());

        // WHERE THEN PROJECT: the scan reads the union so the filter has its columns, and the
        // enumerator trims back down to `keep` once the filter has decided.
        Projection read = _filter is null ? keep : Union(keep, _filterPaths!);

        long natural = SplitPlan.NaturalBatchRows(tree);
        if (natural > int.MaxValue)
        {
            natural = int.MaxValue;
        }

        long cap = _maxBatchRows > 0 && _maxBatchRows < natural ? _maxBatchRows : natural;
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap);

        BatchAsyncEnumerable batches = new BatchAsyncEnumerable(
            _file, tree, read, keep, plan, _degree, _filter, _take, _metrics);

        // Only a filtered scan pays for the skip-empty wrapper; an unfiltered one is the same
        // object graph it has always been, which is what keeps the per-batch allocation figure
        // the allocation tests pin unchanged.
        // The wrapper exists for the filter's own two jobs -- prune before reading, skip emptied
        // batches after -- and a take needs the second of them too: a split whose wanted rows are
        // all it holds still produces a batch, but one gathered down to nothing must not.
        return _filter is null && _take is null
            ? batches
            : new FilteredBatches(batches, _filter, _prune);
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

        long rootRows = tree.Root.RowCount;
        RowRange whole = new RowRange(0, rootRows);
        RowRange rows = _rowsSet ? _rows.Intersect(whole) : whole;
        if (_take is not null)
        {
            rows = _take.Bounds.Intersect(whole);
        }

        Projection keep = _fields is null ? Projection.All : Projection.Create(_fields.Build());
        Projection read = _filter is null ? keep : Union(keep, _filterPaths!);

        long natural = SplitPlan.NaturalBatchRows(tree);
        if (natural > int.MaxValue)
        {
            natural = int.MaxValue;
        }

        long cap = _maxBatchRows > 0 && _maxBatchRows < natural ? _maxBatchRows : natural;
        SplitPlan plan = SplitPlan.Compute(tree, rows, read.RootMask, cap);

        List<PruningStep> steps = [];
        Compute.BlockMask? live = _filter is not null && _prune
            ? await Compute.ZonePruningPlan
                .RefineAsync(_file, tree, _filter, cancellationToken, steps)
                .ConfigureAwait(false)
            : null;

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
                    continue;
                }

                liveSplits++;
                reader.RegisterSegments(in root, split, in mask, segments);
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

            bool fileMayMatch = _filter is null || _file.MayMatch(_filter);
            return new ScanPlan(
                rows.Length, blockRows, blocks, liveBlocks, steps, splits, liveSplits,
                RowsSelectedByIndex: 0, toRead, bytes, _file.FileLength, fileMayMatch);
        }
        finally
        {
            segments.Dispose();
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
