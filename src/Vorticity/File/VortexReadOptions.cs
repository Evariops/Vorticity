// Read-time policy. Carried on the open file and copied into every ScanContext, so it is immutable
// and shared: docs/09-contracts.md §1 allows concurrent scans on one open file.
using System;

namespace Vorticity.File;

/// <summary>Read-time policy, carried on the file and copied into every scan context.</summary>
public sealed class VortexReadOptions
{
    private readonly long _maxDecompressedSize = VortexLimits.DefaultMaxDecompressedSize;

    /// <summary>The defaults: 256 MiB decompression ceiling, no statistics verification.</summary>
    public static VortexReadOptions Default { get; } = new VortexReadOptions();

    /// <summary>
    /// Ceiling on the bytes one decompression step may produce (docs/08-semantics.md §6). Defaults
    /// to <see cref="VortexLimits.DefaultMaxDecompressedSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxDecompressedSize
    {
        get => _maxDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedSize = value;
        }
    }

    /// <summary>
    /// Verify class II statistics — monotonic run ends, <c>is_sorted</c>, zone bounds — instead of
    /// trusting them (docs/08-semantics.md §5). O(n) at first decode when on. Default
    /// <see langword="false"/>.
    /// </summary>
    public bool VerifyStatistics { get; init; }

    /// <summary>
    /// Inspection mode: unknown components are preserved as inert nodes so a dump tool can list
    /// them (docs/03-architecture.md §5). It does <em>not</em> make lazy resolution happen — that
    /// is unconditional (docs/08-semantics.md §4). Default <see langword="false"/>.
    /// </summary>
    public bool AllowUnknownComponents { get; init; }
}
