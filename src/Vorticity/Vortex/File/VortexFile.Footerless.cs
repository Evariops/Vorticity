using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

public sealed partial class VortexFile
{
    /// <summary>
    /// The segment the postscript names under <paramref name="key"/>, read from the end of the file
    /// alone: its EOF marker, then its postscript, in two requests that read no byte of the dtype,
    /// the layout or the footer. Null when the postscript names no such entry.
    /// </summary>
    /// <param name="source">The file's bytes.</param>
    /// <param name="fileLength">The file's length.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="VortexFormatException">The file's end is not a Vortex file's, or its postscript is malformed.</exception>
    internal static async ValueTask<SegmentSpec?> ReadMetadataSpecAsync(
        ISegmentReader source, long fileLength, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (fileLength < VortexFileFormat.EofSize)
        {
            FileThrow.FileTooShort(fileLength);
        }

        int postscriptLength;
        using (SegmentOwner eof = await source
            .ReadRangeAsync(fileLength - VortexFileFormat.EofSize, VortexFileFormat.EofSize, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            if (eof.Length != VortexFileFormat.EofSize)
            {
                FileThrow.ShortRead(fileLength - VortexFileFormat.EofSize, VortexFileFormat.EofSize, eof.Length);
            }

            postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(
                eof.Buffer.Span.Slice(VortexFileFormat.EofPostscriptLengthOffset, 2));
        }

        // The parse checks the marker and bounds the length before it slices anything; a length past
        // the file is a postscript the file cannot hold.
        int window = postscriptLength + VortexFileFormat.EofSize;
        if (window > fileLength)
        {
            FileThrow.PostscriptTruncated(postscriptLength, (int)Math.Min(fileLength, int.MaxValue));
        }

        long offset = fileLength - window;
        using SegmentOwner tail = await source.ReadRangeAsync(offset, window, 1, cancellationToken).ConfigureAwait(false);
        if (tail.Length != window)
        {
            FileThrow.ShortRead(offset, window, tail.Length);
        }

        PostscriptInfo postscript = ParsePostscript(tail.Buffer.Span, offset, fileLength, VortexOpenOptions.Default);
        for (int i = 0; i < postscript.MetadataKeys.Length; i++)
        {
            if (string.Equals(postscript.MetadataKeys[i], key, StringComparison.Ordinal))
            {
                return postscript.MetadataSegments[i];
            }
        }

        return null;
    }
}
