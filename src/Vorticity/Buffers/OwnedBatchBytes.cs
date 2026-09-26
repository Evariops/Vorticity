using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The native bytes the batches callers own hold, and a collection asked for when they pile up.
/// </summary>
/// <remarks>
/// <para>
/// A batch its caller drops undisposed frees its blocks from their finalizers alone, and nothing
/// asks for the collection that would run them: the batch allocated a few managed bytes for
/// megabytes of native ones. Declaring the blocks to the GC is no answer: its pressure counts every
/// declaration since the last collection, whatever was taken back since, so a loop that disposes
/// each of its batches would collect every few batches. The balance counted here goes up with a
/// batch and down with its disposal, and only a balance past a threshold asks for a collection, in
/// the background: a loop that disposes its batches never reaches it.
/// </para>
/// <para>
/// The threshold doubles the balance at each request, so that a caller keeping many batches on
/// purpose costs a collection each time what it keeps doubles, and halves again, down to its floor,
/// once the balance falls under a quarter of it.
/// </para>
/// </remarks>
internal sealed class OwnedBatchBytes
{
    /// <summary>The bytes held for the blocks of each watched pool; null until a pool is watched, so that a pool nobody watches does not look.</summary>
    private static ConditionalWeakTable<AlignedBufferPool, StrongBox<long>>? Watched;

    private readonly long _floor;
    private readonly Action _collect;
    private long _held;
    private long _threshold;

    /// <summary>A balance that calls <paramref name="collect"/> once it reaches <paramref name="floor"/> bytes.</summary>
    /// <param name="floor">The lowest threshold.</param>
    /// <param name="collect">Asks for a collection.</param>
    internal OwnedBatchBytes(long floor, Action collect)
    {
        _floor = floor;
        _threshold = floor;
        _collect = collect;
    }

    /// <summary>
    /// The balance of the process, which asks for a background collection of every generation past
    /// 256 MiB, or past an eighth of the memory the GC may use when that is less.
    /// </summary>
    internal static OwnedBatchBytes Shared { get; } = new OwnedBatchBytes(
        SharedFloor(), static () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false));

    /// <summary>The bytes held.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The balance that asks for the next collection.</summary>
    internal long Threshold => Volatile.Read(ref _threshold);

    /// <summary>Counts, from now on, the bytes the blocks of <paramref name="pool"/> hold for batches callers own.</summary>
    /// <param name="pool">The pool to watch.</param>
    /// <returns>The count, in bytes.</returns>
    internal static StrongBox<long> Watch(AlignedBufferPool pool) =>
        LazyInitializer.EnsureInitialized(ref Watched).GetValue(pool, static _ => new StrongBox<long>());

    /// <summary>Counts <paramref name="bytes"/> more, and asks for a collection when the balance reaches the threshold.</summary>
    /// <param name="bytes">The bytes of the blocks a batch holds.</param>
    internal void Hold(long bytes)
    {
        long held = Interlocked.Add(ref _held, bytes);
        long threshold = Volatile.Read(ref _threshold);
        if (held >= threshold && Interlocked.CompareExchange(ref _threshold, held * 2, threshold) == threshold)
        {
            _collect();
        }
    }

    /// <summary>Counts <paramref name="bytes"/> less, and lowers the threshold again once the balance is under a quarter of it.</summary>
    /// <param name="bytes">The bytes of the blocks given back or freed.</param>
    internal void Release(long bytes)
    {
        long held = Interlocked.Add(ref _held, -bytes);
        long threshold = Volatile.Read(ref _threshold);
        if (threshold > _floor && held < threshold / 4)
        {
            Interlocked.CompareExchange(ref _threshold, Math.Max(_floor, threshold / 2), threshold);
        }
    }

    /// <summary><see cref="Hold"/> on <see cref="Shared"/>, or <see cref="Release"/> when negative, for blocks of <paramref name="pool"/>.</summary>
    /// <param name="pool">The pool the blocks were rented from, or null.</param>
    /// <param name="bytes">The bytes, negative to take them back.</param>
    internal static void Count(AlignedBufferPool? pool, long bytes)
    {
        if (bytes > 0)
        {
            Shared.Hold(bytes);
        }
        else
        {
            Shared.Release(-bytes);
        }

        if (Watched is { } watched && pool is not null && watched.TryGetValue(pool, out StrongBox<long>? count))
        {
            Interlocked.Add(ref count.Value, bytes);
        }
    }

    private static long SharedFloor()
    {
        const long Floor = 256L * 1024 * 1024;
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? Math.Min(Floor, available / 8) : Floor;
    }
}
