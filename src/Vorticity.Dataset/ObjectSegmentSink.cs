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
/// <see cref="CommitAsync"/> creates the object afterwards, and <see cref="DiscardAsync"/> is what a
/// caller that changed its mind calls instead.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are held in chunks from a pool, never in one buffer: a buffer that doubles as the object
/// grows copies it at every step, and hands the pool arrays of every size up to the object's, which a
/// pool then keeps. The first chunk is <see cref="FirstChunkBytes"/>, each next one twice the one
/// before up to <see cref="ChunkBytes"/>, so a small object, as a commit of a few rows writes, holds
/// a few kilobytes of the pool rather than a mebibyte, and a chunk is the most a pool is handed back
/// at once, whatever the object's size.
/// </para>
/// <para>
/// An object that grows past <see cref="DefaultStreamFromBytes"/> starts its put while it is written:
/// the store reads the chunks as they fill and sends its parts, so that the commit waits for the last
/// part and the completion only. The buffer stays, since the object's statistics are read from it
/// before the commit, and the put is ended at the commit, or aborted by a discard. A data object is
/// put on the condition of its key's absence, unless the store holds such a put whole, in memory: its
/// key ends with a fresh uid, which nobody else takes.
/// </para>
/// </remarks>
internal sealed class ObjectSegmentSink : ISegmentSink, IAsyncDisposable
{
    /// <summary>The most bytes this sink buffers before refusing, 1 GiB.</summary>
    public const long DefaultMaxBytes = 1L << 30;

    /// <summary>The bytes of the largest chunks of the buffer, those past the first few.</summary>
    internal const int ChunkBytes = 1 << 20;

    /// <summary>The bytes of the first chunk of the buffer.</summary>
    internal const int FirstChunkBytes = 64 << 10;

    /// <summary>
    /// The bytes past which the object's put starts while it is written: 32 MiB, the part threshold of
    /// the stores of Vorticity.ObjectStorage, below which an object goes in one request at the commit.
    /// </summary>
    internal const long DefaultStreamFromBytes = 32L << 20;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly long _maxBytes;
    private readonly long _streamFrom;
    private readonly MemoryPool<byte> _pool;
    private readonly List<IMemoryOwner<byte>> _chunks = [];

    // The reader the put streams the chunks from, and the put, once the object passed _streamFrom.
    private Feed? _feed;
    private Task<(PutOutcome Outcome, long Stored)>? _upload;

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
    /// <see cref="DefaultMaxBytes"/>, and a null <paramref name="pool"/> the shared aligned one. The put
    /// starts once <paramref name="streamFrom"/> bytes are written, never when it is
    /// <see cref="long.MaxValue"/>.
    /// </summary>
    public ObjectSegmentSink(IObjectStore store, string key, long maxBytes = 0, MemoryPool<byte>? pool = null, long streamFrom = DefaultStreamFromBytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectKey.Check(key);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamFrom);
        _store = store;
        _key = key;
        _maxBytes = maxBytes == 0 ? DefaultMaxBytes : maxBytes;
        _streamFrom = streamFrom;
        _pool = pool ?? AlignedMemoryPool.Shared;
    }

    /// <summary>Whether the object's put started while it was written.</summary>
    internal bool IsStreaming => _feed is not null;

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
    /// is not to be used once <see cref="CommitAsync"/> or <see cref="DiscardAsync"/> released them.
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

        if (_feed is { } feed)
        {
            feed.Publish();
        }
        else if (_written >= _streamFrom)
        {
            // Off the writer's thread: the store's first reads copy what is written already.
            _feed = new Feed(this);
            _feed.Publish();
            Feed reader = _feed;
            _upload = Task.Run(() => UploadAsync(reader));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The put of a streamed object, of unknown length until the commit ends its reader: on the
    /// condition of its key's absence, unless the store holds such a put whole and the key is a data
    /// object's, which a fresh uid names.
    /// </summary>
    private async Task<(PutOutcome Outcome, long Stored)> UploadAsync(Feed reader)
    {
        PutCondition condition = ConditionOf(large: true);
        if (_store is SealedObjectStore sealedStore)
        {
            return await sealedStore.PutSealedAsync(_key, reader, -1, condition, CancellationToken.None).ConfigureAwait(false);
        }

        return (await _store.PutAsync(_key, reader, -1, condition, CancellationToken.None).ConfigureAwait(false), -1);
    }

    /// <summary>
    /// The condition of the put: the key's absence, unless the object is <paramref name="large"/>, past
    /// the bytes from which it streams, on a store that holds a conditional put whole, and its key is a
    /// data object's, which a fresh uid names and nobody else takes.
    /// </summary>
    private PutCondition ConditionOf(bool large)
    {
        if (!large || !SealedObjectStore.TryUid(_key, out _))
        {
            return PutCondition.IfAbsent;
        }

        IObjectStore under = _store is SealedObjectStore sealedStore ? sealedStore.Inner : _store;
        return ObjectStoreSource.Root(under) is IWholeConditionalPuts { HoldsConditionalPutsWhole: true }
            ? PutCondition.None
            : PutCondition.IfAbsent;
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
        if (_upload is { } upload)
        {
            // The parts are on their way: the put ends with the last of them and the completion.
            _feed!.End();
            long stored;
#pragma warning disable VSTHRD003 // The put this sink started when the object passed its threshold.
            (outcome, stored) = await upload.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            _upload = null;
            StoredLength = stored >= 0 ? stored : _written;
        }
        else if (_store is SealedObjectStore sealedStore)
        {
            (outcome, StoredLength) = await sealedStore
                .PutSealedAsync(_key, PipeReader.Create(Bytes()), _written, ConditionOf(_written >= _streamFrom), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await _store
                .PutAsync(_key, PipeReader.Create(Bytes()), _written, ConditionOf(_written >= _streamFrom), cancellationToken).ConfigureAwait(false);
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
    /// Throws the buffered bytes away without creating the object: a put started while the object was
    /// written is aborted first, and awaited, since it reads the chunks until it stops.
    /// </summary>
    /// <returns>A task that completes when the bytes are released.</returns>
    public async ValueTask DiscardAsync()
    {
        _closed = true;
        if (_upload is { } upload)
        {
            _upload = null;
            _feed!.Fail(new OperationCanceledException($"'{_key}' was discarded before its commit."));
            try
            {
#pragma warning disable VSTHRD003 // The put this sink started, which reads its chunks until it stops.
                await upload.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (Exception)
            {
                // The abort the discard asked for, or the failure it cut short: nothing was created.
            }
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

    /// <summary>
    /// The reader a streamed put takes the chunks from as they fill: the bytes written so far, in place,
    /// and a wait for more until the commit ends it or a discard fails it. The writer publishes what it
    /// wrote after each write; the store reads on its own task.
    /// </summary>
    private sealed class Feed(ObjectSegmentSink sink) : PipeReader
    {
        private readonly object _gate = new object();

        // A segment per chunk of the sink, each over the chunk's whole size, added as the chunks are.
        private readonly List<Chunk> _segments = [];
        private long _published;
        private long _consumed;
        private long _examined;
        private int _first;
        private bool _ended;
        private bool _cancelled;
        private Exception? _failure;
        private TaskCompletionSource? _waiting;

        /// <summary>Publishes what the writer wrote since: the chunks it added, and the bytes past them. Called on the writer's thread.</summary>
        internal void Publish()
        {
            TaskCompletionSource? waiting;
            lock (_gate)
            {
                for (int chunk = _segments.Count; chunk < sink._chunks.Count; chunk++)
                {
                    ReadOnlyMemory<byte> memory = sink._chunks[chunk].Memory[..SizeOf(chunk)];
                    _segments.Add(chunk == 0 ? new Chunk(memory, 0) : _segments[^1].Then(memory));
                }

                _published = sink._written;
                waiting = Take();
            }

            waiting?.TrySetResult();
        }

        /// <summary>The object is whole: the reader's next read ends it.</summary>
        internal void End() => Signal(() => _ended = true);

        /// <summary>The object is discarded: the reader's next read throws, and the put aborts.</summary>
        internal void Fail(Exception failure) => Signal(() => _failure = failure);

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                TaskCompletionSource waiting;
                lock (_gate)
                {
                    if (TryResult(out ReadResult result))
                    {
                        return result;
                    }

                    waiting = _waiting ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                // Completed by the writer's next publication, the commit or a discard.
#pragma warning disable VSTHRD003
                await waiting.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
        }

        public override bool TryRead(out ReadResult result)
        {
            lock (_gate)
            {
                return TryResult(out result);
            }
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            lock (_gate)
            {
                _consumed = Math.Max(_consumed, Offset(consumed));
                _examined = Math.Max(_consumed, Offset(examined));

                // The first segment the next read starts in: the one that holds the byte consumed next.
                while (_first < _segments.Count - 1 && _segments[_first].RunningIndex + _segments[_first].Memory.Length <= _consumed)
                {
                    _first++;
                }
            }
        }

        public override void CancelPendingRead() => Signal(() => _cancelled = true);

        public override void Complete(Exception? exception = null)
        {
            // The put stopped reading, whatever its outcome, which the commit or the discard learns
            // from the put itself: the chunks stay the sink's.
        }

        /// <summary>A read's result when there is one to give: bytes not examined yet, the end, a cancellation or a failure.</summary>
        private bool TryResult(out ReadResult result)
        {
            if (_failure is { } failure)
            {
                throw failure;
            }

            if (_cancelled)
            {
                _cancelled = false;
                result = new ReadResult(Bytes(), isCanceled: true, isCompleted: false);
                return true;
            }

            if (_published > _examined || _ended)
            {
                result = new ReadResult(Bytes(), isCanceled: false, isCompleted: _ended);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>The bytes published and not consumed, in place.</summary>
        private ReadOnlySequence<byte> Bytes()
        {
            if (_published == _consumed)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            Chunk start = _segments[_first];
            Chunk end = _segments[^1];
            if (end.RunningIndex >= _published && _segments.Count > 1)
            {
                // The last chunk is empty yet: the bytes end where the one before it does.
                end = _segments[^2];
            }

            return new ReadOnlySequence<byte>(start, (int)(_consumed - start.RunningIndex), end, (int)(_published - end.RunningIndex));
        }

        /// <summary>The offset in the object a position of a sequence this reader handed out stands for.</summary>
        private long Offset(SequencePosition position) =>
            position.GetObject() is Chunk chunk ? chunk.RunningIndex + position.GetInteger() : _consumed;

        /// <summary>Changes the state under the lock, then wakes the read waiting, if any.</summary>
        private void Signal(Action change)
        {
            TaskCompletionSource? waiting;
            lock (_gate)
            {
                change();
                waiting = Take();
            }

            waiting?.TrySetResult();
        }

        private TaskCompletionSource? Take()
        {
            TaskCompletionSource? waiting = _waiting;
            _waiting = null;
            return waiting;
        }
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
