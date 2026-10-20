using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Indexes;

/// <summary>
/// A reader that may keep the regions of an object's indexes that no table of contents lists, the pages of a run's
/// fence table and the segments of its payload, when their reader names them with the checksums the directory or
/// the parent page gave.
/// </summary>
/// <remarks>
/// A lookup of an identifier reads them at every question: a fence page, then a segment of each run, each a round
/// trip of its own after the directory. The session's synopsis cache keeps them on the disk as it keeps the regions
/// the contents declare, and a reader that wraps it passes them on.
/// </remarks>
internal interface IIndexRegionReader
{
    /// <summary>
    /// Reads <paramref name="requests"/> as <see cref="ISegmentReader.ReadManyAsync"/> does, its slot <c>i</c> the
    /// region <paramref name="regions"/>[i] of an index's run: a slot past them, or whose region lies elsewhere or
    /// has no checksum, is read as any other.
    /// </summary>
    /// <param name="requests">The request set, as <see cref="ISegmentReader.ReadManyAsync"/> takes it.</param>
    /// <param name="regions">The regions of the set's slots, in slot order.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when every slot holds its bytes.</returns>
    ValueTask ReadRegionsAsync(SegmentRequestSet requests, ReadOnlyMemory<IndexSegment> regions, CancellationToken cancellationToken);
}

/// <summary>The reads of an index's regions, through any reader.</summary>
internal static class IndexRegionReads
{
    /// <summary>
    /// Reads <paramref name="requests"/>, whose slot <c>i</c> is the region <paramref name="regions"/>[i] of an index's
    /// run, through <paramref name="reader"/>: one that keeps index regions may keep them, any other reads them as any.
    /// </summary>
    /// <param name="reader">The reader of the run's origin.</param>
    /// <param name="requests">The request set.</param>
    /// <param name="regions">The regions of the set's slots, in slot order.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when every slot holds its bytes.</returns>
    internal static ValueTask ReadRegionsAsync(
        this ISegmentReader reader, SegmentRequestSet requests, ReadOnlyMemory<IndexSegment> regions, CancellationToken cancellationToken) =>
        reader is IIndexRegionReader keeping
            ? keeping.ReadRegionsAsync(requests, regions, cancellationToken)
            : reader.ReadManyAsync(requests, cancellationToken);
}
