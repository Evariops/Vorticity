using System;
using System.Buffers.Binary;

namespace Vorticity.Dataset;

/// <summary>
/// The constants of the commit object's byte layout. Unlike a Vortex file, a commit is opened by
/// its head: the preamble and header sit at offset zero so one ranged read covers them, and the
/// trailer carries only what the end can carry — the table's position, the object's intended
/// length and the checksum.
/// </summary>
internal static class CommitFormat
{
    /// <summary>The first eight bytes of every commit object.</summary>
    public static ReadOnlySpan<byte> Magic => "VXCOMMIT"u8;

    /// <summary>The last four bytes of every commit object.</summary>
    public static ReadOnlySpan<byte> TrailerMagic => "VXCT"u8;

    /// <summary>
    /// The one format this library writes and reads. Formats 1 to 4 were the dataset before its layout was rebuilt
    /// around partitions, sort keys and objects addressable to the granule; a commit of any of them, or of a later
    /// format, is refused with a message that says which, and is never read in part.
    /// </summary>
    public const uint Version = 5;

    /// <summary>
    /// Refuses a commit object of format <paramref name="format"/> unless it is <see cref="Version"/>: an earlier
    /// release's dataset is rebuilt rather than read, and a later release's would be misread.
    /// </summary>
    /// <exception cref="CommitFormatException">The format is not <see cref="Version"/>.</exception>
    public static void Check(uint format)
    {
        if (format == Version)
        {
            return;
        }

        throw new CommitFormatException(format < Version
            ? $"The dataset was written in commit format {format} by an earlier release; this library reads format {Version} alone, and such a dataset is rebuilt from its rows."
            : $"The dataset was written in commit format {format} by a later release; this library reads format {Version} alone.");
    }

    /// <summary>Bytes before the header: the magic, the format and the header's length.</summary>
    public const int PreambleBytes = 16;

    /// <summary>Bytes of the trailer.</summary>
    public const int TrailerBytes = 32;

    /// <summary>
    /// The one ranged read an open makes. The header is kept under this by construction, so this
    /// many bytes hold everything an open needs, usually with the recent path's pages.
    /// </summary>
    public const int OpenBytes = 256 << 10;

    /// <summary>The smallest object that could be a commit: a preamble, an empty header, a trailer.</summary>
    public const int MinimumBytes = PreambleBytes + TrailerBytes;

    /// <summary>Writes the preamble into at least <see cref="PreambleBytes"/> bytes.</summary>
    public static void WritePreamble(Span<byte> destination, int headerLength, uint format)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], format);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)headerLength);
    }

    /// <summary>
    /// Writes the trailer into at least <see cref="TrailerBytes"/> bytes. The checksum is XXH3-64
    /// over the header followed by the table; the pages are checked by whoever reads them.
    /// </summary>
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

/// <summary>
/// A commit object's trailer, as read back. <c>ObjectLength</c> is what the writer intended, so a
/// truncated object reports its shortfall instead of reading its tail as offsets.
/// </summary>
internal readonly record struct CommitTrailer(long TableOffset, int TableLength, long ObjectLength, ulong Checksum);

/// <summary>A commit object is not what it claims to be.</summary>
internal sealed class CommitFormatException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CommitFormatException()
        : base("The commit object is malformed.")
    {
    }

    /// <summary>Carries a message.</summary>
    public CommitFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    public CommitFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
