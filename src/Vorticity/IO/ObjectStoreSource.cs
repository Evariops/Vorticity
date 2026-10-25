using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>
/// An <see cref="ISegmentReader"/> over one object of an <see cref="IObjectStore"/>. A batch's
/// segments are coalesced into runs, the runs read together, each straight into a 64-byte aligned
/// block, and each segment is a slice of its run's block: the bytes land once, where the scan decodes
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The object's length comes from the caller when it knows it, as a dataset's entry records it, and
/// costs no request. Otherwise the first call reads the object's tail on a store that reads suffixes,
/// whose answer says the length, and keeps the tail for the open that follows, which then reads it
/// without another request; another store answers a head.
/// </para>
/// <para>
/// The first answer fixes the object's token, and every later read asks the store for that token, so
/// that an object replaced under a reader fails its next read rather than mixing two objects.
/// </para>
/// </remarks>
internal sealed class ObjectStoreSource : ISegmentReader
{
    /// <summary>The tail an open reads first, and the slack that lets the open start it on a 64-byte boundary.</summary>
    internal const int TailBytes = 65_536;

    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly SegmentReadOptions _options;
    private readonly bool _ownsStore;
    private long _length;
    private string? _token;
    private SegmentOwner? _tail;
    private long _tailStart;
    private int _tailBytes;
    private int _lengthRefused;
    private int _disposed;

    /// <summary>A source over <paramref name="key"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="length">The object's length when the caller knows it, which spares its request; -1 otherwise.</param>
    /// <param name="options">The coalescing budgets, an object store's when null (<see cref="SegmentReadOptions.Remote"/>).</param>
    /// <param name="ownsStore">Whether disposing the source disposes the store.</param>
    internal ObjectStoreSource(IObjectStore store, string key, long length = -1, SegmentReadOptions? options = null, bool ownsStore = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, -1);
        _store = store;
        _key = key;
        _length = length;
        _givenLength = length;
        _options = options ?? SegmentReadOptions.Remote;
        _ownsStore = ownsStore;
    }

    /// <summary>The object's key.</summary>
    internal string Key => _key;

    /// <summary>
    /// Whether <paramref name="obj"/> reads the same object: the same store, key and length, the length
    /// known to both. A session's cache keys segments by their source, so two readers of one object, as a
    /// dataset opens one for each read that needs no footer, share what either read. A data object's key
    /// names one version of its bytes, and an object rewritten under the same key with another length is
    /// another source.
    /// </summary>
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj)
        || (obj is ObjectStoreSource other && _givenLength >= 0 && other._givenLength == _givenLength && ReferenceEquals(other._store, _store)
            && string.Equals(other._key, _key, StringComparison.Ordinal));

    /// <inheritdoc/>
    public override int GetHashCode() => _givenLength >= 0
        ? HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_store), StringComparer.Ordinal.GetHashCode(_key), _givenLength)
        : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    // The length the caller gave, -1 for none: what equality reads, since the length learned later would
    // change the hash of a key already kept.
    private readonly long _givenLength;

    /// <summary>The store the object lies in.</summary>
    internal IObjectStore Store => _store;

    /// <summary>
    /// The object's token as of the first answer that gave it, or null before any. A caller holding a
    /// token from elsewhere compares it with this one: a difference means the bytes under the key changed.
    /// </summary>
    internal string? Token => Volatile.Read(ref _token);

    /// <summary>
    /// Whether an answer gave another length than the one the caller recorded, which a read refused as
    /// a format error: the object under the key is not the one the caller expects. A sealed dataset
    /// reports it as the failed binding it is.
    /// </summary>
    internal bool LengthRefused => Volatile.Read(ref _lengthRefused) != 0;

    /// <inheritdoc/>
    public int TailReadSize => TailBytes;

    /// <inheritdoc/>
    public async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        long known = Interlocked.Read(ref _length);
        if (known >= 0)
        {
            return known;
        }

        if ((_store.Capabilities & ObjectStoreCapabilities.ReadsSuffix) == 0)
        {
            ObjectHead head = await _store.HeadAsync(_key, cancellationToken).ConfigureAwait(false)
                ?? throw ObjectNotFoundException.For(_key);
            Note(head.Token);
            NoteLength(head.Length);
            return head.Length;
        }

        // The tail and the length in one answer. The block is read from its start, then the bytes are
        // moved to keep each file offset's alignment, so the open's 64-byte aligned read is a slice.
        int wanted = TailBytes + VortexLimits.MaxAlignment;
        NativeSegmentOwner block = AlignedBufferPool.Shared.Rent(wanted + VortexLimits.MaxAlignment, VortexLimits.MaxAlignment);
        try
        {
            ObjectRead read = await TailAsync(block.WritableMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            Note(read.Token);
            long start = read.ObjectLength - read.BytesRead;
            int pad = (int)(start & (VortexLimits.MaxAlignment - 1));
            if (pad != 0)
            {
                block.WritableSpan[..read.BytesRead].CopyTo(block.WritableSpan[pad..]);
            }

            NoteLength(read.ObjectLength);
            Volatile.Write(ref _tailStart, start);
            Volatile.Write(ref _tailBytes, pad + read.BytesRead);
            if (Interlocked.CompareExchange(ref _tail, block, null) is null)
            {
                block = null!;
            }

            return read.ObjectLength;
        }
        finally
        {
            block?.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        return length == 0
            ? new ValueTask<SegmentOwner>(new EmptySegmentOwner())
            : ReadBlockAsync(offset, length, spec.AlignmentExponent, exact: true, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (alignment < 1 || alignment > VortexLimits.MaxAlignment || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, $"A power of two in [1, {VortexLimits.MaxAlignment}] was expected.");
        }

        // The tail the length came with, served once to the open that asks for it. The tail is published
        // after its start and size, so reading it first makes them current.
        if (Volatile.Read(ref _tail) is not null)
        {
            long tailStart = Volatile.Read(ref _tailStart);
            long known = Interlocked.Read(ref _length);
            int available = (int)Math.Min(length, known - offset);
            int pad = (int)(tailStart & (VortexLimits.MaxAlignment - 1));
            long relative = offset - tailStart + pad;
            if (offset >= tailStart && available >= 0 && relative + available <= Volatile.Read(ref _tailBytes)
                && Interlocked.Exchange(ref _tail, null) is { } tail)
            {
                try
                {
                    VortexBuffer view = VortexBuffer.FromPinned(tail.Buffer.Span.Slice((int)relative, available), AlignmentExponentOf(offset, alignment));
                    return new ValueTask<SegmentOwner>(new SliceSegmentOwner(tail, view));
                }
                finally
                {
                    tail.Release();
                }
            }
        }

        return length == 0
            ? new ValueTask<SegmentOwner>(new EmptySegmentOwner())
            : ReadBlockAsync(offset, length, AlignmentExponentOf(offset, alignment), exact: false, cancellationToken);
    }

    /// <inheritdoc/>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.IsPopulated)
        {
            return;
        }

        int registered = requests.Count;
        int[] order = ArrayPool<int>.Shared.Rent(Math.Max(registered, 1));
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(Math.Max(registered, 1));
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(Math.Max(registered, 1));
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(Math.Max(registered, 1));
        ValueTask<ObjectRead>[] reads = ArrayPool<ValueTask<ObjectRead>>.Shared.Rent(Math.Max(registered, 1));
        NativeSegmentOwner?[] blocks = ArrayPool<NativeSegmentOwner?>.Shared.Rent(Math.Max(registered, 1));
        int started = 0;
        try
        {
            int pending = 0;
            for (int slot = 0; slot < registered; slot++)
            {
                if (!requests.IsFilled(slot))
                {
                    order[pending] = slot;
                    keys[pending] = requests.GetSpec(slot).Offset;
                    pending++;
                }
            }

            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            keys.AsSpan(0, pending).Sort(order.AsSpan(0, pending));
            for (int i = 0; i < pending; i++)
            {
                sorted[i] = requests.GetSpec(order[i]);
                SegmentIo.ValidateSpec(in sorted[i], out _, out _);
            }

            int runCount = SegmentCoalescer.Plan(sorted.AsSpan(0, pending), runs.AsSpan(0, pending), _options);

            // Started together, so the runs cost one step of the critical path however many they are,
            // each read straight into the block its segments will be slices of.
            string? token = Volatile.Read(ref _token);
            for (; started < runCount; started++)
            {
                CoalescedRun run = runs[started];
                int pad = (int)(run.Start & (VortexLimits.MaxAlignment - 1));
                int length = checked((int)run.Length);
                NativeSegmentOwner block = Rent(pad + length);
                blocks[started] = block;

                // Kept to be awaited below, each exactly once: the runs are in flight together, the
                // session's bound counting the batch, the store's connections the requests.
#pragma warning disable CA2012
                reads[started] = RangeAsync(run.Start, block.WritableMemory(pad, length), token, cancellationToken);
#pragma warning restore CA2012
            }

            // Every read is awaited before anything throws, so that none is left running into a block
            // given back.
            Exception? failure = null;
            for (int r = 0; r < runCount; r++)
            {
                try
                {
                    ObjectRead read = await reads[r].ConfigureAwait(false);
                    Note(read.Token);
                    NoteLength(read.ObjectLength);
                    if (read.BytesRead != runs[r].Length)
                    {
                        throw new VortexFormatException($"Run {runs[r].Start}+{runs[r].Length} of '{_key}' ended after {read.BytesRead} bytes.");
                    }
                }
                catch (Exception caught)
                {
                    failure ??= caught;
                }
            }

            started = 0;
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            for (int r = 0; r < runCount; r++)
            {
                CoalescedRun run = runs[r];
                NativeSegmentOwner block = blocks[r]!;
                int pad = (int)(run.Start & (VortexLimits.MaxAlignment - 1));
                ReadOnlySpan<byte> bytes = block.Buffer.Span;
                for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
                {
                    SegmentSpec spec = sorted[k];
                    ReadOnlySpan<byte> slice = bytes.Slice((int)((long)spec.Offset - run.Start) + pad, (int)spec.Length);
                    requests.SetSharedResult(order[k], block, VortexBuffer.FromPinned(slice, spec.AlignmentExponent));
                }
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
        finally
        {
            // A read still running after a failure that came before its await writes into its block:
            // it is awaited before the block goes back.
            for (int r = 0; r < started; r++)
            {
                try
                {
                    await reads[r].ConfigureAwait(false);
                }
                catch
                {
                    // The failure that is on its way out says more.
                }
            }

            for (int r = 0; r < blocks.Length && blocks[r] is not null; r++)
            {
                blocks[r]!.Release();
                blocks[r] = null;
            }

            ArrayPool<NativeSegmentOwner?>.Shared.Return(blocks, clearArray: true);
            ArrayPool<ValueTask<ObjectRead>>.Shared.Return(reads, clearArray: true);
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Interlocked.Exchange(ref _tail, null)?.Release();
        return _ownsStore ? _store.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads <c>[offset, offset + length)</c> into a block of its own, which keeps the offset's
    /// alignment; with <paramref name="exact"/>, an answer short of the length is a format error,
    /// otherwise the block is the bytes the object has.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<SegmentOwner> ReadBlockAsync(long offset, int length, int alignmentExponent, bool exact, CancellationToken cancellationToken)
    {
        int pad = (int)(offset & (VortexLimits.MaxAlignment - 1));
        NativeSegmentOwner block = Rent(pad + length);
        try
        {
            ObjectRead read;
            try
            {
                read = await RangeAsync(offset, block.WritableMemory(pad, length), Volatile.Read(ref _token), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException cause) when (cause.ParamName == "offset")
            {
                // A range that starts wholly past the end is a format error, not an argument error.
                throw new VortexFormatException($"Range {offset}+{length} starts past the end of '{_key}'.", cause);
            }

            Note(read.Token);
            NoteLength(read.ObjectLength);
            if (exact && read.BytesRead != length)
            {
                throw new VortexFormatException($"Segment {offset}+{length} of '{_key}' ended after {read.BytesRead} bytes.");
            }

            VortexBuffer view = VortexBuffer.FromPinned(block.Buffer.Span.Slice(pad, read.BytesRead), alignmentExponent);
            return new SliceSegmentOwner(block, view);
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>One request for a range, bound to the object's token once it is known.</summary>
    private ValueTask<ObjectRead> RangeAsync(long offset, Memory<byte> destination, string? token, CancellationToken cancellationToken) =>
        _store.ReadAsync(_key, offset, destination, token, cancellationToken);

    /// <summary>The request for the tail, which says the object's length with it.</summary>
    private ValueTask<ObjectRead> TailAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        _store.ReadTailAsync(_key, destination, cancellationToken);

    /// <summary>A 64-byte aligned block of <paramref name="length"/> bytes: pooled up to the pool's largest size class.</summary>
    private NativeSegmentOwner Rent(int length) =>
        length <= _options.MaxPooledBytes
            ? AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment)
            : NativeSegmentOwner.Allocate(length, VortexLimits.MaxAlignment);

    /// <summary>Records the object's token, and refuses an answer about another object.</summary>
    private void Note(string token)
    {
        string? known = Interlocked.CompareExchange(ref _token, token, null);
        if (known is not null && !ReferenceEquals(known, token) && !string.Equals(known, token, StringComparison.Ordinal))
        {
            throw ObjectStoreExtensions.Changed(_key, known, token);
        }
    }

    /// <summary>
    /// Records the object's length, and refuses an answer that gives another: the object under the key
    /// is not the one whose length the caller recorded.
    /// </summary>
    private void NoteLength(long objectLength)
    {
        long known = Interlocked.CompareExchange(ref _length, objectLength, -1);
        if (known >= 0 && known != objectLength)
        {
            Volatile.Write(ref _lengthRefused, 1);
            throw new VortexFormatException($"'{_key}' is {objectLength} bytes where {known} were recorded: it is not the object its reader expects.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>The alignment a slice at <paramref name="offset"/> keeps, which a block laid at the offset's own alignment gives it: what was asked, or less at an offset off that boundary.</summary>
    private static int AlignmentExponentOf(long offset, int alignment) =>
        (offset & (alignment - 1)) == 0 ? System.Numerics.BitOperations.Log2((uint)alignment) : 0;
}
