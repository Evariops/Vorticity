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

    /// <summary>Flushes whatever the sink is buffering.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes what the sink adds once the file's last byte is in, before the writer's last flush: a
    /// sealing stage's last frame and trailer. Nothing for a plain sink.
    /// </summary>
    ValueTask FinishAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
