using System;

namespace Vorticity.File;

/// <summary>
/// Frozen constants of the Vortex file container. They are part of the format, not tuning knobs:
/// changing one makes this reader disagree with every conformant writer.
/// </summary>
internal static class VortexFileFormat
{
    /// <summary>The 4-byte magic, <c>VTXF</c>, written at file offset 0 and again in the EOF marker.</summary>
    public static ReadOnlySpan<byte> MagicBytes => "VTXF"u8;

    /// <summary>Size of the end-of-file marker: <c>u16 version</c>, <c>u16 postscript_length</c>, magic.</summary>
    public const int EofSize = 8;

    /// <summary>
    /// The only file format version this library reads. Compared for <em>exact</em> equality:
    /// there is no <c>&lt;=</c> forward compatibility.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>
    /// Largest legal postscript, <c>u16.MaxValue - EofSize</c>. Same value as
    /// <see cref="VortexLimits.MaxPostscriptSize"/>, restated here because it is a format constant
    /// rather than a resource cap.
    /// </summary>
    public const int MaxPostscriptSize = 65527;

    /// <summary>
    /// Bytes read from the tail on open: <see cref="MaxPostscriptSize"/> + <see cref="EofSize"/>
    /// = 65535, one byte short of 64 KiB and not to be rounded up. By construction it always covers
    /// the postscript, which is what keeps the open path to one or two round trips. A caller may
    /// raise it, never lower it.
    /// </summary>
    public const int InitialReadSize = MaxPostscriptSize + EofSize;

    /// <summary>Byte offset of the version field inside the EOF marker.</summary>
    internal const int EofVersionOffset = 0;

    /// <summary>Byte offset of the postscript length inside the EOF marker.</summary>
    internal const int EofPostscriptLengthOffset = 2;

    /// <summary>Byte offset of the trailing magic inside the EOF marker.</summary>
    internal const int EofMagicOffset = 4;

    /// <summary>
    /// Alignment requested for the tail read. Eight bytes is what a FlatBuffers root whose widest
    /// member is a <c>uint64</c> needs, so every structure sliced out of the window keeps the
    /// natural alignment its own FlatBuffer gave it relative to the window start.
    /// </summary>
    internal const int TailAlignment = 8;
}
