// The sidecar index file - docs/10-indexes.md §8, third way: `file.vortex.idx`, for stores that
// cannot append (the Iceberg Puffin pattern).
//
//   "VXIX"                        magic
//   run payloads                  array blobs, aligned like a data file's segments
//   directory                     the IndexDirectory message, version byte first, with the indexed
//                                 file's length and SHA-256 and the sidecar's own encoding table
//   u64 directory offset, u32 directory length, u32 version, "VXIX"      the trailer, 20 bytes
//
// THE SIDECAR IS BOUND TO ONE FILE, BYTE FOR BYTE. A file rewritten under the same name would make
// every run a lie about rows it no longer has, so the directory records the file's length and hash
// and a mismatch refuses the sidecar whole -- an index is a hint, and a stale one is none.
//
// ITS PAYLOADS NAME THEIR OWN ENCODINGS. A payload is an array blob whose nodes name encodings by
// index into a footer's table; the data file's footer is not the sidecar's to extend, so the
// directory carries the table the payloads were written against.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
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
            if (length < Magic.Length + TrailerSize)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return (null, null, "the sidecar is too short to hold a trailer");
            }

            long offset;
            int size;
            using (SegmentOwner trailer = await source.ReadRangeAsync(length - TrailerSize, TrailerSize, 1, cancellationToken).ConfigureAwait(false))
            {
                (offset, size, string? bad) = ReadTrailer(trailer.Buffer, length);
                if (bad is not null)
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                    return (null, null, bad);
                }
            }

            IndexDirectory? directory;
            string? reason;
            using (SegmentOwner bytes = await source.ReadRangeAsync(offset, size, 1, cancellationToken).ConfigureAwait(false))
            {
                if (!IndexDirectory.TryParse(bytes.Buffer.Span, (ulong)file.RowCount, (ulong)offset, out directory, out reason))
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                    return (null, null, "the sidecar's " + reason);
                }
            }

            if (directory!.FileLength != (ulong)file.FileLength || directory.FileSha256 is not { Length: 32 } expected)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return (null, null, $"the sidecar indexes a file of {directory.FileLength} bytes, not this one of {file.FileLength}: it is stale");
            }

            byte[] actual = await HashAsync(file.Segments, file.FileLength, cancellationToken).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return (null, null, "the sidecar's SHA-256 is not this file's: it is stale");
            }

            if (directory.ArrayEncodings is null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return (null, null, "the sidecar carries no encoding table for its payloads");
            }

            return (source, directory, null);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static (long Offset, int Length, string? Reason) ReadTrailer(VortexBuffer trailer, long length)
    {
        ReadOnlySpan<byte> bytes = trailer.Span;
        if (!bytes[16..].SequenceEqual(Magic))
        {
            return (0, 0, "the sidecar's trailer does not end with VXIX");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Version)
        {
            return (0, 0, "the sidecar's version is not one this library reads");
        }

        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        return offset < (ulong)Magic.Length || offset + size > (ulong)(length - TrailerSize)
            ? (0, 0, "the sidecar's trailer names a directory outside it")
            : ((long)offset, (int)size, null);
    }

    /// <summary>The SHA-256 of a whole file, read a mebibyte at a time.</summary>
    internal static async ValueTask<byte[]> HashAsync(ISegmentSource source, long length, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long at = 0; at < length; at += 1 << 20)
        {
            int size = (int)Math.Min(1 << 20, length - at);
            using SegmentOwner chunk = await source.ReadRangeAsync(at, size, 1, cancellationToken).ConfigureAwait(false);
            Append(hash, chunk.Buffer);
        }

        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, VortexBuffer buffer) => hash.AppendData(buffer.Span);
}
