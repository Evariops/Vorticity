using System;
using Vorticity.Buffers;

namespace Vorticity.Sealing;

/// <summary>
/// What a sealed object's reader may keep on a local disk: the object's trailer, and the frames that hold a
/// region its table of contents declares, each as the store holds it. Never a key, never a plaintext byte. The
/// reader authenticates what it reads back as it does what the store sends, frame by frame, and drops what fails.
/// </summary>
internal interface ISealedFrameCache
{
    /// <summary>The object's trailer as the store holds it; false when the cache does not hold it.</summary>
    bool TryReadTrailer(out SegmentOwner trailer);

    /// <summary>Keeps the object's trailer, once it has opened the object.</summary>
    void AdmitTrailer(ReadOnlySpan<byte> trailer);

    /// <summary>Drops the trailer kept, which did not open the object.</summary>
    void DropTrailer();

    /// <summary>Whether plaintext <c>[offset, offset + length)</c> is a region the object's table of contents declares.</summary>
    bool Declares(ulong offset, uint length);

    /// <summary>Whether <paramref name="plain"/>, read for the declared region at <paramref name="offset"/>, holds its checksum.</summary>
    bool Holds(ulong offset, uint length, ReadOnlySpan<byte> plain);

    /// <summary>Whether the cache holds the frame of <paramref name="length"/> bytes at <paramref name="cipherOffset"/> in the object.</summary>
    bool HoldsFrame(long cipherOffset, int length);

    /// <summary>The frame at <paramref name="cipherOffset"/>, as many bytes as <paramref name="destination"/> holds; false when not held, or torn.</summary>
    bool TryReadFrame(long cipherOffset, Span<byte> destination);

    /// <summary>Keeps the frame at <paramref name="cipherOffset"/>, once authenticated.</summary>
    void AdmitFrame(long cipherOffset, ReadOnlySpan<byte> frame);

    /// <summary>Drops the frame kept at <paramref name="cipherOffset"/>, which did not authenticate.</summary>
    void DropFrame(long cipherOffset, int length);
}
