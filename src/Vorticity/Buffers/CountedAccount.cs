using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The blocks of one piece of a larger work, taken and given back in the work's account and counted here too: what the
/// piece holds now, measured rather than modelled. A slice of a merge (<c>SlicedMerge</c>): its inputs' batches and windows,
/// and the runs it keeps waiting in them.
/// </summary>
/// <remarks>
/// A slice merged ahead kept up to 16 384 runs waiting, each holding its input's batch: over log records, whose runs are
/// long, a slice's every batch, and two slices of two inputs held 608 MiB of a jobs' budget of 672 before the writer took
/// 65, and the job was given up three times (2026-11-18). What a slice holds bounds what it keeps waiting.
/// </remarks>
internal sealed class CountedAccount(MemoryAccount work) : MemoryAccount
{
    private long _held;

    /// <summary>The bytes the piece's blocks hold now.</summary>
    internal long Held => Volatile.Read(ref _held);

    internal override bool TryTake(long bytes)
    {
        if (!work.TryTake(bytes))
        {
            return false;
        }

        Interlocked.Add(ref _held, bytes);
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
