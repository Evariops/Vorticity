// Opens a commit object - docs/13-dataset.md §3: "A reader opens a commit with one ranged read of
// its first 256 KiB, which covers the header by construction, exactly as a Vortex file is opened by
// its tail (02 §1)."
//
// ONE READ, AND WHAT IT TAKES TO MEAN IT. The header is at offset zero, so the first read covers it
// whenever the object is that read or larger. What the first read does NOT cover is the trailer of
// a large object -- and the trailer is where the checksum and the intended length live. So opening
// has two shapes and this class says which one it is in: a SMALL object (the read reached its end)
// is verified whole, header and table and length; a LARGE one is opened on its header alone and
// `VerifyAsync` is the offline pass that reads the trailer and the table. That is the honest
// reading of §7's "no reader ever computes it. `verify` does, offline, object by object", applied
// to the object's own checksum rather than to a data object's.
//
// A TEAR IS A SENTENCE, NEVER A GUESS. Every field a reader takes from these bytes is bounded by
// the bytes themselves before it is used: the preamble's magic, the header's length against what
// was read, the trailer's magic, the table's offset and length against the object's length, and the
// checksum last. The tear test walks a real object truncating it at every byte and requires a
// CommitFormatException with a reason at each one -- never a wrong answer, never an
// IndexOutOfRangeException.
using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>A commit object, as a reader sees it.</summary>
public sealed class CommitObject
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
    /// The table, when the open read reached the end of the object; empty otherwise.
    /// </summary>
    /// <remarks>
    /// A reader needs no table: a page is found by the reference that names it. The table is for
    /// `verify` and for repack, which read the object whole anyway.
    /// </remarks>
    public CommitTable Table { get; }

    /// <summary>The trailer, when the open read reached the end of the object.</summary>
    public CommitTrailer? Trailer { get; }

    /// <summary>The object's length when it is known: from the trailer, or from the open read.</summary>
    public long Length { get; }

    /// <summary>Where the pages region begins, which is one past the header.</summary>
    public long HeaderEnd { get; }

    /// <summary>
    /// Opens a commit object from the first bytes of it, as §3's one ranged read hands them over.
    /// </summary>
    /// <param name="bytes">
    /// The object's first bytes, at least <see cref="CommitFormat.MinimumBytes"/> of them. When
    /// they are the whole object, everything is checked; when they are a prefix, the header is.
    /// </param>
    /// <param name="objectLength">
    /// What the store says the object measures, or a negative number when the caller does not know.
    /// Used to tell "this read is the whole object" from "this read is its first 256 KiB".
    /// </param>
    /// <returns>The commit object.</returns>
    /// <exception cref="CommitFormatException">The bytes are not a commit object.</exception>
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
        if (format != CommitFormat.Version)
        {
            throw new CommitFormatException(
                $"This library reads commit format {CommitFormat.Version} and the object is format {format}.");
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

        // WHOLE OR PREFIX. The read is the whole object when the store said so, or when the caller
        // did not say and the bytes end in a trailer that claims exactly this length.
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

    /// <summary>
    /// Opens the commit object at <paramref name="key"/> with one ranged read (§3).
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The commit object.</returns>
    /// <exception cref="ObjectNotFoundException">No object has that key.</exception>
    /// <exception cref="CommitFormatException">The object is not a commit object.</exception>
    public static async ValueTask<CommitObject> OpenAsync(
        IObjectStore store, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        using ObjectRange range = await store
            .GetRangeAsync(key, 0, CommitFormat.OpenBytes, cancellationToken).ConfigureAwait(false);

        // The read is the whole object exactly when it came back short of what was asked for.
        long length = range.Length < CommitFormat.OpenBytes ? range.Length : -1;
        return Open(range.Bytes.Span, length);
    }

    /// <summary>The bytes of a page this object holds, from a buffer that holds the object.</summary>
    /// <param name="bytes">The object's bytes, from offset zero.</param>
    /// <param name="reference">The reference, which must name this object's version.</param>
    /// <returns>The page's bytes.</returns>
    /// <exception cref="CommitFormatException">
    /// The reference names another version, lies outside the object, or the page does not hash to
    /// what the reference says.
    /// </exception>
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

    /// <summary>The offset one past an object's header, which every page offset is relative to.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Where the pages region starts.</returns>
    /// <exception cref="CommitFormatException">The object is not a commit object.</exception>
    /// <remarks>
    /// Sixteen bytes: the magic, the format and the header's length. A reader that holds a commit
    /// already knows this and should not ask; a reader following a reference into an OLDER commit
    /// asks once per version and remembers.
    /// </remarks>
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

        ReadOnlySpan<byte> preamble = range.Bytes.Span;
        if (!preamble[..8].SequenceEqual(CommitFormat.Magic))
        {
            throw new CommitFormatException($"'{key}' does not start with a commit object's magic.");
        }

        return CommitFormat.PreambleBytes + BinaryPrimitives.ReadUInt32LittleEndian(preamble[12..]);
    }

    /// <summary>Reads one page through the store, checking it against its reference.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The key of the object the reference names.</param>
    /// <param name="reference">The reference, whose offset is relative to the pages region.</param>
    /// <param name="pagesStart">Where that object's pages region starts (<see cref="PagesStartAsync"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page's bytes.</returns>
    /// <exception cref="CommitFormatException">The page does not hash to what the reference says.</exception>
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

        UInt128 hash = XxHash128.HashToUInt128(range.Bytes.Span);
        if (hash != reference.Hash)
        {
            throw new CommitFormatException(
                $"The page at {reference.Offset} hashes to {hash:x32} and its reference says {reference.Hash:x32}.");
        }

        return range.Bytes.ToArray();
    }

    /// <summary>Whether the bytes end in a trailer that claims exactly their length.</summary>
    /// <param name="bytes">The bytes read.</param>
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
    /// <param name="bytes">The whole object.</param>
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
