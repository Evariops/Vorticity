// Every throw on the open path goes through here, [MethodImpl(NoInlining)] so the caller stays
// inlineable (PHASE1-CONTRACTS.md §1.4). Messages name the offending value: a corrupt file is
// diagnosed from the exception text alone in every bug report we will ever receive.
using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vorticity.File;

/// <summary>Throw helpers for the file open path.</summary>
internal static class FileThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void FileTooShort(long length) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"A Vortex file is at least {VortexFileFormat.EofSize} bytes (the EOF marker); this one is {length}."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BadTrailingMagic(byte b0, byte b1, byte b2, byte b3) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Malformed file: the EOF marker's magic is 0x{b0:x2}{b1:x2}{b2:x2}{b3:x2}, expected 'VTXF'."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BadLeadingMagic(byte b0, byte b1, byte b2, byte b3) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Malformed file: the leading magic at offset 0 is 0x{b0:x2}{b1:x2}{b2:x2}{b3:x2}, expected 'VTXF'."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void UnsupportedVersion(ushort version) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Malformed file: unsupported version {version}. Only version {VortexFileFormat.Version} is defined; the version is compared for exact equality, there is no forward compatibility."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void PostscriptTooLarge(int length) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Postscript length {length} exceeds the format maximum of {VortexFileFormat.MaxPostscriptSize} bytes."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void PostscriptTruncated(int postscriptLength, int available) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"The initial read of {available} bytes is shorter than the " +
            $"{postscriptLength + VortexFileFormat.EofSize} bytes the postscript and EOF marker need."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SegmentPastEndOfFile(
        string what, int index, ulong offset, uint length, long fileLength) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name(what, index)} at offset {offset} with length {length} extends past the end of the {fileLength}-byte file."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SegmentMisaligned(string what, int index, ulong offset, int alignment) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name(what, index)} offset {offset} is not aligned to {alignment}."));

    /// <summary>
    /// Composes a segment's name for a message. Called only from a throw helper, so the open path
    /// never allocates a name for a segment it accepts - which matters on a footer that declares
    /// tens of thousands of them.
    /// </summary>
    private static string Name(string what, int index) =>
        index < 0 ? what : string.Create(CultureInfo.InvariantCulture, $"{what} {index}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SegmentsOutOfOrder(int index, ulong previous, ulong current) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Segment offsets are not ordered: segment {index} starts at {current}, before segment " +
            $"{index - 1} at {previous}. The comparison is non-decreasing because zero-length segments are legal."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SegmentBeforeWindow(string what, ulong offset, long windowOffset) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"{what} offset {offset} is smaller than the read offset {windowOffset}."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SegmentOutsideWindow(string what, ulong offset, uint length, int windowLength) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"{what} of length {length} at offset {offset} is out of bounds of the {windowLength}-byte read window."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void BufferLongerThanFile(int bufferLength, long fileLength) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Footer buffer length {bufferLength} exceeds the declared file size {fileLength}."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void FooterRegionTooLarge(long bytes) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"The footer segments start {bytes} bytes before the tail window, which does not fit a single read. A conformant writer places every footer segment within the last 64 KiB."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MissingDType() =>
        throw new VortexFormatException(
            "Vortex file doesn't embed a DType and none provided to VortexOpenOptions.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void RowCountTooLarge(ulong rowCount) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"The root layout claims {rowCount} rows, which does not fit a signed 64-bit row index."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ShortRead(long offset, int wanted, int got) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Read of {wanted} bytes at offset {offset} returned {got}; the file is shorter than its own footer claims."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StatisticsFieldCount(int actual, int expected, string shape) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"File statistics carry {actual} field entries but the {shape} root DType needs exactly {expected}."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CorruptedPrecision(string field, byte value) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"Corrupted {field} field: {value}. Only Exact (1) and Inexact (0) are defined."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SpecIndexOutOfRange(string what, int index, int count) =>
        throw new VortexFormatException(string.Create(
            CultureInfo.InvariantCulture,
            $"{what} index {index} is outside the {count} entries the footer declares."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ExtraReadNeeded() =>
        throw new VortexFormatException(
            "The footer segments still fall outside the read window after one extension. " +
            "Exactly one extension always suffices for a well-formed file.");
}
