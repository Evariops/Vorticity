// The bytes of a commit object - docs/13-dataset.md §3, which says what it holds and §13.M says why
// it is one object: "one commit object: header with the inlined top levels, pages, fragments,
// table ... one PutIfAbsent, no orphan but data objects, the header read like a file's tail, the
// recent path in one ranged read".
//
// THE LAYOUT, and the one way it differs from a Vortex file. A Vortex file is opened by its TAIL,
// because its writer cannot know its footer's offsets until it has written everything. A commit
// object is opened by its HEAD, because §3 promises "one ranged read of its first 256 KiB, which
// covers the header by construction" -- and that promise is only keepable if the header is at
// offset zero. So the preamble and the header come first, and the trailer at the end carries what
// only the end can carry: where the table is, how long the object should be, and the checksum.
//
//   0          magic "VXCOMMIT"                       8
//   8          format version, little-endian u32      4
//   12         header length, little-endian u32       4
//   16         header, proto3                         headerLength
//   ...        pages and fragments, back to back      (in the order they were added -- step 42b;
//                                                     addressed by reference, never scanned)
//   ...        table, proto3                          (what the object holds, for verify and repack)
//   length-32  table offset, u64                      8
//   length-24  table length, u32                      4
//   length-20  object length, u64                     8
//   length-12  XXH3-64 over header ++ table           8
//   length-4   magic "VXCT"                           4
//
// WHY THE OBJECT'S OWN LENGTH IS IN THE TRAILER. §11's store hands back what it has; a truncated
// object is a shorter object, not an error. Recording the length the writer intended turns every
// truncation into a sentence -- "it should hold N bytes and holds M" -- including the one that
// truncates the trailer itself, which then fails on the magic instead of reading garbage as
// offsets. That is what the tear test walks byte by byte.
//
// WHY THE CHECKSUM COVERS THE HEADER AND THE TABLE AND NOT THE PAGES. §7: "a commit object carries
// an XXH3-64 over its header and its table", and pages are "addressed by their XXH3-128 in every
// reference". Each page is checked by whoever reads it, against the reference that sent them there;
// re-checking them all at open would make opening cost the whole object.
using System;
using System.Buffers.Binary;

namespace Vorticity.Dataset;

/// <summary>The constants of the commit object's byte layout.</summary>
public static class CommitFormat
{
    /// <summary>The first eight bytes of every commit object.</summary>
    public static ReadOnlySpan<byte> Magic => "VXCOMMIT"u8;

    /// <summary>The last four bytes of every commit object.</summary>
    public static ReadOnlySpan<byte> TrailerMagic => "VXCT"u8;

    /// <summary>The format this library writes and reads.</summary>
    public const uint Version = 1;

    /// <summary>Bytes before the header: the magic, the format and the header's length.</summary>
    public const int PreambleBytes = 16;

    /// <summary>Bytes of the trailer.</summary>
    public const int TrailerBytes = 32;

    /// <summary>
    /// What a reader asks for when it opens a commit: §3's "one ranged read of its first 256 KiB".
    /// </summary>
    /// <remarks>
    /// The header is under this by construction — §3 keeps the level table's pages inlined "while
    /// the header stays under 256 KiB" — so one read of this many bytes holds everything an open
    /// needs, and usually the recent path's pages with it.
    /// </remarks>
    public const int OpenBytes = 256 << 10;

    /// <summary>The smallest object that could be a commit: a preamble, an empty header, a trailer.</summary>
    public const int MinimumBytes = PreambleBytes + TrailerBytes;

    /// <summary>Writes the preamble into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="PreambleBytes"/> bytes.</param>
    /// <param name="headerLength">The header's bytes.</param>
    public static void WritePreamble(Span<byte> destination, int headerLength)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)headerLength);
    }

    /// <summary>Writes the trailer into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="TrailerBytes"/> bytes.</param>
    /// <param name="tableOffset">Where the table starts.</param>
    /// <param name="tableLength">The table's bytes.</param>
    /// <param name="objectLength">What the whole object measures.</param>
    /// <param name="checksum">XXH3-64 over the header followed by the table.</param>
    public static void WriteTrailer(
        Span<byte> destination, long tableOffset, int tableLength, long objectLength, ulong checksum)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)tableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)tableLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], (ulong)objectLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[20..], checksum);
        TrailerMagic.CopyTo(destination[28..]);
    }
}

/// <summary>A commit object's trailer, as read back.</summary>
/// <param name="TableOffset">Where the table starts.</param>
/// <param name="TableLength">The table's bytes.</param>
/// <param name="ObjectLength">What the whole object should measure.</param>
/// <param name="Checksum">XXH3-64 over the header followed by the table.</param>
public readonly record struct CommitTrailer(long TableOffset, int TableLength, long ObjectLength, ulong Checksum);

/// <summary>A commit object is not what it claims to be.</summary>
public sealed class CommitFormatException : Exception
{
    /// <summary>Required by the exception guidelines.</summary>
    public CommitFormatException()
        : base("The commit object is malformed.")
    {
    }

    /// <summary>Carries a message.</summary>
    /// <param name="message">The message.</param>
    public CommitFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CommitFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
