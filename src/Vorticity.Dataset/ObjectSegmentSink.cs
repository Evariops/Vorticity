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
/// An <see cref="ISegmentSink"/> that becomes one object of an <see cref="IObjectStore"/>: held whole
/// up to <see cref="DefaultStreamedFromBytes"/>, and past it sent to the store as it is written. It
/// outlives its writer on purpose: disposing only closes it to further writes and keeps the bytes,
/// since a writer disposes its sink before the file is known to be complete. <see cref="CommitAsync"/>
/// creates the object afterwards, and <see cref="Discard"/> is what a caller that changed its mind
/// calls instead.
/// </summary>
/// <remarks>
/// <para>
/// The bytes held are in chunks from a pool, never in one buffer: a buffer that doubles as the object
/// grows copies it at every step, and hands the pool arrays of every size up to the object's, which a
/// pool then keeps. The first chunk is <see cref="FirstChunkBytes"/>, each next one twice the one
/// before up to <see cref="ChunkBytes"/>, so a small object, as a commit of a few rows writes, holds
/// a few kilobytes of the pool rather than a mebibyte, and a chunk is the most a pool is handed back
/// at once, whatever the object's size.
/// </para>
/// <para>
/// An object that passes the threshold goes through a pipe to the store's put, which sends it in parts
/// as they fill, its parts in flight in the store's own buffers: the writer waits on the pipe when the
/// link is slower, and the sink holds a few mebibytes whatever the object's size. Its bytes are then
/// not there to read: the dataset reads its statistics from the store once it is put.
/// </para>
/// </remarks>
internal sealed class ObjectSegmentSink : ISegmentSink, IAsyncDisposable
{
    /// <summary>The most bytes an object takes unless the dataset says otherwise: none, the store refuses what its parts cannot reach.</summary>
    public const long DefaultMaxBytes = long.MaxValue;

    /// <summary>
    /// The bytes past which the object is sent as it is written rather than held: the part threshold
    /// every store uses by default, under which an object goes in one request anyway and the dataset
    /// reads its statistics from memory, without a request.
    /// </summary>
    internal const long DefaultStreamedFromBytes = 32L << 20;

    /// <summary>The bytes a pipe to the store holds before the writer waits.</summary>
    private const int PipeBytes = 4 * ChunkBytes;

    /// <summary>The bytes of the largest chunks of the buffer, those past the first few.</summary>
    internal const int ChunkBytes = 1 << 20;

    /// <summary>The bytes of the first chunk of the buffer.</summary>
    internal const int FirstChunkBytes = 64 << 10;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly long _maxBytes;
    private readonly long _streamedFrom;

    /// <summary>The most bytes a streamed object takes, which the store sizes its parts from; -1 when unknown.</summary>
    private readonly long _bound;
    private readonly MemoryPool<byte> _pool;
    private readonly List<IMemoryOwner<byte>> _chunks = [];

    /// <summary>
    /// The pipe a streamed object goes through, and the store's put that reads it; null while the object
    /// is held. A pipe completed with an exception is what stops the put of an object given up.
    /// </summary>
    private Pipe? _pipe;
    private Task<(PutOutcome Outcome, long StoredLength)>? _put;

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
    /// <see cref="DefaultMaxBytes"/>, <paramref name="streamedFrom"/> of 0
    /// <see cref="DefaultStreamedFromBytes"/>, and a null <paramref name="pool"/> the shared aligned one.
    /// A <paramref name="bound"/> is the most bytes the object takes if it streams, which the store sizes
    /// its parts from (<see cref="ObjectBound"/>); -1 for none, the length then unknown to the store
    /// until the end.
    /// </summary>
    public ObjectSegmentSink(IObjectStore store, string key, long maxBytes = 0, MemoryPool<byte>? pool = null, long streamedFrom = 0, long bound = -1)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(streamedFrom);
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, -1);
        _store = store;
        _key = key;
        _maxBytes = maxBytes == 0 ? DefaultMaxBytes : maxBytes;
        _streamedFrom = streamedFrom == 0 ? DefaultStreamedFromBytes : streamedFrom;
        _bound = bound < 0 ? -1 : Math.Max(bound, _streamedFrom);
        _pool = pool ?? AlignedMemoryPool.Shared;
    }

    /// <summary>Whether the object passed the threshold and goes to the store as it is written: its bytes are not held.</summary>
    public bool IsStreamed => _pipe is not null;

    /// <summary>The bytes the sink holds now: the object's while it is held, and none once it streams.</summary>
    internal long HeldBytes => _pipe is null && !_released ? _written : 0;

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
    /// <exception cref="InvalidOperationException">The object is streamed: its bytes are in the store.</exception>
    public ISegmentSource Content()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        return _pipe is null
            ? new ChunkSource(this)
            : throw new InvalidOperationException($"'{_key}' went to the store as it was written, and its bytes are not held.");
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
                $"'{_key}' would take {wanted} bytes, over the {_maxBytes} an object may take " +
                "(DatasetOptions.MaxObjectBytes).");
        }

        // An object held whole goes with its length; one streamed was announced to the store as at most its
        // bound, which the store refuses content past, so the rewrite hears of it here, at the write.
        if (_bound >= 0 && wanted > _bound)
        {
            throw new ObjectPastBoundException(
                $"'{_key}' would take {wanted} bytes, past the {_bound} its rewrite announced to the store.");
        }

        if (_pipe is not null || wanted > _streamedFrom)
        {
            return StreamAsync(data, cancellationToken);
        }

        Hold(data.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>Holds <paramref name="data"/> in the chunks.</summary>
    private void Hold(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> rest = data;
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
    }

    /// <summary>
    /// Sends <paramref name="data"/> through the pipe to the store, starting the put with what was held
    /// when the object first passes the threshold: the chunks go in, and back to the pool.
    /// </summary>
    private async ValueTask StreamAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            _pipe = new Pipe(new PipeOptions(pool: _pool, pauseWriterThreshold: PipeBytes, resumeWriterThreshold: PipeBytes / 2, useSynchronizationContext: false));
            _put = PutStreamedAsync(_pipe.Reader);
            for (int chunk = 0; chunk < _chunks.Count; chunk++)
            {
                await SendAsync(Held(chunk), cancellationToken).ConfigureAwait(false);
            }

            foreach (IMemoryOwner<byte> chunk in _chunks)
            {
                chunk.Dispose();
            }

            _chunks.Clear();
            _used = 0;
        }

        _hash.Append(data.Span);
        _written += data.Length;
        await SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes to the pipe and waits while it is full; a put that stopped reading says why.</summary>
    private async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        FlushResult flushed = await _pipe!.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        if (flushed.IsCompleted)
        {
            // The put stopped reading, which only its failure does: its failure is the write's.
#pragma warning disable VSTHRD003
            await _put!.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            throw new ObjectStoreException($"The put of '{_key}' stopped reading its content before its end.");
        }
    }

    /// <summary>
    /// The store's put of the streamed object, run beside the writer: its outcome and the bytes the store
    /// holds, -1 when they are those written. Its length is unknown, and its bound, when the sink has one,
    /// is what the store sizes its parts from.
    /// </summary>
    private async Task<(PutOutcome Outcome, long StoredLength)> PutStreamedAsync(PipeReader content)
    {
        await Task.Yield();
        PutCondition condition = SealedObjectStore.TryUid(_key, out _) ? PutCondition.None : PutCondition.IfAbsent;
        if (_store is SealedObjectStore sealedStore)
        {
            return await sealedStore.PutSealedAsync(_key, content, -1, condition, CancellationToken.None, _bound).ConfigureAwait(false);
        }

        return (await _store.PutAsync(_key, content, -1, _bound, condition, CancellationToken.None).ConfigureAwait(false), -1);
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
    /// <remarks>
    /// The bytes go from the buffer where they lie: a store that sends them takes them in place, whole or
    /// in parts, and sends them again from there on a retry. A data object's key ends with a fresh uid,
    /// which nobody else takes, so its put carries no condition, whole or in parts, on every service; any
    /// other object is created only if its key is absent.
    /// </remarks>
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
        if (_pipe is not null)
        {
            // The last bytes are in the pipe: its end completes the put.
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003
            (outcome, long stored) = await _put!.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            StoredLength = stored >= 0 ? stored : _written;
            _committed = true;
            Release();
            return outcome;
        }

        PutCondition condition = SealedObjectStore.TryUid(_key, out _) ? PutCondition.None : PutCondition.IfAbsent;
        if (_store is SealedObjectStore sealedStore)
        {
            (outcome, StoredLength) = await sealedStore.PutSealedAsync(_key, Bytes(), condition, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await _store.PutAsync(_key, Bytes(), condition, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Throws the bytes away without creating the object: those held go back to the pool, and the put
    /// of a streamed object is cancelled, its upload given up by the store.
    /// </summary>
    public void Discard()
    {
        _closed = true;
        if (_pipe is not null && !_committed)
        {
            _pipe.Writer.Complete(new OperationCanceledException($"The object '{_key}' was given up."));

            // Its failure is the abandonment's own: seen, so that nothing reports it unobserved.
            _ = _put!.ContinueWith(static put => _ = put.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

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
