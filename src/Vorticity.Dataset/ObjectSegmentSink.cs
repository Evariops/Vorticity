using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="ISegmentSink"/> that becomes one object of an <see cref="IObjectStore"/>, buffering
/// the whole object. It outlives its writer on purpose: disposing only closes it to further writes
/// and keeps the bytes, since a writer disposes its sink before the file is known to be complete.
/// <see cref="CommitAsync"/> creates the object afterwards, and <see cref="Discard"/> is what a
/// caller that changed its mind calls instead.
/// </summary>
/// <remarks>
/// The bytes are held in chunks from a pool, never in one buffer: a buffer that doubles as the object
/// grows copies it at every step, and hands the pool arrays of every size up to the object's, which a
/// pool then keeps. The first chunk is <see cref="FirstChunkBytes"/>, each next one twice the one
/// before up to <see cref="ChunkBytes"/>, so a small object, as a commit of a few rows writes, holds
/// a few kilobytes of the pool rather than a mebibyte, and a chunk is the most a pool is handed back
/// at once, whatever the object's size.
/// </remarks>
internal sealed class ObjectSegmentSink : ISegmentSink, IAsyncDisposable
{
    /// <summary>The most bytes this sink buffers before refusing, 1 GiB.</summary>
    public const long DefaultMaxBytes = 1L << 30;

    /// <summary>The bytes of the largest chunks of the buffer, those past the first few.</summary>
    internal const int ChunkBytes = 1 << 20;

    /// <summary>The bytes of the first chunk of the buffer.</summary>
    internal const int FirstChunkBytes = 64 << 10;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly long _maxBytes;
    private readonly MemoryPool<byte> _pool;
    private readonly List<IMemoryOwner<byte>> _chunks = [];

    /// <summary>Appended to as the bytes go by, rather than computed from the buffer.</summary>
    private readonly System.IO.Hashing.XxHash128 _hash = new System.IO.Hashing.XxHash128();

    /// <summary>The bytes written, which survives the buffer's release so the object's size does.</summary>
    private long _written;

    /// <summary>The bytes written into the last chunk.</summary>
    private int _used;
    private bool _committed;
    private bool _closed;
    private bool _released;

    /// <summary>
    /// Opens a sink that will create <paramref name="key"/>; <paramref name="maxBytes"/> of 0 means
    /// <see cref="DefaultMaxBytes"/>, and a null <paramref name="pool"/> the shared aligned one.
    /// </summary>
    public ObjectSegmentSink(IObjectStore store, string key, long maxBytes = 0, MemoryPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _store = store;
        _key = key;
        _maxBytes = maxBytes == 0 ? DefaultMaxBytes : maxBytes;
        _pool = pool ?? AlignedMemoryPool.Shared;
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
    /// A source over the bytes written so far, so that a caller can read the file's own statistics
    /// out of them rather than read the object back after the put. It reads the chunks in place and
    /// is not to be used once <see cref="CommitAsync"/> or <see cref="Discard"/> released them.
    /// </summary>
    public ISegmentSource Content()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        return new ChunkSource(this);
    }

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
        long wanted = _written + data.Length;
        if (wanted > _maxBytes)
        {
            throw new ObjectStoreException(
                $"'{_key}' would buffer {wanted} bytes, over the {_maxBytes} an object may take while it is " +
                "written (DatasetOptions.MaxObjectBytes).");
        }

        ReadOnlySpan<byte> rest = data.Span;
        _hash.Append(rest);
        while (!rest.IsEmpty)
        {
            if (_chunks.Count == 0 || _used == SizeOf(_chunks.Count - 1))
            {
                _chunks.Add(_pool.Rent(SizeOf(_chunks.Count)));
                _used = 0;
            }

            Span<byte> room = _chunks[^1].Memory.Span[_used..SizeOf(_chunks.Count - 1)];
            int taken = Math.Min(room.Length, rest.Length);
            rest[..taken].CopyTo(room);
            rest = rest[taken..];
            _used += taken;
            _written += taken;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The bytes chunk <paramref name="chunk"/> takes of what is written: twice the one before, up to <see cref="ChunkBytes"/>.</summary>
    private static int SizeOf(int chunk) => chunk >= 4 ? ChunkBytes : Math.Min(FirstChunkBytes << chunk, ChunkBytes);

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
        PutOutcome outcome;
        if (_store is SealedObjectStore sealedStore)
        {
            (outcome, StoredLength) = await sealedStore
                .PutAsync(_key, PipeReader.Create(Bytes()), _written, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await _store
                .PutIfAbsentAsync(_key, PipeReader.Create(Bytes()), _written, cancellationToken).ConfigureAwait(false);
            StoredLength = _written;
        }

        _committed = true;
        Release();
        return outcome;
    }

    /// <summary>
    /// The bytes the store holds once <see cref="CommitAsync"/> has run: what was written, or more for
    /// an encrypted dataset's object, which the store holds sealed.
    /// </summary>
    public long StoredLength { get; private set; } = -1;

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

    /// <summary>The bytes written, chunk after chunk, as one sequence.</summary>
    private ReadOnlySequence<byte> Bytes()
    {
        if (_written == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        Chunk first = new Chunk(Held(0), 0);
        Chunk last = first;
        for (int chunk = 1; chunk < _chunks.Count; chunk++)
        {
            last = last.Then(Held(chunk));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    /// <summary>The written part of one chunk: all of it but for the last.</summary>
    private ReadOnlyMemory<byte> Held(int chunk) =>
        _chunks[chunk].Memory[..(chunk == _chunks.Count - 1 ? _used : SizeOf(chunk))];

    private void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        foreach (IMemoryOwner<byte> chunk in _chunks)
        {
            chunk.Dispose();
        }

        _chunks.Clear();
    }

    /// <summary>One chunk of the sequence <see cref="Bytes"/> hands the store.</summary>
    private sealed class Chunk : ReadOnlySequenceSegment<byte>
    {
        internal Chunk(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        internal Chunk Then(ReadOnlyMemory<byte> memory)
        {
            Chunk next = new Chunk(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    /// <summary>
    /// The bytes written so far as a file to open: a range inside one chunk is handed out in place,
    /// one across chunks as the sequence it is, and the reader copies it where it needs one buffer.
    /// </summary>
    private sealed class ChunkSource(ObjectSegmentSink sink) : ISegmentSource
    {
        private readonly ReadOnlySequence<byte> _bytes = sink.Bytes();

        public long Length => _bytes.Length;

        public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<SegmentLease>(Lease(range));
        }

        public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<SegmentRange> wanted = ranges.Span;
            Span<SegmentLease> filled = leases.Span;
            for (int i = 0; i < wanted.Length; i++)
            {
                filled[i] = Lease(wanted[i]);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private SegmentLease Lease(SegmentRange range)
        {
            ObjectDisposedException.ThrowIf(sink._released, sink);
            long offset = Math.Min(range.Offset, _bytes.Length);
            long length = Math.Min(range.Length, _bytes.Length - offset);
            return new SegmentLease(_bytes.Slice(offset, length));
        }
    }
}
