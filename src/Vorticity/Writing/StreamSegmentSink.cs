using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// An <see cref="ISegmentSink"/> over a <see cref="Stream"/>. The position is counted here, so the
/// stream need not be seekable.
/// </summary>
public sealed class StreamSegmentSink : ISegmentSink, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private long _position;

    /// <summary>Wraps <paramref name="stream"/>.</summary>
    /// <param name="stream">The destination. Must be writable.</param>
    /// <param name="ownsStream">Whether disposing this sink disposes the stream.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    public StreamSegmentSink(Stream stream, bool ownsStream = false)
        : this(stream, ownsStream, 0)
    {
    }

    /// <summary>Wraps a stream already holding part of the file: an append.</summary>
    internal StreamSegmentSink(Stream stream, bool ownsStream, long position)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _position = position;
    }

    /// <inheritdoc/>
    public long Position => _position;

    /// <inheritdoc/>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        _position += data.Length;
    }

    /// <inheritdoc/>
    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        new ValueTask(_stream.FlushAsync(cancellationToken));

    /// <summary>Flushes, and disposes the stream when this sink owns it.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stream.FlushAsync().ConfigureAwait(false);
        if (_ownsStream)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
