// One column's state across the whole file - docs/11-write-strategy.md §3.0.
//
// The writer keeps a TREE of these, one per column the file summarizes, and each is a state machine
// fed by every WriteAsync rather than by every emission. That inversion is the point of stage 1:
// what a column knows about itself now accumulates as the rows arrive, in block coordinates counted
// from row 0 of the file, instead of being recomputed from whatever chunk the rows happened to land
// in.
//
// WHAT IT HOLDS TODAY is the open block's partial and the closed blocks' summaries - a few hundred
// bytes plus ~200 per block, which is what §3.7 budgets for it. The stages after this one add the
// chooser's plan memory (§3.4.3), the running distinct table (§3.2.2) and the index builders
// (docs/10-indexes.md §7) to the same object, which is why it is a class with a growing state and
// not a tuple.
using System.Collections.Generic;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>The per-column state the writer carries from the first batch to the footer.</summary>
internal sealed class ColumnWriter
{
    /// <summary>One summary per closed block, in block order, until the zone map is written.</summary>
    private readonly List<BlockStats> _closed = [];

    /// <summary>The block in progress; every batch that covers part of it folds into this one.</summary>
    private BlockStats _open;

    /// <summary>The closed blocks, in order. Zone <c>z</c> is entry <c>z</c>.</summary>
    internal IReadOnlyList<BlockStats> Blocks => _closed;

    /// <summary>
    /// Folds rows <c>[start, start + count)</c> of <paramref name="nodeIndex"/> into the open block.
    /// </summary>
    /// <param name="arena">The arena holding the batch.</param>
    /// <param name="nodeIndex">This column inside the batch.</param>
    /// <param name="start">First row of the range, inside the batch.</param>
    /// <param name="count">How many rows; the caller has cut the range at the block boundary.</param>
    internal void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count) =>
        BlockStatsPass.Accumulate(arena, nodeIndex, start, count, ref _open);

    /// <summary>Seals the open block and starts the next one.</summary>
    /// <remarks>
    /// Called when the block's last row has been seen, which is the moment its statistics are final
    /// - and, from stage 9 on, the moment its Bloom filter is built from the hash buffer
    /// (docs/10-indexes.md §4.3). A block with no rows is never closed: the caller only closes a
    /// boundary it has crossed.
    /// </remarks>
    internal void CloseBlock()
    {
        _closed.Add(_open);
        _open = default;
    }
}
