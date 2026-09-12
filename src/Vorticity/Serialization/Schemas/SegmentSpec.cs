// The FlatBuffers `struct SegmentSpec` of spec/flatbuffers/footer.fbs. A FlatBuffers struct has no
// vtable and no indirection: 16 bytes inline, so `Footer.segment_specs` is read by reinterpreting
// the vector's element bytes with MemoryMarshal.Cast and never traversed (docs/02-format.md §3).
//
// The field order and widths below are the wire layout and may not be reordered. `Pack = 1` plus
// `Size = 16` pins it: natural packing already produces offsets 0/8/12/13/14 with no interior
// padding, and the explicit size is what the module initializer in SchemaLayoutChecks asserts
// (docs/09-contracts.md §7).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.Schemas;

/// <summary>
/// A FlatBuffers <c>struct</c> (16 bytes, inline, no vtable) locating one segment in the file.
/// </summary>
/// <remarks>
/// spec/flatbuffers/footer.fbs. Read as a reinterpreted <see cref="ReadOnlySpan{T}"/> of
/// <see cref="SegmentSpec"/> over <see cref="FooterView.SegmentSpecs"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public readonly struct SegmentSpec
{
    /// <summary>Byte 0..8. Offset relative to the start of the file.</summary>
    public readonly ulong Offset;

    /// <summary>Byte 8..12. Length in bytes.</summary>
    public readonly uint Length;

    /// <summary>
    /// Byte 12..13. Alignment is <c>1 &lt;&lt; AlignmentExponent</c>. Untrusted: run it through
    /// <see cref="VortexLimits.CheckAlignmentExponent"/> before using it.
    /// </summary>
    public readonly byte AlignmentExponent;

    /// <summary>Byte 13..14. Reserved: an index into <c>Footer.compression_specs</c>. Unused in v1.</summary>
    public readonly byte Compression;

    /// <summary>Byte 14..16. Reserved: an index into <c>Footer.encryption_specs</c>. Unused in v1.</summary>
    public readonly ushort Encryption;

    /// <summary>Creates a segment locator.</summary>
    /// <param name="offset">Offset relative to the start of the file.</param>
    /// <param name="length">Length in bytes.</param>
    /// <param name="alignmentExponent">Base-2 exponent of the segment's alignment.</param>
    /// <param name="compression">Reserved index into <c>Footer.compression_specs</c>.</param>
    /// <param name="encryption">Reserved index into <c>Footer.encryption_specs</c>.</param>
    public SegmentSpec(
        ulong offset,
        uint length,
        byte alignmentExponent,
        byte compression,
        ushort encryption)
    {
        Offset = offset;
        Length = length;
        AlignmentExponent = alignmentExponent;
        Compression = compression;
        Encryption = encryption;
    }

    /// <summary>Exclusive end offset, <c>Offset + Length</c>.</summary>
    /// <exception cref="VortexFormatException">
    /// The sum wraps past <see cref="ulong.MaxValue"/>. Both terms come straight from the file, so
    /// the addition is checked rather than allowed to produce an end that precedes its start.
    /// </exception>
    public ulong End
    {
        get
        {
            ulong end = Offset + Length;
            if (end < Offset)
            {
                ThrowEndOverflow(Offset, Length);
            }

            return end;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEndOverflow(ulong offset, uint length) =>
        throw new VortexFormatException(
            $"Segment at offset {offset} with length {length} ends past ulong.MaxValue.");
}
