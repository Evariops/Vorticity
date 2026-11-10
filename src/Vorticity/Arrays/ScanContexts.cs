using System;
using System.Threading;
using Vorticity.Buffers;
using Vorticity.File;

namespace Vorticity.Arrays;

/// <summary>
/// The contexts scans decode their batches in, kept from one scan to the next: a context is a
/// dozen arrays -- its node and canonical arenas, its scalars, its segment set -- that a scan built
/// per lane and dropped, where the next scan, of the same file or of another, needed the same.
/// </summary>
/// <remarks>
/// A context comes back recycled, holding nothing of the scan or the file it served, and is bound
/// to the next scan's file when it is taken. <see cref="Shared"/> keeps what a take on a lane per
/// processor holds, three contexts a lane, and never less than a few scans at once hold, a lane
/// each; or, up to its ceiling, as many as the scans took at once since it last let them go, which
/// many questions side by side, a lane each, take past the first bound; a context past what it
/// keeps is disposed. A context keeps the arrays its widest batch grew,
/// a few hundred bytes a column, so what a swept pool keeps goes after a collection of the oldest
/// generation once no scan has taken a context over a minute, and when the machine's memory load
/// is high.
/// </remarks>
internal sealed class ScanContexts : ISweptAfterCollections
{
    /// <summary>How long no scan takes a context before a sweep lets the kept ones go.</summary>
    internal const long IdleMilliseconds = 60_000;

    private ScanContext?[] _contexts;
    private readonly Lock _gate = new Lock();
    private int _count;

    // The contexts kept whatever the scans took, the most kept however many they took at once, the contexts taken and not
    // given back, and the most of them out at once since the pool last let its contexts go: what it keeps.
    private readonly int _floor;
    private readonly int _ceiling;

    // Whether the contexts made are a reader of metadata's, small and noting no tree size: an index's payloads decoded.
    private readonly bool _metadata;
    private int _lent;
    private int _mostLent;

    /// <summary>Whether a scan took a context since the last sweep.</summary>
    private bool _taken;

    /// <summary>When the sweep that last found a context taken ran.</summary>
    private long _idleSince;

    private int _sweeps;

    /// <summary>A pool that keeps up to <paramref name="capacity"/> contexts.</summary>
    /// <param name="capacity">The most contexts kept.</param>
    internal ScanContexts(int capacity)
        : this(capacity, capacity)
    {
    }

    /// <summary>
    /// A pool that keeps up to <paramref name="floor"/> contexts, or as many as the scans took at once since it last let them
    /// go, up to <paramref name="ceiling"/>.
    /// </summary>
    /// <param name="floor">The contexts kept whatever the scans took.</param>
    /// <param name="ceiling">The most contexts kept.</param>
    /// <param name="metadata">Whether the contexts made read metadata (<see cref="Metadata"/>).</param>
    internal ScanContexts(int floor, int ceiling, bool metadata = false)
    {
        _metadata = metadata;
        _floor = floor;
        _ceiling = Math.Max(floor, ceiling);
        _contexts = new ScanContext?[floor];
    }

    /// <summary>
    /// The pool every scan takes its contexts from: three contexts a processor, or as many as the scans took at once, up to
    /// 32 a processor. Each lane of a scan takes one, and so does each split it reads ahead: 32 questions at one lane each
    /// over a short store held more than the three a processor, every lane past them making a context and the pool
    /// disposing it when it came back, 300 KB a question (2026-11-08).
    /// </summary>
    internal static ScanContexts Shared { get; } =
        new ScanContexts(Math.Max(16, 3 * Environment.ProcessorCount), Math.Max(64, 32 * Environment.ProcessorCount)).Swept();

    /// <summary>
    /// The pool the readers of a file's indexes take their contexts from, contexts of metadata: a Bloom filter's nodes or a
    /// key index's pages decoded, a context made and disposed for each read of an origin's payloads before (2026-11-08).
    /// </summary>
    internal static ScanContexts Metadata { get; } =
        new ScanContexts(Math.Max(16, Environment.ProcessorCount), Math.Max(64, 8 * Environment.ProcessorCount), metadata: true).Swept();

    /// <summary>
    /// The pool the readers of indexes read with no open file take their contexts from, contexts over an encoding table: a
    /// dataset's search by key decoded each object's key index in a context made for it and disposed, 44 % of the bytes a
    /// search allocated (2026-11-10).
    /// </summary>
    internal static ScanContexts Footerless { get; } =
        new ScanContexts(Math.Max(16, Environment.ProcessorCount), Math.Max(64, 8 * Environment.ProcessorCount), metadata: true).Swept();

    /// <summary>A context an earlier read over an encoding table gave back, over <paramref name="arrayEncodingIds"/>, or a new one.</summary>
    internal ScanContext RentEncodings(string[] arrayEncodingIds)
    {
        ScanContext? context = null;
        lock (_gate)
        {
            context = Take();
        }

        if (context is null)
        {
            return new ScanContext(arrayEncodingIds);
        }

        context.RebindEncodings(arrayEncodingIds);
        return context;
    }

    /// <summary>The most contexts the scans took at once since the pool last let its contexts go, for tests and the bench.</summary>
    internal int MostLent
    {
        get
        {
            lock (_gate)
            {
                return _mostLent;
            }
        }
    }

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
            context = Take();
        }

        if (context is null)
        {
            return _metadata ? new ScanContext(file, ScanContext.MetadataCapacity) : ScanContext.Pooled(file);
        }

        context.Rebind(file);
        return context;
    }

    /// <summary>
    /// A context an earlier scan gave back, bound to no file and to <paramref name="options"/>, or a
    /// new one: what a reader of another format decodes into. A pool keeps the contexts of one kind,
    /// detached or bound to files, never both.
    /// </summary>
    /// <param name="options">The read options the scan decodes under.</param>
    internal ScanContext RentDetached(VortexReadOptions options)
    {
        ScanContext? context = null;
        lock (_gate)
        {
            context = Take();
        }

        if (context is null)
        {
            return new ScanContext([], options);
        }

        context.RebindDetached(options);
        return context;
    }

    /// <summary>Recycles <paramref name="context"/> and keeps it for a later scan, or disposes it past the bound.</summary>
    /// <param name="context">A context <see cref="Rent"/> gave, which nothing reads any more.</param>
    internal void Return(ScanContext context)
    {
        context.Recycle();
        lock (_gate)
        {
            // A context made elsewhere and given back counts no taking.
            _lent = Math.Max(0, _lent - 1);
            if (_count < Math.Max(_floor, _mostLent))
            {
                if (_count == _contexts.Length)
                {
                    Array.Resize(ref _contexts, Math.Min(2 * _contexts.Length, _ceiling));
                }

                _contexts[_count++] = context;
                return;
            }
        }

        context.Dispose();
    }

    /// <summary>A context kept, or null for none; the takings counted. Under the gate.</summary>
    private ScanContext? Take()
    {
        _taken = true;
        if (++_lent > _mostLent)
        {
            _mostLent = Math.Min(_lent, _ceiling);
        }

        if (_count == 0)
        {
            return null;
        }

        ScanContext context = _contexts[--_count]!;
        _contexts[_count] = null;
        return context;
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

            // The takings of before forgotten: the pool keeps its floor until scans take more at once again.
            _mostLent = _lent;
            if (_contexts.Length > _floor)
            {
                _contexts = new ScanContext?[_floor];
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
