using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.Schemas;

/// <summary>
/// A FlatBuffers <c>struct</c> (8 bytes, inline) describing one data buffer inside an array blob.
/// It is the <c>Buffer</c> struct of spec/flatbuffers/array.fbs, named <c>BufferSpec</c> here so it
/// does not collide with <c>Vorticity.Buffers.VortexBuffer</c>.
/// </summary>
/// <remarks>
/// Its natural alignment is 4, not 8: the widest member is a <c>uint32</c>. A reader that demanded
/// 8-byte alignment would reject legal files, because writers commonly place this vector at a
/// 4-mod-8 offset.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
public readonly struct BufferSpec
{
    /// <summary>Byte 0..2. Padding bytes written immediately before this buffer.</summary>
    public readonly ushort Padding;

    /// <summary>Byte 2..3. Alignment is <c>1 &lt;&lt; AlignmentExponent</c>. Untrusted.</summary>
    public readonly byte AlignmentExponent;

    /// <summary>Byte 3..4. <c>enum Compression : uint8 { None = 0, LZ4 = 1 }</c>.</summary>
    public readonly byte Compression;

    /// <summary>Byte 4..8. Length of the buffer in bytes.</summary>
    public readonly uint Length;

    /// <summary>Creates a buffer descriptor.</summary>
    /// <param name="padding">Padding bytes written immediately before the buffer.</param>
    /// <param name="alignmentExponent">Base-2 exponent of the buffer's alignment.</param>
    /// <param name="compression">A <see cref="BufferCompression"/> value.</param>
    /// <param name="length">Length of the buffer in bytes.</param>
    public BufferSpec(ushort padding, byte alignmentExponent, byte compression, uint length)
    {
        Padding = padding;
        AlignmentExponent = alignmentExponent;
        Compression = compression;
        Length = length;
    }
}

/// <summary>Per-buffer compression, <c>enum Compression : uint8</c> in spec/flatbuffers/array.fbs.</summary>
public enum BufferCompression : byte
{
    /// <summary>The buffer is stored verbatim.</summary>
    None = 0,

    /// <summary>
    /// The buffer is LZ4-compressed. Declared by the schema and implemented by nothing: no Vortex
    /// release writes this value or reads this field, and the format records neither the framing
    /// nor a decompressed length, so a buffer carrying it is refused rather than decoded.
    /// </summary>
    LZ4 = 1,
}

/// <summary>
/// Per-segment compression, <c>enum CompressionScheme : uint8</c> in spec/flatbuffers/footer.fbs.
/// </summary>
/// <remarks>
/// A value outside this set is <em>not</em> rejected by the schema accessors. Compression is a
/// registry component like any other: classifying an unknown scheme is the consumer's job, and an
/// unknown component must never be an open-time error. No known file uses this field —
/// <c>compression_specs</c> is empty throughout the golden corpus.
/// </remarks>
public enum CompressionScheme : byte
{
    /// <summary>The segment is stored verbatim.</summary>
    None = 0,

    /// <summary>LZ4.</summary>
    LZ4 = 1,

    /// <summary>zlib / Deflate.</summary>
    ZLib = 2,

    /// <summary>Zstandard.</summary>
    ZStd = 3,
}

/// <summary>
/// Statistic exactness, <c>enum Precision : uint8</c> in spec/flatbuffers/array.fbs.
/// </summary>
/// <remarks>
/// Like <see cref="CompressionScheme"/>, an out-of-domain value is returned as read rather than
/// rejected: statistics are advisory, and a value nobody understands must never make a file
/// unreadable.
/// </remarks>
public enum StatPrecision : byte
{
    /// <summary>The statistic bounds the true value but may not equal it.</summary>
    Inexact = 0,

    /// <summary>The statistic is the true value.</summary>
    Exact = 1,
}

/// <summary>
/// Load-time verification of the two inline struct layouts the zero-copy readers reinterpret.
/// </summary>
internal static class SchemaLayoutChecks
{
    // CA2255 warns that ModuleInitializer is meant for applications. Here it is deliberate, exactly
    // as in VortexRuntimeChecks: the sizes are a wire contract and every zero-copy reinterpretation
    // in this file depends on them, so they are asserted once before any caller can hand us bytes.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Deliberate inline-struct size guard; see docs/09-contracts.md §7.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Unsafe.SizeOf<SegmentSpec>() != 16)
        {
            throw new PlatformNotSupportedException(
                $"SegmentSpec must occupy exactly 16 bytes (spec/flatbuffers/footer.fbs) but this " +
                $"runtime lays it out in {Unsafe.SizeOf<SegmentSpec>()}.");
        }

        if (Unsafe.SizeOf<BufferSpec>() != 8)
        {
            throw new PlatformNotSupportedException(
                $"BufferSpec must occupy exactly 8 bytes (spec/flatbuffers/array.fbs) but this " +
                $"runtime lays it out in {Unsafe.SizeOf<BufferSpec>()}.");
        }
    }
}
