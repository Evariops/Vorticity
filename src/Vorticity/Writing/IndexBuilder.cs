// What every streaming index builder answers to (docs/10-indexes.md §7.2).
//
// THE WRITER'S RHYTHM, AND NOTHING ELSE. Rows arrive in ranges while their batch is live; blocks
// close; chunks close, each a whole number of blocks; the data ends. A builder turns those events
// into payloads, which the index writer lays into blobs between chunks, and it can give up at any
// of them -- whole, with a reason.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>One column's index, built as the column is written.</summary>
internal abstract class IndexBuilder : IDisposable
{
    /// <summary>Payloads closed and waiting to be written.</summary>
    internal Queue<PendingPayload> Pending { get; } = new Queue<PendingPayload>();

    /// <summary>Why the whole index was dropped; <see langword="null"/> while it lives.</summary>
    internal string? Abandoned { get; private set; }

    /// <summary>Feeds rows <c>[start, start + count)</c> of the column's node.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="nodeIndex">The column in it.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    internal abstract void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count);

    /// <summary>Seals the open block.</summary>
    internal abstract void CloseBlock();

    /// <summary>A chunk went out: its blocks and its rows.</summary>
    /// <param name="firstBlock">Its first block.</param>
    /// <param name="blocks">How many blocks it covers.</param>
    /// <param name="firstRow">Its first row.</param>
    /// <param name="rows">How many rows.</param>
    internal virtual void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
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
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
    }
}
