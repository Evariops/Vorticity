using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="ISegmentSink"/> that becomes one object of an <see cref="IObjectStore"/>, buffering
/// the whole object. It outlives its writer on purpose: disposing only closes it to further writes
/// and keeps the bytes, since a writer disposes its sink before the file is known to be complete.
/// <see cref="CommitAsync"/> creates the object afterwards, and <see cref="Discard"/> is what a
/// caller that changed its mind calls instead.
/// </summary>
public sealed class ObjectSegmentSink : ISegmentSink, IAsyncDisposable
{
    /// <summary>The most bytes this sink buffers before refusing, 1 GiB.</summary>
    public const long DefaultMaxBytes = 1L << 30;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly long _maxBytes;
    private byte[] _buffer;
    private int _length;

    /// <summary>Appended to as the bytes go by, rather than computed from the buffer.</summary>
    private readonly System.IO.Hashing.XxHash128 _hash = new System.IO.Hashing.XxHash128();

    /// <summary>The bytes written, which survives the buffer's release so the object's size does.</summary>
    private long _written;
    private bool _committed;
    private bool _closed;
    private bool _released;

    /// <summary>
    /// Opens a sink that will create <paramref name="key"/>; <paramref name="maxBytes"/> of 0 means
    /// <see cref="DefaultMaxBytes"/>.
    /// </summary>
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

    /// <summary>The XXH3-128 of everything written so far, as the leaf entry records it.</summary>
    public UInt128 ContentHash => _hash.GetCurrentHashAsUInt128();

    /// <summary>
    /// The bytes written so far, so that a caller can read the file's own statistics out of them
    /// rather than read the object back after the put. Empty once the buffer is released by
    /// <see cref="CommitAsync"/> or <see cref="Discard"/>, so it is not a value to hold on to.
    /// </summary>
    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _length);

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
        _hash.Append(data.Span);
        _length = (int)wanted;
        _written = wanted;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Does nothing: nothing is durable until the object exists, and the object exists all at once
    /// or not at all, which is <see cref="CommitAsync"/>.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates the object from everything written so far, answering
    /// <see cref="PutOutcome.Exists"/> when another writer got there first -- which for a commit
    /// object is the answer, not an error.
    /// </summary>
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
    public void Discard()
    {
        _closed = true;
        Release();
    }

    /// <summary>Closes the sink to further writes and keeps the bytes.</summary>
    public ValueTask DisposeAsync()
    {
        _closed = true;
        return ValueTask.CompletedTask;
    }

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
