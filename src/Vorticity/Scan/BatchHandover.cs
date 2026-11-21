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
    /// What the consumer counts a batch of the scan to weigh decoded, from what it knows of the file's rows; 0 for none.
    /// The scan takes a window of <see cref="Ahead"/> for that when it is more than its own count of a split, which goes
    /// by the schema alone: a split of a shredded variant's rows weighed several times what its schema says.
    /// </summary>
    internal long WindowBytes;

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
