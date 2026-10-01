using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A commit object, as a reader sees it. Opening it reads only its first bytes, so an object short
/// enough for that read is checked whole while a larger one is opened on its header alone and
/// leaves its trailer, table and checksum to the offline verification pass. Every field taken from
/// the bytes is bounded against them first, so a truncated object raises
/// <see cref="CommitFormatException"/> rather than answering wrongly.
/// </summary>
internal sealed class CommitObject
{
    private CommitObject(CommitHeader header, CommitTable table, CommitTrailer? trailer, long length, long headerEnd)
    {
        Header = header;
        Table = table;
        Trailer = trailer;
        Length = length;
        HeaderEnd = headerEnd;
    }

    /// <summary>The header, always present.</summary>
    public CommitHeader Header { get; }

    /// <summary>
    /// The table, when the open read reached the end of the object; empty otherwise. A reader needs
    /// no table — a page is found by the reference that names it — so only verification and repack,
    /// which read the object whole, depend on it.
    /// </summary>
    public CommitTable Table { get; }

    /// <summary>The trailer, when the open read reached the end of the object.</summary>
    public CommitTrailer? Trailer { get; }

    /// <summary>The object's length when it is known: from the trailer, or from the open read.</summary>
    public long Length { get; }

    /// <summary>Where the pages region begins, which is one past the header.</summary>
    public long HeaderEnd { get; }

    /// <summary>
    /// The start of the pages region that the open read brought back past the header: where the
    /// pages this version wrote and did not inline lie, when they lie inside the read. Empty for an
    /// object opened from bytes a caller already held.
    /// </summary>
    public ReadOnlyMemory<byte> Held { get; private init; }

    /// <summary>
    /// Opens a commit object from its first bytes, at least <see cref="CommitFormat.MinimumBytes"/>
    /// of them. <paramref name="objectLength"/> is what the store says the object measures, or
    /// negative when the caller does not know; it decides whether these bytes are the whole object,
    /// and so whether everything or only the header is checked.
    /// </summary>
    public static CommitObject Open(ReadOnlySpan<byte> bytes, long objectLength = -1)
    {
        if (bytes.Length < CommitFormat.PreambleBytes)
        {
            throw new CommitFormatException(
                $"A commit object starts with {CommitFormat.PreambleBytes} bytes and {bytes.Length} were read.");
        }

        if (!bytes[..8].SequenceEqual(CommitFormat.Magic))
        {
            throw new CommitFormatException("These bytes do not start with a commit object's magic.");
        }

        uint format = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (format < CommitFormat.OldestRead || format > CommitFormat.Version)
        {
            throw new CommitFormatException(
                $"This library reads commit formats {CommitFormat.OldestRead} to {CommitFormat.Version} and the object is format {format}.");
        }

        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        long headerEnd = (long)CommitFormat.PreambleBytes + headerLength;
        if (headerLength > int.MaxValue - CommitFormat.PreambleBytes)
        {
            throw new CommitFormatException($"A header of {headerLength} bytes is not a header.");
        }

        if (headerEnd > bytes.Length)
        {
            throw new CommitFormatException(
                $"The header claims {headerLength} bytes and only {bytes.Length - CommitFormat.PreambleBytes} " +
                "were read after the preamble.");
        }

        ReadOnlySpan<byte> headerBytes = bytes.Slice(CommitFormat.PreambleBytes, (int)headerLength);
        CommitHeader header = CommitHeader.Read(headerBytes);

        // The read is the whole object when the store said so, or when the caller did not say and
        // the bytes end in a trailer that claims exactly this length.
        bool whole = objectLength >= 0
            ? objectLength == bytes.Length
            : ClaimsOwnLength(bytes);
        if (!whole)
        {
            if (objectLength >= 0 && objectLength < headerEnd)
            {
                throw new CommitFormatException(
                    $"The object is {objectLength} bytes and its header ends at {headerEnd}.");
            }

            return new CommitObject(header, CommitTable.Empty, null, objectLength, headerEnd);
        }

        CommitTrailer trailer = ReadTrailer(bytes);
        if (trailer.ObjectLength != bytes.Length)
        {
            throw new CommitFormatException(
                $"The object claims {trailer.ObjectLength} bytes and holds {bytes.Length}.");
        }

        if (trailer.TableOffset < headerEnd
            || trailer.TableLength < 0
            || trailer.TableOffset + trailer.TableLength > bytes.Length - CommitFormat.TrailerBytes)
        {
            throw new CommitFormatException(
                $"The table at {trailer.TableOffset}+{trailer.TableLength} does not lie inside the object.");
        }

        ReadOnlySpan<byte> tableBytes = bytes.Slice((int)trailer.TableOffset, trailer.TableLength);
        XxHash3 checksum = new XxHash3();
        checksum.Append(headerBytes);
        checksum.Append(tableBytes);
        ulong computed = checksum.GetCurrentHashAsUInt64();
        if (computed != trailer.Checksum)
        {
            throw new CommitFormatException(
                $"The commit's checksum is {trailer.Checksum:x16} and its bytes hash to {computed:x16}.");
        }

        return new CommitObject(header, CommitTable.Read(tableBytes), trailer, bytes.Length, headerEnd);
    }

    /// <summary>Opens the commit object at a key with one ranged read.</summary>
    public static async ValueTask<CommitObject> OpenAsync(
        IObjectStore store, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using ObjectRange range = await store
            .GetRangeAsync(key, 0, CommitFormat.OpenBytes, cancellationToken).ConfigureAwait(false);

        // The read is the whole object exactly when it came back short of what was asked for.
        long length = range.Length < CommitFormat.OpenBytes ? range.Length : -1;
        ReadOnlySpan<byte> bytes = range.Contiguous().Span;
        CommitObject opened = Open(bytes, length);
        return opened.HeaderEnd < bytes.Length
            ? new CommitObject(opened.Header, opened.Table, opened.Trailer, opened.Length, opened.HeaderEnd)
            {
                Held = bytes[(int)opened.HeaderEnd..].ToArray(),
            }
            : opened;
    }

    /// <summary>
    /// The commit object its writer has just placed, as an open would find it without reading it: the
    /// header it wrote, where its pages region starts, and what of that region the open's read would
    /// bring back. Its table and trailer stay the writer's; no reader needs them.
    /// </summary>
    public static CommitObject Placed(CommitHeader header, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(bytes);
        int headerEnd = CommitFormat.PreambleBytes + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));

        // An object past the read is copied out of, since a slice of it would keep all of it.
        ReadOnlyMemory<byte> held = bytes.Length <= CommitFormat.OpenBytes
            ? bytes.AsMemory(headerEnd)
            : bytes.AsSpan(headerEnd, Math.Max(CommitFormat.OpenBytes - headerEnd, 0)).ToArray();
        return new CommitObject(header, CommitTable.Empty, null, bytes.Length, headerEnd) { Held = held };
    }

    /// <summary>
    /// The bytes of a page this object holds, taken from a buffer holding the object from offset
    /// zero. The reference must name this object's version, and the page is checked against its
    /// hash.
    /// </summary>
    public ReadOnlySpan<byte> Page(ReadOnlySpan<byte> bytes, PageReference reference)
    {
        if (reference.Version != Header.Version)
        {
            throw new CommitFormatException(
                $"This object is version {Header.Version} and the reference names version {reference.Version}.");
        }

        long at = HeaderEnd + reference.Offset;
        if (reference.Offset < 0 || at + reference.Length > bytes.Length)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset}+{reference.Length} does not lie inside the object.");
        }

        ReadOnlySpan<byte> page = bytes.Slice((int)at, reference.Length);
        UInt128 hash = XxHash128.HashToUInt128(page);
        if (hash != reference.Hash)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset} hashes to {hash:x32} and its reference says {reference.Hash:x32}.");
        }

        return page;
    }

    /// <summary>
    /// The offset one past an object's header, which every page offset is relative to. A reader
    /// holding the commit already knows it; one following a reference into an older commit asks
    /// once per version and remembers.
    /// </summary>
    public static async ValueTask<long> PagesStartAsync(
        IObjectStore store, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using ObjectRange range = await store
            .GetRangeAsync(key, 0, CommitFormat.PreambleBytes, cancellationToken).ConfigureAwait(false);
        if (range.Length < CommitFormat.PreambleBytes)
        {
            throw new CommitFormatException($"'{key}' is {range.Length} bytes and holds no preamble.");
        }

        ReadOnlySpan<byte> preamble = range.Contiguous().Span;
        if (!preamble[..8].SequenceEqual(CommitFormat.Magic))
        {
            throw new CommitFormatException($"'{key}' does not start with a commit object's magic.");
        }

        return CommitFormat.PreambleBytes + BinaryPrimitives.ReadUInt32LittleEndian(preamble[12..]);
    }

    /// <summary>
    /// Reads one page through the store, checking it against its reference, whose offset is
    /// relative to the pages region given by <see cref="PagesStartAsync"/>. The page is copied out
    /// of the range because the range is only a lease on the store's buffer and the caller outlives
    /// it; a commit's pages are small enough that owning them is the simpler choice.
    /// </summary>
    public static async ValueTask<byte[]> ReadPageAsync(
        IObjectStore store,
        string key,
        PageReference reference,
        long pagesStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!reference.Exists)
        {
            throw new CommitFormatException("The reference names no page.");
        }

        using ObjectRange range = await store
            .GetRangeAsync(key, pagesStart + reference.Offset, reference.Length, cancellationToken)
            .ConfigureAwait(false);
        if (range.Length != reference.Length)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset} is {reference.Length} bytes and {range.Length} were read.");
        }

        byte[] page = range.Bytes.ToArray();
        UInt128 hash = XxHash128.HashToUInt128(page);
        if (hash != reference.Hash)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset} hashes to {hash:x32} and its reference says {reference.Hash:x32}.");
        }

        return page;
    }

    /// <summary>Whether the bytes end in a trailer that claims exactly their length.</summary>
    private static bool ClaimsOwnLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < CommitFormat.MinimumBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> trailer = bytes[^CommitFormat.TrailerBytes..];
        return trailer[^4..].SequenceEqual(CommitFormat.TrailerMagic)
            && (long)BinaryPrimitives.ReadUInt64LittleEndian(trailer[12..]) == bytes.Length;
    }

    /// <summary>Reads and bounds-checks the trailer of a complete object.</summary>
    private static CommitTrailer ReadTrailer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < CommitFormat.MinimumBytes)
        {
            throw new CommitFormatException(
                $"A commit object is at least {CommitFormat.MinimumBytes} bytes and this one is {bytes.Length}.");
        }

        ReadOnlySpan<byte> trailer = bytes[^CommitFormat.TrailerBytes..];
        if (!trailer[^4..].SequenceEqual(CommitFormat.TrailerMagic))
        {
            throw new CommitFormatException("The object does not end with a commit object's trailer.");
        }

        ulong tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(trailer);
        uint tableLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]);
        ulong objectLength = BinaryPrimitives.ReadUInt64LittleEndian(trailer[12..]);
        ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(trailer[20..]);
        if (tableOffset > long.MaxValue || objectLength > long.MaxValue || tableLength > int.MaxValue)
        {
            throw new CommitFormatException("The trailer's offsets are not offsets.");
        }

        return new CommitTrailer((long)tableOffset, (int)tableLength, (long)objectLength, checksum);
    }
}
