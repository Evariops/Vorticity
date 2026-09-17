// The write adapter of docs/13-dataset.md §11: "data objects are written through the store by a
// forward-only `ISegmentSink` adapter (03 §3.8), which is what lets the S3 library use a multipart
// upload".
//
// WHY THERE IS A `CommitAsync` AND WHY `FlushAsync` DOES NOTHING. The sink contract of 03 §3.8 says
// a flush makes the bytes "durable as far as the sink is concerned". For an object store, nothing
// is durable until the object exists, and an object exists all at once or not at all -- that is
// §11's atomic `PutIfAbsent` and it is the property the whole commit protocol rests on. So the
// honest reading of the contract here is: a flush is a no-op because there is nothing between a
// buffer and an object, and the put is a call of its own whose ANSWER the caller needs (`Created`
// or `Exists` is the outcome of a commit, §8.1).
//
// THIS ONE BUFFERS THE WHOLE OBJECT, which is right for a commit object -- §3 keeps its header
// under 256 KiB and the object is a header, some pages and some fragments -- and wrong for a data
// object of many gigabytes. The seam is what matters: an S3 sink writes each part as it fills and
// completes the upload in `CommitAsync`, and the writer above it cannot tell the difference. The
// ceiling below is there so that the wrong choice fails with a sentence rather than an
// `OutOfMemoryException`.
//
// IT OUTLIVES ITS WRITER, ON PURPOSE. `VortexFileWriter.DisposeAsync` completes the file and then
// disposes the sink it was handed, which is right for a stream and wrong for an object: the file is
// not finished until that dispose returns, so a sink that put its bytes when disposed would create
// the object before knowing whether the file was complete -- and one that freed its buffer there
// could never create it at all. So a dispose here CLOSES the sink (no more writes) and keeps the
// bytes; `CommitAsync` is what creates the object, afterwards, and `Discard` is what a caller that
// changed its mind calls instead.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>An <see cref="ISegmentSink"/> that becomes one object of an <see cref="IObjectStore"/>.</summary>
public sealed class ObjectSegmentSink : ISegmentSink, IAsyncDisposable
{
    /// <summary>The most bytes this sink buffers before refusing, 1 GiB.</summary>
    public const long DefaultMaxBytes = 1L << 30;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly long _maxBytes;
    private byte[] _buffer;
    private int _length;

    /// <summary>The bytes written, which survives the buffer's release so the object's size does.</summary>
    private long _written;
    private bool _committed;
    private bool _closed;
    private bool _released;

    /// <summary>Opens a sink that will create <paramref name="key"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="maxBytes">The most bytes to buffer, or 0 for <see cref="DefaultMaxBytes"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ObjectSegmentSink(IObjectStore store, string key, long maxBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _store = store;
        _key = key;
        _maxBytes = maxBytes == 0 ? DefaultMaxBytes : maxBytes;
        _buffer = [];
    }

    /// <inheritdoc/>
    public long Position => _written;

    /// <summary>The key this sink creates.</summary>
    public string Key => _key;

    /// <summary>Whether <see cref="CommitAsync"/> has run.</summary>
    public bool IsCommitted => _committed;

    /// <summary>Whether the sink is closed to further writes: its writer disposed it.</summary>
    public bool IsClosed => _closed;

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_closed)
        {
            throw new InvalidOperationException(
                $"'{_key}' is closed: its writer disposed it, and a sink takes no bytes after that.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        long wanted = (long)_length + data.Length;
        if (wanted > _maxBytes)
        {
            throw new ObjectStoreException(
                $"'{_key}' would buffer {wanted} bytes, over this sink's {_maxBytes}. A store that " +
                "streams -- a multipart upload -- is what writes an object this size (13 §11).");
        }

        if (wanted > _buffer.Length)
        {
            Grow((int)wanted);
        }

        data.Span.CopyTo(_buffer.AsSpan(_length));
        _length = (int)wanted;
        _written = wanted;
        return ValueTask.CompletedTask;
    }

    /// <summary>Does nothing, and the file comment says why.</summary>
    /// <param name="cancellationToken">Cancels nothing.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates the object from everything written so far.</summary>
    /// <param name="cancellationToken">Cancels the put.</param>
    /// <returns>
    /// <see cref="PutOutcome.Created"/>, or <see cref="PutOutcome.Exists"/> when another writer got
    /// there first — which for a commit object is the answer, not an error (§8.1).
    /// </returns>
    /// <exception cref="InvalidOperationException">It was already committed.</exception>
    public async ValueTask<PutOutcome> CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_committed)
        {
            throw new InvalidOperationException($"'{_key}' was already committed.");
        }

        _closed = true;
        PutOutcome outcome = await _store
            .PutIfAbsentAsync(_key, _buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
        _committed = true;
        Release();
        return outcome;
    }

    /// <summary>Throws the buffered bytes away without creating the object.</summary>
    /// <remarks>
    /// What a caller that changed its mind, or whose write failed, calls instead of
    /// <see cref="CommitAsync"/>. A sink that is neither committed nor discarded simply holds its
    /// buffer until the garbage collector takes it: nothing reaches the store either way.
    /// </remarks>
    public void Discard()
    {
        _closed = true;
        Release();
    }

    /// <summary>Closes the sink to further writes and keeps the bytes, as the file comment says.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        _closed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Gives the buffer back to the pool, once.</summary>
    private void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        byte[] buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Grows the buffer to at least <paramref name="wanted"/> bytes, doubling.</summary>
    /// <param name="wanted">The bytes needed.</param>
    private void Grow(int wanted)
    {
        int size = Math.Max(wanted, Math.Max(_buffer.Length * 2, 64 * 1024));
        byte[] grown = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _length).CopyTo(grown);
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = grown;
    }
}
