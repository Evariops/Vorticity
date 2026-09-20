using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Schemas;
using Vorticity.Writing;

namespace Vorticity.File;

/// <summary>
/// The identity entry of a file's postscript: sixteen random bytes minted afresh every time a
/// postscript is written, naming one version of the file's bytes rather than a path, so that
/// anything bound to a file - an index kept outside it, a dataset's leaf entry - can refuse a file
/// that has changed under it without reading a single byte of data. The entry sits immediately
/// before the postscript, inside the tail every open already reads, so checking one costs no
/// request; it is only a hint, and a missing, misplaced or wrongly sized entry leaves the file
/// perfectly readable with no identity, which is also the case for files other writers produce.
/// </summary>
internal static class FileIdentity
{
    /// <summary>The postscript metadata key.</summary>
    internal static ReadOnlySpan<byte> MetadataKeyUtf8 => "vorticity.identity"u8;

    /// <summary>The value's length: a <see cref="Guid"/>'s sixteen bytes.</summary>
    internal const int Length = 16;

    /// <summary>
    /// Writes the identity a postscript will name: the caller's when it pinned one, a fresh random
    /// one otherwise.
    /// </summary>
    /// <param name="sink">The file's sink, positioned where the entry goes.</param>
    /// <param name="pinned">The identity the write options name, if any.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The buffer is rented rather than allocated, so that writing a file stays within its
    /// allocation budget, and returning it at once is safe because <see cref="ISegmentSink"/> has
    /// consumed the bytes by the time its call completes.
    /// </remarks>
    internal static async ValueTask WriteAsync(ISegmentSink sink, Guid? pinned, CancellationToken cancellationToken)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Length);
        try
        {
            (pinned ?? Guid.NewGuid()).TryWriteBytes(bytes);
            await sink.WriteAsync(bytes.AsMemory(0, Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// The identity a file's tail names, or null when the file has none usable.
    /// </summary>
    /// <param name="keys">The postscript's metadata keys.</param>
    /// <param name="segments">The postscript's metadata segments, parallel to <paramref name="keys"/>.</param>
    /// <param name="window">The tail the open read.</param>
    /// <param name="windowOffset">Where the tail starts in the file.</param>
    /// <remarks>
    /// An entry lying outside the window counts as no identity rather than being fetched, so this
    /// never issues a read. This library's writers put the entry right before the postscript, which
    /// the window always covers.
    /// </remarks>
    internal static Guid? Find(
        byte[][] keys, SegmentSpec[] segments, ReadOnlySpan<byte> window, long windowOffset)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (!keys[i].AsSpan().SequenceEqual(MetadataKeyUtf8))
            {
                continue;
            }

            ref readonly SegmentSpec spec = ref segments[i];
            if (spec.Offset < (ulong)windowOffset)
            {
                return null;
            }

            ulong relative = spec.Offset - (ulong)windowOffset;
            return relative > (ulong)window.Length || (ulong)window.Length - relative < spec.Length
                ? null
                : Parse(window.Slice((int)relative, (int)spec.Length));
        }

        return null;
    }

    /// <summary>The identity in an entry's value, or null when the value is not one.</summary>
    /// <param name="value">The entry's bytes.</param>
    internal static Guid? Parse(ReadOnlySpan<byte> value) =>
        value.Length == Length ? new Guid(value) : null;
}
