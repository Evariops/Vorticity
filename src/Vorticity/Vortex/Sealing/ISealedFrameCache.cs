using System;
using Vorticity.Buffers;

namespace Vorticity.Sealing;

/// <summary>
/// What a sealed object's reader may keep on a local disk: the object's trailer, the frames that hold a region its table
/// of contents declares, and, when the cache keeps data, the frames of the other segments it reads, each as the store
/// holds it. Never a key, never a plaintext byte. The reader authenticates what it reads back as it does what the store
/// sends, frame by frame, and drops what fails.
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

    /// <summary>
    /// Whether the reader keeps, besides the frames of the declared regions, the frames of every other segment it reads: the
    /// object's data and its footer, which the cache writes behind the read.
    /// </summary>
    bool KeepsData { get; }

    /// <summary>Whether the cache holds the frame of <paramref name="length"/> bytes at <paramref name="cipherOffset"/> in the object, kept for a region or for the data.</summary>
    bool HoldsFrame(long cipherOffset, int length);

    /// <summary>
    /// The frame of <paramref name="length"/> bytes at <paramref name="cipherOffset"/>, for the caller to release: a view of its
    /// file, or of the buffer it waits in for its write; false when not held, or torn.
    /// </summary>
    bool TryReadFrame(long cipherOffset, int length, out SegmentOwner frame);

    /// <summary>Keeps the frame at <paramref name="cipherOffset"/> of a declared region, once authenticated.</summary>
    void AdmitFrame(long cipherOffset, ReadOnlySpan<byte> frame);

    /// <summary>
    /// Keeps the frame at <paramref name="cipherOffset"/> of a segment that is no declared region, once authenticated, behind
    /// the reader: <paramref name="owner"/>, whose memory holds <paramref name="frame"/>, is held until the cache's writer has
    /// written it. Not kept when the cache keeps no data, nor when the writes waiting would hold up the read.
    /// </summary>
    void AdmitDataFrame(long cipherOffset, SegmentOwner owner, VortexBuffer frame);

    /// <summary>Drops the frame kept at <paramref name="cipherOffset"/>, which did not authenticate.</summary>
    void DropFrame(long cipherOffset, int length);
}
