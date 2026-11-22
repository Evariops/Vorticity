using System;

namespace Vorticity.Scanning;

/// <summary>
/// The batches a scan hands over to its consumer, which keeps each one past the scan's next step and gives it back once
/// done with it: a compaction's input windows, which a merge's writers read until they have copied the runs they cite.
/// </summary>
/// <remarks>
/// <para>
/// The scan keeps the lane that decoded a batch taken, its context and its arena, until the batch is given back, and with
/// it what the batch reads: the segments and the chunks it borrows stay held from the oldest batch taken on. A batch the
/// consumer does not take goes back at the scan's next step, as any batch does.
/// </para>
/// <para>
/// <see cref="Prepare"/> runs on the decode of each batch, on the lane that decoded it, or on the consumer's flow when it
/// decoded the batch itself: a merge's input encodes its keys there, beside the decode, rather than on the merge's thread.
/// What it returns stays with the lane, and is handed to it again with the lane's next batch, to reuse.
/// </para>
/// </remarks>
internal sealed class BatchHandover
{
    /// <param name="prepare">What the decode of each batch adds, given what the lane's batch before had; null for nothing.</param>
    internal BatchHandover(Func<RecordBatch, object?, object?>? prepare = null) => Prepare = prepare;

    /// <summary>What the decode of each batch adds, on its lane, given what the lane's batch before had.</summary>
    internal Func<RecordBatch, object?, object?>? Prepare { get; }

    /// <summary>What the consumer keeps with the scan for <see cref="Prepare"/> to read: a job's input, how its batches are shaped.</summary>
    internal object? State { get; set; }

    /// <summary>The windows the merge's inputs share past their own, which the scan takes from once its consumer reads it; null for none.</summary>
    internal DecodeAhead? Ahead { get; init; }

    /// <summary>
    /// The chunks of the file's variant columns the scan takes as their shredding holds them, for a consumer that writes
    /// them under a shredding of its own (<see cref="Arrays.ScanContext.TakenVariants"/>); null for none. Set before the scan starts.
    /// </summary>
    internal Types.Variants.TakenVariants? Taken { get; set; }

    /// <summary>
    /// The splits in flight the scan keeps of its own, past which it takes windows of <see cref="Ahead"/>; and those it holds
    /// now. Here rather than in the scan, which every scan would pay for; on the consumer's flow, the scan's pump.
    /// </summary>
    internal int Own = int.MaxValue;

    /// <inheritdoc cref="Own"/>
    internal int Borrowed;

    /// <summary>The decoded bytes the scan took each window of <see cref="Ahead"/> for, which it gives it back for: those of a split of its plan.</summary>
    internal long BorrowedBytes;

    /// <summary>
    /// What a batch of the scan weighs decoded as the consumer reads it of the file's own record, the rows of a batch at
    /// what the object's entry says a row of it weighs; 0 when it says nothing. What the scan reserves for a batch before
    /// one of its splits was measured: from then on, what the heaviest measured held.
    /// </summary>
    internal long WindowBytes;

    /// <summary>
    /// What the consumer reserves its memory in, which the scan reserves in before it takes it (<see cref="MemoryReserve"/>):
    /// a chunk's segments before it reads them, what a read of a chunk decodes whole before it decodes it, a batch before
    /// its decode; null for a consumer that reserves nothing. Set before the scan starts.
    /// </summary>
    /// <remarks>
    /// A split decoded ahead on a lane asks without waiting, and when the room is not there is left to the consumer
    /// (<see cref="BatchAsyncEnumerator"/>), which asks on its own flow, the job giving back what it started ahead, and
    /// runs it. A split is run again once what it lacked is reserved, or the chunk another lane was decoding is published.
    /// </remarks>
    internal MemoryReserve? Reserve { get; set; }

    /// <summary>The splits of the scan run again for room their consumer's reserve did not hold when they first asked.</summary>
    internal long Reruns;

    /// <summary>The splits of the scan run again once a chunk another lane was decoding was published, rather than decode it too.</summary>
    internal long ChunkWaits;

    /// <summary>Whether the consumer's flow runs a split of the scan itself now, reserving its room: on the consumer's flow.</summary>
    internal bool Running;

    /// <summary>The scan that hands its batches over, once started.</summary>
    internal BatchAsyncEnumerator? Scan { get; set; }

    /// <summary>
    /// Takes over the scan's current batch, when <paramref name="batch"/> is it or a view a wrapper made of it in its arena:
    /// the scan keeps the batch, its arena and its lane, until <see cref="GiveBack"/>. Says what <see cref="Prepare"/> made of
    /// the scan's batch, and whether <paramref name="batch"/> is that batch itself; a view the wrapper reuses at its next
    /// step, the consumer keeps a view of its own over the same arena.
    /// </summary>
    internal bool TryTake(RecordBatch batch, out object? prepared, out RecordBatch? taken)
    {
        if (Scan is { } scan && scan.TryHandOver(batch, out prepared, out bool same))
        {
            taken = same ? batch : scan.Current;
            return true;
        }

        prepared = null;
        taken = null;
        return false;
    }

    /// <summary>Gives back <paramref name="taken"/>, the scan's batch taken before, which the scan may then reuse.</summary>
    internal void GiveBack(RecordBatch taken) => Scan?.TakeBack(taken);
}
