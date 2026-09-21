using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// An async range-read source over one Vortex file.
/// </summary>
/// <remarks>
/// <para>
/// Object storage stays outside the core — it would need an HTTP client, and the core takes no
/// dependencies — so this is the seam an outside implementer fills. Everything below is a
/// requirement on every implementation, stated rather than implied, because such an implementer
/// has no one to ask.
/// </para>
/// <para>
/// <b>Thread safety.</b> Implementations must be thread-safe: concurrent splits read through one
/// source. A <see cref="SegmentRequestSet"/>, by contrast, belongs to one flow and is never
/// shared.
/// </para>
/// <para>
/// <b>Partial failure.</b> <see cref="ReadManyAsync"/> is all-or-nothing. If any range fails,
/// every buffer already acquired inside that call is released and the exception propagates;
/// <see cref="SegmentRequestSet.IsPopulated"/> stays <see langword="false"/>. Retry policy belongs
/// to the source, not to the reader.
/// </para>
/// <para>
/// <b>Ownership.</b> Returned buffers are owned by the source until the batch that requested them
/// is disposed. Every returned <see cref="SegmentOwner"/> carries the caller's reference already:
/// the caller <see cref="SegmentOwner.Release"/>s it exactly once. A caching source
/// <see cref="SegmentOwner.Retain"/>s before handing out. The reader never frees what it did not
/// allocate.
/// </para>
/// <para>
/// <b>Cache.</b> Optional and source-side. Eviction policy and memory budget are the source's
/// business; the reader assumes nothing about hit rates and never requires a segment to still be
/// cached.
/// </para>
/// <para>
/// <b>Cancellation.</b> A cancelled <see cref="ReadManyAsync"/> leaves the cache consistent: a
/// segment is fully present or fully absent, never partially populated. Throw
/// <see cref="OperationCanceledException"/> — after releasing what you acquired.
/// </para>
/// </remarks>
internal interface ISegmentReader : IAsyncDisposable
{
    /// <summary>The total size of the underlying file in bytes, if known without I/O.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The file length in bytes.</returns>
    ValueTask<long> GetLengthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads one segment.
    /// </summary>
    /// <param name="spec">The locator, straight off the wire.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// An owner carrying the caller's reference, which the caller releases exactly once. Its
    /// <see cref="SegmentOwner.Buffer"/> is exactly <c>spec.Length</c> bytes — a short read is
    /// <see cref="VortexFormatException"/>, never a truncated buffer.
    /// </returns>
    /// <exception cref="VortexFormatException">
    /// The spec is malformed, escapes the file, or the file is shorter than it claims.
    /// </exception>
    ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every registered segment in <paramref name="requests"/>, coalescing adjacent ranges.
    /// </summary>
    /// <param name="requests">
    /// The caller's request set. On success it is <see cref="SegmentRequestSet.Complete">completed
    /// </see>; on any failure every buffer acquired inside this call is released and the set is
    /// left registered but unpopulated, ready to retry. An already-populated set is left untouched.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when every slot holds its bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requests"/> is null.</exception>
    /// <exception cref="VortexFormatException">A spec is malformed or escapes the file.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an arbitrary byte range — used only by the open path, for the 64 KiB tail.
    /// </summary>
    /// <param name="offset">A non-negative file offset.</param>
    /// <param name="length">
    /// The number of bytes wanted. Clamped to the file: asking for 64 KiB of a 3 KiB file returns
    /// 3 KiB. A range that starts wholly past the end is <see cref="VortexFormatException"/>.
    /// </param>
    /// <param name="alignment">
    /// The alignment the returned buffer's base address must satisfy: a power of two in
    /// <c>[1, <see cref="VortexLimits.MaxAlignment"/>]</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An owner carrying the caller's reference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative, or
    /// <paramref name="alignment"/> is not a usable power of two.
    /// </exception>
    /// <exception cref="VortexFormatException">The range starts past the end of the file.</exception>
    ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken);
}
