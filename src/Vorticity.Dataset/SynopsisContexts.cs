using System;
using Vorticity.Arrays;

namespace Vorticity.Dataset;

/// <summary>
/// The contexts the synopsis tables of objects read without their open are decoded into, kept from one object to the next:
/// a context is a dozen arrays, which every object of a question built and dropped, half of what a warm question over 48
/// objects allocated. Their arenas of dtypes are kept too, the batches of a set holding none of them once the set is let
/// go: an arena made for each object was a sixth of what such a question allocated.
/// </summary>
/// <remarks>
/// A pool of its own, apart from the scans', whose contexts are bound to files. Its bound is that of the scans' pool, three
/// contexts a lane: a context past it is disposed, and the kept ones go after a collection once no read has taken one over
/// a minute.
/// </remarks>
internal static class SynopsisContexts
{
    /// <summary>The pool every read of an object's synopsis tables takes its context from.</summary>
    internal static ScanContexts Shared { get; } = new ScanContexts(Math.Max(16, 3 * Environment.ProcessorCount)) { KeepsTypes = true }.Swept();
}
