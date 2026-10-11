using Vorticity.Arrays;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// The contexts Parquet scans decode their fields' batches in, kept from one scan to the next, detached
/// from any Vortex file: those of the fields that decode side by side or ahead of the read.
/// </summary>
/// <remarks>
/// A pool of its own, apart from the core's, whose contexts are bound to Vortex files. Its bound is
/// what a few wide scans hold at once, three contexts a field of a hundred: a context past it is
/// disposed, and the kept ones go after a collection once no scan has taken one over a minute.
/// </remarks>
internal static class DetachedContexts
{
    /// <summary>The pool every Parquet scan takes its contexts from.</summary>
    internal static ScanContexts Shared { get; } = new ScanContexts(1_024).Swept();
}
