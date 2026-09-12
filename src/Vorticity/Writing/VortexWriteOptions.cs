// Write-time policy.
//
// One switch today, and it is the one a caller most plausibly wants off: compression trades write
// CPU and a decode step on read for file size, and a caller producing a scratch file that will be
// read once may prefer neither. It is ON by default because that is the useful answer for a format
// whose point is compression.
using System;

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
}
