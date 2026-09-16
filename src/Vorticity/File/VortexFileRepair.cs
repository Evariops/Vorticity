// The repair of a torn append - docs/11-write-strategy.md §3.8, docs/10-indexes.md §8.
//
// AN APPEND IS NOT ATOMIC, AND ITS TEAR IS ALWAYS AT THE END. Nothing an append writes precedes the
// old end of file, so a file whose tail is invalid holds, somewhere before its end, the complete
// file it was before: its postscript and its EOF marker are still there, untouched. The repair
// walks back from the end for an EOF marker, opens the prefix that ends with it as a file, and
// truncates to the first prefix that opens. The old directory's `previous_eof` is not needed to
// find it -- the torn directory could not be read anyway -- and the walk also finds the file of an
// append that tore after its postscript but before its last flush.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.File;

/// <summary>What <see cref="VortexFileRepair.RepairAsync"/> did.</summary>
/// <param name="OriginalLength">The file's length before.</param>
/// <param name="Length">Its length after.</param>
/// <param name="Truncated">Whether bytes were cut.</param>
public sealed record VortexRepairResult(long OriginalLength, long Length, bool Truncated);

/// <summary>Truncates a file whose tail a torn append left invalid.</summary>
public static class VortexFileRepair
{
    private const int Window = 1 << 20;

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

        FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            stream.SetLength(end);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new VortexRepairResult(length, end, true);
    }

    /// <summary>The end of the longest prefix of the file that opens, without changing it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The prefix's length: the file's own when it is valid.</returns>
    /// <exception cref="VortexFormatException">No prefix of the file is a valid Vortex file.</exception>
    public static async ValueTask<long> ValidLengthAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await ValidEndAsync(path, new FileInfo(path).Length, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ValidEndAsync(string path, long length, CancellationToken cancellationToken)
    {
        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
        await using (source.ConfigureAwait(false))
        {
            if (await OpensAsync(source, length, cancellationToken).ConfigureAwait(false))
            {
                return length;
            }

            // Walk back window by window; a marker may straddle two windows, so they overlap.
            int magicLength = VortexFileFormat.MagicBytes.Length;
            long high = length;
            while (high > VortexFileFormat.EofSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long low = Math.Max(0, high - Window);
                int size = (int)(high - low);
                long found;
                using (SegmentOwner owner = await source.ReadRangeAsync(low, size, 1, cancellationToken).ConfigureAwait(false))
                {
                    found = LastMarker(owner.Buffer, low, length);
                }

                if (found > 0)
                {
                    if (await OpensAsync(source, found, cancellationToken).ConfigureAwait(false))
                    {
                        return found;
                    }

                    // Not a file end after all: keep walking below it.
                    high = found - 1;
                    continue;
                }

                if (low == 0)
                {
                    break;
                }

                high = low + magicLength;
            }
        }

        throw new VortexFormatException($"{path} holds no valid Vortex file at any length: nothing to repair to.");
    }

    /// <summary>
    /// The end of the last EOF record inside a window that ends before the file does, or -1: the
    /// magic, preceded by the format's version.
    /// </summary>
    private static long LastMarker(VortexBuffer window, long low, long length)
    {
        ReadOnlySpan<byte> magic = VortexFileFormat.MagicBytes;
        ReadOnlySpan<byte> bytes = window.Span;
        for (int at = bytes.Length - magic.Length; at >= 4; at--)
        {
            long end = low + at + magic.Length;
            if (end < length
                && bytes.Slice(at, magic.Length).SequenceEqual(magic)
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at - 4)..]) == VortexFileFormat.Version)
            {
                return end;
            }
        }

        return -1;
    }

    private static async ValueTask<bool> OpensAsync(ISegmentSource source, long length, CancellationToken cancellationToken)
    {
        try
        {
            PrefixSource prefix = new PrefixSource(source, length);
            VortexFile file = await VortexFile.OpenAsync(prefix, new VortexOpenOptions { LeaveSourceOpen = true }, cancellationToken)
                .ConfigureAwait(false);
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
    private sealed class PrefixSource(ISegmentSource inner, long length) : ISegmentSource
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

        public ValueTask DisposeAsync() => default;

        private void Check(SegmentSpec spec)
        {
            if (spec.Offset + spec.Length > (ulong)length)
            {
                throw new VortexFormatException($"A segment at {spec.Offset} runs past the {length}-byte prefix.");
            }
        }
    }
}
