using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The blocks of one piece of a larger work, taken and given back in the work's account and counted here too: what the
/// piece holds now, and the most it held, measured rather than modelled. A slice of a merge (<c>SlicedMerge</c>): its
/// inputs' batches and windows, and the runs it keeps waiting in them. An input of a merge: its scan's reads, what it
/// decodes whole of its chunks, and its batches (<c>JobMemory.OpenInput</c>).
/// </summary>
/// <remarks>
/// A slice merged ahead kept up to 16 384 runs waiting, each holding its input's batch: over log records, whose runs are
/// long, a slice's every batch, and two slices of two inputs held 608 MiB of a jobs' budget of 672 before the writer took
/// 65, and the job was given up three times (2026-11-18). What a slice holds bounds what it keeps waiting.
/// </remarks>
internal sealed class CountedAccount(MemoryAccount work) : MemoryAccount
{
    private long _held;
    private long _peak;

    /// <summary>The bytes the piece's blocks hold now.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The most bytes the piece's blocks held at once.</summary>
    internal long Peak => Volatile.Read(ref _peak);

    internal override bool TryTake(long bytes)
    {
        if (!work.TryTake(bytes))
        {
            return false;
        }

        // A block is rented a chunk or a batch, never a row: the peak is kept on that path, one compare a block.
        long held = Interlocked.Add(ref _held, bytes);
        long peak = Volatile.Read(ref _peak);
        while (held > peak)
        {
            long seen = Interlocked.CompareExchange(ref _peak, held, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }

        return true;
    }

    internal override void Reused(long bytes) => work.Reused(bytes);

    internal override void Give(long bytes)
    {
        Interlocked.Add(ref _held, -bytes);
        work.Give(bytes);
    }

    internal override void Give(long bytes, bool kept)
    {
        Interlocked.Add(ref _held, -bytes);
        work.Give(bytes, kept);
    }

    internal override VortexMemoryException Refused(long bytes) => work.Refused(bytes);
}
