using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// The blocks of one piece of a larger work, taken and given back in the work's account and counted here too: what the
/// piece holds now, and the most it held, measured rather than modelled. A slice of a merge (<c>SlicedMerge</c>): its
/// inputs' batches and windows, and the runs it keeps waiting in them. An input of a merge: its scan's reads, what it
/// decodes whole of its chunks, and its batches (<c>JobMemory.OpenInput</c>).
/// </summary>
/// <remarks>
/// <para>
/// A slice merged ahead kept up to 16 384 runs waiting, each holding its input's batch: over log records, whose runs are
/// long, a slice's every batch, and two slices of two inputs held 608 MiB of a jobs' budget of 672 before the writer took
/// 65, and the job was given up three times (2026-11-18). What a slice holds bounds what it keeps waiting.
/// </para>
/// <para>
/// With a <paramref name="reserve"/>, the piece's blocks take first what it reserved before it rented them
/// (<see cref="MemoryReserve.Take"/>): bytes the work's budget counts already, which the work's account does not reserve
/// again. A block the account then refuses is one the piece did not reserve and the budget does not hold: its refusal says
/// what was lacked (<see cref="VortexMemoryException.ShortOfRoom"/>), for the piece to reserve it and run its step again.
/// </para>
/// </remarks>
internal sealed class CountedAccount(MemoryAccount work, MemoryReserve? reserve = null) : MemoryAccount
{
    private long _held;
    private long _peak;

    /// <summary>The bytes the piece's blocks hold now.</summary>
    internal long Held => Volatile.Read(ref _held);

    /// <summary>The most bytes the piece's blocks held at once.</summary>
    internal long Peak => Volatile.Read(ref _peak);

    /// <summary>What the piece reserves in before it rents; null for a piece that reserves nothing.</summary>
    internal MemoryReserve? Reserve => reserve;

    internal override bool TryTake(long bytes) => TryTake(bytes, 0, kept: false);

    /// <inheritdoc/>
    internal override bool TryTake(long bytes, long reserved, bool kept)
    {
        // What the piece reserved first; past it, what the budget holds now, and no more: a piece that reserves is one that
        // can stop and run its step again, where the work's own account would count the block past its budget.
        long spare = 0;
        if (reserve is not null)
        {
            spare = reserve.Take(bytes - reserved);
            long rest = bytes - reserved - spare;
            if (rest > 0 && !reserve.Holds(rest))
            {
                reserve.PutBack(spare);
                return false;
            }
        }

        if (!work.TryTake(bytes, reserved + spare, kept))
        {
            reserve?.PutBack(spare);
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

    internal override VortexMemoryException Refused(long bytes)
    {
        VortexMemoryException refused = work.Refused(bytes);
        if (reserve is not null && refused.ShortOfRoom == 0)
        {
            refused.ShortOfRoom = bytes;
        }

        return refused;
    }
}
