using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// One object waiting to be merged. <c>Bound</c> is a lower bound on its keys in the merge's
/// encoding, empty when none is known, which has it opened before any row is emitted; of two rows
/// with one key, the lower <c>Rank</c> comes first. <c>FirstRow</c> is where its rows start among
/// the dataset's, which its runs' rows are counted from.
/// </summary>
internal readonly record struct MergeObject(ObjectEntry Entry, ReadOnlyMemory<byte> Bound, long Rank, long FirstRow = 0);

/// <summary>
/// Objects' key-ordered batches merged into one key order, one contiguous run of a batch at a time.
/// Rows compare by their row encoding, the same order the tree and the seeks use, so the merge
/// cannot disagree with the sources it merges; an object is opened only once its bound is at or
/// below the smallest key an open input holds. Past a few open inputs they are a heap by key and
/// rank, so a run's input is the top and the key that ends its run is the smaller of the top's two
/// children: a run costs the logarithm of the inputs open rather than passes over all of them.
/// </summary>
internal sealed class KeyOrderedMerge : IAsyncDisposable
{
    private readonly DatasetSnapshot _version;
    private readonly IAsyncEnumerator<MergeObject> _objects;
    private readonly Func<ObjectLease, ObjectEntry, IAsyncEnumerable<RecordBatch>> _batches;
    private readonly Action<ObjectLease>? _opened;
    private readonly string[] _paths;
    private readonly RowSortField[] _fields;
    private readonly bool _rankedTies;
    private readonly CancellationToken _cancellationToken;

    // The open inputs; past LinearOpen of them, a binary min-heap by current key, then rank.
    private readonly List<MergeInput> _open = [];
    private bool _started;
    private bool _pending;
    private MergeInput? _emitting;
    private int _count;

    // The run's window when the run is part of a batch, and once the run is stepped past, disposed
    // and kept for the next window to bind again: one object for the merge rather than one per run.
    private RecordBatch? _window;

    // The arena the run's window is cut into, made at the first window and reset once the run is
    // stepped past. Cut into the batch's own arena, the windows' records would stay there until the
    // batch is released: a handful a run, and a run a row where keys interleave row by row.
    private CanonicalArena? _windows;

    private RecordBatch? _current;
    private bool _disposed;

    /// <summary>
    /// A merge over <paramref name="objects"/>, which must arrive by ascending bound, and whose
    /// batches, from <paramref name="batches"/>, must all deliver <paramref name="paths"/> in the same
    /// direction and gathered, a batch holding only the rows it delivers. With
    /// <paramref name="rankedTies"/>, equal keys come in rank order as a read promises; without it
    /// the emitting input runs through them, which keeps a compaction's runs longest.
    /// </summary>
    internal KeyOrderedMerge(
        DatasetSnapshot version,
        IAsyncEnumerable<MergeObject> objects,
        IReadOnlyList<string> paths,
        bool descending,
        bool rankedTies,
        Func<ObjectLease, ObjectEntry, IAsyncEnumerable<RecordBatch>> batches,
        Action<ObjectLease>? opened,
        CancellationToken cancellationToken)
    {
        _version = version;
        _objects = objects.GetAsyncEnumerator(cancellationToken);
        _batches = batches;
        _opened = opened;
        _paths = [.. paths];
        _fields = new RowSortField[_paths.Length];
        Array.Fill(_fields, Field(descending));
        _rankedTies = rankedTies;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The run the merge is on: an input's batch, or a window of it.</summary>
    /// <exception cref="InvalidOperationException">The merge is not on a run.</exception>
    internal RecordBatch Current => _current ?? throw new InvalidOperationException("The merge is not on a run.");

    /// <summary>The run's first key, row-encoded.</summary>
    internal ReadOnlySpan<byte> FirstKey
    {
        get
        {
            MergeInput input = Emitting();
            return input.KeyAt(input.Row);
        }
    }

    /// <summary>The run's last key, row-encoded.</summary>
    internal ReadOnlySpan<byte> LastKey
    {
        get
        {
            MergeInput input = Emitting();
            return input.KeyAt(input.Row + _count - 1);
        }
    }

    /// <summary>The most inputs held open at once.</summary>
    internal int MostOpen { get; private set; }

    /// <summary>Where the rows of the object the run comes from start among the dataset's.</summary>
    internal long CurrentFirstRow => Emitting().FirstRow;

    /// <summary>
    /// How one key column sorts in a merge in this direction. Nulls go last in both directions,
    /// matching where the key-ordered read puts them, so an object's non-null minimum stays a lower
    /// bound on every row it delivers.
    /// </summary>
    internal static RowSortField Field(bool descending) =>
        RowSortField.Ascending.WithNullsLast().WithDescending(descending);

    /// <summary>Moves to the next run, opening what could hold it.</summary>
    internal async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StepAsync().ConfigureAwait(false);
        if (!_started)
        {
            _started = true;
            _pending = await _objects.MoveNextAsync().ConfigureAwait(false);
        }

        while (MayHoldNext())
        {
            MergeObject next = _objects.Current;
            _pending = await _objects.MoveNextAsync().ConfigureAwait(false);
            await OpenAsync(next).ConfigureAwait(false);
        }

        if (_open.Count == 0)
        {
            return false;
        }

        (MergeInput chosen, int count) = Choose();
        _emitting = chosen;
        _count = count;
        RecordBatch batch = chosen.Batch;
        if (chosen.Row == 0 && count == batch.RowCount)
        {
            _current = batch;
        }
        else
        {
            _windows ??= new CanonicalArena();
            _window = batch.Window(chosen.Row, count, _windows, _window);
            _current = _window;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current = null;
        _emitting = null;
        _window?.Dispose();
        _window = null;
        _windows?.Reset();
        _windows = null;
        foreach (MergeInput input in _open)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
        await _objects.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask StepAsync()
    {
        if (_emitting is not { } input)
        {
            return;
        }

        int count = _count;
        _emitting = null;
        _count = 0;
        _current = null;
        _window?.Dispose();
        _windows?.Reset();

        // Over the heap, the run's input is its top: it sinks to its new key, or leaves.
        bool heaped = Heaped;
        if (await input.AdvanceAsync(count).ConfigureAwait(false))
        {
            if (heaped)
            {
                SiftDown(0);
            }
        }
        else
        {
            if (heaped)
            {
                RemoveTop();
            }
            else
            {
                _open.Remove(input);
            }

            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The most inputs the merge looks over in full rather than keeps in a heap: a pass over this
    /// few costs less than sifting its keys.
    /// </summary>
    private const int LinearOpen = 8;

    /// <summary>Whether the open inputs are ordered as a heap, which they are past <see cref="LinearOpen"/>.</summary>
    private bool Heaped => _open.Count > LinearOpen;

    /// <summary>Whether the next unopened object could hold a key at or below every open input's.</summary>
    private bool MayHoldNext()
    {
        if (!_pending)
        {
            return false;
        }

        if (_open.Count == 0)
        {
            return true;
        }

        ReadOnlySpan<byte> smallest = _open[0].Key;
        if (!Heaped)
        {
            for (int i = 1; i < _open.Count; i++)
            {
                if (_open[i].Key.SequenceCompareTo(smallest) < 0)
                {
                    smallest = _open[i].Key;
                }
            }
        }

        return _objects.Current.Bound.Span.SequenceCompareTo(smallest) <= 0;
    }

    private async ValueTask OpenAsync(MergeObject next)
    {
        ObjectLease lease = await _version.RentAsync(next.Entry, _cancellationToken).ConfigureAwait(false);
        _opened?.Invoke(lease);
        MergeInput input = new MergeInput(lease, next.Entry.Key, next.Rank, next.FirstRow, _paths, _fields);
        try
        {
            await input.StartAsync(_batches(lease, next.Entry), _cancellationToken).ConfigureAwait(false);
            if (input.IsLive && input.Key.SequenceCompareTo(next.Bound.Span) < 0)
            {
                throw new VortexFormatException(
                    $"'{next.Entry.Key}' holds a key below the bound it was ordered by: its summaries " +
                    "or its leaf key are not a lower bound on its rows, so a key-ordered read across " +
                    "objects would deliver them out of order.");
            }
        }
        catch
        {
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!input.IsLive)
        {
            await input.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Push(input);
        MostOpen = Math.Max(MostOpen, _open.Count);
    }

    /// <summary>The input holding the smallest key, and how many of its rows go before any other's.</summary>
    private (MergeInput Chosen, int Count) Choose()
    {
        // The run ends at the smallest key another input holds, including it only when that input
        // ranks after the chosen one -- which is the same as asking whether the lowest rank among
        // the inputs sharing that key ranks after it.
        MergeInput chosen = _open[0];
        ReadOnlySpan<byte> limit = default;
        long limitRank = 0;
        bool bounded = false;
        if (Heaped)
        {
            // The chosen input is the top, and the smallest key another holds is the smaller of
            // the top's children, with the lowest rank among its holders: the heap orders ties by
            // rank.
            MergeInput left = _open[1];
            limit = left.Key;
            limitRank = left.Rank;
            bounded = true;
            MergeInput right = _open[2];
            ReadOnlySpan<byte> rightKey = right.Key;
            if (Before(rightKey, right.Rank, limit, limitRank))
            {
                limit = rightKey;
                limitRank = right.Rank;
            }
        }
        else
        {
            // One pass, keeping each key in a local: fetching a key costs more than comparing two,
            // and choosing then bounding separately would read every key three times over.
            ReadOnlySpan<byte> chosenKey = chosen.Key;
            for (int i = 1; i < _open.Count; i++)
            {
                MergeInput input = _open[i];
                ReadOnlySpan<byte> key = input.Key;
                int order = key.SequenceCompareTo(chosenKey);
                if (order < 0 || (order == 0 && input.Rank < chosen.Rank))
                {
                    // The input it displaces becomes one of the others, and bounds the run like them.
                    Tighten(ref limit, ref limitRank, ref bounded, chosenKey, chosen.Rank);
                    chosen = input;
                    chosenKey = key;
                }
                else
                {
                    Tighten(ref limit, ref limitRank, ref bounded, key, input.Rank);
                }
            }
        }

        if (_pending)
        {
            // A bound carries no rank, and a run stops strictly before it when ranks are asked for:
            // the lowest rank there is says exactly that, and nothing when they are not.
            Tighten(ref limit, ref limitRank, ref bounded, _objects.Current.Bound.Span, long.MinValue);
        }

        bool inclusive = !_rankedTies || limitRank > chosen.Rank;

        int count = bounded ? chosen.RunUpTo(limit, inclusive) : chosen.Remaining;
        if (count <= 0)
        {
            throw new InvalidOperationException(
                $"The merge chose '{chosen.Object}' and could emit none of its rows.");
        }

        return (chosen, count);
    }

    /// <summary>
    /// Lowers the run's limit to <paramref name="key"/> when it is the smaller one, keeping the
    /// lowest rank among the holders of the limit.
    /// </summary>
    private static void Tighten(
        ref ReadOnlySpan<byte> limit, ref long limitRank, ref bool bounded, ReadOnlySpan<byte> key, long rank)
    {
        int order = bounded ? key.SequenceCompareTo(limit) : -1;
        if (order < 0)
        {
            limit = key;
            limitRank = rank;
            bounded = true;
        }
        else if (order == 0 && rank < limitRank)
        {
            limitRank = rank;
        }
    }

    private MergeInput Emitting() =>
        _emitting ?? throw new InvalidOperationException("The merge is not on a run.");

    private void Push(MergeInput input)
    {
        _open.Add(input);
        if (_open.Count == LinearOpen + 1)
        {
            // Past the inputs looked over in full: the list becomes a heap, bottom up.
            for (int node = (_open.Count / 2) - 1; node >= 0; node--)
            {
                SiftDown(node);
            }

            return;
        }

        if (!Heaped)
        {
            return;
        }

        ReadOnlySpan<byte> key = input.Key;
        int at = _open.Count - 1;
        while (at > 0)
        {
            int parent = (at - 1) >> 1;
            MergeInput above = _open[parent];
            if (!Before(key, input.Rank, above.Key, above.Rank))
            {
                break;
            }

            _open[at] = above;
            at = parent;
        }

        _open[at] = input;
    }

    private void RemoveTop()
    {
        int last = _open.Count - 1;
        _open[0] = _open[last];
        _open.RemoveAt(last);
        if (_open.Count > 0)
        {
            SiftDown(0);
        }
    }

    private void SiftDown(int at)
    {
        // Each key is read once a level: reading one costs more than comparing two.
        MergeInput input = _open[at];
        ReadOnlySpan<byte> key = input.Key;
        int count = _open.Count;
        while (true)
        {
            int child = (2 * at) + 1;
            if (child >= count)
            {
                break;
            }

            MergeInput smaller = _open[child];
            ReadOnlySpan<byte> smallerKey = smaller.Key;
            if (child + 1 < count)
            {
                MergeInput right = _open[child + 1];
                ReadOnlySpan<byte> rightKey = right.Key;
                if (Before(rightKey, right.Rank, smallerKey, smaller.Rank))
                {
                    child++;
                    smaller = right;
                    smallerKey = rightKey;
                }
            }

            if (!Before(smallerKey, smaller.Rank, key, input.Rank))
            {
                break;
            }

            _open[at] = smaller;
            at = child;
        }

        _open[at] = input;
    }

    /// <summary>Whether a row of key <paramref name="left"/> comes before one of key <paramref name="right"/>: by key, a tie to the lower rank.</summary>
    private static bool Before(ReadOnlySpan<byte> left, long leftRank, ReadOnlySpan<byte> right, long rightRank)
    {
        int order = left.SequenceCompareTo(right);
        return order < 0 || (order == 0 && leftRank < rightRank);
    }

    /// <summary>One input of the merge: an object's batches in key order, and their keys row-encoded.</summary>
    private sealed class MergeInput : IAsyncDisposable
    {
        private readonly ObjectLease _lease;
        private readonly string[] _paths;
        private readonly RowSortField[] _fields;
        private IAsyncEnumerator<RecordBatch>? _batches;
        private RecordBatch? _batch;
        private RowKeys? _keys;
        private int _row;

        // Kept apart from the batch, which is handed out whole when a run is all of it: a consumer
        // may dispose what it was given, and stepping past it must not read it again.
        private int _rows;

        internal MergeInput(ObjectLease lease, string key, long rank, long firstRow, string[] paths, RowSortField[] fields)
        {
            _lease = lease;
            Object = key;
            Rank = rank;
            FirstRow = firstRow;
            _paths = paths;
            _fields = fields;
        }

        /// <summary>The object's key in the store, for a message.</summary>
        internal string Object { get; }

        /// <summary>Where its ties go.</summary>
        internal long Rank { get; }

        /// <summary>Where the object's rows start among the dataset's.</summary>
        internal long FirstRow { get; }

        internal bool IsLive => _batch is not null;

        internal RecordBatch Batch => _batch!;

        internal int Row => _row;

        /// <summary>The rows left in its current batch.</summary>
        internal int Remaining => _batch is null ? 0 : _rows - _row;

        /// <summary>Its current key, row-encoded.</summary>
        internal ReadOnlySpan<byte> Key => _keys!.Row(_row);

        internal ReadOnlySpan<byte> KeyAt(int row) => _keys!.Row(row);

        internal ValueTask StartAsync(IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken)
        {
            _batches = batches.GetAsyncEnumerator(cancellationToken);
            return NextAsync();
        }

        /// <summary>
        /// How many rows from here on lie below <paramref name="limit"/>, or at it when
        /// <paramref name="inclusive"/>.
        /// </summary>
        internal int RunUpTo(ReadOnlySpan<byte> limit, bool inclusive)
        {
            int end = _row;
            while (end < _rows)
            {
                int order = _keys!.Row(end).SequenceCompareTo(limit);
                if (order > 0 || (order == 0 && !inclusive))
                {
                    break;
                }

                end++;
            }

            return end - _row;
        }

        /// <summary>
        /// Steps over the <paramref name="count"/> rows just emitted, taking the next batch after
        /// the last, and says whether a row is left.
        /// </summary>
        internal async ValueTask<bool> AdvanceAsync(int count)
        {
            _row += count;
            if (_row >= _rows)
            {
                await NextAsync().ConfigureAwait(false);
            }

            return IsLive;
        }

        public async ValueTask DisposeAsync()
        {
            _keys?.Dispose();
            _keys = null;
            _batch = null;
            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
                _batches = null;
            }

            await _lease.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Takes the next batch and row-encodes its keys: a key of several columns has no ordering
        /// to compare in place, and the encoding gives one value per row that sorts as the key does.
        /// </summary>
        private async ValueTask NextAsync()
        {
            _keys?.Dispose();
            _keys = null;
            _batch = null;
            _row = 0;
            _rows = 0;
            while (await _batches!.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = _batches.Current;
                if (batch.RowCount == 0)
                {
                    continue;
                }

                _batch = batch;
                _rows = batch.RowCount;
                _keys = RowEncoder.Encode(batch.Arena, Columns(batch, _paths), _fields);
                return;
            }
        }

        private static int[] Columns(RecordBatch batch, IReadOnlyList<string> paths)
        {
            int[] columns = new int[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                DType at = batch.DType;
                int node = batch.RootIndex;
                foreach (string segment in paths[i].Split('.'))
                {
                    int field = at.IndexOfField(segment);
                    if (field < 0)
                    {
                        throw new ArgumentException(
                            $"'{paths[i]}' names no column of the batch's schema.", nameof(paths));
                    }

                    node = batch.Arena.GetNode(node).GetFieldIndex(field);
                    at = at.GetField(field);
                }

                columns[i] = node;
            }

            return columns;
        }
    }
}
