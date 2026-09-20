using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// Where a writer puts its bytes. Strictly sequential and forward-only: nothing already written can
/// be revisited to fix an offset, so offsets and padding must be settled before each write.
/// </summary>
public interface ISegmentSink
{
    /// <summary>How many bytes have been written so far, which is the next write's file offset.</summary>
    long Position { get; }

    /// <summary>Appends bytes that are valid only for the duration of the call.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Flushes whatever the sink is buffering.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}
