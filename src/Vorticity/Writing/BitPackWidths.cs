namespace Vorticity.Writing;

/// <summary>
/// Where each width histogram lives inside the pair's shared buffer: entry <c>w</c> counts the
/// values needing exactly <c>w</c> bits, the raw domain first and the zigzag domain second, the two
/// sharing a buffer because they are always produced and consumed together. Neither domain needs
/// the chunk's minimum, so both can be counted as the rows arrive, and the counters live in a
/// pooled per-block buffer rather than in the block's stats because the chooser consumes them when
/// the chunk is emitted instead of holding them to the footer.
/// </summary>
internal static class BitPackWidths
{
    /// <summary>Widths 0 to 64 inclusive, in one domain.</summary>
    internal const int Domain = 65;

    /// <summary>Where the zigzag domain starts.</summary>
    internal const int ZigZagOffset = Domain;

    /// <summary>How many counters a (column, block) pair of histograms needs.</summary>
    internal const int Length = Domain * 2;
}
