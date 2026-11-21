using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Writing;

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
internal sealed class KeyOrderedMerge : IMergedRuns
{
    private readonly DatasetSnapshot _version;
    private readonly IAsyncEnumerator<MergeObject> _objects;
    private readonly Func<ObjectLease, ObjectEntry, BatchHandover?, IAsyncEnumerable<RecordBatch>> _batches;
    private readonly Action<ObjectLease>? _opened;
    private readonly SortKey _key;
    private readonly bool _rankedTies;
    private readonly CancellationToken _cancellationToken;
    private readonly ReadOnlyMemory<byte> _low;
    private readonly ReadOnlyMemory<byte> _high;

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

    // The windows the inputs moved past, in that order, each given back once the ledger says no writer reads it; the
    // inputs done, disposed once their last window is given back; and the windows given back, with their keys' buffers,
    // for the next batches: a level's objects, a batch or two each, rented, zeroed and gave back three arrays an object.
    private readonly Queue<Window> _retired = new();
    private readonly List<MergeInput> _ending = [];
    private readonly Stack<Window> _spareWindows = new();

    /// <summary>
    /// A merge over <paramref name="objects"/>, which must arrive by ascending bound, and whose
    /// batches, from <paramref name="batches"/>, must all deliver the sources of <paramref name="key"/> in its
    /// direction and gathered, a batch holding only the rows it delivers. With
    /// <paramref name="rankedTies"/>, equal keys come in rank order as a read promises; without it
    /// the emitting input runs through them, which keeps a compaction's runs longest. With <paramref name="low"/> or
    /// <paramref name="high"/>, the merge holds the rows from the one to before the other alone, each a key in the
    /// merge's encoding that may be a leading part of one: every input skips its rows below the first and ends at the
    /// second.
    /// </summary>
    internal KeyOrderedMerge(
        DatasetSnapshot version,
        IAsyncEnumerable<MergeObject> objects,
        SortKey key,
        bool rankedTies,
        Func<ObjectLease, ObjectEntry, BatchHandover?, IAsyncEnumerable<RecordBatch>> batches,
        Action<ObjectLease>? opened,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> low = default,
        ReadOnlyMemory<byte> high = default)
    {
        _low = low;
        _high = high;
        _version = version;
        _objects = objects.GetAsyncEnumerator(cancellationToken);
        _batches = batches;
        _opened = opened;
        _key = key;
        _rankedTies = rankedTies;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Whom the windows of the merge's runs are counted for: the question's account, or a budget's for a job, the merge
    /// giving them back as it ends.
    /// </summary>
    internal required Vorticity.Buffers.MemoryAccount Account { get; init; }

    /// <summary>
    /// Where an input is opened from: a compaction's copies in its scratch (<see cref="JobMemory.RentAsync"/>); null for the
    /// version's own objects.
    /// </summary>
    internal Func<ObjectEntry, CancellationToken, ValueTask<ObjectLease>>? Rent { get; init; }

    /// <summary>
    /// The rows emitted of each input, by its rank: what a piece of a consolidation took of each input, which it marks once
    /// its objects are committed; null when nothing counts them. This merge's alone: no other merge adds to it at once.
    /// </summary>
    internal long[]? Emitted { get; init; }

    /// <summary>
    /// The runs the merge's writers keep where they lie in its inputs' batches (<see cref="CurrentRun"/>): with it, each
    /// input's batch is a window the merge keeps past the input's next one, until every run noted on it is copied, and
    /// its keys are encoded by the decode that made it. Null for a merge whose caller is done with a run at the next step.
    /// </summary>
    internal RunLedger? Ledger { get; init; }

    /// <summary>
    /// The windows the inputs decode ahead past their own two, shared by the job's merges (<see cref="Scanning.DecodeAhead"/>):
    /// the input the merge drains decodes on several lanes. Null for none; it needs a <see cref="Ledger"/>, whose inputs hand
    /// their batches over.
    /// </summary>
    internal Scanning.DecodeAhead? Ahead { get; init; }

    /// <summary>The run the merge is on: an input's batch, or a window of it, cut when asked for.</summary>
    /// <exception cref="InvalidOperationException">The merge is not on a run.</exception>
    internal RecordBatch Current
    {
        get
        {
            if (_current is null && _emitting is { } chosen)
            {
                RecordBatch batch = chosen.Batch;
                if (chosen.Row == 0 && _count == batch.RowCount)
                {
                    _current = batch;
                }
                else
                {
                    _windows ??= new Vorticity.Arrays.CanonicalArena(64, Account);
                    _window = batch.Window(chosen.Row, _count, _windows, _window);
                    _current = _window;
                }
            }

            return _current ?? throw new InvalidOperationException("The merge is not on a run.");
        }
    }

    /// <summary>The rows of the run the merge is on.</summary>
    public int CurrentRows => _emitting is not null ? _count : throw new InvalidOperationException("The merge is not on a run.");

    /// <summary>
    /// Where the run the merge is on lies: the arena and the root of its input's batch, its first row there, and the bytes a
    /// row of the batch names on average. Valid, under a <see cref="Ledger"/>, until the ledger says every run noted on the
    /// batch is copied; else until the next step.
    /// </summary>
    public (Vorticity.Arrays.CanonicalArena Arena, int Root, int Row, double RowBytes) CurrentRun
    {
        get
        {
            MergeInput input = Emitting();
            Window window = input.Current;
            return (window.Batch!.Arena, window.Batch.RootIndex, input.Row, window.RowBytes());
        }
    }

    /// <summary>The window of the input's batch the run lies in, and the run's first row there: what a slice hands on.</summary>
    internal (Window Window, int Row) CurrentWindow
    {
        get
        {
            MergeInput input = Emitting();
            return (input.Current, input.Row);
        }
    }

    /// <summary>
    /// What the merge calls as an input moves past a window it took runs of, for a merge whose runs another flow takes later
    /// (<see cref="SlicedMerge"/>): that flow says when it has taken every run of the window, by setting the window's
    /// <see cref="Window.CitedUpTo"/> to the runs the ledger had noted then; the merge gives the window back once those are
    /// copied. Null for a merge whose caller takes each run as it comes.
    /// </summary>
    internal Action<Window>? Retiring { get; init; }

    /// <summary>The key of the run's row <paramref name="row"/>, row-encoded.</summary>
    public ReadOnlySpan<byte> KeyAt(int row)
    {
        MergeInput input = Emitting();
        return input.KeyAt(input.Row + row);
    }

    /// <summary>The most inputs held open at once.</summary>
    internal int MostOpen { get; private set; }

    /// <summary>The inputs' batches taken over from their scans, under a <see cref="Ledger"/>.</summary>
    internal long WindowsTaken { get; private set; }

    /// <summary>The inputs' batches copied to be kept, under a <see cref="Ledger"/>: not their scan's own.</summary>
    internal long WindowsCopied { get; private set; }

    /// <summary>The inputs' batches whose keys the merge's thread encoded, under a <see cref="Ledger"/>: none their decode did.</summary>
    internal long WindowsKeyedHere { get; private set; }

    /// <summary>The key in the store of the object the run comes from.</summary>
    internal string CurrentObject => Emitting().Object;

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
    public async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StepAsync().ConfigureAwait(false);
        await GiveBackCopiedAsync().ConfigureAwait(false);
        if (!_started)
        {
            _started = true;
            _pending = await _objects.MoveNextAsync().ConfigureAwait(false);
        }

        while (MayHoldNext())
        {
            MergeObject next = _objects.Current;
            _pending = await _objects.MoveNextAsync().ConfigureAwait(false);

            // Inputs done still wait for their windows, which a writer reads: the caller has its writers let go of them when
            // the input to open does not hold beside them, and they go first.
            if (_ending.Count > 0 && LetGo is { } letGo)
            {
                await letGo(next.Entry).ConfigureAwait(false);
                await GiveBackCopiedAsync().ConfigureAwait(false);
            }

            await OpenAsync(next).ConfigureAwait(false);
        }

        if (_open.Count == 0)
        {
            return false;
        }

        (MergeInput chosen, int count) = Choose();
        _emitting = chosen;
        _count = count;
        // The merge's own count: a range of a piece counts into one of its own, added to the piece's once the range is written,
        // and a merge in slices counts none, so no other merge adds to it, and a run costs no fence.
        if (Emitted is { } emitted && (ulong)chosen.Rank < (ulong)emitted.Length)
        {
            emitted[chosen.Rank] += count;
        }
        // The run's window is cut when a caller asks for it (Current): a writer that takes the run where it lies
        // (CurrentRun) needs none.
        _current = null;
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

        // Every window goes back, read by no writer any more: the merge's caller has its writers let go of the inputs
        // before it ends the merge, or has given them up.
        while (_retired.TryDequeue(out Window? retired))
        {
            await GiveBackAsync(retired).ConfigureAwait(false);
        }

        foreach (MergeInput input in _open)
        {
            input.Ended = true;
            if (input.TakeCurrent() is { } current)
            {
                await GiveBackAsync(current).ConfigureAwait(false);
            }

            if (input.LiveWindows == 0)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }

        _open.Clear();
        foreach (MergeInput input in _ending)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }

        _ending.Clear();
        while (_spareWindows.TryPop(out Window? spare))
        {
            spare.OwnKeys?.Dispose();
        }

        _shapeState?.Dispose();
        _shapeState = null;
        await _objects.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The window of <paramref name="input"/>'s next batch, <paramref name="batch"/>: taken over from its scan, its keys
    /// encoded by the decode that made it, under a <see cref="Ledger"/>; copied into a context of its own when the batch is
    /// not the scan's, an input of deleted rows or of another schema; else the batch as it is, valid until the next one.
    /// </summary>
    private Window OpenWindow(MergeInput input, RecordBatch batch, ObjectLease lease)
    {
        Window window = _spareWindows.TryPop(out Window? spare) ? spare : new Window();
        window.Input = input;
        input.LiveWindows++;
        if (input.Handover is { } handover && handover.TryTake(batch, out object? prepared, out RecordBatch? taken))
        {
            WindowsTaken++;
            window.Taken = taken;
            if (ReferenceEquals(taken, batch) && prepared is WindowKeys made)
            {
                // Shaped, its keys encoded and its bytes counted, by its decode, on its lane: the lane's view is the window's
                // until it is given back. No row left of it, it is an empty window.
                window.Batch = made.View is { RowCount: > 0 } view ? view : null;
                window.Keys = made.Keys;
                window.Bytes = made.RowBytes;
                return window;
            }

            // A view a wrapper made of the scan's batch in its arena: a view of the window's own, since the wrapper binds its
            // own again at its next step.
            window.Batch = window.View = RecordBatch.Over(batch.Arena, batch.RootIndex, batch.StartRow, window.View);
        }
        else if (Ledger is not null)
        {
            // Not its scan's own: shaped here, then copied to be kept.
            WindowsCopied++;
            RecordBatch? shaped = input.Handover?.State is InputShaping shaping ? shaping.Shape(batch, _shapeState ??= new InputShapeState()) : batch;
            if (shaped is not { RowCount: > 0 })
            {
                return window;
            }

            Vorticity.Arrays.ScanContext copy = Vorticity.Arrays.ScanContexts.Shared.Rent(lease.File, Account);
            window.Copy = copy;
            window.Batch = new RecordBatch(copy, copy.Canonical.CopyFrom(shaped.Arena, shaped.RootIndex), shaped.StartRow);
        }
        else
        {
            window.Batch = batch;
        }

        if (Ledger is not null)
        {
            WindowsKeyedHere++;
        }

        window.Keys = window.OwnKeys = _key.Encode(window.Batch, window.OwnKeys);
        return window;
    }

    // What the merge shapes an input's batch with when its scan did not, the batch not taken over.
    private InputShapeState? _shapeState;

    /// <summary>
    /// What a merge's input adds to each batch its scan decodes, on the lane that decodes it (<see cref="BatchHandover"/>):
    /// the batch shaped as <paramref name="shaping"/> says, its keys, and the bytes a row names, given what the lane's
    /// batch before had.
    /// </summary>
    private object? Prepare(RecordBatch batch, object? previous, InputShaping? shaping)
    {
        WindowKeys made = previous as WindowKeys ?? new WindowKeys();
        RecordBatch? view = shaping is null ? batch : shaping.Shape(batch, made);
        made.View = view;
        if (view is null || view.RowCount == 0)
        {
            return made;
        }

        made.Keys = _key.Encode(view, made.Keys);
        made.RowBytes = Window.BytesPerRow(view);
        return made;
    }

    /// <summary>
    /// Sets <paramref name="window"/>, which its input moved past, aside until the ledger says every run noted on it so far is
    /// copied; given back at once without a ledger, or when it cites nothing.
    /// </summary>
    private ValueTask RetireAsync(Window window, bool cited)
    {
        if (Ledger is not { } ledger || !cited)
        {
            return GiveBackAsync(window);
        }

        // Its runs taken later by another flow, which dates them as it takes the last: until then, no number covers them.
        if (Retiring is { } retiring)
        {
            window.CitedUpTo = long.MaxValue;
            _retired.Enqueue(window);
            retiring(window);
            return ValueTask.CompletedTask;
        }

        window.CitedUpTo = ledger.Noted;
        _retired.Enqueue(window);
        return ValueTask.CompletedTask;
    }

    /// <summary>Gives back the windows set aside whose runs the writers have all copied, oldest first.</summary>
    private async ValueTask GiveBackCopiedAsync()
    {
        if (Ledger is not { } ledger || _retired.Count == 0)
        {
            return;
        }

        long copied = ledger.Copied;
        while (_retired.TryPeek(out Window? oldest) && Volatile.Read(ref oldest.CitedUpTo) <= copied)
        {
            _retired.Dequeue();
            await GiveBackAsync(oldest).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives <paramref name="window"/> back: its batch to its scan, or its copy's context to the pool; and disposes its input
    /// once the input is done and holds no window any more.
    /// </summary>
    private async ValueTask GiveBackAsync(Window window)
    {
        MergeInput input = window.Input!;
        if (window.Taken is { } taken)
        {
            // A view of the window's own goes; one its lane shaped is the lane's, bound again with its next batch.
            if (window.Batch is not null && ReferenceEquals(window.Batch, window.View))
            {
                window.View.Dispose();
            }

            input.Handover!.GiveBack(taken);
        }
        else if (window.Copy is { } copy)
        {
            window.Batch!.Dispose();
            Vorticity.Arrays.ScanContexts.Shared.Return(copy);
        }

        window.Clear();
        _spareWindows.Push(window);
        input.LiveWindows--;
        if (input.Ended && input.LiveWindows == 0 && _ending.Remove(input))
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
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

            // Done, it goes once its windows are back: a window a writer still reads holds its scan.
            input.Ended = true;
            if (input.LiveWindows == 0)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _ending.Add(input);
            }
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
        ObjectLease lease = Rent is { } rent
            ? await rent(next.Entry, _cancellationToken).ConfigureAwait(false)
            : await _version.RentAsync(next.Entry, _cancellationToken).ConfigureAwait(false);
        _opened?.Invoke(lease);
        MergeInput input = new MergeInput(this, lease, next.Entry.Key, next.Rank, next.FirstRow, _low, _high);
        try
        {
            await input.StartAsync(_batches(lease, next.Entry, input.Handover), _cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// What the merge calls before it opens the object of an entry while inputs it is done with still wait for their
    /// windows to come back, runs noted on them not copied yet: the caller, which drives the writers, has them copy the
    /// runs they keep when the object would not hold beside those inputs, and the merge gives the windows back and lets
    /// the inputs go before it opens the object. Null for a merge whose caller does not drive the writers, a slice merged
    /// ahead.
    /// </summary>
    /// <remarks>
    /// Objects that follow one another along the keys, a level's or those one merge wrote, are open one at a time, and a
    /// job counts them so (<see cref="DatasetCompactor.Peak"/>). An input done went only once the writers had copied its
    /// last runs, a few blocks later, and the next one opened beside it meanwhile: two objects of a run held at once what
    /// the job counted one to hold.
    /// </remarks>
    internal Func<ObjectEntry, ValueTask>? LetGo { get; init; }

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

    /// <summary>
    /// One input of the merge: an object's batches in key order, and their keys row-encoded; under a window, its rows from the
    /// low bound to before the high one.
    /// </summary>
    internal sealed class MergeInput : IAsyncDisposable
    {
        private readonly KeyOrderedMerge _merge;
        private readonly ObjectLease _lease;
        private readonly ReadOnlyMemory<byte> _high;

        // The low bound until a batch reaches it, past which every row does; the high one once a batch reached it, past
        // which no row is wanted.
        private ReadOnlyMemory<byte> _low;
        private bool _ended;
        private IAsyncEnumerator<RecordBatch>? _batches;
        private Window? _window;
        private int _row;
        private int _first;

        // Kept apart from the batch, which is handed out whole when a run is all of it: a consumer
        // may dispose what it was given, and stepping past it must not read it again.
        private int _rows;

        internal MergeInput(KeyOrderedMerge merge, ObjectLease lease, string key, long rank, long firstRow, ReadOnlyMemory<byte> low, ReadOnlyMemory<byte> high)
        {
            _merge = merge;
            _lease = lease;
            _low = low;
            _high = high;
            Object = key;
            Rank = rank;
            FirstRow = firstRow;
            Handover = merge.Ledger is null ? null : new BatchHandover(Prepare) { Ahead = merge.Ahead };
        }

        /// <summary>What its scan hands its batches over through, under a ledger; null otherwise.</summary>
        internal BatchHandover? Handover { get; }

        /// <summary>What the decode of each batch of its scan adds beside it: the batch shaped, its keys, the bytes a row names.</summary>
        private object? Prepare(RecordBatch batch, object? previous) => _merge.Prepare(batch, previous, Handover!.State as InputShaping);

        /// <summary>Its windows not given back yet: the current one and those set aside.</summary>
        internal int LiveWindows { get; set; }

        /// <summary>Whether it has no row left, and goes once its windows are back.</summary>
        internal bool Ended { get; set; }

        /// <summary>The window of its current batch.</summary>
        internal Window Current => _window!;

        /// <summary>Takes its current window from it, for the merge to give back as it ends; null when it has none.</summary>
        internal Window? TakeCurrent()
        {
            Window? window = _window;
            _window = null;
            return window;
        }

        /// <summary>The object's key in the store, for a message.</summary>
        internal string Object { get; }

        /// <summary>Where its ties go.</summary>
        internal long Rank { get; }

        /// <summary>Where the object's rows start among the dataset's.</summary>
        internal long FirstRow { get; }

        internal bool IsLive => _window is not null;

        internal RecordBatch Batch => _window!.Batch!;

        internal int Row => _row;

        /// <summary>The rows left in its current batch.</summary>
        internal int Remaining => _window is null ? 0 : _rows - _row;

        /// <summary>Its current key, row-encoded.</summary>
        internal ReadOnlySpan<byte> Key => _window!.Keys!.Row(_row);

        internal ReadOnlySpan<byte> KeyAt(int row) => _window!.Keys!.Row(row);

        internal ValueTask StartAsync(IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken)
        {
            _batches = batches.GetAsyncEnumerator(cancellationToken);
            return NextAsync();
        }

        /// <summary>
        /// How many rows from here on lie below <paramref name="limit"/>, or at it when
        /// <paramref name="inclusive"/>.
        /// </summary>
        /// <remarks>
        /// The batch's last row first: objects whose keys do not interleave, a level's, give runs of whole batches, each one
        /// comparison where a comparison a row took a key-ordered read of 2M rows over 256 such objects 7.3 ns a row. Else
        /// by gallop, doubling past the run's start, then by halves: a run of n rows in about 2 log n comparisons.
        /// </remarks>
        internal int RunUpTo(ReadOnlySpan<byte> limit, bool inclusive)
        {
            RowKeys keys = _window!.Keys!;
            if (Within(keys.Row(_rows - 1), limit, inclusive))
            {
                return _rows - _row;
            }

            // The rows in [_row, low) lie within the limit, the one at high past it.
            int low = _row;
            int high = _rows - 1;
            for (int step = 1; low + step < high; step <<= 1)
            {
                if (!Within(keys.Row(low + step - 1), limit, inclusive))
                {
                    high = low + step - 1;
                    break;
                }

                low += step;
            }

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (Within(keys.Row(middle), limit, inclusive))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low - _row;

            static bool Within(ReadOnlySpan<byte> key, ReadOnlySpan<byte> limit, bool inclusive)
            {
                int order = key.SequenceCompareTo(limit);
                return order < 0 || (order == 0 && inclusive);
            }
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
            // A window still held goes with the input: its copy back to the pool, its scan's lane with the scan.
            if (TakeCurrent() is { } window)
            {
                _merge.Drop(window);
            }

            if (_batches is not null)
            {
                await _batches.DisposeAsync().ConfigureAwait(false);
                _batches = null;
            }

            await _lease.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Takes the next batch as a window, its keys row-encoded: a key of several columns has no ordering to compare in
        /// place, and the encoding gives one value per row that sorts as the key does. Under a range, the batch's rows below
        /// its low bound and from its high one on are left out. The window of the batch before is set aside until no writer
        /// reads it (<see cref="RetireAsync"/>). A window's keys go into the buffers of a window given back before: rented and
        /// given back a batch, a merge of 2M rows spent 14 % of its samples renting, zeroing and returning them.
        /// </summary>
        private async ValueTask NextAsync()
        {
            if (TakeCurrent() is { } last)
            {
                await _merge.RetireAsync(last, cited: _row > _first).ConfigureAwait(false);
            }

            _row = 0;
            _rows = 0;
            _first = 0;
            while (!_ended && await _batches!.MoveNextAsync().ConfigureAwait(false))
            {
                RecordBatch batch = _batches.Current;
                if (batch.RowCount == 0)
                {
                    continue;
                }

                // Its rows as shaped: its deleted rows left out, its columns reshaped. None left, it goes back at once.
                Window window = _merge.OpenWindow(this, batch, _lease);
                if (window.Batch is not { RowCount: > 0 } held)
                {
                    await _merge.GiveBackAsync(window).ConfigureAwait(false);
                    continue;
                }

                RowKeys keys = window.Keys!;
                int from = 0;
                int to = held.RowCount;
                if (!_low.IsEmpty)
                {
                    from = FirstAtOrPast(keys, 0, to, _low.Span);
                    if (from < to)
                    {
                        _low = default;
                    }
                }

                if (!_high.IsEmpty && keys.Row(to - 1).SequenceCompareTo(_high.Span) >= 0)
                {
                    to = FirstAtOrPast(keys, from, to, _high.Span);
                    _ended = true;
                }

                if (from == to)
                {
                    await _merge.GiveBackAsync(window).ConfigureAwait(false);
                    continue;
                }

                _window = window;
                _row = from;
                _first = from;
                _rows = to;
                return;
            }
        }

        /// <summary>The first row from <paramref name="from"/> to <paramref name="to"/> whose key lies at or past <paramref name="bound"/>, by halves.</summary>
        private static int FirstAtOrPast(RowKeys keys, int from, int to, ReadOnlySpan<byte> bound)
        {
            while (from < to)
            {
                int middle = from + ((to - from) >> 1);
                if (keys.Row(middle).SequenceCompareTo(bound) < 0)
                {
                    from = middle + 1;
                }
                else
                {
                    to = middle;
                }
            }

            return from;
        }
    }

    /// <summary>Lets <paramref name="window"/> go with its input, given up: its copy back to the pool, no writer reading it.</summary>
    private void Drop(Window window)
    {
        if (window.Taken is { } taken)
        {
            if (window.Batch is not null && ReferenceEquals(window.Batch, window.View))
            {
                window.View.Dispose();
            }

            window.Input!.Handover!.GiveBack(taken);
        }
        else if (window.Copy is { } copy)
        {
            window.Batch!.Dispose();
            Vorticity.Arrays.ScanContexts.Shared.Return(copy);
        }

        window.Input!.LiveWindows--;
        window.Clear();
        if (!_disposed)
        {
            _spareWindows.Push(window);
        }
        else
        {
            window.OwnKeys?.Dispose();
        }
    }

    /// <summary>
    /// One batch of an input, which the merge keeps until no writer reads it: the batch, its rows' keys row-encoded, and
    /// the runs noted on the ledger when the input moved past it.
    /// </summary>
    internal sealed class Window
    {
        /// <summary>The input it is a batch of.</summary>
        internal MergeInput? Input;

        /// <summary>The batch: its scan's, taken over; a copy in <see cref="Copy"/>; or the input's own, valid until its next.</summary>
        internal RecordBatch? Batch;

        /// <summary>Its rows' keys: those the decode encoded, the lane's, or <see cref="OwnKeys"/>.</summary>
        internal RowKeys? Keys;

        /// <summary>The keys' buffers the window keeps from one batch to the next, for a batch whose decode encoded none.</summary>
        internal RowKeys? OwnKeys;

        /// <summary>Its scan's batch, taken over, to give back to it; null for a batch not taken over.</summary>
        internal RecordBatch? Taken;

        /// <summary>The window's own view over its scan's arena, for a batch a wrapper reshaped there; kept from one batch to the next.</summary>
        internal RecordBatch? View;

        /// <summary>The context of a copy of the batch, when the batch was not its scan's.</summary>
        internal Vorticity.Arrays.ScanContext? Copy;

        /// <summary>The runs the ledger had noted when the input moved past it: given back once every one is copied.</summary>
        internal long CitedUpTo;

        /// <summary>The bytes a row of the batch names on average; negative until counted.</summary>
        internal double Bytes = -1;

        /// <summary>The bytes a row of the batch names on average, counted at the first run a writer keeps of it.</summary>
        internal double RowBytes() => Bytes >= 0 ? Bytes : Bytes = BytesPerRow(Batch!);

        /// <summary>The bytes a row of <paramref name="batch"/> names on average: what the rows a writer keeps of it are sized by.</summary>
        internal static double BytesPerRow(RecordBatch batch) =>
            (double)batch.Arena.NamedBytes(batch.RootIndex) / Math.Max(1, batch.RowCount);

        /// <summary>Forgets its batch, keeping its keys' buffers.</summary>
        internal void Clear()
        {
            Input = null;
            Batch = null;
            Keys = null;
            Taken = null;
            Copy = null;
            CitedUpTo = 0;
            Bytes = -1;
        }
    }

    /// <summary>
    /// What the decode of an input's batch makes beside it, on its lane: the batch shaped (<see cref="InputShaping"/>), its
    /// keys, and the bytes a row names.
    /// </summary>
    private sealed class WindowKeys : InputShapeState
    {
        /// <summary>The batch the merge reads: the scan's, or a view of it shaped; null when no row of it is left.</summary>
        internal RecordBatch? View;

        internal RowKeys? Keys;
        internal double RowBytes;

        public override void Dispose()
        {
            Keys?.Dispose();
            Keys = null;
            View = null;
            base.Dispose();
        }
    }
}
