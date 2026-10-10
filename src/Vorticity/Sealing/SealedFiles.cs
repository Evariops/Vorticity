using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Sealing;

/// <summary>How a session tells a sealed file from a plain one, and which reader it opens it through.</summary>
/// <remarks>
/// A sealed file whose last append was torn ends with neither magic. Its header still says it is
/// sealed, and the trailer of the version before the append still lies whole before the tear, since an
/// append writes nothing before the old end: the open walks back to it, as a plain open walks back to
/// its last end-of-file record.
/// </remarks>
internal static class SealedFiles
{
    /// <summary>The trailers a walk tries before it gives up, as many as a plain walk tries.</summary>
    private const int MaxCandidates = 16;

    /// <summary>Whether the object <paramref name="reader"/> reads ends as a sealed object does: one read of its last four bytes.</summary>
    internal static async ValueTask<bool> EndsSealedAsync(ISegmentReader reader, CancellationToken cancellationToken)
    {
        long length = await reader.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        return await LastFourAsync(reader, length, cancellationToken).ConfigureAwait(false) == TrailerMagic;
    }

    /// <summary>
    /// The reader a session opens <paramref name="reader"/>'s file through: a sealed reader over it
    /// when the file is sealed, <paramref name="reader"/> itself when it is plain and the session reads
    /// plaintext. A sealed file whose last append was torn opens at the version before it, under a
    /// policy that allows it, and the tear comes back with the reader.
    /// </summary>
    /// <param name="reader">The file's bytes.</param>
    /// <param name="ownsReader">Whether the sealed reader disposes <paramref name="reader"/>.</param>
    /// <param name="session">The session, which holds a keyring.</param>
    /// <param name="name">What the refusal of a plain file calls it.</param>
    /// <param name="policy">What a torn tail does.</param>
    /// <param name="cancellationToken">Cancels the reads and the unwrap.</param>
    /// <exception cref="VortexEncryptionException">The file is plain and the session refuses plaintext, or its key cannot be had.</exception>
    /// <exception cref="VortexFormatException">The file is sealed, its tail is torn, and the policy refuses it or no whole version precedes the tear.</exception>
    internal static async ValueTask<(ISegmentReader Reader, VortexTornTail? Torn)> ReaderAsync(
        ISegmentReader reader, bool ownsReader, VortexSession session, string name, VortexTornTailPolicy policy,
        CancellationToken cancellationToken)
    {
        long length = await reader.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        uint last = await LastFourAsync(reader, length, cancellationToken).ConfigureAwait(false);
        if (last == TrailerMagic)
        {
            return (await SealedSegmentReader.OpenAsync(reader, ownsReader, session.Keys!.UnwrapAsync, cancellationToken).ConfigureAwait(false), null);
        }

        // A plain file's own magic settles it without a read of the header.
        if (last != PlainMagic && await HeaderAsync(reader, length, cancellationToken).ConfigureAwait(false) is not null)
        {
            return await OpenTornAsync(reader, ownsReader, length, session, name, policy, cancellationToken).ConfigureAwait(false);
        }

        if (session.Options.RefusePlaintext)
        {
            throw RefusedPlain(name);
        }

        return (reader, null);
    }

    /// <summary>
    /// Where the longest prefix of a sealed object that ends with a whole trailer ends: the object's
    /// own length when its trailer is whole, the end of the trailer before a torn append otherwise, or
    /// -1 when there is none. The trailer's layout is checked, not its commitments, which need the key.
    /// </summary>
    /// <param name="reader">The object.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<long> WholeEndAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        if (await HeaderAsync(reader, length, cancellationToken).ConfigureAwait(false) is not { } header)
        {
            return -1;
        }

        if (await LastFourAsync(reader, length, cancellationToken).ConfigureAwait(false) == TrailerMagic
            && await EndsTrailerAsync(reader, length, header, cancellationToken).ConfigureAwait(false))
        {
            return length;
        }

        return await PreviousEndAsync(reader, length, header, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the sealed object <paramref name="reader"/> reads is whole: it begins with a header and
    /// ends with a trailer whose layout reads under the header's descriptor, its frames filling the
    /// bytes between. A sealing stage writes the trailer last, so a whole envelope holds its whole
    /// plaintext. No key is needed, and none of the frames is read.
    /// </summary>
    /// <param name="reader">The object.</param>
    /// <param name="length">Its length.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<bool> IsWholeAsync(ISegmentReader reader, long length, CancellationToken cancellationToken) =>
        await HeaderAsync(reader, length, cancellationToken).ConfigureAwait(false) is { } header
        && await LastFourAsync(reader, length, cancellationToken).ConfigureAwait(false) == TrailerMagic
        && await EndsTrailerAsync(reader, length, header, cancellationToken).ConfigureAwait(false);

    /// <summary>The refusal of a plain file by a session that reads sealed ones only.</summary>
    internal static VortexEncryptionException RefusedPlain(string name) =>
        VortexEncryptionException.Refused(
            $"'{name}' is not sealed, and the session refuses plaintext (VortexSessionOptions.RefusePlaintext).");

    /// <summary><c>VXSE</c>, read as a little-endian integer.</summary>
    private static uint TrailerMagic => BinaryPrimitives.ReadUInt32LittleEndian(SealedFormat.TrailerMagic);

    /// <summary><c>VTXF</c>, read as a little-endian integer.</summary>
    private static uint PlainMagic => BinaryPrimitives.ReadUInt32LittleEndian(File.VortexFileFormat.MagicBytes);

    /// <summary>The object's last four bytes as a little-endian integer, or 0 when it is shorter.</summary>
    private static async ValueTask<uint> LastFourAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        if (length < 4)
        {
            return 0;
        }

        SegmentOwner end = await reader.ReadRangeAsync(length - 4, 4, 1, cancellationToken).ConfigureAwait(false);
        try
        {
            return end.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(end.Buffer.Span) : 0;
        }
        finally
        {
            end.Release();
        }
    }

    /// <summary>The descriptor of the object's header, or null when it does not begin as a sealed object.</summary>
    private static async ValueTask<SealDescriptor?> HeaderAsync(ISegmentReader reader, long length, CancellationToken cancellationToken)
    {
        if (length < SealedFormat.HeaderPrefixBytes)
        {
            return null;
        }

        SegmentOwner prefix = await reader.ReadRangeAsync(0, SealedFormat.HeaderPrefixBytes, 1, cancellationToken).ConfigureAwait(false);
        int descriptorLength;
        try
        {
            ReadOnlySpan<byte> bytes = prefix.Buffer.Span;
            if (bytes.Length != SealedFormat.HeaderPrefixBytes || !bytes[..SealedFormat.HeaderMagic.Length].SequenceEqual(SealedFormat.HeaderMagic))
            {
                return null;
            }

            descriptorLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[SealedFormat.HeaderMagic.Length..]);
        }
        finally
        {
            prefix.Release();
        }

        if (descriptorLength <= 0 || descriptorLength > length - SealedFormat.HeaderPrefixBytes)
        {
            return null;
        }

        SegmentOwner descriptor = await reader.ReadRangeAsync(SealedFormat.HeaderPrefixBytes, descriptorLength, 1, cancellationToken).ConfigureAwait(false);
        try
        {
            SealDescriptor read = SealDescriptor.Read(descriptor.Buffer.Span, out int consumed);
            return consumed == descriptorLength ? read : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
        catch (VortexUnsupportedException)
        {
            return null;
        }
        finally
        {
            descriptor.Release();
        }
    }

    /// <summary>The version before a torn append, through a sealed reader over the prefix that ends with its trailer.</summary>
    private static async ValueTask<(ISegmentReader Reader, VortexTornTail? Torn)> OpenTornAsync(
        ISegmentReader reader, bool ownsReader, long length, VortexSession session, string name, VortexTornTailPolicy policy,
        CancellationToken cancellationToken)
    {
        const string Reason = "the bytes after its last whole trailer end no trailer of a sealed file";
        if (policy == VortexTornTailPolicy.Refuse)
        {
            throw new VortexFormatException($"'{name}' is sealed and its tail is torn: {Reason}.");
        }

        long end = await WholeEndAsync(reader, length, cancellationToken).ConfigureAwait(false);
        if (end <= 0)
        {
            throw new VortexFormatException($"'{name}' is sealed and its tail is torn, and no whole trailer precedes the tear: no version of it can be read.");
        }

        // On failure the prefix holds nothing of its own, and the caller disposes the reader under it.
        VortexFileRepair.PrefixSource prefix = new VortexFileRepair.PrefixSource(reader, end, ownsReader);
        SealedSegmentReader opened = await SealedSegmentReader.OpenAsync(prefix, ownsInner: true, session.Keys!.UnwrapAsync, cancellationToken).ConfigureAwait(false);
        return (opened, new VortexTornTail(length, end, Reason));
    }

    /// <summary>
    /// The end of the last whole trailer before <paramref name="length"/>, walking back window by
    /// window, or -1. A trailer is recognised by its magic, then checked whole: its length fits, its
    /// layout reads, and its descriptor is the header's.
    /// </summary>
    private static async ValueTask<long> PreviousEndAsync(ISegmentReader reader, long length, SealDescriptor header, CancellationToken cancellationToken)
    {
        int magic = SealedFormat.TrailerMagic.Length;
        long floor = SealedFormat.HeaderPrefixBytes + header.Length;
        int candidates = 0;
        long high = length;
        while (high - floor > magic)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long low = Math.Max(floor, high - VortexFileRepair.Window);
            int size = (int)(high - low);
            SegmentOwner window = await reader.ReadRangeAsync(low, size, 1, cancellationToken).ConfigureAwait(false);
            try
            {
                for (int at = LastMagic(window.Buffer.Span, low, length, size); at >= 0; at = LastMagic(window.Buffer.Span, low, length, at))
                {
                    long end = low + at + magic;
                    if (await EndsTrailerAsync(reader, end, header, cancellationToken).ConfigureAwait(false))
                    {
                        return end;
                    }

                    if (++candidates == MaxCandidates)
                    {
                        return -1;
                    }
                }
            }
            finally
            {
                window.Release();
            }

            if (low == floor)
            {
                break;
            }

            // A magic may straddle two windows: the next one reaches the three bytes this one could hold.
            high = low + magic - 1;
        }

        return -1;
    }

    /// <summary>Where the last <c>VXSE</c> of a window begins, below <paramref name="below"/>, in a trailer that ends before the object does; or -1.</summary>
    private static int LastMagic(ReadOnlySpan<byte> bytes, long low, long length, int below)
    {
        ReadOnlySpan<byte> magic = SealedFormat.TrailerMagic;
        for (int at = Math.Min(bytes.Length - magic.Length, below - 1); at >= 0; at--)
        {
            if (low + at + magic.Length < length && bytes.Slice(at, magic.Length).SequenceEqual(magic))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Whether a whole trailer of this object ends at <paramref name="end"/>: its layout reads over the first <paramref name="end"/> bytes, under the header's descriptor.</summary>
    private static async ValueTask<bool> EndsTrailerAsync(ISegmentReader reader, long end, SealDescriptor header, CancellationToken cancellationToken)
    {
        if (end < SealedFormat.HeaderPrefixBytes + SealedFormat.TrailerSuffixBytes)
        {
            return false;
        }

        SegmentOwner suffix = await reader.ReadRangeAsync(end - SealedFormat.TrailerSuffixBytes, SealedFormat.TrailerSuffixBytes, 1, cancellationToken).ConfigureAwait(false);
        int trailerLength;
        try
        {
            trailerLength = SealedLayout.TrailerLength(suffix.Buffer.Span, end);
        }
        catch (VortexFormatException)
        {
            return false;
        }
        finally
        {
            suffix.Release();
        }

        SegmentOwner trailer = await reader.ReadRangeAsync(end - trailerLength, trailerLength, 1, cancellationToken).ConfigureAwait(false);
        try
        {
            return trailer.Length == trailerLength
                && SealedLayout.Read(trailer.Buffer.Span, end).Descriptor.SameAs(header.Bytes);
        }
        catch (VortexFormatException)
        {
            return false;
        }
        catch (VortexUnsupportedException)
        {
            return false;
        }
        finally
        {
            trailer.Release();
        }
    }
}
