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
    /// The format this library writes for a header without source offsets. Format 2 headers may
    /// retire column names, lock the store and say where earlier versions' pages start, and its pages
    /// carry tallies and deletion vectors: a reader of format 1 would read such a dataset wrongly
    /// where it did not fail, a renamed column as missing and a locked store as one it may rewrite,
    /// and refuses it by this number instead.
    /// </summary>
    public const uint Version = 2;

    /// <summary>
    /// The format of a header that counts source offsets, and the newest this library reads. A writer
    /// of format 2 would rebuild such a header without the field it does not know, and so forget
    /// every offset at its first commit: the entries it counted would be counted again. It refuses the
    /// dataset by this number instead.
    /// </summary>
    public const uint WithSources = 3;

    /// <summary>
    /// The format of a header that names features (<see cref="DatasetFeatures"/>): its tree holds
    /// entries with fields past their summaries, which a reader of format 3 parses as deleted rows and
    /// refuses, or as nothing at all. Once a dataset uses one, every later commit names it, since the
    /// pages that hold such entries stay in its trees. It refuses the dataset by this number instead.
    /// </summary>
    public const uint WithFeatures = 4;

    /// <summary>The newest format this library reads.</summary>
    public const uint Newest = WithFeatures;

    /// <summary>The format a commit object of <paramref name="header"/> is written in.</summary>
    public static uint For(CommitHeader header) =>
        header.Features != DatasetFeatures.None || header.ErasureDelay is not null ? WithFeatures
        : header.Sources.Count > 0 ? WithSources
        : Version;

    /// <summary>
    /// The oldest format this library reads: format 1 holds nothing format 2 does not read the same,
    /// so a dataset written by a reader of it opens here as it is.
    /// </summary>
    public const uint OldestRead = 1;

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
/// What a dataset uses of commit format 4, as its header names it: a library that reads format 3
/// would misread each, so a header that names one is written in format 4, and every header after it
/// names it too.
/// </summary>
[Flags]
internal enum DatasetFeatures : ulong
{
    /// <summary>Nothing past format 3.</summary>
    None = 0,

    /// <summary>Leaf entries that carry fields of format 4, such as their object's footer length.</summary>
    EntryFields = 1,

    /// <summary>
    /// A delay within which deleted rows are erased from the store's bytes: a library that does not know
    /// it would keep marks past it, against what the application owes.
    /// </summary>
    ErasureDelay = 2,

    /// <summary>
    /// Compaction by periods, under a lateness horizon: a library that does not know it would compact
    /// the levels as it compacts any dataset, against the order its tiers keep.
    /// </summary>
    Periods = 4,

    /// <summary>Columns whose values questions look up, which a library that does not know them would drop from the headers it writes.</summary>
    Identifiers = 8,

    /// <summary>
    /// Leading key columns that partition the objects: a library that does not know them would merge objects of
    /// two partitions, against the retention each holds.
    /// </summary>
    Partitions = 16,

    /// <summary>
    /// Keys of variant columns every job shreds into columns of their own, which a library that does not know them
    /// would drop from the headers it writes.
    /// </summary>
    ShreddedPaths = 32,
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
