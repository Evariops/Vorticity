// The tiers a count is pushed through - docs/12-index-reads.md §5.2 - as switches, because the
// property that makes a count testable is that a wrong proof is a wrong count: §11 runs every
// count with each tier forced off in turn and asserts that the numbers agree. Internal, and meant
// for the tests; a caller who wants a slower count has WithPruning(false).
using System;

namespace Vorticity.Scan;

/// <summary>Which proofs a count may take before decoding.</summary>
[Flags]
internal enum CountTiers
{
    /// <summary>Decode only.</summary>
    None = 0,

    /// <summary>
    /// An exact index covers the predicate and the count is the sum of its slices. No source
    /// provides one yet; the flag is here so that the switch exists when one does.
    /// </summary>
    ExactCover = 1,

    /// <summary>The zone maps decide the block whole: <c>ZonePruner.TryCount</c>.</summary>
    FullBlock = 2,

    /// <summary>
    /// The block is decoded and the filter evaluated. Always taken when nothing else decides; the
    /// flag names it, it does not turn it off.
    /// </summary>
    Decode = 4,

    /// <summary>Every tier, cheapest first.</summary>
    All = ExactCover | FullBlock | Decode,
}
