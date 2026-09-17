// The identity of one version of a file's bytes - docs/13-dataset.md §7.
//
// SIXTEEN RANDOM BYTES, MINTED AT EVERY POSTSCRIPT. A write mints one, an append mints another, and
// so does the post-hoc indexing that rewrites the tail: the identity names a VERSION of the bytes,
// never a path. Anything bound to a file -- an index outside it, a dataset's leaf entry -- records
// the identity it was built against and refuses the file when the identity differs, without a
// single byte of data read. That is what replaces the sidecar's SHA-256, which read the whole file
// to prove the same thing (docs/10-indexes.md §8.1.1).
//
// WRITTEN LAST, RIGHT BEFORE THE POSTSCRIPT, as a postscript metadata entry
// (`vorticity.identity`). The postscript is a few hundred bytes, so the 64 KiB tail every open
// reads already covers the entry: checking an identity costs no request. A strict Rust 0.86.1
// reader validates the entry's bounds and never loads it (vortex-file-0.86.1/src/open.rs,
// `collect_initial_segments`: metadata segments are not offset-sorted and may lie anywhere).
//
// A HINT, LIKE EVERY METADATA VALUE HERE (docs/08-semantics.md §5). An entry of the wrong length,
// or one outside the tail, is no identity: the file opens, and `VortexFile.Identity` is null, as it
// is for every file another writer produced.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Schemas;
using Vorticity.Writing;

namespace Vorticity.File;

/// <summary>The identity entry of a file's postscript.</summary>
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
    /// RENTED, NOT ALLOCATED: the write-path ceilings hold a file to the byte, and the sink has
    /// written the bytes by the time its call completes (<see cref="ISegmentSink"/>).
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
    /// NO REQUEST, EVER: an entry outside the window is not read, it is no identity. This library's
    /// writers put the entry right before the postscript, which the window always covers.
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
