using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Sealing;

/// <summary>
/// The plaintext of a sealed object, read through the source that holds its sealed bytes: every
/// range is widened to whole frames, read in one ciphertext run, and decrypted into one 64-byte
/// aligned block, of which each segment is a slice.
/// </summary>
/// <remarks>
/// <para>
/// A batch's segments are coalesced over the plaintext, as any source coalesces them, and each run
/// becomes one ciphertext range. The ranges of a batch go to the inner source together, through its
/// own <see cref="ISegmentReader.ReadManyAsync"/>, so a source that issues them at once still does,
/// and the batch costs the dependent round trips it costs in plaintext.
/// </para>
/// <para>
/// No decoder ever sees a byte that was not authenticated: a frame whose tag fails wipes the block
/// it was decrypted into and fails the read with a <see cref="VortexEncryptionException"/>.
/// </para>
/// <para>
/// The open reads the trailer and the last frames in one read, and keeps their plaintext for the
/// Vortex open that follows, which then reads its tail without another request. Thread-safe: the
/// lanes of a scan read through one reader, each with a cipher instance of its own.
/// </para>
/// </remarks>
internal sealed class SealedSegmentReader : ISegmentReader
{
    /// <summary>A run at or below this size is always published by slicing, as a plain source does.</summary>
    private const int AlwaysSliceRunBytes = 64 * 1024;

    private readonly ISegmentReader _inner;
    private readonly bool _ownsInner;
    private readonly SealedLayout _layout;
    private readonly EpochCipher[] _ciphers;
    private readonly SegmentReadOptions _options;
    private readonly long _plainLength;
    private readonly int _frameSize;
    private readonly long _tailStart;
    private readonly int _tailReadSize;
    private readonly SegmentRequestSet?[] _sets = new SegmentRequestSet?[4];
    private SegmentOwner? _tail;
    private int _disposed;

    private SealedSegmentReader(
        ISegmentReader inner, bool ownsInner, SealedLayout layout, EpochCipher[] ciphers, SegmentOwner? tail, long tailStart, SegmentReadOptions options)
    {
        _inner = inner;
        _ownsInner = ownsInner;
        _layout = layout;
        _ciphers = ciphers;
        _plainLength = layout.PlainLength;
        _frameSize = layout.FrameSize;
        _tail = tail;
        _tailStart = tailStart;
        _options = options;
        long held = tail is null ? 0 : _plainLength - tailStart;
        _tailReadSize = (int)Math.Clamp(held - VortexLimits.MaxAlignment, 8 * 1024, VortexFileFormat.InitialReadSize);
    }

    /// <summary>What the object's trailer says: its descriptor and its epochs.</summary>
    internal SealedLayout Layout => _layout;

    /// <summary>The source of the sealed bytes.</summary>
    internal ISegmentReader Inner => _inner;

    /// <summary>
    /// Opens the sealed object <paramref name="inner"/> reads: one read of its trailer and its last
    /// frames, the data key from <paramref name="unwrap"/>, the commitment of every epoch checked, and
    /// the last frames decrypted for the open that follows.
    /// </summary>
    /// <param name="inner">The sealed bytes.</param>
    /// <param name="ownsInner">Whether disposing the reader disposes <paramref name="inner"/>.</param>
    /// <param name="unwrap">The data key the descriptor names, handed over: the reader derives its keys from it and disposes it.</param>
    /// <param name="cancellationToken">Cancels the reads and the unwrap.</param>
    /// <param name="options">The coalescing budgets, the defaults when null.</param>
    /// <returns>The reader; on failure nothing is left open but <paramref name="inner"/>, which the caller still owns.</returns>
    /// <exception cref="VortexFormatException">The bytes are not a sealed object.</exception>
    /// <exception cref="VortexEncryptionException">A commitment fails, or the key cannot be unwrapped.</exception>
    internal static async ValueTask<SealedSegmentReader> OpenAsync(
        ISegmentReader inner, bool ownsInner, Func<SealDescriptor, CancellationToken, ValueTask<DataKey>> unwrap,
        CancellationToken cancellationToken, SegmentReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(unwrap);
        long length = await inner.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length < SealedFormat.HeaderPrefixBytes + SealedFormat.TrailerSuffixBytes)
        {
            throw new VortexFormatException($"Malformed sealed object: {length} bytes are too few for one.");
        }

        int window = (int)Math.Min(length, SealedFormat.OpenReadBytes);
        long endStart = length - window;
        SegmentOwner end = await inner.ReadRangeAsync(endStart, window, VortexLimits.MaxAlignment, cancellationToken).ConfigureAwait(false);
        EpochCipher[]? ciphers = null;
        SegmentOwner? tail = null;
        try
        {
            if (end.Length != window)
            {
                SegmentIo.ThrowTruncatedRead(endStart, window, end.Length);
            }

            int trailerLength = SealedLayout.TrailerLength(end.Buffer.Span, length);
            SealedLayout layout;
            if (trailerLength <= window)
            {
                layout = SealedLayout.Read(end.Buffer.Span[^trailerLength..], length);
            }
            else
            {
                // A trailer longer than the read, which only a long key or many epochs make: read it
                // whole, and leave the last frames to the reads that want them.
                SegmentOwner trailer = await inner.ReadRangeAsync(length - trailerLength, trailerLength, 1, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (trailer.Length != trailerLength)
                    {
                        SegmentIo.ThrowTruncatedRead(length - trailerLength, trailerLength, trailer.Length);
                    }

                    layout = SealedLayout.Read(trailer.Buffer.Span, length);
                }
                finally
                {
                    trailer.Release();
                }
            }

            DataKey dataKey = await unwrap(layout.Descriptor, cancellationToken).ConfigureAwait(false);
            try
            {
                ciphers = Ciphers(layout, dataKey);
            }
            finally
            {
                dataKey.Dispose();
            }

            long tailStart = TailOf(layout, endStart);
            if (tailStart < layout.PlainLength)
            {
                tail = Decrypt(layout, ciphers, end.Buffer.Span, endStart, tailStart, layout.PlainLength);
            }
            else if (layout.Epochs[^1] is { PlainLength: 0 } empty && empty.FramesStart >= endStart)
            {
                // The last frame is checked at the open, as the decrypted tail checks it otherwise: an
                // empty epoch's is its tag alone, which no read would ever reach.
                OpenEmpty(ciphers[^1], empty, end.Buffer.Span[(int)(empty.FramesStart - endStart)..]);
            }

            SealedSegmentReader reader = new SealedSegmentReader(inner, ownsInner, layout, ciphers, tail, tailStart, options ?? SegmentReadOptions.Default);
            ciphers = null;
            tail = null;
            return reader;
        }
        finally
        {
            end.Release();
            tail?.Release();
            if (ciphers is not null)
            {
                foreach (EpochCipher cipher in ciphers)
                {
                    cipher?.Dispose();
                }
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(_plainLength);
    }

    /// <inheritdoc/>
    public int TailReadSize => _tailReadSize;

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        SegmentIo.ValidateSpec(in spec, out long offset, out int length);
        SegmentIo.CheckInFile(offset, length, _plainLength);
        return length == 0
            ? new ValueTask<SegmentOwner>(new EmptySegmentOwner())
            : ReadPlainAsync(offset, length, 1 << spec.AlignmentExponent, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        int actual = SegmentIo.ClampRange(offset, length, alignment, _plainLength);
        if (actual == 0)
        {
            return new ValueTask<SegmentOwner>(new EmptySegmentOwner());
        }

        // The open's tail, decrypted with the trailer: served once, then let go, since the file keeps
        // what it read and serves the segments lying there itself.
        if (offset >= _tailStart && Interlocked.Exchange(ref _tail, null) is { } tail)
        {
            try
            {
                if (Slice(tail, _tailStart, offset, actual, alignment, out VortexBuffer view))
                {
                    return new ValueTask<SegmentOwner>(new SliceSegmentOwner(tail, view));
                }
            }
            finally
            {
                tail.Release();
            }
        }

        return ReadPlainAsync(offset, actual, alignment, cancellationToken);
    }

    /// <inheritdoc/>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (requests.IsPopulated)
        {
            return;
        }

        int registered = requests.Count;
        if (registered == 0)
        {
            requests.Complete();
            return;
        }

        int[] order = ArrayPool<int>.Shared.Rent(registered);
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(registered);
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(registered);
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(registered);
        int[] groups = ArrayPool<int>.Shared.Rent(registered + 1);
        SegmentRequestSet ciphertext = RentSet();
        try
        {
            int pending = Collect(requests, order, keys);
            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            int runCount = Plan(requests, order, keys, sorted, runs, pending, _plainLength, _options);
            int groupCount = Group(runs, runCount, groups, ciphertext);
            await _inner.ReadManyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            for (int g = 0; g < groupCount; g++)
            {
                Publish(requests, ciphertext, g, runs, groups[g], groups[g + 1], sorted, order);
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
            ReturnSet(ciphertext);
            ArrayPool<int>.Shared.Return(groups);
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>Wipes the keys and lets the tail go; disposes the inner source when the reader owns it.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Interlocked.Exchange(ref _tail, null)?.Release();
        foreach (EpochCipher cipher in _ciphers)
        {
            cipher.Dispose();
        }

        for (int i = 0; i < _sets.Length; i++)
        {
            Interlocked.Exchange(ref _sets[i], null)?.Release();
        }

        return _ownsInner ? _inner.DisposeAsync() : ValueTask.CompletedTask;
    }

    // ---- the open ---------------------------------------------------------------------------

    /// <summary>Every epoch's key, its commitment checked in constant time against the one recorded.</summary>
    private static EpochCipher[] Ciphers(SealedLayout layout, DataKey dataKey)
    {
        SealedEpoch[] epochs = layout.Epochs;
        EpochCipher[] ciphers = new EpochCipher[epochs.Length];
        Span<byte> key = stackalloc byte[SealedFormat.KeyBytes];
        Span<byte> commitment = stackalloc byte[SealedFormat.CommitmentBytes];
        try
        {
            for (int e = 0; e < epochs.Length; e++)
            {
                SealedFormat.Derive(dataKey.Key, epochs[e].Salt.Span, layout.Descriptor.Hash, (uint)e, epochs[e].PlainStart, key, commitment);
                if (!CryptographicOperations.FixedTimeEquals(commitment, epochs[e].Commitment.Span))
                {
                    throw VortexEncryptionException.Unauthenticated(
                        $"The key of epoch {e} of the sealed object is not the one its commitment names: the object was altered, or its data key is another's.");
                }

                ciphers[e] = new EpochCipher(key);
            }

            return ciphers;
        }
        catch
        {
            foreach (EpochCipher cipher in ciphers)
            {
                cipher?.Dispose();
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Checks the one frame of an empty epoch, its tag over no plaintext, final.</summary>
    private static void OpenEmpty(EpochCipher cipher, SealedEpoch epoch, ReadOnlySpan<byte> frame)
    {
        AesGcm aes = cipher.Rent();
        try
        {
            EpochCipher.Open(aes, 0, final: true, ReadOnlySpan<byte>.Empty, frame[..SealedFormat.TagBytes], Span<byte>.Empty, epoch.FramesStart);
        }
        finally
        {
            cipher.Return(aes);
        }
    }

    /// <summary>The plaintext offset of the last epoch's first frame that the open's read holds whole, or the plaintext's end when none.</summary>
    private static long TailOf(SealedLayout layout, long endStart)
    {
        SealedEpoch last = layout.Epochs[^1];
        int frameSize = layout.FrameSize;
        long stride = frameSize + SealedFormat.TagBytes;
        long skipped = endStart <= last.FramesStart ? 0 : ((endStart - last.FramesStart) + stride - 1) / stride;
        long start = last.PlainStart + (skipped * frameSize);
        return Math.Min(start, last.PlainEnd);
    }

    // ---- frames -----------------------------------------------------------------------------

    /// <summary>The plaintext offset of the frame that holds <paramref name="plainOffset"/>.</summary>
    private long FrameStart(long plainOffset)
    {
        SealedEpoch epoch = _layout.Epochs[_layout.EpochOf(plainOffset)];
        return epoch.PlainStart + ((plainOffset - epoch.PlainStart) / _frameSize * _frameSize);
    }

    /// <summary>The plaintext offset past the frame that holds <paramref name="plainOffset"/>.</summary>
    private long FrameEnd(long plainOffset)
    {
        SealedEpoch epoch = _layout.Epochs[_layout.EpochOf(plainOffset)];
        long start = epoch.PlainStart + ((plainOffset - epoch.PlainStart) / _frameSize * _frameSize);
        return Math.Min(start + _frameSize, epoch.PlainEnd);
    }

    /// <summary>The object offset of the frame that starts at plaintext <paramref name="frameStart"/>.</summary>
    private static long CipherOffset(SealedLayout layout, long frameStart)
    {
        SealedEpoch epoch = layout.Epochs[layout.EpochOf(frameStart)];
        long index = (frameStart - epoch.PlainStart) / layout.FrameSize;
        return epoch.FramesStart + (index * (layout.FrameSize + SealedFormat.TagBytes));
    }

    /// <summary>The object range of the frames covering plaintext <c>[frameStart, frameEnd)</c>, both frame bounds.</summary>
    private (long Start, long End) CipherRange(long frameStart, long frameEnd)
    {
        long lastStart = FrameStart(frameEnd - 1);
        return (CipherOffset(_layout, frameStart), CipherOffset(_layout, lastStart) + (frameEnd - lastStart) + SealedFormat.TagBytes);
    }

    /// <summary>
    /// Decrypts the frames covering plaintext <c>[frameStart, frameEnd)</c> from <paramref name="ciphertext"/>,
    /// whose first byte is object offset <paramref name="cipherBase"/>, into a new 64-byte aligned
    /// block that keeps each offset's alignment: the block starts <c>frameStart % 64</c> bytes before
    /// the plaintext.
    /// </summary>
    /// <exception cref="VortexEncryptionException">A frame does not authenticate; the block is wiped and released.</exception>
    private static NativeSegmentOwner Decrypt(
        SealedLayout layout, EpochCipher[] ciphers, ReadOnlySpan<byte> ciphertext, long cipherBase, long frameStart, long frameEnd)
    {
        int pad = (int)(frameStart & (VortexLimits.MaxAlignment - 1));
        int plainBytes = checked((int)(frameEnd - frameStart));
        NativeSegmentOwner block = plainBytes + pad <= SegmentReadOptions.DefaultMaxPooledBytes
            ? AlignedBufferPool.Shared.Rent(plainBytes + pad, VortexLimits.MaxAlignment)
            : NativeSegmentOwner.Allocate(plainBytes + pad, VortexLimits.MaxAlignment);
        Span<byte> plain = block.WritableSpan.Slice(pad, plainBytes);
        int frameSize = layout.FrameSize;
        int current = -1;
        AesGcm? aes = null;
        try
        {
            long at = frameStart;
            while (at < frameEnd)
            {
                int e = layout.EpochOf(at);
                SealedEpoch epoch = layout.Epochs[e];
                if (e != current)
                {
                    if (aes is not null)
                    {
                        ciphers[current].Return(aes);
                    }

                    aes = ciphers[e].Rent();
                    current = e;
                }

                long index = (at - epoch.PlainStart) / frameSize;
                int length = (int)Math.Min(frameSize, epoch.PlainEnd - at);
                bool final = at + length == epoch.PlainEnd;
                long cipherOffset = epoch.FramesStart + (index * (frameSize + SealedFormat.TagBytes));
                int relative = checked((int)(cipherOffset - cipherBase));
                EpochCipher.Open(
                    aes!, index, final, ciphertext.Slice(relative, length), ciphertext.Slice(relative + length, SealedFormat.TagBytes),
                    plain.Slice((int)(at - frameStart), length), cipherOffset);
                at += length;
            }

            return block;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(block.WritableSpan);
            block.Release();
            throw;
        }
        finally
        {
            if (aes is not null)
            {
                ciphers[current].Return(aes);
            }
        }
    }

    /// <summary>Reads plaintext <c>[offset, offset + length)</c> on its own: its frames in one ciphertext range, decrypted into a block.</summary>
    private async ValueTask<SegmentOwner> ReadPlainAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        long frameStart = FrameStart(offset);
        long frameEnd = FrameEnd(offset + length - 1);
        (long cipherStart, long cipherEnd) = CipherRange(frameStart, frameEnd);
        int cipherLength = checked((int)(cipherEnd - cipherStart));
        SegmentOwner cipher = await _inner.ReadRangeAsync(cipherStart, cipherLength, 1, cancellationToken).ConfigureAwait(false);
        NativeSegmentOwner block;
        try
        {
            if (cipher.Length != cipherLength)
            {
                SegmentIo.ThrowTruncatedRead(cipherStart, cipherLength, cipher.Length);
            }

            block = Decrypt(_layout, _ciphers, cipher.Buffer.Span, cipherStart, frameStart, frameEnd);
        }
        finally
        {
            cipher.Release();
        }

        try
        {
            if (Slice(block, frameStart, offset, length, alignment, out VortexBuffer view))
            {
                return new SliceSegmentOwner(block, view);
            }

            // An offset that is not a multiple of the alignment asked for: copied to a block's base.
            NativeSegmentOwner copy = AlignedBufferPool.Shared.Rent(length, VortexLimits.MaxAlignment);
            view.Span.CopyTo(copy.WritableSpan);
            return copy;
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// The view of plaintext <c>[offset, offset + length)</c> inside <paramref name="block"/>, whose
    /// plaintext starts at <paramref name="blockStart"/>; false when that range is not in the block, or
    /// its address would not have <paramref name="alignment"/>, in which case the view is still set when
    /// the range is inside.
    /// </summary>
    private static unsafe bool Slice(SegmentOwner block, long blockStart, long offset, int length, int alignment, out VortexBuffer view)
    {
        int pad = (int)(blockStart & (VortexLimits.MaxAlignment - 1));
        ReadOnlySpan<byte> bytes = block.Buffer.Span;
        long relative = offset - blockStart + pad;
        if (offset < blockStart || relative + length > bytes.Length)
        {
            view = VortexBuffer.Empty;
            return false;
        }

        ReadOnlySpan<byte> slice = bytes.Slice((int)relative, length);
        nuint address = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(slice));
        bool aligned = (address & (nuint)(alignment - 1)) == 0;
        view = VortexBuffer.FromPinned(slice, aligned ? System.Numerics.BitOperations.Log2((uint)alignment) : 0);
        return aligned;
    }

    // ---- batches ----------------------------------------------------------------------------

    /// <summary>The slots that still need bytes, keyed by offset for the sort.</summary>
    private static int Collect(SegmentRequestSet requests, int[] order, ulong[] keys)
    {
        int pending = 0;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (!requests.IsFilled(slot))
            {
                order[pending] = slot;
                keys[pending] = requests.GetSpec(slot).Offset;
                pending++;
            }
        }

        return pending;
    }

    /// <summary>Sorts the pending specs by offset, checks them against the plaintext, and plans the coalesced runs over it.</summary>
    private static int Plan(
        SegmentRequestSet requests, int[] order, ulong[] keys, SegmentSpec[] sorted, CoalescedRun[] runs,
        int pending, long plainLength, SegmentReadOptions options)
    {
        keys.AsSpan(0, pending).Sort(order.AsSpan(0, pending));
        for (int i = 0; i < pending; i++)
        {
            sorted[i] = requests.GetSpec(order[i]);
            SegmentIo.ValidateSpec(in sorted[i], out long offset, out int length);
            SegmentIo.CheckInFile(offset, length, plainLength);
        }

        return SegmentCoalescer.Plan(sorted.AsSpan(0, pending), runs.AsSpan(0, pending), options);
    }

    /// <summary>
    /// Groups the runs whose frames touch, so that no frame is read or decrypted twice, and registers
    /// each group's ciphertext range in <paramref name="ciphertext"/>, in order: group <c>g</c> is the
    /// runs <c>[groups[g], groups[g + 1])</c> and slot <c>g</c>.
    /// </summary>
    private int Group(CoalescedRun[] runs, int runCount, int[] groups, SegmentRequestSet ciphertext)
    {
        int groupCount = 0;
        long groupStart = 0;
        long groupEnd = 0;
        for (int r = 0; r < runCount; r++)
        {
            long start = FrameStart(runs[r].Start);
            long end = FrameEnd(runs[r].End - 1);
            if (groupCount > 0 && start < groupEnd)
            {
                groupEnd = Math.Max(groupEnd, end);
                continue;
            }

            if (groupCount > 0)
            {
                Register(ciphertext, groupStart, groupEnd);
            }

            groups[groupCount++] = r;
            groupStart = start;
            groupEnd = end;
        }

        Register(ciphertext, groupStart, groupEnd);
        groups[groupCount] = runCount;
        return groupCount;
    }

    private void Register(SegmentRequestSet ciphertext, long frameStart, long frameEnd)
    {
        (long start, long end) = CipherRange(frameStart, frameEnd);
        ciphertext.Add(new SegmentSpec((ulong)start, checked((uint)(end - start)), 0, 0, 0));
    }

    /// <summary>Decrypts group <paramref name="group"/> and publishes the segments of its runs.</summary>
    private void Publish(
        SegmentRequestSet requests, SegmentRequestSet ciphertext, int group, CoalescedRun[] runs, int firstRun, int endRun,
        SegmentSpec[] sorted, int[] order)
    {
        long frameStart = FrameStart(runs[firstRun].Start);
        long frameEnd = FrameEnd(runs[endRun - 1].End - 1);
        long cipherStart = CipherOffset(_layout, frameStart);
        NativeSegmentOwner block = Decrypt(_layout, _ciphers, ciphertext.GetBuffer(group).Span, cipherStart, frameStart, frameEnd);
        try
        {
            ReadOnlySpan<byte> bytes = block.Buffer.Span;
            int pad = (int)(frameStart & (VortexLimits.MaxAlignment - 1));
            for (int r = firstRun; r < endRun; r++)
            {
                CoalescedRun run = runs[r];
                bool slice = run.Length <= AlwaysSliceRunBytes || Useful(in run, sorted) * 2 >= run.Length;
                for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
                {
                    SegmentSpec spec = sorted[k];
                    ReadOnlySpan<byte> segment = bytes.Slice((int)((long)spec.Offset - frameStart + pad), (int)spec.Length);
                    if (slice)
                    {
                        requests.SetSharedResult(order[k], block, VortexBuffer.FromPinned(segment, spec.AlignmentExponent));
                        continue;
                    }

                    // A sparse run would pin its whole block for a few segments: each is copied out.
                    NativeSegmentOwner owner = spec.Length <= _options.MaxPooledBytes
                        ? AlignedBufferPool.Shared.Rent((int)spec.Length, VortexLimits.MaxAlignment)
                        : NativeSegmentOwner.Allocate((int)spec.Length, VortexLimits.MaxAlignment);
                    try
                    {
                        segment.CopyTo(owner.WritableSpan);
                        requests.SetSharedResult(order[k], owner, VortexBuffer.FromPinned(owner.Buffer.Span, spec.AlignmentExponent));
                    }
                    finally
                    {
                        owner.Release();
                    }
                }
            }
        }
        finally
        {
            block.Release();
        }
    }

    private static long Useful(in CoalescedRun run, SegmentSpec[] sorted)
    {
        long useful = 0;
        for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
        {
            useful += sorted[k].Length;
        }

        return useful;
    }

    private SegmentRequestSet RentSet()
    {
        for (int i = 0; i < _sets.Length; i++)
        {
            if (Interlocked.Exchange(ref _sets[i], null) is { } kept)
            {
                return kept;
            }
        }

        return new SegmentRequestSet();
    }

    private void ReturnSet(SegmentRequestSet set)
    {
        set.Release();
        if (Volatile.Read(ref _disposed) == 0)
        {
            for (int i = 0; i < _sets.Length; i++)
            {
                if (Interlocked.CompareExchange(ref _sets[i], set, null) is null)
                {
                    return;
                }
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
