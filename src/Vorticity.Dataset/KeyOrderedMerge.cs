// The k-way merge of docs/13-dataset.md §5.3 and §6.6, shared by the compaction that writes it and
// the scan that reads it: several objects' key-ordered batches merged into one key order.
//
// THE MERGE EMITS RUNS, NOT ROWS. At every step one input holds the smallest key; the rows it may
// emit before another input takes over are the ones at or below the smallest key the others are
// holding. That is a contiguous window of its current batch, so the merge hands out windows of
// batches rather than rows: k comparisons per window instead of per row, and a consumer keeps
// receiving batches. `RecordBatch.Window` is the seam this needs; a whole batch goes through as it is.
//
// THE COMPARATOR IS THE ROW ENCODING, for one reason: it is the order the tree, the runs and the
// seeks already use (06's memcmp order), so a merge cannot disagree with the sources it merges. A
// per-dtype comparison written here would be a second order, and the first time the two differed
// the dataset would hold an object whose own run says it is sorted and whose rows are not.
//
// AN OBJECT IS OPENED WHEN IT COULD HOLD THE NEXT ROW, NOT BEFORE. The objects arrive in the order of
// a lower bound on their keys — the tree's own order on the clustering key, whose leaf key IS the
// encoded minimum (§4.1), or summary bounds sorted beforehand — and one is opened only once its bound
// is at or below the smallest key an open input holds. Two claims of §6.6 follow, and neither needs a
// line of code of its own. The inputs held open are the objects whose ranges reach the current key:
// all of level 0 at worst, and ONE PER LEVEL above it, since a level's objects are key-disjoint
// (§5.2) and the next one's minimum lies above everything the current one still holds — "≤ 8 + L
// cursors, bounded", held by §5.2's invariant rather than by this file. And a consumer that stops
// after k rows has opened no object whose bound lies past the k-th key: "an object whose min exceeds
// the current k-th best is skipped".
//
// TIES FOLLOW THE DATASET'S ORDER WHEN A READER ASKS. Each object carries a rank — its place in the
// walk, negated for a descending merge — and of two rows with one key the lower rank goes first: a
// file's order for its ties (12 §4.4) raised to objects, which makes a descending merge the exact
// reverse of an ascending one. A run then stops strictly before an unopened object's bound, whose
// rank against the objects behind it is not known. A compaction does not ask: ties may fall either
// way in a sorted output, and letting the emitting input run through them keeps its runs longest.
// Ranked ties cut a run at every key two interleaved inputs share, and on the randomised stream of
// the compaction tests that turned the first compaction's 493 rows into 3 objects and 21 641 bytes
// instead of 2 and 16 609.
//
// A BOUND THAT LIES IS REFUSED, NOT FOLLOWED. An object whose first row lies below the bound it was
// opened under would have its rows delivered after larger ones; the merge fails at that moment and
// names the object, rather than deliver an order that is wrong.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.RowEncoding;
using Vorticity.Scan;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>One object waiting to be merged.</summary>
/// <param name="Entry">Its leaf entry.</param>
/// <param name="Bound">
/// A lower bound on its keys in the merge's encoding; empty when none is known, which has it opened
/// before any row is emitted.
/// </param>
/// <param name="Rank">Where its ties go: of two rows with one key, the lower rank's comes first.</param>
internal readonly record struct MergeObject(ObjectEntry Entry, ReadOnlyMemory<byte> Bound, long Rank);

/// <summary>Objects' key-ordered batches merged into one key order, one run of rows at a time.</summary>
internal sealed class KeyOrderedMerge : IAsyncDisposable
{
    private readonly VortexDataset _dataset;
    private readonly IAsyncEnumerator<MergeObject> _objects;
    private readonly Func<VortexFile, ScanBuilder> _scan;
    private readonly Action<ObjectLease>? _opened;
    private readonly string[] _paths;
    private readonly RowSortField[] _fields;
    private readonly bool _rankedTies;
    private readonly CancellationToken _cancellationToken;
    private readonly List<MergeInput> _open = [];
    private bool _started;
    private bool _pending;
    private MergeInput? _emitting;
    private int _count;
    private RecordBatch? _window;
    private RecordBatch? _current;
    private bool _disposed;

    /// <summary>A merge over <paramref name="objects"/>, which arrive in the order of their bounds.</summary>
    /// <param name="dataset">Where the objects are rented from.</param>
    /// <param name="objects">The objects, by ascending bound.</param>
    /// <param name="paths">The key's columns, which every batch the scans deliver must carry.</param>
    /// <param name="descending">Whether the scans deliver the keys largest first.</param>
    /// <param name="rankedTies">
    /// Whether equal keys come in rank order, as a read promises; otherwise the emitting input runs
    /// through them, which is what a compaction wants.
    /// </param>
    /// <param name="scan">One object's key-ordered scan, on the same key and in the same direction.</param>
    /// <param name="opened">Told of every object opened, or null.</param>
    /// <param name="cancellationToken">Cancels the opens and the reads.</param>
    internal KeyOrderedMerge(
        VortexDataset dataset,
        IAsyncEnumerable<MergeObject> objects,
        IReadOnlyList<string> paths,
        bool descending,
        bool rankedTies,
        Func<VortexFile, ScanBuilder> scan,
        Action<ObjectLease>? opened,
        CancellationToken cancellationToken)
    {
        _dataset = dataset;
        _objects = objects.GetAsyncEnumerator(cancellationToken);
        _scan = scan;
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

    /// <summary>The most inputs held open at once — §6.6's bounded number, measured.</summary>
    internal int MostOpen { get; private set; }

    /// <summary>How one key column sorts in a merge in this direction.</summary>
    /// <param name="descending">Whether the largest key comes first.</param>
    /// <returns>The field; its encoding makes the smallest bytes come first either way.</returns>
    /// <remarks>
    /// NULLS LAST, in both directions: where the core's key-ordered read delivers a null key
    /// (`ScanBuilder.InKeyOrder`), and not the row encoding's own default, which puts them first. A
    /// non-null key encodes to the same bytes under either choice, so the leaf keys and the summary
    /// bounds the merge orders objects by -- non-null minima and maxima, encoded with the clustering
    /// key's default -- compare as they always have; and with nulls last, an object's non-null
    /// minimum stays a lower bound on every row it delivers.
    /// </remarks>
    internal static RowSortField Field(bool descending) =>
        RowSortField.Ascending.WithNullsLast().WithDescending(descending);

    /// <summary>Moves to the next run, opening what could hold it.</summary>
    /// <returns>Whether there is one.</returns>
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
            // The whole batch is the run: no window, no gather, the batch goes straight through.
            _current = batch;
        }
        else
        {
            _window = batch.Window(chosen.Row, count);
            _current = _window;
        }

        return true;
    }

    /// <inheritdoc/>
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
        foreach (MergeInput input in _open)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
        await _objects.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Steps the input that emitted the last run over it, and closes it when it is done.</summary>
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
        _window = null;
        if (!await input.AdvanceAsync(count).ConfigureAwait(false))
        {
            _open.Remove(input);
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

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
        for (int i = 1; i < _open.Count; i++)
        {
            if (_open[i].Key.SequenceCompareTo(smallest) < 0)
            {
                smallest = _open[i].Key;
            }
        }

        return _objects.Current.Bound.Span.SequenceCompareTo(smallest) <= 0;
    }

    /// <summary>Opens one object's scan and takes its first batch.</summary>
    private async ValueTask OpenAsync(MergeObject next)
    {
        ObjectLease lease = await _dataset.RentAsync(next.Entry, _cancellationToken).ConfigureAwait(false);
        _opened?.Invoke(lease);
        MergeInput input = new MergeInput(lease, next.Entry.Key, next.Rank, _paths, _fields);
        try
        {
            await input.StartAsync(_scan(lease.File), _cancellationToken).ConfigureAwait(false);
            if (input.IsLive && input.Key.SequenceCompareTo(next.Bound.Span) < 0)
            {
                throw new VortexFormatException(
                    $"'{next.Entry.Key}' holds a key below the bound it was ordered by: its summaries " +
                    "or its leaf key are not a lower bound on its rows, so a key-ordered read across " +
                    "objects would deliver them out of order (13 §6.6).");
            }
        }
        catch
        {
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!input.IsLive)
        {
            // Nothing survived its filter: it was opened, and it holds no row to merge.
            await input.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _open.Add(input);
        MostOpen = Math.Max(MostOpen, _open.Count);
    }

    /// <summary>The input holding the smallest key, and how many of its rows go before any other's.</summary>
    private (MergeInput Chosen, int Count) Choose()
    {
        // ONE PASS, BECAUSE FETCHING A KEY COSTS MORE THAN COMPARING TWO. A row's encoded key is
        // validated and rebuilt on every read -- about 1,1 ns against 1,0 to compare a pair -- and
        // choosing in one pass and bounding in another read each key three times over. Here each
        // is read once and kept in a local, which is two thirds of the fetching gone.
        //
        // The rows the chosen input may emit are those below the smallest key another input holds,
        // and at it when that input ranks after this one; strictly below an unopened object's
        // bound, whose rank against the objects behind it is not known. Unranked, a tie goes to
        // whoever is emitting. That "at it when they all rank after" is an AND over the inputs
        // sharing the smallest other key, which is the same as asking whether the LOWEST rank among
        // them ranks after the chosen one -- and that can be tracked while the chosen input is
        // still moving, where the flag itself could not.
        MergeInput chosen = _open[0];
        ReadOnlySpan<byte> chosenKey = chosen.Key;
        ReadOnlySpan<byte> limit = default;
        long limitRank = 0;
        bool bounded = false;
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

        if (_pending)
        {
            // A bound carries no rank, and a run stops strictly before it when ranks are asked
            // for: the lowest rank there is says exactly that, and says nothing when they are not.
            Tighten(ref limit, ref limitRank, ref bounded, _objects.Current.Bound.Span, long.MinValue);
        }

        bool inclusive = !_rankedTies || limitRank > chosen.Rank;

        int count = bounded ? chosen.RunUpTo(limit, inclusive) : chosen.Remaining;
        if (count <= 0)
        {
            // The smallest key is below every other input's and below every unopened bound, so its
            // row always goes: a run of nothing would be a merge that never ends.
            throw new InvalidOperationException(
                $"The merge chose '{chosen.Object}' and could emit none of its rows (13 §6.6).");
        }

        return (chosen, count);
    }

    /// <summary>Lowers the run's limit to <paramref name="key"/> when it is the smaller one.</summary>
    /// <param name="limit">The smallest key seen so far among the inputs that are not emitting.</param>
    /// <param name="limitRank">The lowest rank among those holding it.</param>
    /// <param name="bounded">Whether any of them has been seen yet.</param>
    /// <param name="key">The key to lower the limit to.</param>
    /// <param name="rank">Its holder's rank.</param>
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

        internal MergeInput(ObjectLease lease, string key, long rank, string[] paths, RowSortField[] fields)
        {
            _lease = lease;
            Object = key;
            Rank = rank;
            _paths = paths;
            _fields = fields;
        }

        /// <summary>The object's key in the store, for a message.</summary>
        internal string Object { get; }

        /// <summary>Where its ties go.</summary>
        internal long Rank { get; }

        /// <summary>Whether it still holds a row.</summary>
        internal bool IsLive => _batch is not null;

        /// <summary>Its current batch.</summary>
        internal RecordBatch Batch => _batch!;

        /// <summary>Its current row within its current batch.</summary>
        internal int Row => _row;

        /// <summary>The rows left in its current batch.</summary>
        internal int Remaining => _batch is null ? 0 : _rows - _row;

        /// <summary>Its current key, row-encoded (06).</summary>
        internal ReadOnlySpan<byte> Key => _keys!.Row(_row);

        /// <summary>The encoded key of one row of its current batch.</summary>
        /// <param name="row">The row, within the batch.</param>
        /// <returns>Its key.</returns>
        internal ReadOnlySpan<byte> KeyAt(int row) => _keys!.Row(row);

        /// <summary>Starts the object's scan and takes its first batch.</summary>
        /// <param name="scan">The object's key-ordered scan.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        internal ValueTask StartAsync(ScanBuilder scan, CancellationToken cancellationToken)
        {
            _batches = scan.ExecuteAsync().GetAsyncEnumerator(cancellationToken);
            return NextAsync();
        }

        /// <summary>How many rows from here on lie below <paramref name="limit"/>, or at it.</summary>
        /// <param name="limit">The smallest key another input holds, or an unopened object's bound.</param>
        /// <param name="inclusive">Whether a row at <paramref name="limit"/> goes too.</param>
        /// <returns>The run's length.</returns>
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

        /// <summary>Steps over <paramref name="count"/> rows, taking the next batch after the last.</summary>
        /// <param name="count">The run just emitted.</param>
        /// <returns>Whether a row is left.</returns>
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

        /// <summary>Takes the next batch and row-encodes its keys.</summary>
        /// <remarks>
        /// A key of several columns has no ordering to compare in place: the merge needs one value
        /// per row that sorts the way the key does, and encoding the row is what produces it.
        /// </remarks>
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

        /// <summary>The canonical node of each key column of a batch.</summary>
        private static int[] Columns(RecordBatch batch, IReadOnlyList<string> paths)
        {
            int[] columns = new int[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                DType at = batch.Schema;
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
