using System;

namespace Vorticity.Scanning;

/// <summary>
/// Which proofs a terminal may take before decoding. The tiers are switches because a wrong proof
/// is a wrong answer: the tests run every count and every extreme with each tier forced off in
/// turn and require the numbers to agree. A caller who merely wants the slower answer turns
/// pruning off instead.
/// </summary>
[Flags]
internal enum TerminalTiers
{
    /// <summary>Decode only.</summary>
    None = 0,

    /// <summary>
    /// An exact source -- a sorted column or sorted runs -- covers the predicate: the count is the
    /// sum of its slices, an extreme a seek to one end (<c>Keys/ExactCover</c>).
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
    /// The file's own statistic answers a <c>Min</c> or <c>Max</c> over the whole file, when that
    /// statistic is <c>Exact</c>.
    /// </summary>
    FileStatistic = 8,

    /// <summary>
    /// The zone map's bounds answer a <c>Min</c> or <c>Max</c> over a whole zone: an
    /// <c>Exact</c> bound is the answer, an <c>Inexact</c> one a candidate to decode only when it
    /// could beat the best found so far.
    /// </summary>
    ZoneBounds = 16,

    /// <summary>Every tier, cheapest first.</summary>
    All = ExactCover | FullBlock | Decode | FileStatistic | ZoneBounds,
}
