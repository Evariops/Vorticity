// The sidecar index file - docs/10-indexes.md §8, third way: `file.vortex.idx`, for stores that
// cannot append (the Iceberg Puffin pattern).
//
//   "VXIX"                        magic
//   run payloads                  array blobs, aligned like a data file's segments
//   directory                     the IndexDirectory message, version byte first, with the indexed
//                                 file's length, identity, store token and XXH3-128, and the
//                                 sidecar's own encoding table
//   u64 directory offset, u32 directory length, u32 version, "VXIX"      the trailer, 20 bytes
//
// THE SIDECAR IS BOUND TO ONE VERSION OF ONE FILE (13 §7, step 26). A file rewritten under the same
// name would make every run a lie about rows it no longer has, so a mismatch refuses the sidecar
// whole -- an index is a hint, and a stale one is none. The binding is what a reader can check
// without reading the file: its length and its identity, which every write and every append mints
// anew, from the tail the open already holds. A file without an identity -- written by another
// writer -- is bound by the store's token instead, its length and modification time on a file
// system, which is a heuristic and is said to be one. The SHA-256 that bound a sidecar before this
// step made every open read the whole file; a sidecar that carries only that binds by nothing a
// reader checks, and is refused.
//
// THE FILE'S HASH IS THE INDEXER'S. It reads the whole file to index it anyway, so it records the
// XXH3-128 of the bytes; no reader computes it, and `vxdump --verify` compares it offline.
//
// ITS PAYLOADS NAME THEIR OWN ENCODINGS. A payload is an array blob whose nodes name encodings by
// index into a footer's table; the data file's footer is not the sidecar's to extend, so the
// directory carries the table the payloads were written against.
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Indexes;

/// <summary>The sidecar file's container.</summary>
internal static class IndexSidecar
{
    /// <summary>The magic at both ends.</summary>
    internal static ReadOnlySpan<byte> Magic => "VXIX"u8;

    /// <summary>The trailer's length.</summary>
    internal const int TrailerSize = 20;

    /// <summary>The one version this library writes and reads.</summary>
    internal const uint Version = 1;

    /// <summary>The trailer for a directory at <paramref name="offset"/>.</summary>
    internal static byte[] Trailer(long offset, int length)
    {
        byte[] trailer = new byte[TrailerSize];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, (ulong)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(8), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(12), Version);
        Magic.CopyTo(trailer.AsSpan(16));
        return trailer;
    }

    /// <summary>
    /// Opens a sidecar and reads its directory, refusing it with a reason when it is not one, or
    /// is not the sidecar of <paramref name="file"/>.
    /// </summary>
    /// <returns>The sidecar's source and directory, or a reason.</returns>
    internal static async ValueTask<(ISegmentSource? Source, IndexDirectory? Directory, string? Reason)> OpenAsync(
        string path, File.VortexFile file, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(path))
        {
            return (null, null, $"the sidecar {path} does not exist");
        }

        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
        try
        {
            long length = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            (IndexDirectory? directory, string? reason) =
                await ReadAsync(source, length, file, "sidecar", cancellationToken).ConfigureAwait(false);
            if (directory is null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return (null, null, reason);
            }

            return (source, directory, null);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads the container's directory from any source holding it whole, refusing it with a reason
    /// when it is not one, or is not this file's.
    /// </summary>
    /// <param name="source">The bytes, starting at the magic: a file, or a fragment's blob (13 §6.4).</param>
    /// <param name="length">How long they are.</param>
    /// <param name="file">The data object the index is offered for.</param>
    /// <param name="what">What to call it in a refusal: "sidecar" or "fragment".</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The directory, or the reason it was refused.</returns>
    /// <remarks>
    /// THE CONTAINER IS POSITION-INDEPENDENT, which is what lets a fragment be the same bytes inside
    /// a commit object (13 §12: "one fragment in one commit object; the word goes"). Every offset in
    /// it is relative to its own start, so the reader needs a source that begins at the magic and
    /// nothing else — not a path, not a length in some enclosing file.
    /// </remarks>
    internal static async ValueTask<(IndexDirectory? Directory, string? Reason)> ReadAsync(
        ISegmentSource source, long length, File.VortexFile file, string what, CancellationToken cancellationToken)
    {
        if (length < Magic.Length + TrailerSize)
        {
            return (null, $"the {what} is too short to hold a trailer");
        }

        long offset;
        int size;
        using (SegmentOwner trailer = await source
            .ReadRangeAsync(length - TrailerSize, TrailerSize, 1, cancellationToken).ConfigureAwait(false))
        {
            (offset, size, string? bad) = ReadTrailer(trailer.Buffer, length, what);
            if (bad is not null)
            {
                return (null, bad);
            }
        }

        IndexDirectory? directory;
        string? reason;
        using (SegmentOwner bytes = await source
            .ReadRangeAsync(offset, size, 1, cancellationToken).ConfigureAwait(false))
        {
            if (!IndexDirectory.TryParse(
                bytes.Buffer.Span, (ulong)file.RowCount, (ulong)offset, out directory, out reason))
            {
                return (null, $"the {what}'s " + reason);
            }
        }

        if (Unbound(directory!, file, what) is { } stale)
        {
            return (null, stale);
        }

        return directory!.ArrayEncodings is null
            ? (null, $"the {what} carries no encoding table for its payloads")
            : (directory, null);
    }

    private static (long Offset, int Length, string? Reason) ReadTrailer(
        VortexBuffer trailer, long length, string what)
    {
        ReadOnlySpan<byte> bytes = trailer.Span;
        if (!bytes[16..].SequenceEqual(Magic))
        {
            return (0, 0, $"the {what}'s trailer does not end with VXIX");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Version)
        {
            return (0, 0, $"the {what}'s version is not one this library reads");
        }

        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        return offset < (ulong)Magic.Length || offset + size > (ulong)(length - TrailerSize)
            ? (0, 0, $"the {what}'s trailer names a directory outside it")
            : ((long)offset, (int)size, null);
    }

    /// <summary>
    /// Why <paramref name="directory"/> does not describe <paramref name="file"/>, or null when it
    /// does: the length, then the identity, or the store's token for a file without one. Nothing of
    /// the file is read.
    /// </summary>
    /// <param name="directory">The sidecar's directory.</param>
    /// <param name="file">The file it is offered for.</param>
    /// <param name="what">What to call it in a refusal: "sidecar" or "fragment" (13 §6.4).</param>
    internal static string? Unbound(IndexDirectory directory, File.VortexFile file, string what = "sidecar")
    {
        if (directory.FileLength != (ulong)file.FileLength)
        {
            return $"the {what} indexes a file of {directory.FileLength} bytes, not this one of {file.FileLength}: it is stale";
        }

        if (directory.FileIdentity is { } identity)
        {
            return file.Identity == identity
                ? null
                : $"the {what} indexes the version {identity:N} of the file, and this is {(file.Identity is { } other ? other.ToString("N") : "a file without an identity")}: it is stale";
        }

        if (directory.FileToken is { } token)
        {
            if (TokenOf(file) is not { } current)
            {
                return $"the {what} binds a file without an identity by its store token, and this file was not opened from a path that gives one";
            }

            return string.Equals(current, token, StringComparison.Ordinal)
                ? null
                : $"the {what} was written for the store token {token}, and the file's is {current}: it is stale (a heuristic for a file without an identity)";
        }

        return directory.LegacySha256
            ? $"the {what} binds its file by a SHA-256, which a reader no longer computes (13 §7): rebuild it"
            : $"the {what} names neither the file's identity nor its store token";
    }

    /// <summary>The store token of the file at <paramref name="path"/>: its length and modification time.</summary>
    /// <param name="path">A file on a file system.</param>
    internal static string TokenOf(string path)
    {
        FileInfo info = new FileInfo(path);
        return FormattableString.Invariant($"fs:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    /// <summary>
    /// The store tokens of files opened from a path while a sidecar was asked for, taken at the
    /// open. A file opened otherwise has none, and pays nothing for the table.
    /// </summary>
    private static readonly ConditionalWeakTable<File.VortexFile, string> Tokens = [];

    /// <summary>Records the store token of a file opened from <paramref name="path"/>.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">Where it was opened from.</param>
    internal static void RememberToken(File.VortexFile file, string path) => Tokens.AddOrUpdate(file, TokenOf(path));

    private static string? TokenOf(File.VortexFile file) => Tokens.TryGetValue(file, out string? token) ? token : null;

    /// <summary>The XXH3-128 of a whole file, read a mebibyte at a time.</summary>
    /// <param name="source">The file.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<UInt128> HashAsync(ISegmentSource source, long length, CancellationToken cancellationToken)
    {
        XxHash128 hash = new XxHash128();
        for (long at = 0; at < length; at += 1 << 20)
        {
            int size = (int)Math.Min(1 << 20, length - at);
            using SegmentOwner chunk = await source.ReadRangeAsync(at, size, 1, cancellationToken).ConfigureAwait(false);
            Append(hash, chunk.Buffer);
        }

        return hash.GetCurrentHashAsUInt128();
    }

    private static void Append(XxHash128 hash, VortexBuffer buffer) => hash.Append(buffer.Span);
}
