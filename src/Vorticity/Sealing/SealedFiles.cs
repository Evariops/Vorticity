using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Sealing;

/// <summary>How a session tells a sealed file from a plain one, and which reader it opens it through.</summary>
internal static class SealedFiles
{
    /// <summary>Whether the object <paramref name="reader"/> reads ends as a sealed object does: one read of its last four bytes.</summary>
    internal static async ValueTask<bool> EndsSealedAsync(ISegmentReader reader, CancellationToken cancellationToken)
    {
        long length = await reader.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length < SealedFormat.TrailerMagic.Length)
        {
            return false;
        }

        SegmentOwner end = await reader
            .ReadRangeAsync(length - SealedFormat.TrailerMagic.Length, SealedFormat.TrailerMagic.Length, 1, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return SealedLayout.EndsSealed(end.Buffer.Span);
        }
        finally
        {
            end.Release();
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> is sealed.</summary>
    internal static async ValueTask<bool> IsSealedAsync(string path, CancellationToken cancellationToken)
    {
        FileSegmentSource file = new FileSegmentSource(path);
        await using (file.ConfigureAwait(false))
        {
            return await EndsSealedAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The reader a session opens <paramref name="reader"/>'s file through: a sealed reader over it
    /// when the file is sealed, <paramref name="reader"/> itself when it is plain and the session reads
    /// plaintext. Null when the file is sealed, so that the caller learns it must not open it plain.
    /// </summary>
    /// <param name="reader">The file's bytes.</param>
    /// <param name="ownsReader">Whether the sealed reader disposes <paramref name="reader"/>.</param>
    /// <param name="session">The session, which holds a keyring.</param>
    /// <param name="name">What the refusal of a plain file calls it.</param>
    /// <param name="cancellationToken">Cancels the reads and the unwrap.</param>
    /// <exception cref="VortexEncryptionException">The file is plain and the session refuses plaintext, or its key cannot be had.</exception>
    internal static async ValueTask<ISegmentReader> ReaderAsync(
        ISegmentReader reader, bool ownsReader, VortexSession session, string name, CancellationToken cancellationToken)
    {
        if (!await EndsSealedAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (session.Options.RefusePlaintext)
            {
                throw RefusedPlain(name);
            }

            return reader;
        }

        return await SealedSegmentReader.OpenAsync(reader, ownsReader, session.Keys!.UnwrapAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The refusal of a plain file by a session that reads sealed ones only.</summary>
    internal static VortexEncryptionException RefusedPlain(string name) =>
        VortexEncryptionException.Refused(
            $"'{name}' is not sealed, and the session refuses plaintext (VortexSessionOptions.RefusePlaintext).");
}
