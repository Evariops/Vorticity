// The tiers a terminal is pushed through - docs/12-index-reads.md §5.2 and §5.3 - as switches,
// because the property that makes a terminal testable is that a wrong proof is a wrong answer:
// §11 runs every count and every extreme with each tier forced off in turn and asserts that the
// numbers agree. Internal, and meant for the tests; a caller who wants a slower answer has
// WithPruning(false).
using System;

namespace Vorticity.Scan;

/// <summary>Which proofs a terminal may take before decoding.</summary>
[Flags]
internal enum TerminalTiers
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

    /// <summary>
    /// The file's own statistic answers a <c>Min</c> or <c>Max</c> over the whole file, when it
    /// is <c>Exact</c> (§5.3, first resolution).
    /// </summary>
    FileStatistic = 8,

    /// <summary>
    /// The zone map's bounds answer a <c>Min</c> or <c>Max</c> over a whole zone: an
    /// <c>Exact</c> bound is the answer, an <c>Inexact</c> one a candidate to decode only when it
    /// could beat the best (§5.3, second resolution).
    /// </summary>
    ZoneBounds = 16,

    /// <summary>Every tier, cheapest first.</summary>
    All = ExactCover | FullBlock | Decode | FileStatistic | ZoneBounds,
}
