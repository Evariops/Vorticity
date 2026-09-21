using System;
using System.Runtime.CompilerServices;

namespace Vorticity;

/// <summary>
/// Hard resource caps enforced by every parser. A violation is a <see cref="VortexFormatException"/>.
/// Each cap is declared once here rather than invented at a call site, so no parser can enforce a
/// different bound than its neighbour.
/// </summary>
public static class VortexLimits
{
    /// <summary>
    /// Maximum <c>alignment_exponent</c>. The wire field is a <c>u8</c>, so an unchecked file can
    /// demand 2^255; 64 bytes covers every legitimate alignment including 16-byte varbinview views.
    /// </summary>
    public const int MaxAlignmentExponent = 6;

    /// <summary>Maximum alignment in bytes, <c>1 &lt;&lt; <see cref="MaxAlignmentExponent"/></c>.</summary>
    public const int MaxAlignment = 1 << MaxAlignmentExponent;

    /// <summary>Maximum nesting depth of a DType tree. Guards the stack during schema parsing.</summary>
    public const int MaxDTypeDepth = 64;

    /// <summary>Maximum nesting depth of a layout tree.</summary>
    public const int MaxLayoutDepth = 64;

    /// <summary>Maximum nesting depth of an array encoding tree.</summary>
    public const int MaxArrayDepth = 64;

    /// <summary>Format-mandated postscript ceiling: <c>u16::MAX - EOF_SIZE</c>.</summary>
    public const int MaxPostscriptSize = 65527;

    /// <summary>Format-mandated maximum number of user metadata segments.</summary>
    public const int MaxMetadataSegments = 16;

    /// <summary>Format-mandated maximum length in UTF-8 bytes of a user metadata key.</summary>
    public const int MaxMetadataKeyLength = 64;

    /// <summary>Footer cap on <c>compression_specs</c>.</summary>
    public const int MaxCompressionSpecs = 8;

    /// <summary>
    /// Default ceiling on the decompressed size of a single segment or buffer: a 1 KiB Zstd frame
    /// can claim to expand to 100 GiB. Configurable per open; this is the default.
    /// </summary>
    public const long DefaultMaxDecompressedSize = 256L * 1024 * 1024;

    /// <summary>
    /// Maximum depth of a FlatBuffers table traversal, independent of the semantic tree depths
    /// above. Guards the verifier itself.
    /// </summary>
    public const int MaxFlatBufferDepth = 128;

    /// <summary>
    /// Maximum number of FlatBuffers tables one traversal may visit.
    /// </summary>
    /// <remarks>
    /// Forward-only uoffsets make cycles impossible, but they do <em>not</em> make sharing
    /// impossible: two slots may legally resolve to the same table, so the object graph is a DAG.
    /// A consumer that walks a DAG as a tree is exponential in depth, and the depth cap above
    /// cannot see that — a 1.5 KB buffer of shared children stays inside every depth, offset and
    /// bounds rule while costing 2^63 visits. The reference verifiers bound exactly this with a
    /// total-table budget (<c>flatbuffers::Verifier</c>'s <c>max_tables</c> in C++, and
    /// <c>VerifierOptions::max_tables</c> in Rust, both defaulting to 1 000 000), so this cap
    /// matches theirs.
    /// </remarks>
    public const int MaxFlatBufferTables = 1_000_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CheckAlignmentExponent(byte exponent)
    {
        if (exponent > MaxAlignmentExponent)
        {
            ThrowAlignmentExponent(exponent);
        }

        return 1 << exponent;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAlignmentExponent(byte exponent) =>
        throw new VortexFormatException(
            $"alignment_exponent {exponent} exceeds the cap of {MaxAlignmentExponent} " +
            $"({MaxAlignment} bytes).");

    /// <summary>Refuses a nesting depth past <paramref name="max"/>.</summary>
    /// <param name="depth">The depth about to be entered.</param>
    /// <param name="max">The cap for this kind of tree; one of the constants above.</param>
    /// <param name="what">What is being nested, for the message.</param>
    /// <remarks>
    /// This stays internal, unlike <see cref="CheckAlignmentExponent"/>: the alignment check is
    /// named in <c>SegmentSpec</c>'s own documentation as something an implementer of a segment
    /// source calls, whereas this one runs only while parsing or decoding, on trees a caller never
    /// builds.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckDepth(int depth, int max, string what)
    {
        if ((uint)depth > (uint)max)
        {
            ThrowDepth(depth, max, what);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDepth(int depth, int max, string what) =>
        throw new VortexFormatException($"{what} nesting depth {depth} exceeds the cap of {max}.");
}
