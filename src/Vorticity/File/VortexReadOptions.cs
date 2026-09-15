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

    /// <summary>
    /// THE INTERNAL SWITCH OF PERF-AUDIT-v2.md Z1b. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// A constant column canonicalizes today by TILING: the element is written once and doubled over
    /// the whole column, so a million rows of eight bytes cost eight megabytes to say one number.
    /// With this on, `ConstantCanonicalizer` emits <see cref="Arrays.CanonicalKind.Constant"/>
    /// instead -- the element and a length -- and every consumer resolves each row to the same
    /// window.
    /// <para>
    /// IT IS A PER-SCAN OPTION AND NOT A STATIC FLAG, deliberately: the two forms have to coexist in
    /// ONE process until the refactor reaches its exit (§3.7 condition 5), and a mutable global
    /// would make any test that flips it poison every test running beside it.
    /// </para>
    /// <para>
    /// It is not a supported knob and it will go away: §3.7 requires the switch to be REMOVED at the
    /// exit, the old shape carried by the benchmark instead. A refactor that does not reach its exit
    /// closes with its measurement rather than living here forever.
    /// </para>
    /// </remarks>
    internal bool ConstantForm { get; init; }
}
