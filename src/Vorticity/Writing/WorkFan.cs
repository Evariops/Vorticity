using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>A piece of work cut into items that run in any order, on any thread.</summary>
internal interface IFanWork
{
    /// <summary>Runs item <paramref name="item"/> of the piece <paramref name="fan"/> runs, once, on the thread that claimed it.</summary>
    void Run(WorkFan fan, int item);
}

/// <summary>
/// Runs the items of one piece of work on several threads, the calling one included, and returns
/// once every item has run: what the writer spreads over its degree of parallelism.
/// </summary>
/// <remarks>
/// <para>
/// Rented by a writer, used by it alone, and given back for the next writer. The helpers are made
/// once and queued again for every piece, so a piece costs no allocation, nor a file once one has
/// run; the calling thread claims items as well, and the helpers join only a piece that is open.
/// The caller waits for the last item, closes the piece, then waits for every helper that joined to
/// leave, so that a helper the pool runs late finds it closed and touches nothing, or finds the
/// next one open, whichever writer's it is, and works for it. One piece runs at a time.
/// </para>
/// <para>
/// Waiting costs the caller nothing it could have done instead: it waits only for items another
/// thread has claimed and is running, since it claims every item left itself.
/// </para>
/// </remarks>
internal sealed class WorkFan
{
    /// <summary>The fans kept for later writers: what a few writers at once hold, one each.</summary>
    private const int Kept = 8;

    private static readonly WorkFan?[] Pool = new WorkFan?[Kept];
    private static readonly Lock Gate = new Lock();
    private static int _pooled;

    private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
    private Helper[] _helpers = [];
    private int[] _items = [];
    private IFanWork? _work;
    private int _count;
    private int _next;
    private int _remaining;
    private int _active;
    private bool _running;
    private volatile bool _open;
    private Exception? _error;

    private WorkFan()
    {
    }

    /// <summary>The threads a piece may run on, the calling one included.</summary>
    internal int Lanes { get; private set; }

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
    internal static WorkFan Rent(int lanes)
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
                return;
            }
        }

        fan._finished.Dispose();
    }

    /// <summary>
    /// Runs items <c>[0, count)</c> of <paramref name="work"/>, which read
    /// <paramref name="state"/> and <paramref name="value"/> back from the fan, and rethrows the
    /// first an item threw.
    /// </summary>
    /// <exception cref="InvalidOperationException">A piece is running already: an item started another.</exception>
    internal void Run(IFanWork work, int count, object? state = null, int value = 0)
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
        _remaining = count;
        _error = null;
        _finished.Reset();
        _open = true;

        int helpers = Math.Min(Lanes - 1, count - 1);
        for (int i = 0; i < helpers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
        }

        Work();
        if (Volatile.Read(ref _remaining) > 0)
        {
            _finished.Wait();
        }

        _open = false;
        SpinWait spin = default;
        while (Volatile.Read(ref _active) != 0)
        {
            spin.SpinOnce();
        }

        _work = null;
        State = null;
        _running = false;
        if (_error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    private void Join()
    {
        Interlocked.Increment(ref _active);
        try
        {
            if (_open)
            {
                Work();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
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
            finally
            {
                if (Interlocked.Decrement(ref _remaining) == 0)
                {
                    _finished.Set();
                }
            }
        }
    }

    private sealed class Helper(WorkFan owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.Join();
    }
}
