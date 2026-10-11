using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// Where a writer puts its bytes. Strictly sequential and forward-only: nothing already written can
/// be revisited to fix an offset, so offsets and padding must be settled before each write.
/// </summary>
internal interface ISegmentSink
{
    /// <summary>How many bytes have been written so far, which is the next write's file offset.</summary>
    long Position { get; }

    /// <summary>Appends bytes that are valid only for the duration of the call.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Appends bytes the caller leaves as they are until the next <see cref="FlushAsync"/> completes: a
    /// sink over a file writes them where they lie, gathered with the rest of the flush, and any other
    /// copies them as <see cref="WriteAsync"/> does.
    /// </summary>
    ValueTask LendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => WriteAsync(data, cancellationToken);

    /// <summary>Flushes whatever the sink is buffering.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
