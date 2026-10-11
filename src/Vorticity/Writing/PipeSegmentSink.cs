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

    /// <summary>
    /// The put that reads the pipe, for a file written to a store: a reader that completes before the
    /// file did has failed, and the put's own failure says why.
    /// </summary>
    internal Task? Upload { get; set; }

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

    /// <summary>Lends <paramref name="data"/> to a pipe over a file, which writes it where it lies; copies it into any other.</summary>
    public ValueTask LendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_pipe is not FilePipeWriter file)
        {
            return WriteAsync(data, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!data.IsEmpty)
        {
            file.Lend(data);
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
            await ThrowReaderGoneAsync(Upload).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The failure of a pipe whose reader completed before the file did: the put's that read it, when
    /// a store read it and failed, the reader's early end otherwise.
    /// </summary>
    internal static async ValueTask ThrowReaderGoneAsync(Task? upload)
    {
        if (upload is not null)
        {
            // The put completed its reader on its way out: its failure follows at once.
#pragma warning disable VSTHRD003
            await upload.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        throw new InvalidOperationException("The pipe's reader completed before the file did.");
    }
}
