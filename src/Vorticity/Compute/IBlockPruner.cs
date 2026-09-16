namespace Vorticity.Compute;

/// <summary>
/// A pruning structure, reduced to the one operation docs/11-write-strategy.md §6.1 gives every
/// one of them: refine a mask of live blocks.
/// </summary>
/// <remarks>
/// A pruner clears the bits of the blocks it can PROVE hold no matching row and touches nothing
/// else -- never a bit another pruner cleared, never a bit for a block it is unsure of. The scan
/// runs them cheapest first and stops when the mask is empty; because each one only ever clears,
/// that order changes the work done and never the answer.
/// </remarks>
internal interface IBlockPruner
{
    /// <summary>Kills every block of <paramref name="live"/> this structure proves cannot match.</summary>
    /// <param name="live">The scan's mask; only live blocks need looking at.</param>
    void Refine(BlockMask live);
}
