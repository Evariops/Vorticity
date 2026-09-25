using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// A sink over a <see cref="PipeWriter"/>: every write lands in the pipe's buffer, and only a flush
/// hands the bytes on, so the sink's backpressure reaches the producer at <c>FlushAsync</c>.
/// </summary>
internal sealed class PipeSegmentSink : ISegmentSink
{
    private readonly PipeWriter _pipe;
    private long _position;
    private long _unflushed;

    internal PipeSegmentSink(PipeWriter pipe, long position = 0)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        _pipe = pipe;
        _position = position;
    }

    public long Position => _position;

    /// <summary>Bytes written into the pipe and not yet flushed.</summary>
    internal long UnflushedBytes => _unflushed;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!data.IsEmpty)
        {
            _pipe.Write(data.Span);
            _position += data.Length;
            _unflushed += data.Length;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        FlushResult result = await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        _unflushed = 0;
        if (result.IsCanceled)
        {
            throw new OperationCanceledException("The pipe's flush was canceled.");
        }

        if (result.IsCompleted)
        {
            throw new InvalidOperationException("The pipe's reader completed before the file did.");
        }
    }
}
