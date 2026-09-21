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

/// <summary>
/// The container an index fragment is written in: run payloads, then the directory, then a trailer
/// naming it. It carries its own encoding table and every offset in it counts from its own first
/// byte, so the same code reads it wherever it is kept -- a range of a larger object, or bytes in
/// memory. Its directory binds it to one version of one file, because a file rewritten under the
/// same key would leave every run describing rows that are gone.
/// </summary>
internal static class IndexContainer
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
    /// Reads a container's directory from any source holding it whole, refusing it with a reason
    /// when it is not one, or is not this file's.
    /// </summary>
    /// <param name="source">The bytes, starting at the magic.</param>
    /// <param name="length">How long they are.</param>
    /// <param name="file">The data object the index is offered for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The directory, or the reason it was refused.</returns>
    internal static async ValueTask<(IndexDirectory? Directory, string? Reason)> ReadAsync(
        ISegmentSource source, long length, VortexFile file, CancellationToken cancellationToken)
    {
        if (length < Magic.Length + TrailerSize)
        {
            return (null, "the fragment is too short to hold a trailer");
        }

        long offset;
        int size;
        using (SegmentOwner trailer = await source
            .ReadRangeAsync(length - TrailerSize, TrailerSize, 1, cancellationToken).ConfigureAwait(false))
        {
            (offset, size, string? bad) = ReadTrailer(trailer.Buffer, length);
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
                return (null, "the fragment's " + reason);
            }
        }

        if (Unbound(directory!, file) is { } stale)
        {
            return (null, stale);
        }

        return directory!.ArrayEncodings is null
            ? (null, "the fragment carries no encoding table for its payloads")
            : (directory, null);
    }

    private static (long Offset, int Length, string? Reason) ReadTrailer(VortexBuffer trailer, long length)
    {
        ReadOnlySpan<byte> bytes = trailer.Span;
        if (!bytes[16..].SequenceEqual(Magic))
        {
            return (0, 0, "the fragment's trailer does not end with VXIX");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Version)
        {
            return (0, 0, "the fragment's version is not one this library reads");
        }

        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        return offset < (ulong)Magic.Length || offset + size > (ulong)(length - TrailerSize)
            ? (0, 0, "the fragment's trailer names a directory outside it")
            : ((long)offset, (int)size, null);
    }

    /// <summary>
    /// Why <paramref name="directory"/> does not describe <paramref name="file"/>, or null when it
    /// does: the length, then the identity, or the store's token for a file without one. Nothing of
    /// the file is read.
    /// </summary>
    /// <param name="directory">The fragment's directory.</param>
    /// <param name="file">The file it is offered for.</param>
    internal static string? Unbound(IndexDirectory directory, VortexFile file)
    {
        if (directory.FileLength != (ulong)file.FileLength)
        {
            return $"the fragment indexes a file of {directory.FileLength} bytes, not this one of {file.FileLength}: it is stale";
        }

        if (directory.FileIdentity is { } identity)
        {
            return file.StoredIdentity == identity
                ? null
                : $"the fragment indexes the version {identity:N} of the file, and this is {(file.StoredIdentity is { } other ? other.ToString("N") : "a file without an identity")}: it is stale";
        }

        if (directory.FileToken is { } token)
        {
            if (TokenOf(file) is not { } current)
            {
                return "the fragment binds a file without an identity by its store token, and this file was not opened from a path that gives one";
            }

            return string.Equals(current, token, StringComparison.Ordinal)
                ? null
                : $"the fragment was written for the store token {token}, and the file's is {current}: it is stale (a heuristic for a file without an identity)";
        }

        return "the fragment names neither the file's identity nor its store token";
    }

    /// <summary>The store token of the file at <paramref name="path"/>: its length and modification time.</summary>
    /// <param name="path">A file on a file system.</param>
    internal static string TokenOf(string path)
    {
        FileInfo info = new FileInfo(path);
        return FormattableString.Invariant($"fs:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    /// <summary>
    /// The store tokens of files opened from a path with fragments to bind, taken at the open. A file
    /// opened otherwise has none, and pays nothing for the table.
    /// </summary>
    private static readonly ConditionalWeakTable<VortexFile, string> Tokens = [];

    /// <summary>Records the store token of a file opened from <paramref name="path"/>.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">Where it was opened from.</param>
    internal static void RememberToken(VortexFile file, string path) => Tokens.AddOrUpdate(file, TokenOf(path));

    private static string? TokenOf(VortexFile file) => Tokens.TryGetValue(file, out string? token) ? token : null;

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
