// The writer's seam - docs/03-architecture.md §3.8, the mirror of ISegmentSource.
//
// SINGLE-PASS, FORWARD-ONLY, NO SEEKING. The format is designed for exactly this order -- segments,
// then metadata flatbuffers, then the postscript, then the EOF marker -- and that is what makes an
// S3 multipart upload trivial in the layer above. Nothing here can go back and fix an offset it
// already wrote, which is why every offset the footer records is the position the sink reported
// BEFORE the write rather than one computed afterwards.
//
// The consequence the architecture note asks to be written down before someone implements it the
// slow way: inter-segment padding is computed as each segment goes out, because a segment's
// required alignment is known when it is written. No full-file materialization is ever needed, and
// a giant buffer "for simplicity" would throw the streaming property away.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>Where a writer puts its bytes. Sequential; never seeks.</summary>
public interface ISegmentSink
{
    /// <summary>How many bytes have been written so far, which is the next write's file offset.</summary>
    long Position { get; }

    /// <summary>Appends <paramref name="data"/>.</summary>
    /// <param name="data">The bytes. Valid only for the duration of the call.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are accepted.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Flushes whatever the sink is buffering.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A task that completes when the bytes are durable as far as the sink is concerned.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
