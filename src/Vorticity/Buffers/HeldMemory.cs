using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The kinds of memory kept from one operation to the next, in the order the registry asks them back: what rebuilds
/// without a request first, what costs requests to rebuild last.
/// </summary>
internal enum KeptKind
{
    /// <summary>The blocks the pool keeps for the next rents, its byte blocks and its typed ones.</summary>
    Blocks,

    /// <summary>The segments a cache keeps for the next scans of the same files.</summary>
    Segments,

    /// <summary>The objects a dataset handle keeps open for its next questions.</summary>
    Objects,

    /// <summary>The tree pages a dataset handle keeps for its next walks.</summary>
    Pages,

    /// <summary>What a dataset handle keeps parsed for its next questions: synopses, partitions' indexes, filters' pages, index entries.</summary>
    Memos,
}

/// <summary>
/// What keeps memory from one operation to the next, so that the next goes faster: the pool's blocks kept for the next
/// rent, a cache of segments, a dataset handle's caches. It says what it is and what it holds now, gives back what it
/// keeps above its floors when the registry asks, and lets go of what nothing used of late.
/// </summary>
internal interface IHeldMemory
{
    /// <summary>What it is, as the gauges tag it: <c>pool</c>, <c>segments</c>, <c>datasets</c>.</summary>
    string Holder { get; }

    /// <summary>The bytes it holds now.</summary>
    long HeldBytes { get; }

    /// <summary>
    /// Lets go of up to <paramref name="bytes"/> of what it keeps of <paramref name="kind"/>, above that kind's floor: the
    /// bytes let go, none when it keeps nothing of the kind.
    /// </summary>
    long GiveBack(KeptKind kind, long bytes);

    /// <summary>
    /// Lets go of what nothing took since it was last asked, above its floors: after a collection of the oldest generation
    /// (<paramref name="collection"/>) for what rebuilds without a request, at each turn of the registry's slow timer for
    /// everything.
    /// </summary>
    void LetGoUnused(bool collection);
}

/// <summary>
/// The registry of the memory kept: every <see cref="IHeldMemory"/> of the process, held weakly, so that a holder nothing
/// else references goes with its memory and leaves the list with it. What the gauges and
/// <see cref="VortexDiagnostics.GetMemoryReport"/> read, and what gives the memory kept back, in the order of
/// <see cref="KeptKind"/>, when the work needs its room.
/// </summary>
/// <remarks>
/// <para>
/// Kept memory has no budget of its own: it holds what the work leaves, above floors where rebuilding costs requests.
/// It lets go three ways. After each collection of the oldest generation, the pool lets go of what no rent took since
/// the one before: under pressure the collector runs more often, and the pool empties faster. At each turn of a slow
/// timer, every holder does the same, caches included: an idle process neither allocates nor collects, and its kept
/// memory goes down to its floors within two turns. And when a reservation needs room, or the machine's memory load is
/// high at a collection, everything above the floors goes, in the order of the kinds.
/// </para>
/// </remarks>
internal static class HeldMemory
{
    /// <summary>
    /// The period of the slow timer that lets go of what nothing used, in milliseconds: an idle process is down to its floors
    /// within a minute. A turn took 1.1 to 1.6 ms with 40 handles open, and their floors kept everything they read: the
    /// questions asked after two turns made no request, where the first round made 160 (<c>--micro reader</c>, 2026-11-16).
    /// </summary>
    internal const int RestTickMilliseconds = 30_000;

    private static readonly ConditionalWeakTable<IHeldMemory, object?> Holders = new ConditionalWeakTable<IHeldMemory, object?>();

    // The sweeper after collections and the slow timer, held for as long as the process runs.
    private static readonly Sweeper s_sweeper = Sweeper.Start();

    // 1 while a give-back of everything above the floors, asked by a high memory load, is queued or under way.
    private static int s_relieving;

    /// <summary>Lists <paramref name="holder"/> until it is let go, or <see cref="Unregister"/> takes it out.</summary>
    internal static void Register(IHeldMemory holder)
    {
        GC.KeepAlive(s_sweeper);
        Holders.AddOrUpdate(holder, null);
    }

    /// <summary>Takes <paramref name="holder"/> out of the list, once it holds nothing more.</summary>
    internal static void Unregister(IHeldMemory holder) => Holders.Remove(holder);

    /// <summary>
    /// Gives back up to <paramref name="bytes"/> of the memory kept above its floors, kind after kind in the order of
    /// <see cref="KeptKind"/>: the bytes let go, which the next collection takes. The blocks a cache lets go return to the
    /// pool, which would keep them: they go too.
    /// </summary>
    internal static long GiveBack(long bytes)
    {
        long given = 0;
        bool cachesGave = false;
        for (KeptKind kind = KeptKind.Blocks; kind <= KeptKind.Memos && given < bytes; kind++)
        {
            long before = given;
            foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
            {
                given += pair.Key.GiveBack(kind, bytes - given);
                if (given >= bytes)
                {
                    break;
                }
            }

            cachesGave |= kind != KeptKind.Blocks && given > before;
        }

        if (cachesGave)
        {
            foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
            {
                pair.Key.GiveBack(KeptKind.Blocks, long.MaxValue);
            }
        }

        return given;
    }

    /// <summary>Has every holder let go of what nothing took since it was last asked (<see cref="IHeldMemory.LetGoUnused"/>).</summary>
    internal static void LetGoUnused(bool collection)
    {
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            pair.Key.LetGoUnused(collection);
        }
    }

    /// <summary>The bytes each kind of holder holds now, and how many holders of each kind there are.</summary>
    internal static Dictionary<string, (long Bytes, int Holders)> ByHolder()
    {
        Dictionary<string, (long Bytes, int Holders)> held = new Dictionary<string, (long Bytes, int Holders)>(StringComparer.Ordinal);
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            IHeldMemory holder = pair.Key;
            (long bytes, int count) = held.GetValueOrDefault(holder.Holder);
            held[holder.Holder] = (bytes + holder.HeldBytes, count + 1);
        }

        return held;
    }

    /// <summary>The bytes every holder holds now: what the registry knows of the memory kept.</summary>
    internal static long TotalBytes()
    {
        long bytes = 0;
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            bytes += pair.Key.HeldBytes;
        }

        return bytes;
    }

    /// <summary>The bytes the holders of type <typeparamref name="T"/> hold now.</summary>
    internal static long BytesOf<T>()
        where T : IHeldMemory => BytesOf<T>(static holder => holder.HeldBytes);

    /// <summary>The sum of <paramref name="bytes"/> over the holders of type <typeparamref name="T"/>.</summary>
    internal static long BytesOf<T>(Func<T, long> bytes)
        where T : IHeldMemory
    {
        long sum = 0;
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            if (pair.Key is T holder)
            {
                sum += bytes(holder);
            }
        }

        return sum;
    }

    /// <summary>
    /// Gives back everything above the floors on a thread of the pool, once at a time: the machine's memory load is high.
    /// A sweep runs on the finalizer thread, which closing a dataset's objects must not hold up.
    /// </summary>
    private static void Relieve()
    {
        if (Interlocked.Exchange(ref s_relieving, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static _ =>
            {
                try
                {
                    GiveBack(long.MaxValue);
                }
                finally
                {
                    Volatile.Write(ref s_relieving, 0);
                }
            },
            null);
    }

    /// <summary>What sweeps after collections of the oldest generation, and turns the slow timer.</summary>
    private sealed class Sweeper(Timer timer) : ISweptAfterCollections
    {
        /// <summary>The slow timer, held so that it turns for as long as the process runs.</summary>
        internal Timer Timer { get; } = timer;

        internal static Sweeper Start()
        {
            // Made with no flow of the thread that first registers: its locals are no business of the timer's.
            Timer timer;
            using (ExecutionContext.SuppressFlow())
            {
                timer = new Timer(static _ => LetGoUnused(collection: false), null, RestTickMilliseconds, RestTickMilliseconds);
            }

            Sweeper sweeper = new Sweeper(timer);
            CollectionSweeper<Sweeper>.Register(sweeper);
            return sweeper;
        }

        public void Sweep()
        {
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            if (memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes)
            {
                Relieve();
                return;
            }

            LetGoUnused(collection: true);
        }
    }
}
