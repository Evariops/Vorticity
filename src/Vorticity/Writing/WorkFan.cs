using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Vorticity.Writing;

/// <summary>A piece of work cut into items that run in any order, on any thread.</summary>
internal interface IFanWork
{
    /// <summary>Runs item <paramref name="item"/> of the piece <paramref name="fan"/> runs, once, on the thread that claimed it.</summary>
    void Run(WorkFan fan, int item);
}

/// <summary>
/// Runs the items of one piece of work on several threads, the calling one included, and completes
/// once every item has run: what the writer spreads over its degree of parallelism.
/// </summary>
/// <remarks>
/// <para>
/// Rented by a writer, used by it alone, and given back for the next writer. The helpers are made
/// once and queued again for every piece, and the fan is itself what the caller awaits, so a piece
/// costs no allocation; the calling thread claims items as well, and the helpers join only a piece
/// that is open. Once the caller has run every item left, it closes the piece and awaits every
/// helper that joined to leave, the last of which completes the piece: a helper the pool runs late
/// finds it closed and touches nothing, or finds the next one open and works for it, and the caller
/// never awaits one the pool has not run. No thread waits: the caller's is free while the helpers
/// finish. A helper still queued is not queued again. One piece runs at a time.
/// </para>
/// <para>
/// A helper of a fan with a <see cref="Budget"/> is queued only on a lane of the session's it takes
/// first and gives back as it leaves: the writes of a session never compute on more threads than its
/// degree, however many writers fan out at once, and a fan whose lanes are all taken runs its
/// piece on its caller's alone. A helper the pool runs late holds its lane until it leaves, and
/// works only for a piece of the lanes it took it from.
/// </para>
/// <para>
/// Awaiting costs the caller nothing it could have done instead: it awaits only items another
/// thread has claimed and is running, since it claims every item left itself.
/// </para>
/// </remarks>
internal sealed class WorkFan : IValueTaskSource
{
    /// <summary>The fans kept for later writers: what a few writers at once hold, one each.</summary>
    private const int Kept = 8;

    private static readonly WorkFan?[] Pool = new WorkFan?[Kept];
    private static readonly Lock Gate = new Lock();
    private static int _pooled;

    // What the caller awaits once it has run every item left: completed by the last helper to leave, the piece closed.
    private ManualResetValueTaskSourceCore<bool> _left;
    private Helper[] _helpers = [];
    private int[] _items = [];
    private IFanWork? _work;
    private int _count;
    private int _next;
    private int _active;
    private int _awaiting;
    private bool _running;
    private volatile bool _open;
    private Exception? _error;

    private WorkFan()
    {
    }

    /// <summary>The threads a piece may run on, the calling one included.</summary>
    internal int Lanes { get; private set; }

    /// <summary>
    /// The session's lanes a helper joins on, one each, or none when none is free: the calling thread runs the items no
    /// helper takes, on the lane it holds. Null for helpers that take no lane.
    /// </summary>
    internal LaneBudget? Budget { get; private set; }

    /// <summary>What the piece running reads, as its caller passed it: an object and a number.</summary>
    internal object? State { get; private set; }

    /// <inheritdoc cref="State"/>
    internal int Value { get; private set; }

    /// <summary>
    /// Room for <paramref name="count"/> numbers describing the next piece's items, kept from one
    /// piece to the next and read back through <see cref="Item"/>.
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

    /// <summary>A fan an earlier writer gave back, or a new one, with helpers for <paramref name="lanes"/> threads.</summary>
    /// <param name="lanes">The threads a piece may run on, the calling one included.</param>
    /// <param name="budget">The session's lanes the helpers join on; null for helpers that take none.</param>
    internal static WorkFan Rent(int lanes, LaneBudget? budget = null)
    {
        WorkFan? fan = null;
        lock (Gate)
        {
            if (_pooled > 0)
            {
                fan = Pool[--_pooled];
                Pool[_pooled] = null;
            }
        }

        fan ??= new WorkFan();
        fan.Lanes = lanes;
        fan.Budget = budget;
        int had = fan._helpers.Length;
        if (had < lanes - 1)
        {
            Array.Resize(ref fan._helpers, lanes - 1);
            for (int i = had; i < lanes - 1; i++)
            {
                fan._helpers[i] = new Helper(fan);
            }
        }

        return fan;
    }

    /// <summary>Keeps <paramref name="fan"/> for a later writer, or lets it go past the bound.</summary>
    internal static void Return(WorkFan fan)
    {
        lock (Gate)
        {
            if (_pooled < Kept)
            {
                Pool[_pooled++] = fan;
            }
        }
    }

    /// <summary>
    /// Runs items <c>[0, count)</c> of <paramref name="work"/>, which read
    /// <paramref name="state"/> and <paramref name="value"/> back from the fan, and rethrows the
    /// first an item threw. Completes at once when no helper is still at work once the caller has run
    /// every item left; else once the last helper leaves, on its thread, the caller's held by nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A piece is running already: an item started another.</exception>
    internal ValueTask RunAsync(IFanWork work, int count, object? state = null, int value = 0)
    {
        if (_running)
        {
            throw new InvalidOperationException("A piece of work started another on the threads it runs on.");
        }

        _running = true;
        State = state;
        Value = value;
        _work = work;
        _count = count;
        _next = 0;
        _error = null;
        _left.Reset();
        _open = true;

        // A helper joins on a lane of the session's it holds alone, or not at all: the degree is the lanes at work. One
        // the pool has not run yet for an earlier piece is not queued again: it joins this one if it comes while it is open.
        int helpers = Math.Min(Lanes - 1, count - 1);
        LaneBudget? budget = Budget;
        for (int i = 0; i < helpers; i++)
        {
            Helper helper = _helpers[i];
            if (Volatile.Read(ref helper.Queued) != 0)
            {
                continue;
            }

            long entered = 0;
            if (budget is not null && !budget.TryEnter(out entered))
            {
                break;
            }

            helper.Budget = budget;
            helper.Entered = entered;
            Volatile.Write(ref helper.Queued, 1);
            ThreadPool.UnsafeQueueUserWorkItem(helper, preferLocal: false);
        }

        // Every item claimed, the caller's done: the piece closes. The items the helpers claimed run on helpers counted
        // among the active, each of which leaves once its item is done; one the pool runs late finds the piece closed and
        // touches nothing, so the caller awaits no work the pool has yet to start. The fences keep the close from passing
        // the count's read, and the awaiting from passing it: a helper either is counted here or reads the piece closed,
        // and the last to leave either completes the piece or left it for the caller to.
        Work();
        _open = false;
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

    private void Join(Helper helper)
    {
        Interlocked.Increment(ref _active);
        try
        {
            // A helper works only for a piece of the lanes it took its own from: one the pool runs late, once the fan went
            // to a writer of another session, leaves.
            if (_open && ReferenceEquals(helper.Budget, Budget))
            {
                Work();
            }
        }
        finally
        {
            // Its lane read before it may be queued again, which sets another; the piece completed once the lane is given
            // back, by the last helper to leave a closed piece the caller awaits.
            LaneBudget? budget = helper.Budget;
            long entered = helper.Entered;
            Volatile.Write(ref helper.Queued, 0);
            bool last = Interlocked.Decrement(ref _active) == 0;
            budget?.Exit(entered);
            if (last && Interlocked.CompareExchange(ref _awaiting, 0, 1) == 1)
            {
                _left.SetResult(true);
            }
        }
    }

    private void Work()
    {
        IFanWork work = _work!;
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

    private sealed class Helper(WorkFan owner) : IThreadPoolWorkItem
    {
        /// <summary>The lanes the helper took its own from for the piece it was queued for; null for none.</summary>
        internal LaneBudget? Budget;

        /// <summary>When it took its lane.</summary>
        internal long Entered;

        /// <summary>1 from its queueing to its leaving: a helper the pool has not run is not queued again.</summary>
        internal int Queued;

        public void Execute() => owner.Join(this);
    }
}
