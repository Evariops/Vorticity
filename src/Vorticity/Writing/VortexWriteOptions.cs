// Write-time policy.
//
// Two switches. COMPRESSION is the one a caller most plausibly wants off: it trades write CPU and a
// decode step on read for file size, and a caller producing a scratch file that will be read once
// may prefer neither. It is ON by default because that is the useful answer for a format whose
// point is compression.
//
// The TARGET EDITION is the one that decides who can read the result, and it is not cosmetic: an
// edition is a frozen set of component ids, so naming one is the only way to say "any Vortex from
// version N onward can read this".
using System;
using Vorticity.Editions;

namespace Vorticity.Writing;

/// <summary>Policy for one written file.</summary>
public sealed class VortexWriteOptions
{
    /// <summary>The defaults: compression on.</summary>
    public static VortexWriteOptions Default { get; } = new VortexWriteOptions();

    /// <summary>
    /// Whether the writer may pick an encoding per column chunk. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Turning it off writes every column canonically, which is what the round-trip and
    /// cross-check suites use to separate "the writer produced the wrong bytes" from "the
    /// compressor chose badly".
    /// </remarks>
    public bool Compress { get; init; } = true;

    /// <summary>
    /// The edition every component in the file must belong to. Default
    /// <see cref="EditionRegistry.Newest"/>, which is what the reference writer defaults to.
    /// </summary>
    /// <remarks>
    /// THE DEFAULT IS THE NEWEST FROZEN EDITION, not the read-forever floor, and the difference
    /// matters because the floor is not a target this writer can meet for every schema. Two of its
    /// own outputs say so: `vortex.zoned` and all six zone-map aggregates first appear in
    /// `core2026.08.0`, and `vortex.uuid` in `core2026.08.3` (spec/editions). A default of
    /// `core2025.05.0` would have been a claim the files themselves contradict, and one that turns
    /// a uuid column into a write failure for nobody's benefit.
    ///
    /// LOWER TARGETS ARE HONOURED RATHER THAN APPROXIMATED. Below `core2026.08.0` the zone map is
    /// omitted, because pruning is an optimization and dropping it costs correctness nothing; the
    /// scheme candidates are derived from the target before anything is measured, so the
    /// compressor never elects an encoding it cannot serialize. What the writer genuinely cannot
    /// express within the target - a List column at `core2025.05.0`, whose canonical form here is
    /// `vortex.listview`, which that edition does not carry - FAILS THE WRITE, naming the id and
    /// the edition that introduced it. Producing a file the target's readers cannot open would be
    /// the one unacceptable answer.
    /// </remarks>
    public VortexEdition TargetEdition { get; init; } = EditionRegistry.Newest;
}
