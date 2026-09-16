// What every streaming index builder answers to (docs/10-indexes.md §7.2).
//
// THE WRITER'S RHYTHM, AND NOTHING ELSE. Rows arrive in ranges while their batch is live; blocks
// close; chunks close, each a whole number of blocks; the data ends. A builder turns those events
// into payloads, which the index writer lays into blobs between chunks, and it can give up at any
// of them -- whole, with a reason.
//
// `AUTO` JUDGES AS EARLY AS IT CAN (10 §5.5): the decision is when to abandon, never when to start.
// A builder born of `Auto` hears whether the column's first block climbed, sizes its filters against
// the column's raw bytes at every block, and is judged against the column's written bytes at every
// chunk -- always before its payloads are written, so a builder given up on leaves nothing in the
// file, and a column it does not serve costs at most a generation of hashing.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>What the writer knows about a column when one of its chunks closes.</summary>
/// <param name="ColumnBytes">The column's data bytes written so far.</param>
internal readonly record struct ColumnFacts(long ColumnBytes);

/// <summary>One column's index, built as the column is written.</summary>
internal abstract class IndexBuilder : IDisposable
{
    private long _pending;

    /// <summary>Payloads closed and waiting to be written.</summary>
    internal Queue<PendingPayload> Pending { get; } = new Queue<PendingPayload>();

    /// <summary>Why the whole index was dropped; <see langword="null"/> while it lives.</summary>
    internal string? Abandoned { get; private set; }

    /// <summary>
    /// The share of the column's bytes, in parts per thousand, this builder may take before
    /// <c>Auto</c> gives it up; <c>0</c> for a builder the caller asked for by name.
    /// </summary>
    internal int AutoShare { get; init; }

    /// <summary>
    /// The bytes its payloads took in the file, the estimate of those waiting, and what it holds
    /// that is not a payload yet.
    /// </summary>
    internal long Bytes => WrittenBytes + _pending + OpenBytes;

    /// <summary>The bytes its payloads took in the file.</summary>
    internal long WrittenBytes { get; private set; }

    /// <summary>What the builder has built and not yet queued: a Bloom generation's open filters.</summary>
    protected virtual long OpenBytes => 0;

    /// <summary>Feeds rows <c>[start, start + count)</c> of the column's node.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="nodeIndex">The column in it.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    internal abstract void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count);

    /// <summary>Seals the open block.</summary>
    internal abstract void CloseBlock();

    /// <summary>
    /// What the statistics say of the column's first block, before it is sealed: the earliest fact
    /// `Auto` can act on.
    /// </summary>
    /// <param name="sorted">The block's `is_sorted`, when tracked.</param>
    internal virtual void FirstBlock(bool? sorted)
    {
    }

    /// <summary>
    /// Numbers the builder's first block and row, for an append (docs/11 §3.8): its runs cover the
    /// file's blocks from <paramref name="block"/>, the ones before being the old file's.
    /// </summary>
    /// <param name="block">The first block this builder sees.</param>
    /// <param name="row">Its first row.</param>
    internal virtual void Start(int block, long row)
    {
    }

    /// <summary>A chunk went out: its blocks and its rows.</summary>
    /// <param name="firstBlock">Its first block.</param>
    /// <param name="blocks">How many blocks it covers.</param>
    /// <param name="firstRow">Its first row.</param>
    /// <param name="rows">How many rows.</param>
    internal virtual void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
    }

    /// <summary>
    /// <c>Auto</c>'s verdict after a chunk: gives up when the builder costs more of the column than
    /// its share, or when the column's facts say it cannot serve.
    /// </summary>
    /// <param name="facts">What the writer knows of the column.</param>
    internal virtual void Judge(ColumnFacts facts)
    {
        if (AutoShare <= 0 || Abandoned is not null || facts.ColumnBytes <= 0)
        {
            return;
        }

        if (Bytes * 1000 > facts.ColumnBytes * AutoShare)
        {
            Abandon(
                $"Auto gave it up: {Bytes} bytes against {facts.ColumnBytes} bytes of column, over " +
                $"its share of {AutoShare}‰ (docs/10-indexes.md §5.5)");
        }
    }

    /// <summary>The data ended; close what only the end closes.</summary>
    internal virtual void EndOfData()
    {
    }

    /// <summary>Drops the whole index, with its reason; what was written stays dead weight.</summary>
    /// <param name="reason">Why.</param>
    internal virtual void Abandon(string reason)
    {
        Abandoned ??= reason;
        Pending.Clear();
        _pending = 0;
    }

    /// <summary>Queues a payload, counting its estimate toward the builder.</summary>
    /// <param name="payload">The payload.</param>
    protected void Enqueue(PendingPayload payload)
    {
        payload.Owner = this;
        _pending += payload.Estimate;
        Pending.Enqueue(payload);
    }

    /// <summary>A payload of this builder landed: its estimate becomes its length.</summary>
    /// <param name="payload">The payload.</param>
    /// <param name="length">Its bytes in the file.</param>
    internal void Placed(PendingPayload payload, long length)
    {
        _pending = Math.Max(0, _pending - payload.Estimate);
        WrittenBytes += length;
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
    }
}
