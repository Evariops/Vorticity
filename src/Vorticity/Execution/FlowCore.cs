using System;
using System.Threading;

namespace Vorticity.Execution;

/// <summary>The flows of the core, upstream first: a pump takes the work of the most downstream flow that has some.</summary>
internal enum Flow
{
    /// <summary>Reading: what decodes what the store gave.</summary>
    Read,

    /// <summary>Merging: what a job's merge cuts.</summary>
    Merge,

    /// <summary>Writing: what ingests blocks and compresses chunks.</summary>
    Write,
}

/// <summary>
/// The pumps of a session: the work items of the pool that compute its work, each on a lane of <see cref="Lanes"/> it
/// holds, taking the work offered to the core one unit after another, the most downstream flow first, until none is left.
/// </summary>
/// <remarks>
/// <para>
/// A pump is a work item of the pool, never a thread of the library's: it is queued while a lane is free, takes the lane as
/// it starts, runs the units it finds one after another without going back to the pool, and gives its lane back once it
/// finds none, or at a unit's end when work that holds no lane waits for one (<see cref="LaneBudget.Waiting"/>). A pump the
/// pool runs late holds no lane meanwhile. A lane given back by anything that held one starts a pump for the work still
/// queued (<see cref="LaneFreed"/>), so that no work waits while a lane is free, and none runs past the degree.
/// </para>
/// <para>
/// The queue is one lock, taken once per unit, never per row. A unit is synchronous: what waits, for the store or for
/// memory, waits before it is offered, on no lane. Whatever offered a unit and holds a lane takes it back to run it itself
/// rather than wait for a pump (<see cref="TryWithdraw"/>): what holds a lane never waits for another.
/// </para>
/// </remarks>
internal sealed class FlowCore
{
    private const int FlowCount = 3;

    private readonly Lock _gate = new Lock();
    private readonly CoreWork?[] _heads = new CoreWork?[FlowCount];
    private readonly CoreWork?[] _tails = new CoreWork?[FlowCount];
    private readonly Pump[] _idle;
    private int _idleCount;
    private int _queued;
    private int _pending;
    private long _pumpsStarted;
    private long _unitsRun;
    private long _yields;

    /// <param name="lanes">The lanes the pumps hold, one each: the session's degree.</param>
    internal FlowCore(LaneBudget lanes)
    {
        Lanes = lanes;
        _idle = new Pump[lanes.Lanes];
        for (int i = 0; i < _idle.Length; i++)
        {
            _idle[i] = new Pump(this);
        }

        _idleCount = _idle.Length;
        lanes.Core = this;
    }

    /// <summary>
    /// The core of work that has no session, a writer made without one: as many lanes as the processor has, so that such
    /// work spread over its own degree never runs on more threads than there are cores.
    /// </summary>
    internal static FlowCore Process { get; } = new FlowCore(new LaneBudget(Environment.ProcessorCount));

    /// <summary>The lanes the pumps hold, shared with everything else the session computes on.</summary>
    internal LaneBudget Lanes { get; }

    /// <summary>The pumps started since the core was made: each one a trip through the pool.</summary>
    internal long PumpsStarted => Interlocked.Read(ref _pumpsStarted);

    /// <summary>The units the pumps ran, a claim of a batch counted as one.</summary>
    internal long UnitsRun => Interlocked.Read(ref _unitsRun);

    /// <summary>The pumps that gave their lane to work waiting for one, at a unit's end.</summary>
    internal long Yields => Interlocked.Read(ref _yields);

    /// <summary>
    /// Queues <paramref name="work"/> in <paramref name="flow"/> for <paramref name="claims"/> pumps at most, and starts as
    /// many as lanes are free now; the claims left wait for lanes given back. A work still queued stays where it is, and
    /// only more pumps start for it: what offers itself again as it finds more to do.
    /// </summary>
    internal void Offer(CoreWork work, Flow flow, int claims)
    {
        lock (_gate)
        {
            if (!work.Queued)
            {
                work.Flow = flow;
                work.Queued = true;
                work.Previous = _tails[(int)flow];
                work.Next = null;
                if (_tails[(int)flow] is { } tail)
                {
                    tail.Next = work;
                }
                else
                {
                    _heads[(int)flow] = work;
                }

                _tails[(int)flow] = work;
                _queued++;
            }
        }

        Wake(claims);
    }

    /// <summary>
    /// Takes <paramref name="work"/> off the queue, for its owner to run what is left of it on the lane it holds: false once
    /// a pump took its last claim, or once it left the queue.
    /// </summary>
    internal bool TryWithdraw(CoreWork work)
    {
        lock (_gate)
        {
            if (!work.Queued)
            {
                return false;
            }

            Unlink(work);
            return true;
        }
    }

    /// <summary>A lane was given back: a pump starts on it when work is queued.</summary>
    internal void LaneFreed()
    {
        if (Volatile.Read(ref _queued) > 0)
        {
            Wake(1);
        }
    }

    /// <summary>
    /// Queues <paramref name="wanted"/> pumps at most on the pool while work is queued, no more than the lanes free now that
    /// no pump queued already will take: a pump takes its lane as it starts, so that a pump the pool runs late holds none,
    /// where it held one from its queueing and a busy pool left a job's ranges no lane.
    /// </summary>
    private void Wake(int wanted)
    {
        for (; wanted > 0 && Volatile.Read(ref _queued) > 0; wanted--)
        {
            Pump? pump = null;
            lock (_gate)
            {
                if (_queued > 0 && _idleCount > 0 && Lanes.Lanes - Lanes.Held - _pending > 0)
                {
                    pump = _idle[--_idleCount];
                    _pending++;
                }
            }

            if (pump is null)
            {
                return;
            }

            Interlocked.Increment(ref _pumpsStarted);
            ThreadPool.UnsafeQueueUserWorkItem(pump, preferLocal: false);
        }
    }

    /// <summary>
    /// A pump's loop, on a lane it takes as it starts: the queued units, the most downstream flow first, until none is left.
    /// No lane free by then, it leaves at once; the lane given back next queues another.
    /// </summary>
    private void Run(Pump pump)
    {
        bool entering = Lanes.TryEnter(out long entered);
        lock (_gate)
        {
            _pending--;
            if (!entering)
            {
                _idle[_idleCount++] = pump;
            }
        }

        if (!entering)
        {
            // A lane given back since the check, by the holder that took the one this pump came for, queues it again.
            if (Volatile.Read(ref _queued) > 0 && Lanes.Held < Lanes.Lanes)
            {
                Wake(1);
            }

            return;
        }

        while (true)
        {
            CoreWork? work = null;
            long ticket = 0;
            lock (_gate)
            {
                for (int flow = FlowCount - 1; flow >= 0 && work is null; flow--)
                {
                    while (_heads[flow] is { } head)
                    {
                        if (head.TryClaim(out bool more, out ticket))
                        {
                            if (!more)
                            {
                                Unlink(head);
                            }

                            work = head;
                            break;
                        }

                        Unlink(head);
                    }
                }

                if (work is null)
                {
                    _idle[_idleCount++] = pump;
                }
            }

            if (work is null)
            {
                break;
            }

            bool awaited = work.Run(ticket);
            Interlocked.Increment(ref _unitsRun);
            if (awaited)
            {
                // The work's owner awaits it, and holds its own lane: this one goes back first, then the owner's flow goes on
                // on this thread, as the pool's would.
                lock (_gate)
                {
                    _idle[_idleCount++] = pump;
                }

                Lanes.Exit(entered);
                work.Signal();
                return;
            }

            if (Lanes.Waiting > 0)
            {
                // Work that holds no lane waits for one, a job about to start: this one goes to it at the unit's end, and the
                // units left take the next lane given back.
                Interlocked.Increment(ref _yields);
                lock (_gate)
                {
                    _idle[_idleCount++] = pump;
                }

                break;
            }
        }

        // Given back with the queue looked at under the lock: work offered since, whose pump found no lane free, starts on it.
        Lanes.Exit(entered);
    }

    private void Unlink(CoreWork work)
    {
        int flow = (int)work.Flow;
        if (work.Previous is { } previous)
        {
            previous.Next = work.Next;
        }
        else
        {
            _heads[flow] = work.Next;
        }

        if (work.Next is { } next)
        {
            next.Previous = work.Previous;
        }
        else
        {
            _tails[flow] = work.Previous;
        }

        work.Previous = null;
        work.Next = null;
        work.Queued = false;
        _queued--;
    }

    private sealed class Pump(FlowCore core) : IThreadPoolWorkItem
    {
        public void Execute() => core.Run(this);
    }
}
