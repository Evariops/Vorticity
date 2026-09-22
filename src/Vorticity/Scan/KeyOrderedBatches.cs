using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Layouts;

namespace Vorticity.Scanning;

/// <summary>A scan whose batches come in the key order of one column.</summary>
/// <remarks>
/// <para>
/// A key cursor drives the scan instead of the split planner. It walks one window of entries -- the
/// batch size -- inside the slices the filter's conjuncts on the key column allow, skips the entries
/// whose block the mask has pruned, and hands the window's rows to the take push-down as a
/// selection; the splits the window touches are read and concatenated in file order, the filter runs
/// on that, and the survivors are gathered in the window's key order. Only one window is ever held,
/// so reordering never buffers the whole result and a consumer that stops enumerating never reads
/// past its stop.
/// </para>
/// <para>
/// The scan's degree applies within a window, whose splits are independent: they are cut into as
/// many contiguous groups as there are lanes, and each lane's root is referenced rather than copied
/// into the scan's context. Windows themselves stay sequential. What this costs depends on how well
/// key order correlates with file order -- on a sorted column a window is a contiguous read, while
/// over an uncorrelated column a window of n entries can touch n splits -- and the metrics sink
/// counts the windows and the splits they touched so a caller can tell which case it is in.
/// </para>
/// </remarks>
internal sealed class KeyOrderedBatches : IAsyncEnumerable<RecordBatch>
{
    private readonly BatchAsyncEnumerable _scan;
    private readonly VortexExpr? _filter;

    /// <summary>Holds what the filter is worth preparing once rather than once per batch.</summary>
    private readonly FilterEvaluator? _evaluator;
    private readonly string _path;
    private readonly string[]? _composite;
    private readonly bool _descending;
    private readonly bool _prune;
    private readonly bool _indexes;
    private readonly int _window;
    private readonly IAsyncEnumerable<RecordBatch>? _nulls;

    /// <param name="scan">The compiled scan, whose projections, plan and sink this one reads under.</param>
    /// <param name="filter">The predicate, or null.</param>
    /// <param name="path">The key column, or a composite key's first.</param>
    /// <param name="composite">A composite key's columns in key order, or null for one column.</param>
    /// <param name="descending">Whether the order is reversed.</param>
    /// <param name="prune">Whether the mask of live blocks skips entries.</param>
    /// <param name="indexes">Whether the index directory may serve.</param>
    /// <param name="window">The entries a window walks: the batch size.</param>
    /// <param name="nulls">The rows whose key is null, delivered after the walk; null when there can be none.</param>
    internal KeyOrderedBatches(
        BatchAsyncEnumerable scan,
        VortexExpr? filter,
        string path,
        string[]? composite,
        bool descending,
        bool prune,
        bool indexes,
        int window,
        IAsyncEnumerable<RecordBatch>? nulls)
    {
        _scan = scan;
        _filter = filter;
        _evaluator = filter is null ? null : new FilterEvaluator(filter);
        _path = path;
        _composite = composite;
        _descending = descending;
        _prune = prune;
        _indexes = indexes;
        _window = Math.Max(window, 1);
        _nulls = nulls;
    }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(this, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>
    {
        private readonly KeyOrderedBatches _owner;
        private readonly BatchAsyncEnumerable _scan;
        private readonly FieldMask _read;
        private readonly FieldMask _keep;
        private readonly CancellationToken _token;
        private readonly ScanContext _context;
        private readonly ScanContext[]? _lanes;
        private readonly GroupWaiter[]? _waiters;
        private readonly ScanSegments _held;
        private readonly RetainedChunks _retained;
        private readonly long[] _rows;
        private readonly long[] _sorted;
        private readonly RowSelection _selection;
        private readonly List<RowRange> _splits = [];
        private KeySource? _source;
        private List<(long Low, long High)> _slices = [];
        private BlockMask? _live;
        private int _slice;
        private long _left;
        private long _first;
        private bool _positioned;
        private bool _started;
        private bool _disposed;
        private RecordBatch? _current;
        private IAsyncEnumerator<RecordBatch>? _tail;

        internal Enumerator(KeyOrderedBatches owner, CancellationToken token)
        {
            _owner = owner;
            _scan = owner._scan;
            _read = _scan.ReadProjection.RootMask;
            _keep = _scan.Projection.RootMask;
            _token = token;

            // What the windows read and decode is held across them, and shared with the key source
            // that walks the same column: a window of a sorted key is the next window of the same
            // chunk, and the source's zones are windows of it too.
            _held = new ScanSegments(Math.Max(_scan.Degree, 1));
            _retained = new RetainedChunks((_read.IsAll ? _scan.Tree.Root.ChildCount : _read.NamedFieldCount) + 1, Math.Max(_scan.Degree, 1));
            _context = new ScanContext(_scan.File) { Metrics = _scan.Metrics };
            _context.ShareRetained(_retained);

            // Rented rather than allocated: a window is the batch size, and two arrays of that many
            // longs would be the large object heap's from a modest block size up.
            _rows = ArrayPool<long>.Shared.Rent(owner._window);
            _sorted = ArrayPool<long>.Shared.Rent(owner._window);

            // The window's rows, sorted, deduplicated and bounded by `Plan` and `CollectAsync`:
            // one selection per scan, refilled per window.
            _selection = RowSelection.Over(_sorted);
            if (_scan.Degree > 1)
            {
                _lanes = new ScanContext[_scan.Degree];
                _waiters = new GroupWaiter[_scan.Degree];
                for (int i = 0; i < _lanes.Length; i++)
                {
                    _lanes[i] = new ScanContext(_scan.File) { Metrics = _scan.Metrics };
                    _lanes[i].ShareRetained(_retained);
                    _waiters[i] = new GroupWaiter();
                }
            }
        }

        public RecordBatch Current =>
            _tail is not null ? _tail.Current : _current ?? ScanThrow.NoCurrentBatch<RecordBatch>();

        public async ValueTask<bool> MoveNextAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Release();
            if (_tail is not null)
            {
                return await _tail.MoveNextAsync().ConfigureAwait(false);
            }

            if (!_started)
            {
                await StartAsync().ConfigureAwait(false);
                _started = true;
            }

            while (true)
            {
                _token.ThrowIfCancellationRequested();

                // Everything the window reads, its key source's zones included, is numbered from
                // here: once the window is executed, what nothing from here on used is released.
                _first = _held.NextTicket();
                int count = await CollectAsync().ConfigureAwait(false);
                if (count == 0)
                {
                    // The walk is done; the rows no source holds come last (ScanBuilder.InKeyOrder).
                    if (_owner._nulls is null)
                    {
                        return false;
                    }

                    _tail = _owner._nulls.GetAsyncEnumerator(_token);
                    return await _tail.MoveNextAsync().ConfigureAwait(false);
                }

                try
                {
                    (int root, long first) = await ExecuteAsync(count).ConfigureAwait(false);
                    _held.Release(_first);
                    _retained.Release(_first);
                    if (_context.Canonical.GetNode(root).Length > 0)
                    {
                        _current = new RecordBatch(_context, root, first);
                        _scan.Metrics?.AddBatch(_current.RowCount);
                        return true;
                    }
                }
                catch
                {
                    Release();
                    throw;
                }

                // A window the filter emptied produces no batch, as a filtered scan's split does not.
                Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
            if (_tail is not null)
            {
                await _tail.DisposeAsync().ConfigureAwait(false);
            }

            // The source and the contexts before what they share: each gives up its claims first.
            if (_source is not null)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }

            _context.Dispose();
            if (_lanes is not null)
            {
                foreach (ScanContext lane in _lanes)
                {
                    lane.Dispose();
                }
            }

            _held.Dispose();
            _retained.Dispose();
            ArrayPool<long>.Shared.Return(_rows);
            ArrayPool<long>.Shared.Return(_sorted);
        }

        /// <summary>Drops the current batch and everything the lanes decoded for it.</summary>
        private void Release()
        {
            RecordBatch? batch = _current;
            _current = null;
            batch?.Dispose();
            _context.ResetBatch();

            if (_lanes is not null)
            {
                foreach (ScanContext lane in _lanes)
                {
                    lane.ResetBatch();
                }
            }
        }

        private async ValueTask StartAsync()
        {
            VortexFile file = _scan.File;

            // The pruning first: a filter on the key column reads its zone map, which the key
            // source then takes as read rather than reading again.
            ZonePruningPlan.PruningPlan pruning = default;
            if (_owner._prune && _owner._filter is not null)
            {
                pruning = await ZonePruningPlan
                    .PlanAsync(file, _scan.Tree, _owner._filter, _token, steps: null, _scan.Metrics, _owner._indexes)
                    .ConfigureAwait(false);
                _live = pruning.Live;

                // The walk skips the rows of a dead block one entry at a time, so the blocks are
                // counted here, once, as the plan counts them.
                if (_live is { } live && _scan.Metrics is { } metrics)
                {
                    long dead = 0;
                    for (int block = 0; block < live.BlockCount; block++)
                    {
                        dead += live.IsLive(block) ? 0 : 1;
                    }

                    metrics.AddBlocksPruned(dead);
                }
            }

            KeySource source;
            if (_owner._composite is { } composite)
            {
                // The tuple's own sorted run. The filter names columns, not tuples, so it does not
                // narrow the walk: it prunes and filters as it does for one column.
                (SortedRunsSource? runs, string? reason) = _owner._indexes
                    ? await SortedRunsSource.OpenCompositeAsync(file, composite, _token).ConfigureAwait(false)
                    : (null, "the scan was asked not to use indexes");
                _source = source = runs ?? throw new VortexUnsupportedException(
                    IndexKinds.SortedRuns,
                    ComponentKind.Index,
                    $"A key-ordered read of ({string.Join(", ", composite)}) needs the composite key's sorted runs " +
                    $"(IndexPolicy.ForKey): {reason}.");
                _slices = await ExactCover.RangeAsync(null, source, _owner._path, _token).ConfigureAwait(false);
            }
            else
            {
                (KeySource? opened, _) = await KeyCursorBuilder
                    .OpenSourceAsync(file, _owner._path, _owner._indexes, _token, pruning.Zones?.Column(_owner._path), _scan.Metrics)
                    .ConfigureAwait(false);
                _source = source = opened ?? throw new VortexUnsupportedException(
                    IndexKinds.SortedRuns,
                    ComponentKind.Index,
                    $"InKeyOrder(\"{_owner._path}\") needs a key source: the column is neither stated sorted " +
                    "nor indexed with IndexPolicy.SortedRuns, and ordering an unindexed column would mean " +
                    "holding the whole column in memory.");
                source.Share(_held, _retained);
                _slices = await ExactCover.RangeAsync(_owner._filter, source, _owner._path, _token).ConfigureAwait(false);
            }

            if (_owner._descending)
            {
                _slices.Reverse();
            }
        }

        /// <summary>The next window's rows, in key order; zero at the end.</summary>
        private async ValueTask<int> CollectAsync()
        {
            KeySource source = _source!;
            bool descending = _owner._descending;
            long rowCount = _scan.File.RowCount;
            int count = 0;
            while (count < _owner._window)
            {
                if (!_positioned)
                {
                    if (_slice >= _slices.Count)
                    {
                        break;
                    }

                    (long low, long high) = _slices[_slice];
                    _left = high - low;
                    _positioned = await source
                        .SeekRankAsync(descending ? high - 1 : low, _token)
                        .ConfigureAwait(false);
                    if (!_positioned)
                    {
                        _slice++;
                        continue;
                    }
                }

                long row = source.Row;
                if ((ulong)row >= (ulong)rowCount)
                {
                    throw new VortexFormatException(
                        $"The key source of '{_owner._path}' names row {row} of a {rowCount}-row file.");
                }

                if (_live is null || _live.IsLive((int)(row / _live.BlockRows)))
                {
                    _rows[count++] = row;
                }

                if (--_left == 0)
                {
                    _positioned = false;
                    _slice++;
                    continue;
                }

                _positioned = descending
                    ? await source.PrevAsync(_token).ConfigureAwait(false)
                    : await source.NextAsync(_token).ConfigureAwait(false);
                if (!_positioned)
                {
                    _slice++;
                }
            }

            return count;
        }

        /// <summary>
        /// Reads the window's rows, filters them, and lays them out in key order; the root, and the
        /// smallest file row the batch was read from.
        /// </summary>
        private async ValueTask<(int Root, long First)> ExecuteAsync(int count)
        {
            int distinct = Plan(count);
            _selection.Reset(distinct);
            int root = await DecodeAsync(_selection).ConfigureAwait(false);
            NoteDecoded(distinct);
            return (Arrange(root, count, distinct), _sorted[0]);
        }

        /// <summary>
        /// Counts the blocks the window's rows lie in as decoded. A block two windows reach is
        /// decoded twice and counted twice: what a key order costs over file order.
        /// </summary>
        private void NoteDecoded(int distinct)
        {
            if (_scan.Metrics is not { } metrics)
            {
                return;
            }

            long blockRows = Math.Max(_live?.BlockRows ?? SplitPlan.NaturalBatchRows(_scan.Tree), 1);
            long blocks = 0;
            long last = -1;
            for (int i = 0; i < distinct; i++)
            {
                long block = _sorted[i] / blockRows;
                if (block != last)
                {
                    blocks++;
                    last = block;
                }
            }

            metrics.AddBlocksDecoded(blocks);
        }

        /// <summary>Sorts the window's rows into file order and lists the splits they touch; the distinct rows.</summary>
        private int Plan(int count)
        {
            Span<long> sorted = _sorted.AsSpan(0, count);
            _rows.AsSpan(0, count).CopyTo(sorted);
            sorted.Sort();

            // A source holds a row once; a lying one may not, and the take collapses duplicates.
            int distinct = 1;
            for (int i = 1; i < sorted.Length; i++)
            {
                if (sorted[i] != sorted[distinct - 1])
                {
                    sorted[distinct++] = sorted[i];
                }
            }

            // The splits the window touches, each once, in file order.
            _splits.Clear();
            SplitPlan plan = _scan.Plan;
            for (int i = 0; i < distinct; i++)
            {
                if (_splits.Count == 0 || sorted[i] >= _splits[^1].End)
                {
                    _splits.Add(plan.SplitOf(sorted[i]));
                }
            }

            _scan.Metrics?.AddWindow(_splits.Count);
            Diagnostics.VortexEventSource.Window(_splits.Count);
            return distinct;
        }

        /// <summary>Filters the decoded rows and gathers the survivors in the window's key order, then trims.</summary>
        private int Arrange(int root, int count, int distinct)
        {
            ReadOnlySpan<long> window = _rows.AsSpan(0, count);
            int[] permutation = ArrayPool<int>.Shared.Rent(count);
            byte[] states = ArrayPool<byte>.Shared.Rent(distinct);
            try
            {
                Span<byte> verdicts = states.AsSpan(0, distinct);
                if (_owner._filter is null)
                {
                    verdicts.Fill(Trilean.True);
                }
                else
                {
                    _owner._evaluator!.Evaluate(_context.Canonical, root, distinct, verdicts);
                }

                ReadOnlySpan<long> rows = _sorted.AsSpan(0, distinct);
                int kept = 0;
                foreach (long row in window)
                {
                    int at = IndexOf(rows, row);
                    if (verdicts[at] == Trilean.True)
                    {
                        permutation[kept++] = at;
                    }
                }

                // A window of a sorted column, read ascending with every row kept, is already in
                // key order, so it costs no gather at all.
                ReadOnlySpan<int> order = permutation.AsSpan(0, kept);
                if (kept != distinct || !IsIdentity(order))
                {
                    root = CanonicalFilter.Apply(_context.Canonical, root, order);
                }

                return ProjectionTrim.Apply(_context.Canonical, root, in _read, in _keep, _scan.Schema);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(permutation);
                ArrayPool<byte>.Shared.Return(states);
            }
        }

        /// <summary>Decodes the window's splits, on the lanes when there are any, into one root in file order.</summary>
        private async ValueTask<int> DecodeAsync(RowSelection selection)
        {
            int splits = _splits.Count;
            int groups = _lanes is null ? 1 : Math.Min(_lanes.Length, splits);
            return groups == 1
                ? await DecodeGroupAsync(_context, null, 0, splits, selection).ConfigureAwait(false)
                : await DecodeLanesAsync(groups, selection).ConfigureAwait(false);
        }

        /// <summary>
        /// The splits cut into <paramref name="groups"/> contiguous groups, one per lane. Its own
        /// method because its lambda captures the selection, and a captured parameter costs its
        /// closure at the entry of the method that declares it -- on the sequential path too.
        /// </summary>
        private async Task<int> DecodeLanesAsync(int groups, RowSelection selection)
        {
            int splits = _splits.Count;
            Task<int>[] work = new Task<int>[groups];
            for (int g = 0; g < groups; g++)
            {
                ScanContext lane = _lanes![g];
                GroupWaiter waiter = _waiters![g];
                int from = g * splits / groups;
                int to = (g + 1) * splits / groups;
                work[g] = Task.Run(() => DecodeGroupAsync(lane, waiter, from, to, selection).AsTask(), _token);
            }

            // Every lane is observed before a failure surfaces: an abandoned one would still be
            // decoding into a context the caller's disposal is about to reset.
            int[] roots = await Task.WhenAll(work).ConfigureAwait(false);
            for (int g = 0; g < groups; g++)
            {
                // Records, not bytes: the lane keeps the storage until the batch is released.
                roots[g] = _context.Canonical.ReferenceFrom(_lanes![g].Canonical, roots[g]);
            }

            return Concat(_context, roots);
        }

        /// <summary>
        /// Registers, reads and executes the splits in <c>[from, to)</c> in one context, reading
        /// only what the scan does not hold and waiting for what another group is reading.
        /// </summary>
        private async ValueTask<int> DecodeGroupAsync(ScanContext context, GroupWaiter? waiter, int from, int to, RowSelection selection)
        {
            LayoutTree tree = _scan.Tree;
            long ticket = _held.NextTicket();
            context.Batch = ticket;
            for (int i = from; i < to; i++)
            {
                SplitExecution.Register(context, tree, in _read, _splits[i]);
            }

            try
            {
                while (_held.Claim(context.Segments, ticket, waiter))
                {
                    await waiter!.ReadByOthersAsync().ConfigureAwait(false);
                }

                if (ScanMetrics.Note(_scan.Metrics, context.Segments))
                {
                    await _scan.File.Segments.ReadManyAsync(context.Segments, _token).ConfigureAwait(false);
                }
                else
                {
                    context.Segments.Complete();
                }

                _held.Publish(context.Segments, ticket);
            }
            catch
            {
                _held.Abandon(ticket);
                throw;
            }

            ScanMetrics.Served(_scan.Metrics, context.Segments);
            _token.ThrowIfCancellationRequested();
            int[] parts = ArrayPool<int>.Shared.Rent(to - from);
            try
            {
                for (int i = from; i < to; i++)
                {
                    // The plan's window, as the batch enumerator gives it: a later window of the
                    // key order that comes back to these splits finds the window still decoded.
                    SplitExecution.Window(context, _scan.Plan, _splits[i]);
                    parts[i - from] = SplitExecution.Execute(context, tree, in _read, _splits[i], selection);
                }

                return Concat(context, parts.AsSpan(0, to - from));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(parts);
            }
        }

        /// <summary>
        /// The position of a row the sorted rows hold. Written out: the span overload that takes a
        /// value binds to <c>IComparable&lt;long&gt;</c> and boxes it.
        /// </summary>
        private static int IndexOf(ReadOnlySpan<long> rows, long row)
        {
            int low = 0;
            int high = rows.Length - 1;
            while (low < high)
            {
                int mid = (int)(((uint)low + (uint)high) >> 1);
                if (rows[mid] < row)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private static bool IsIdentity(ReadOnlySpan<int> order)
        {
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] != i)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The parts, in order, as one node of the context.</summary>
        private static int Concat(ScanContext context, ReadOnlySpan<int> parts)
        {
            if (parts.Length == 1)
            {
                return parts[0];
            }

            int length = 0;
            foreach (int part in parts)
            {
                length = checked(length + context.Canonical.GetNode(part).Length);
            }

            return CanonicalConcat.Concat(context.Decode, context.Canonical.GetNode(parts[0]).DType, length, parts);
        }

        /// <summary>One group of a window, able to wait for a segment another group of it is reading.</summary>
        private sealed class GroupWaiter : SegmentWaiter
        {
        }
    }
}
