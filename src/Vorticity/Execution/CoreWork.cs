using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Vorticity.Execution;

/// <summary>
/// Work offered to a <see cref="FlowCore"/>: synchronous, run by the pumps that claim it, each on the lane it holds.
/// </summary>
/// <remarks>
/// Linked into the core's queue by its own fields, so that offering it allocates nothing: a work is queued once at a time,
/// and offered again once it left the queue.
/// </remarks>
internal abstract class CoreWork
{
    /// <summary>The work before this one in its flow's queue, under the core's lock.</summary>
    internal CoreWork? Previous;

    /// <summary>The work after this one in its flow's queue, under the core's lock.</summary>
    internal CoreWork? Next;

    /// <summary>Whether the work is in the queue, under the core's lock.</summary>
    internal bool Queued;

    /// <summary>The flow it was offered in.</summary>
    internal Flow Flow;

    /// <summary>
    /// Under the core's lock: a claim of the work for the pump that will <see cref="Run"/> it, and whether the work stays
    /// queued for another; false when it wants no more pumps, which takes it off the queue.
    /// </summary>
    internal abstract bool TryClaim(out bool more, out long ticket);

    /// <summary>Runs the claim <paramref name="ticket"/> names, on the lane of the pump that took it. Never throws.</summary>
    /// <returns>
    /// Whether the claim ended the work while its owner awaits it: the pump gives its lane back, then <see cref="Signal"/>s
    /// the owner, whose flow goes on on the pump's thread under the owner's own lane, as a pool's continuation would.
    /// </returns>
    internal abstract bool Run(long ticket);

    /// <summary>Completes what the owner awaits, once the pump that ended the work gave its lane back.</summary>
    internal abstract void Signal();
}

/// <summary>
/// One piece of synchronous work, run once: by a pump, or by its owner on the lane it holds when no pump took it yet
/// (<see cref="RunOrWaitAsync"/>).
/// </summary>
/// <remarks>
/// Kept by its owner and offered again once done, so that a unit in steady state allocates nothing. Its failure is thrown
/// to whoever awaits it.
/// </remarks>
internal abstract class CoreUnit : CoreWork, IValueTaskSource
{
    private const int Running = 0;
    private const int Awaited = 1;
    private const int Done = 2;

    private ManualResetValueTaskSourceCore<bool> _done;
    private Exception? _error;
    private int _state = Done;

    /// <summary>The unit's work.</summary>
    protected abstract void Execute();

    /// <summary>
    /// Called once the unit's result is set, whoever ran it: from here on the unit may be offered again, so what lets its
    /// owner go on to offer it again goes here rather than in <see cref="Execute"/>.
    /// </summary>
    protected virtual void Ended()
    {
    }

    /// <summary>Offers the unit to <paramref name="core"/> in <paramref name="flow"/>, for one pump.</summary>
    internal void Offer(FlowCore core, Flow flow)
    {
        _error = null;
        _done.Reset();
        Volatile.Write(ref _state, Running);
        core.Offer(this, flow, 1);
    }

    /// <summary>
    /// The unit done: run here, on the lane the caller holds, when no pump took it yet; else awaited, its pump at work on
    /// it. Rethrows its failure.
    /// </summary>
    internal ValueTask RunOrWaitAsync(FlowCore core)
    {
        if (core.TryWithdraw(this))
        {
            Complete();
            Volatile.Write(ref _state, Done);
            _done.SetResult(true);
            Ended();
        }
        else
        {
            // Awaited while its pump runs it: the pump gives its lane back before it completes the wait. Done already, the
            // wait completes at once.
            Interlocked.CompareExchange(ref _state, Awaited, Running);
        }

        return new ValueTask(this, _done.Version);
    }

    internal override bool TryClaim(out bool more, out long ticket)
    {
        more = false;
        ticket = 0;
        return true;
    }

    internal override bool Run(long ticket)
    {
        Complete();
        if (Interlocked.Exchange(ref _state, Done) == Awaited)
        {
            return true;
        }

        _done.SetResult(true);
        Ended();
        return false;
    }

    internal override void Signal()
    {
        _done.SetResult(true);
        Ended();
    }

    /// <summary>Runs the unit's work, its failure kept for whoever awaits it.</summary>
    private void Complete()
    {
        try
        {
            Execute();
        }
        catch (Exception error)
        {
            _error = error;
        }
    }

    void IValueTaskSource.GetResult(short token)
    {
        _done.GetResult(token);
        if (_error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _done.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _done.OnCompleted(continuation, state, token, flags);
}

/// <summary>A piece of work cut into items that run in any order, on any lane.</summary>
internal interface IBatchWork
{
    /// <summary>Runs item <paramref name="item"/> of the piece <paramref name="batch"/> runs, once, on the lane that claimed it.</summary>
    void Run(CoreBatch batch, int item);
}

/// <summary>
/// Runs the items of one piece of work on its caller and on pumps of the core beside it, and completes once every item has
/// run: what a writer spreads over its degree.
/// </summary>
/// <remarks>
/// <para>
/// Rented by a writer, used by it alone, and given back for the next one. The piece is offered to the core for as many
/// pumps as the writer's degree allows beside its caller, and the caller claims items as well; once it has run every item
/// left, it takes the piece off the queue and awaits every pump that joined to leave, the last of which completes the
/// piece. A pump that claimed the piece late finds it closed and touches nothing; one that claimed a piece since reused
/// finds a ticket of another and leaves. No thread waits: the caller's is free while the pumps finish, and it awaits only
/// items a pump has claimed and is running, since it claims every item left itself.
/// </para>
/// <para>
/// A pump runs a claim of the piece on a lane of the core it holds: the writes of a session never compute on more threads
/// than its degree, however many writers spread at once, and a piece whose lanes are all taken runs on its caller alone.
/// A pump that finished its share takes the next work queued, another writer's piece or a unit, without going back to the
/// pool. One piece runs at a time.
/// </para>
/// </remarks>
internal sealed class CoreBatch : CoreWork, IValueTaskSource
{
    /// <summary>The batches kept for later writers: what a few writers at once hold, one each.</summary>
    private const int Kept = 8;

    private static readonly CoreBatch?[] Pool = new CoreBatch?[Kept];
    private static readonly Lock Gate = new Lock();
    private static int _pooled;

    // What the caller awaits once it has run every item left: completed by the last pump to leave, the piece closed.
    private ManualResetValueTaskSourceCore<bool> _left;
    private int[] _items = [];
    private IBatchWork? _work;
    private int _count;
    private int _next;
    private int _active;
    private int _awaiting;
    private int _claims;
    private long _generation;
    private bool _running;
    private volatile bool _open;
    private Exception? _error;

    private CoreBatch()
    {
    }

    /// <summary>The lanes a piece may run on, the calling one included.</summary>
    internal int Lanes { get; private set; }

    /// <summary>The core whose pumps join a piece; null for a piece that runs on its caller alone.</summary>
    internal FlowCore? Core { get; private set; }

    /// <summary>What the piece running reads, as its caller passed it: an object and a number.</summary>
    internal object? State { get; private set; }

    /// <inheritdoc cref="State"/>
    internal int Value { get; private set; }

    /// <summary>
    /// Room for <paramref name="count"/> numbers describing the next piece's items, kept from one piece to the next and
    /// read back through <see cref="Item"/>.
    /// </summary>
    internal Span<int> Items(int count)
    {
        if (_items.Length < count)
        {
            _items = new int[count];
        }

        return _items.AsSpan(0, count);
    }

    /// <summary>Number <paramref name="index"/> of those <see cref="Items"/> holds.</summary>
    internal int Item(int index) => _items[index];

    /// <summary>A batch an earlier writer gave back, or a new one, for pieces of <paramref name="lanes"/> lanes on <paramref name="core"/>.</summary>
    /// <param name="core">The core whose pumps join a piece; null runs every piece on its caller.</param>
    /// <param name="lanes">The lanes a piece may run on, the calling one included.</param>
    internal static CoreBatch Rent(FlowCore? core, int lanes)
    {
        CoreBatch? batch = null;
        lock (Gate)
        {
            if (_pooled > 0)
            {
                batch = Pool[--_pooled];
                Pool[_pooled] = null;
            }
        }

        batch ??= new CoreBatch();
        batch.Core = core;
        batch.Lanes = lanes;
        return batch;
    }

    /// <summary>Keeps <paramref name="batch"/> for a later writer, or lets it go past the bound.</summary>
    internal static void Return(CoreBatch batch)
    {
        batch.Core = null;
        lock (Gate)
        {
            if (_pooled < Kept)
            {
                Pool[_pooled++] = batch;
            }
        }
    }

    /// <summary>
    /// Runs items <c>[0, count)</c> of <paramref name="work"/>, which read <paramref name="state"/> and
    /// <paramref name="value"/> back from the batch, and rethrows the first an item threw. Completes at once when no pump
    /// is still at work once the caller has run every item left; else once the last pump leaves, on its thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">A piece is running already: an item started another.</exception>
    internal ValueTask RunAsync(IBatchWork work, int count, object? state = null, int value = 0)
    {
        if (_running)
        {
            throw new InvalidOperationException("A piece of work started another on the lanes it runs on.");
        }

        _running = true;
        State = state;
        Value = value;
        _work = work;
        _count = count;
        _next = 0;
        _error = null;
        _left.Reset();
        _generation++;
        _open = true;

        // Pumps join on lanes of the core they hold, as many as are free now and as the piece has items beside the caller's.
        int pumps = Math.Min(Lanes - 1, count - 1);
        FlowCore? core = Core;
        if (core is not null && pumps > 0)
        {
            Volatile.Write(ref _claims, pumps);
            core.Offer(this, Flow.Write, pumps);
        }

        // Every item claimed, the caller's done: the piece closes and leaves the queue. The pumps that claimed it are counted
        // among the active as they claim, each of which leaves once its items are done; one that claimed it as it closed
        // finds it closed and touches nothing. The fences keep the close from passing the count's read, and the awaiting from
        // passing it: a pump either is counted here or reads the piece closed, and the last to leave either completes the
        // piece or left it for the caller to.
        Work();
        _open = false;
        if (core is not null && pumps > 0)
        {
            core.TryWithdraw(this);
        }

        Interlocked.MemoryBarrier();
        Volatile.Write(ref _awaiting, 1);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _active) == 0 && Interlocked.CompareExchange(ref _awaiting, 0, 1) == 1)
        {
            End();
            return default;
        }

        return new ValueTask(this, _left.Version);
    }

    internal override bool TryClaim(out bool more, out long ticket)
    {
        ticket = _generation;
        if (_claims <= 0)
        {
            more = false;
            return false;
        }

        _claims--;
        more = _claims > 0;
        Interlocked.Increment(ref _active);
        return true;
    }

    internal override bool Run(long ticket)
    {
        if (_open && ticket == Volatile.Read(ref _generation))
        {
            Work();
        }

        // The last to leave a piece its caller awaits completes it, once its lane is given back.
        return Interlocked.Decrement(ref _active) == 0 && Interlocked.CompareExchange(ref _awaiting, 0, 1) == 1;
    }

    internal override void Signal() => _left.SetResult(true);

    /// <summary>The piece ended: what it read let go, and the first failure of an item thrown.</summary>
    private void End()
    {
        _work = null;
        State = null;
        _running = false;
        if (_error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    void IValueTaskSource.GetResult(short token)
    {
        _left.GetResult(token);
        End();
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _left.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _left.OnCompleted(continuation, state, token, flags);

    private void Work()
    {
        IBatchWork work = _work!;
        int item;
        while ((item = Interlocked.Increment(ref _next) - 1) < _count)
        {
            try
            {
                work.Run(this, item);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
            }
        }
    }
}
