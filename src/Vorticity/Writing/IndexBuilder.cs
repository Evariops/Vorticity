using System;
using System.Collections.Generic;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>What the writer knows about a column when one of its chunks closes.</summary>
internal readonly record struct ColumnFacts(long ColumnBytes);

/// <summary>
/// One column's index, built as the column is written. It may give up whole at any of the writer's
/// events, with a reason; the judgement always runs before its own payloads go out, so a builder
/// given up on leaves nothing in the file.
/// </summary>
internal abstract class IndexBuilder : IDisposable
{
    private long _pending;

    internal Queue<PendingPayload> Pending { get; } = new Queue<PendingPayload>();

    /// <summary>Why the whole index was dropped; <see langword="null"/> while it lives.</summary>
    internal string? Abandoned { get; private set; }

    /// <summary>
    /// The share of the column's bytes, in parts per thousand, this builder may take before it is
    /// given up; <c>0</c> for a builder the caller asked for by name.
    /// </summary>
    internal int AutoShare { get; init; }

    /// <summary>Everything it costs: written payloads, queued estimates, and what is still open.</summary>
    internal long Bytes => WrittenBytes + _pending + OpenBytes;

    internal long WrittenBytes { get; private set; }

    /// <summary>What the builder has built but still holds unqueued.</summary>
    protected virtual long OpenBytes => 0;

    /// <summary>Feeds rows <c>[start, start + count)</c> of the column's node.</summary>
    internal abstract void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count);

    /// <summary>Seals the open block.</summary>
    internal abstract void CloseBlock();

    /// <summary>
    /// What the statistics say of the column's first block, before it is sealed: the earliest fact a
    /// builder can decide on.
    /// </summary>
    internal virtual void FirstBlock(bool? sorted)
    {
    }

    /// <summary>
    /// Numbers the builder's first block and row, for an append: the blocks before this one belong
    /// to the old file.
    /// </summary>
    internal virtual void Start(int block, long row)
    {
    }

    /// <summary>A chunk went out: its blocks and its rows.</summary>
    internal virtual void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
    }

    /// <summary>
    /// The verdict after a chunk: gives up when the builder costs more of the column than its share,
    /// or when the column's facts say it cannot serve.
    /// </summary>
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
                $"its share of {AutoShare}‰. An explicit policy overrides the share");
        }
    }

    /// <summary>The data ended; close what only the end closes.</summary>
    internal virtual void EndOfData()
    {
    }

    /// <summary>Drops the whole index; anything already written stays in the file as dead weight.</summary>
    internal virtual void Abandon(string reason)
    {
        Abandoned ??= reason;
        Pending.Clear();
        _pending = 0;
    }

    /// <summary>Queues a payload, counting its estimate toward the builder.</summary>
    internal void Enqueue(PendingPayload payload)
    {
        payload.Owner = this;
        _pending += payload.Estimate;
        Pending.Enqueue(payload);
    }

    /// <summary>A payload of this builder landed: its estimate becomes its real length.</summary>
    internal void Placed(PendingPayload payload, long length)
    {
        _pending = Math.Max(0, _pending - payload.Estimate);
        WrittenBytes += length;
    }

    public virtual void Dispose()
    {
    }
}
