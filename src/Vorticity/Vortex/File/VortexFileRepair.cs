using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.File;

namespace Vorticity;

/// <summary>What <see cref="VortexFileRepair.RepairAsync"/> did.</summary>
/// <param name="OriginalLength">The file's length before.</param>
/// <param name="Length">Its length after.</param>
/// <param name="Truncated">Whether bytes were cut.</param>
public sealed record VortexRepairResult(long OriginalLength, long Length, bool Truncated);

/// <summary>A file opened at the last whole version before a torn tail.</summary>
/// <param name="FileLength">The file's length on disk.</param>
/// <param name="ValidLength">The length of the version read: the bytes after it are the torn ones.</param>
/// <param name="Reason">Why the tail did not open.</param>
public sealed record VortexTornTail(long FileLength, long ValidLength, string Reason);

/// <summary>
/// Truncates a file whose tail a torn append left invalid. An append is not atomic and writes
/// nothing before the old end of file, so a file with an invalid tail still holds, somewhere
/// before its end, the complete file it was before, marker included: walking back for the last
/// end-of-file record and keeping the longest prefix that opens recovers it, without needing the
/// torn directory that could not be read anyway.
/// </summary>
/// <remarks>
/// <para>
/// The same walk backs <c>VortexFile.OpenAsync</c>, which reads the prefix it finds and records
/// the tear in <see cref="VortexFile.TornTail"/> without writing anything; truncating stays the
/// caller's decision.
/// </para>
/// <para>
/// A sealed file is cut after the last trailer whose layout reads under the header's descriptor,
/// which needs no key: the commitments that bind its epochs are checked by the open that follows.
/// </para>
/// </remarks>
public static class VortexFileRepair
{
    /// <summary>The bytes the backward walk reads at a time.</summary>
    internal const int Window = 1 << 20;

    /// <summary>
    /// The end-of-file records a walk tries before it gives up: a torn append leaves one before the
    /// tear, while forged bytes can hold one every eight bytes.
    /// </summary>
    private const int MaxCandidates = 16;

    /// <summary>
    /// Leaves a valid file alone, and truncates an invalid one to the longest prefix that opens.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="VortexFormatException">No prefix of the file is a valid Vortex file.</exception>
    public static async ValueTask<VortexRepairResult> RepairAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        long length = new FileInfo(path).Length;
        long end = await ValidEndAsync(path, length, cancellationToken).ConfigureAwait(false);
        if (end == length)
        {
            return new VortexRepairResult(length, length, false);
        }

        using (SafeFileHandle handle = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Reading it for its valid end may have left it mapped, which on Windows forbids the cut.
            Vorticity.IO.MappedFileCache.ReleaseEverywhere(handle);
            RandomAccess.SetLength(handle, end);
        }

        return new VortexRepairResult(length, end, true);
    }

    /// <summary>The end of the longest prefix of the file that opens, without changing it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The prefix's length: the file's own when it is valid.</returns>
    /// <exception cref="VortexFormatException">No prefix of the file is a valid Vortex file.</exception>
    public static async ValueTask<long> GetValidLengthAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await ValidEndAsync(path, new FileInfo(path).Length, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ValidEndAsync(string path, long length, CancellationToken cancellationToken)
    {
        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
        await using (source.ConfigureAwait(false))
        {
            // A sealed file ends where its last whole trailer does, which its bytes say without the key.
            (bool vortex, bool sealedHeader) = await BeginsAsync(source, length, cancellationToken).ConfigureAwait(false);
            if (sealedHeader)
            {
                long sealedEnd = await Sealing.SealedFiles.WholeEndAsync(source, length, cancellationToken).ConfigureAwait(false);
                if (sealedEnd > 0)
                {
                    return sealedEnd;
                }

                throw new VortexFormatException($"{path} is sealed and holds no whole trailer at any length: nothing to repair to.");
            }

            if (await OpensAsync(source, length, cancellationToken).ConfigureAwait(false))
            {
                return length;
            }

            if (await ForeignVersionAsync(source, length, cancellationToken).ConfigureAwait(false) is ushort version)
            {
                FileThrow.UnsupportedVersion(version);
            }

            long end = vortex ? await PreviousEndAsync(source, length, cancellationToken).ConfigureAwait(false) : -1;
            if (end > 0)
            {
                return end;
            }
        }

        throw new VortexFormatException($"{path} holds no valid Vortex file at any length: nothing to repair to.");
    }

    /// <summary>
    /// The end of the longest proper prefix of <paramref name="source"/> that opens as a Vortex file,
    /// or -1 when none does.
    /// </summary>
    /// <param name="source">The file.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    internal static async ValueTask<long> PreviousEndAsync(ISegmentReader source, long length, CancellationToken cancellationToken)
    {
        // Walk back window by window, trying the records of a window from the last down before the
        // next is read. A record is recognised only when its eight bytes are all in one window, and
        // may straddle two, so the next window reaches the seven of them this one could hold.
        int candidates = 0;
        long high = length;
        while (high > VortexFileFormat.EofSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long low = Math.Max(0, high - Window);
            int size = (int)(high - low);
            using (SegmentOwner owner = await source.ReadRangeAsync(low, size, 1, cancellationToken).ConfigureAwait(false))
            {
                for (int at = LastMarker(owner.Buffer.Span, low, length, size); at >= 0; at = LastMarker(owner.Buffer.Span, low, length, at))
                {
                    long end = low + at + VortexFileFormat.MagicBytes.Length;
                    if (await OpensAsync(source, end, cancellationToken).ConfigureAwait(false))
                    {
                        return end;
                    }

                    if (++candidates == MaxCandidates)
                    {
                        return -1;
                    }
                }
            }

            if (low == 0)
            {
                break;
            }

            high = low + VortexFileFormat.EofSize - 1;
        }

        return -1;
    }

    /// <summary>Refuses to write behind a torn tail: what would follow it would follow garbage.</summary>
    /// <param name="path">The file.</param>
    /// <param name="file">The file, opened.</param>
    /// <param name="what">What was asked: "an append", "an index".</param>
    /// <exception cref="VortexFormatException">The file opened at a version before a torn tail.</exception>
    internal static void ThrowIfTorn(string path, VortexFile file, string what)
    {
        if (file.TornTail is { } torn)
        {
            throw new VortexFormatException(
                $"{path} has a torn tail: the {torn.FileLength - torn.ValidLength} bytes after its last whole " +
                $"version do not parse ({torn.Reason}). {what} is written behind a whole file only; " +
                "VortexFileRepair.RepairAsync truncates the torn bytes.");
        }
    }

    /// <summary>
    /// What the first bytes of <paramref name="source"/> say, in one read: whether it begins with the
    /// Vortex magic, as every file this format writes does, and whether it begins with a sealed
    /// object's, as a sealed file whose tail was torn still does.
    /// </summary>
    /// <param name="source">The file.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async ValueTask<(bool Vortex, bool Sealed)> BeginsAsync(ISegmentReader source, long length, CancellationToken cancellationToken)
    {
        int magicLength = VortexFileFormat.MagicBytes.Length;
        ReadOnlySpan<byte> sealedMagic = Sealing.SealedFormat.HeaderMagic;
        bool vortexFits = length >= VortexFileFormat.EofSize + magicLength;
        bool sealedFits = length >= Sealing.SealedFormat.HeaderPrefixBytes;
        if (!vortexFits && !sealedFits)
        {
            return (false, false);
        }

        int read = sealedFits ? sealedMagic.Length : magicLength;
        using SegmentOwner head = await source.ReadRangeAsync(0, read, 1, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = head.Buffer.Span;
        return (
            vortexFits && bytes.Length >= magicLength && bytes[..magicLength].SequenceEqual(VortexFileFormat.MagicBytes),
            sealedFits && bytes.SequenceEqual(Sealing.SealedFormat.HeaderMagic));
    }

    /// <summary>
    /// The version a whole end-of-file record at the end of <paramref name="source"/> names, when it is
    /// not this reader's; null otherwise. Such a file is not torn: its last version is one this reader
    /// cannot read, and an older one is no substitute.
    /// </summary>
    /// <param name="source">The file.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async ValueTask<ushort?> ForeignVersionAsync(ISegmentReader source, long length, CancellationToken cancellationToken)
    {
        if (length < VortexFileFormat.EofSize)
        {
            return null;
        }

        using SegmentOwner record = await source.ReadRangeAsync(
            length - VortexFileFormat.EofSize, VortexFileFormat.EofSize, 1, cancellationToken).ConfigureAwait(false);
        return ForeignVersion(record.Buffer.Span);
    }

    private static ushort? ForeignVersion(ReadOnlySpan<byte> record)
    {
        if (record.Length != VortexFileFormat.EofSize
            || !record.Slice(VortexFileFormat.EofMagicOffset, VortexFileFormat.MagicBytes.Length).SequenceEqual(VortexFileFormat.MagicBytes))
        {
            return null;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(record[VortexFileFormat.EofVersionOffset..]);
        return version == VortexFileFormat.Version ? null : version;
    }

    /// <summary>
    /// Where the magic of the last end-of-file record of a window begins, below
    /// <paramref name="below"/>, or -1: the magic, preceded by the format's version, in a record that
    /// ends before the file does.
    /// </summary>
    private static int LastMarker(ReadOnlySpan<byte> bytes, long low, long length, int below)
    {
        ReadOnlySpan<byte> magic = VortexFileFormat.MagicBytes;
        for (int at = Math.Min(bytes.Length - magic.Length, below - 1); at >= 4; at--)
        {
            if (low + at + magic.Length < length
                && bytes.Slice(at, magic.Length).SequenceEqual(magic)
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at - 4)..]) == VortexFileFormat.Version)
            {
                return at;
            }
        }

        return -1;
    }

    private static async ValueTask<bool> OpensAsync(ISegmentReader source, long length, CancellationToken cancellationToken)
    {
        try
        {
            PrefixSource prefix = new PrefixSource(source, length, ownsInner: false);
            VortexOpenOptions options = new VortexOpenOptions
            {
                LeaveSourceOpen = true,
                FileLength = length,
                TornTail = VortexTornTailPolicy.Refuse,
            };
            VortexFile file = await VortexFile.OpenAsync(prefix, options, cancellationToken).ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (VortexFormatException)
        {
            return false;
        }
        catch (VortexUnsupportedException)
        {
            return false;
        }
    }

    /// <summary>The first <c>length</c> bytes of a source, as if the file ended there.</summary>
    /// <param name="inner">The whole file.</param>
    /// <param name="length">Where the prefix ends.</param>
    /// <param name="ownsInner">Whether disposing the prefix disposes the file.</param>
    internal sealed class PrefixSource(ISegmentReader inner, long length, bool ownsInner) : ISegmentReader
    {
        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => new ValueTask<long>(length);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
        {
            Check(spec);
            return inner.ReadAsync(spec, cancellationToken);
        }

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requests);
            for (int i = 0; i < requests.Count; i++)
            {
                Check(requests.GetSpec(i));
            }

            return inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length1, int alignment, CancellationToken cancellationToken)
        {
            if (offset >= length)
            {
                throw new VortexFormatException($"A read at {offset} starts past the {length}-byte prefix.");
            }

            return inner.ReadRangeAsync(offset, (int)Math.Min(length1, length - offset), alignment, cancellationToken);
        }

        public ValueTask DisposeAsync() => ownsInner ? inner.DisposeAsync() : default;

        private void Check(SegmentSpec spec)
        {
            if (spec.Offset + spec.Length > (ulong)length)
            {
                throw new VortexFormatException($"A segment at {spec.Offset} runs past the {length}-byte prefix.");
            }
        }
    }
}
