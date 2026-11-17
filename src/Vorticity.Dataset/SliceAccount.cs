using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// The blocks of one slice of a merge (<see cref="SlicedMerge"/>), taken and given back in the account of the job's inputs
/// and counted here too: what the slice holds now, its inputs' batches and windows, and the runs it keeps waiting in them.
/// </summary>
/// <remarks>
/// A slice merged ahead kept up to 16 384 runs waiting, each holding its input's batch: over log records, whose runs are
/// long, a slice's every batch, and two slices of two inputs held 608 MiB of a jobs' budget of 672 before the writer took
/// 65, and the job was given up three times (2026-11-18). What a slice holds bounds what it keeps waiting.
/// </remarks>
internal sealed class SliceAccount(MemoryAccount inputs) : MemoryAccount
{
    private long _held;

    /// <summary>The bytes the slice's blocks hold now.</summary>
    internal long Held => Volatile.Read(ref _held);

    internal override bool TryTake(long bytes)
    {
        if (!inputs.TryTake(bytes))
        {
            return false;
        }

        Interlocked.Add(ref _held, bytes);
        return true;
    }

    internal override void Reused(long bytes) => inputs.Reused(bytes);

    internal override void Give(long bytes)
    {
        Interlocked.Add(ref _held, -bytes);
        inputs.Give(bytes);
    }

    internal override void Give(long bytes, bool kept)
    {
        Interlocked.Add(ref _held, -bytes);
        inputs.Give(bytes, kept);
    }

    internal override VortexMemoryException Refused(long bytes) => inputs.Refused(bytes);
}
