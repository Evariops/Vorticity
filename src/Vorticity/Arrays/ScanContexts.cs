using System;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// The contexts scans decode their batches in, kept from one scan to the next: a context is a
/// dozen arrays -- its node and canonical arenas, its scalars, its segment set -- that a scan built
/// per lane and dropped, where the next scan, of the same file or of another, needed the same.
/// </summary>
/// <remarks>
/// A context comes back recycled, holding nothing of the scan or the file it served, and is bound
/// to the next scan's file when it is taken. The bound of <see cref="Shared"/> is what a take on a
/// lane per processor holds, three contexts a lane, and never less than a few scans at once hold,
/// a lane each; a context past it is disposed. A context keeps the arrays its widest batch grew,
/// a few hundred bytes a column, so what a swept pool keeps goes after a collection of the oldest
/// generation once no scan has taken a context over a minute, and when the machine's memory load
/// is high.
/// </remarks>
internal sealed class ScanContexts : ISweptAfterCollections
{
    /// <summary>How long no scan takes a context before a sweep lets the kept ones go.</summary>
    internal const long IdleMilliseconds = 60_000;

    private readonly ScanContext?[] _contexts;
    private readonly Lock _gate = new Lock();
    private int _count;

    /// <summary>Whether a scan took a context since the last sweep.</summary>
    private bool _taken;

    /// <summary>When the sweep that last found a context taken ran.</summary>
    private long _idleSince;

    private int _sweeps;

    /// <summary>A pool that keeps up to <paramref name="capacity"/> contexts.</summary>
    /// <param name="capacity">The most contexts kept.</param>
    internal ScanContexts(int capacity)
    {
        _contexts = new ScanContext?[capacity];
    }

    /// <summary>The pool every scan takes its contexts from.</summary>
    internal static ScanContexts Shared { get; } = new ScanContexts(Math.Max(16, 3 * Environment.ProcessorCount)).Swept();

    /// <summary>The contexts kept, for tests: a pool in use answers from under the caller's feet.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The sweeps run so far, for tests.</summary>
    internal int Sweeps => Volatile.Read(ref _sweeps);

    /// <summary>A context an earlier scan gave back, bound to <paramref name="file"/>, or a new one.</summary>
    /// <param name="file">The file the scan reads.</param>
    internal ScanContext Rent(VortexFile file)
    {
        ScanContext? context = null;
        lock (_gate)
        {
            _taken = true;
            if (_count > 0)
            {
                context = _contexts[--_count];
                _contexts[_count] = null;
            }
        }

        if (context is null)
        {
            return ScanContext.Pooled(file);
        }

        context.Rebind(file);
        return context;
    }

    /// <summary>Recycles <paramref name="context"/> and keeps it for a later scan, or disposes it past the bound.</summary>
    /// <param name="context">A context <see cref="Rent"/> gave, which nothing reads any more.</param>
    internal void Return(ScanContext context)
    {
        context.Recycle();
        lock (_gate)
        {
            if (_count < _contexts.Length)
            {
                _contexts[_count++] = context;
                return;
            }
        }

        context.Dispose();
    }

    /// <summary>This pool, swept after each collection of the oldest generation for as long as it lives.</summary>
    /// <returns>This pool.</returns>
    internal ScanContexts Swept()
    {
        CollectionSweeper<ScanContexts>.Register(this);
        return this;
    }

    /// <summary>
    /// Disposes the contexts kept when no scan has taken one over <see cref="IdleMilliseconds"/>
    /// before <paramref name="now"/>, or when <paramref name="everything"/>. A pool a scan took
    /// from since the last sweep starts its idle time at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The time of the sweep, in the milliseconds of <see cref="Environment.TickCount64"/>.</param>
    /// <param name="everything">Whether the contexts go however recently one was taken: the machine's memory load is high.</param>
    internal void TrimIdle(long now, bool everything)
    {
        lock (_gate)
        {
            if (!everything)
            {
                if (_taken)
                {
                    _taken = false;
                    _idleSince = now;
                    return;
                }

                if (now - _idleSince < IdleMilliseconds)
                {
                    return;
                }
            }

            while (_count > 0)
            {
                ScanContext context = _contexts[--_count]!;
                _contexts[_count] = null;
                context.Dispose();
            }
        }
    }

    /// <summary>A sweep after a collection: the contexts no scan wanted of late, or all of them when the machine's memory load is high.</summary>
    void ISweptAfterCollections.Sweep()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        TrimIdle(Environment.TickCount64, memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes);
        Interlocked.Increment(ref _sweeps);
    }
}
