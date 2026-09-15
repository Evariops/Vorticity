// The layout of the two bit-width histograms docs/11-write-strategy.md §3.2 puts in the fused pass,
// and the one place that decides it.
//
// TWO HISTOGRAMS IN ONE BUFFER, because they are always produced together and always consumed
// together: entry `w` counts the values needing exactly `w` bits, `w` running 0 to 64 inclusive, in
// the RAW domain first and the ZIGZAG domain second. A value's raw width is what frame of reference
// costs when the reference is zero and what plain bit-packing costs always; its zigzag width is what
// `vortex.zigzag` costs. Neither needs the chunk's minimum, which is why both can be counted while
// the rows arrive -- and the framed histogram, which does need it, cannot (§3.2.3).
//
// WHY A FLAT `int[]` AND NOT A FIELD OF `BlockStats`: at 130 counters this is 520 bytes against the
// 56 of a whole `BlockStats`, and §3.7 budgets ~200 bytes per CLOSED block held to the footer. These
// are not held to the footer -- the chooser consumes them when the chunk is emitted and they are
// released -- so they live in a pooled buffer per open block instead, which is the "block scratch"
// row of that same table.
//
// WHAT THIS COSTS, MEASURED ONCE AND NOT AGAIN UNTIL THE REFACTOR IS WHOLE: about 1,2 ns per value
// on a column that never prices bit-packing (`runend`, 1M rows, +1,2 ms), inside §3.2's scalar
// budget of 3 ns. The old pipeline paid zero there because it computed nothing; the target pays
// this everywhere and deletes `BitPackPlan`'s own walks in exchange. Which side wins is a question
// for the finished pipeline, not for this commit -- IMPL-PLAN.md §1.2.
namespace Vorticity.Writing;

/// <summary>Where each width histogram lives inside the pair's shared buffer.</summary>
internal static class BitPackWidths
{
    /// <summary>Widths 0 to 64 inclusive, in one domain.</summary>
    internal const int Domain = 65;

    /// <summary>Where the zigzag domain starts.</summary>
    internal const int ZigZagOffset = Domain;

    /// <summary>How many counters a (column, block) pair of histograms needs.</summary>
    internal const int Length = Domain * 2;
}
