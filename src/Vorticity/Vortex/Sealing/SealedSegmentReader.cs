using System;
using System.Collections.Generic;
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
/// <para>
/// A reader given a cache of sealed bytes (<see cref="ISealedFrameCache"/>) opens from the trailer it
/// keeps, with no read, and reads from the disk every group of frames whose segments' frames it keeps
/// all: a frame of a gap a run reads across is not asked. It decrypts them as it does the store's, and
/// checks each declared region's plaintext against its checksum: what fails is dropped and read from the
/// store. The frames of each declared region the store sends go to the cache once they authenticate,
/// written at once. When the cache keeps data, so do the frames of every other segment, and those of the
/// tail an open decrypted, written behind the read; a range read alone, as an open reads its tail, comes
/// from the disk too when the cache keeps its frames.
/// </para>
/// <para>
/// Readers of the object that miss the same frames at once, through the same cache, wait for one read of them, as those
/// of a plain object's regions do: a group of a batch whose every frame another reader is fetching waits for that read, then
/// reads them from the disk, and from the store what the other read did not leave there. A batch reads its own frames and
/// ends its reads before it waits for another's, so two batches whose groups cross never wait for each other.
/// </para>
/// </remarks>
internal sealed class SealedSegmentReader : ISegmentReader
{
    /// <summary>A run at or below this size is always published by slicing, as a plain source does.</summary>
    private const int AlwaysSliceRunBytes = 64 * 1024;

    /// <summary>A group of a batch the disk served, or that its frames were published for: no slot among the store's ranges.</summary>
    private const int Served = -1;

    /// <summary>A group of a batch whose every frame another reader is fetching: it waits for that read.</summary>
    private const int Waiting = -2;

    private readonly ISegmentReader _inner;
    private readonly bool _ownsInner;
    private readonly SealedLayout _layout;
    private readonly EpochCipher[] _ciphers;
    private readonly SegmentReadOptions _options;
    private readonly ISealedFrameCache? _cache;
    private readonly long _plainLength;
    private readonly int _frameSize;
    private readonly long _tailStart;
    private readonly int _tailReadSize;
    private readonly SegmentRequestSet?[] _sets = new SegmentRequestSet?[4];
    private SegmentOwner? _tail;
    private int _disposed;

    private SealedSegmentReader(
        ISegmentReader inner, bool ownsInner, SealedLayout layout, EpochCipher[] ciphers, SegmentOwner? tail, long tailStart, SegmentReadOptions options,
        ISealedFrameCache? cache)
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
        _cache = cache;
        long held = tail is null ? 0 : _plainLength - tailStart;
        _tailReadSize = (int)Math.Clamp(held - VortexLimits.MaxAlignment, 8 * 1024, VortexFileFormat.InitialReadSize);
    }

    /// <summary>What the object's trailer says: its descriptor and its epochs.</summary>
    internal SealedLayout Layout => _layout;

    /// <summary>The trailer's bytes as the store holds them, read by an open asked to keep them; null otherwise.</summary>
    internal byte[]? Trailer { get; private set; }

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
    /// <param name="cache">What the reader keeps of the object on a local disk, its sealed bytes; null for none.</param>
    /// <param name="trailer">
    /// The object's trailer, which an open before read, to open from with no read, as from a cache's: the reader then reads
    /// no tail ahead, what a read of regions a table of contents declares wants; empty for none.
    /// </param>
    /// <param name="keepTrailer">Whether an open that reads the trailer keeps a copy of its bytes (<see cref="Trailer"/>).</param>
    /// <returns>The reader; on failure nothing is left open but <paramref name="inner"/>, which the caller still owns.</returns>
    /// <exception cref="VortexFormatException">The bytes are not a sealed object.</exception>
    /// <exception cref="VortexEncryptionException">A commitment fails, or the key cannot be unwrapped.</exception>
    internal static async ValueTask<SealedSegmentReader> OpenAsync(
        ISegmentReader inner, bool ownsInner, Func<SealDescriptor, CancellationToken, ValueTask<DataKey>> unwrap,
        CancellationToken cancellationToken, SegmentReadOptions? options = null, ISealedFrameCache? cache = null,
        ReadOnlyMemory<byte> trailer = default, bool keepTrailer = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(unwrap);
        long length = await inner.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length < SealedFormat.HeaderPrefixBytes + SealedFormat.TrailerSuffixBytes)
        {
            throw new VortexFormatException($"Malformed sealed object: {length} bytes are too few for one.");
        }

        if (cache is not null && await OpenKeptAsync(inner, ownsInner, unwrap, length, options, cache, cancellationToken).ConfigureAwait(false) is { } kept)
        {
            return kept;
        }

        // A trailer that does not open the object is read again, as a cache's is.
        if (!trailer.IsEmpty && LayoutOf(trailer.Span, length) is { } known
            && await FromLayoutAsync(inner, ownsInner, unwrap, known, options, cache, cancellationToken).ConfigureAwait(false) is { } given)
        {
            return given;
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
            byte[]? longTrailer = null;
            if (trailerLength <= window)
            {
                layout = SealedLayout.Read(end.Buffer.Span[^trailerLength..], length);
            }
            else
            {
                // A trailer longer than the read, which only a long key or many epochs make: read it
                // whole, and leave the last frames to the reads that want them.
                SegmentOwner whole = await inner.ReadRangeAsync(length - trailerLength, trailerLength, 1, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (whole.Length != trailerLength)
                    {
                        SegmentIo.ThrowTruncatedRead(length - trailerLength, trailerLength, whole.Length);
                    }

                    layout = SealedLayout.Read(whole.Buffer.Span, length);
                    longTrailer = cache is null && !keepTrailer ? null : whole.Buffer.Span.ToArray();
                }
                finally
                {
                    whole.Release();
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
                tail = Decrypt(layout, ciphers, end.Buffer.Span, endStart, tailStart, layout.PlainLength, MemoryAccount.Kept);
            }
            else if (layout.Epochs[^1] is { PlainLength: 0 } empty && empty.FramesStart >= endStart)
            {
                // The last frame is checked at the open, as the decrypted tail checks it otherwise: an
                // empty epoch's is its tag alone, which no read would ever reach.
                OpenEmpty(ciphers[^1], empty, end.Buffer.Span[(int)(empty.FramesStart - endStart)..]);
            }

            // Kept once the object is open: its key unwrapped, every epoch's commitment checked, its last frames
            // authenticated.
            if (longTrailer is not null)
            {
                cache?.AdmitTrailer(longTrailer);
            }
            else
            {
                cache?.AdmitTrailer(end.Buffer.Span[^trailerLength..]);
            }

            SealedSegmentReader reader = new SealedSegmentReader(inner, ownsInner, layout, ciphers, tail, tailStart, options ?? SegmentReadOptions.Default, cache)
            {
                Trailer = !keepTrailer ? null : longTrailer ?? end.Buffer.Span[^trailerLength..].ToArray(),
            };

            // The last frames, authenticated as they were decrypted: the next open, from the trailer kept, reads its tail
            // from them.
            if (cache is { KeepsData: true } && tailStart < layout.PlainLength)
            {
                reader.KeepRange(cache, end, end.Buffer, endStart, tailStart, layout.PlainLength);
            }

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

    /// <summary>
    /// The reader opened from the trailer <paramref name="cache"/> keeps, with no read: its layout, the data key
    /// it names, and every epoch's commitment checked as an open from the store checks them. Null when the cache
    /// keeps no trailer, or one that does not open the object, which it then drops.
    /// </summary>
    private static async ValueTask<SealedSegmentReader?> OpenKeptAsync(
        ISegmentReader inner, bool ownsInner, Func<SealDescriptor, CancellationToken, ValueTask<DataKey>> unwrap, long length,
        SegmentReadOptions? options, ISealedFrameCache cache, CancellationToken cancellationToken)
    {
        if (!cache.TryReadTrailer(out SegmentOwner trailer))
        {
            return null;
        }

        SealedLayout? layout;
        try
        {
            layout = LayoutOf(trailer.Buffer.Span, length);
        }
        finally
        {
            trailer.Release();
        }

        SealedSegmentReader? reader = layout is null ? null : await FromLayoutAsync(inner, ownsInner, unwrap, layout, options, cache, cancellationToken).ConfigureAwait(false);
        if (reader is null)
        {
            cache.DropTrailer();
        }

        return reader;
    }

    /// <summary>What the trailer <paramref name="bytes"/> of an object of <paramref name="length"/> sealed bytes says; null for one that does not read.</summary>
    private static SealedLayout? LayoutOf(ReadOnlySpan<byte> bytes, long length)
    {
        try
        {
            return SealedLayout.TrailerLength(bytes, length) == bytes.Length ? SealedLayout.Read(bytes, length) : null;
        }
        catch (VortexException)
        {
            return null;
        }
    }

    /// <summary>
    /// The reader of an object whose trailer said <paramref name="layout"/>, with no read: the data key it names, and every
    /// epoch's commitment checked as an open from the store checks them; null when they fail, the trailer then not the
    /// object's.
    /// </summary>
    private static async ValueTask<SealedSegmentReader?> FromLayoutAsync(
        ISegmentReader inner, bool ownsInner, Func<SealDescriptor, CancellationToken, ValueTask<DataKey>> unwrap, SealedLayout layout,
        SegmentReadOptions? options, ISealedFrameCache? cache, CancellationToken cancellationToken)
    {
        EpochCipher[] ciphers;
        try
        {
            DataKey dataKey = await unwrap(layout.Descriptor, cancellationToken).ConfigureAwait(false);
            try
            {
                ciphers = Ciphers(layout, dataKey);
            }
            finally
            {
                dataKey.Dispose();
            }
        }
        catch (VortexEncryptionException)
        {
            return null;
        }

        return new SealedSegmentReader(inner, ownsInner, layout, ciphers, null, layout.PlainLength, options ?? SegmentReadOptions.Default, cache);
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
        if (length == 0)
        {
            return new ValueTask<SegmentOwner>(new EmptySegmentOwner());
        }

        return _cache is null ? ReadPlainAsync(offset, length, 1 << spec.AlignmentExponent, cancellationToken) : ReadOneAsync(spec, cancellationToken);
    }

    /// <summary>One segment through the frames the cache keeps: a batch of one.</summary>
    private async ValueTask<SegmentOwner> ReadOneAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        using SegmentRequestSet one = new SegmentRequestSet(1);
        one.Add(spec);
        await ReadManyAsync(one, cancellationToken).ConfigureAwait(false);
        return new SliceSegmentOwner(one.GetOwner(0), one.GetBuffer(0));
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

        ReadScratch scratch = ReadScratch.Rent(registered);
        Block<int> order = scratch.Order;
        Block<SegmentSpec> sorted = scratch.Sorted;
        Block<CoalescedRun> runs = scratch.Runs;

        // A group's first run, then the slot its frames take among the store's ranges; with a cache, the stretches of frames
        // a group's segments lie in, a start and an end each, at most one a segment.
        Block<int> groups = Blocks.Rent<int>(registered + 1);
        Block<int> stored = Blocks.Rent<int>(registered);
        Block<long> stretches = _cache is null ? default : Blocks.Rent<long>(2 * registered);
        SegmentRequestSet ciphertext = RentSet();
        ciphertext.Account = requests.Account;
        SegmentRequestSet? late = null;
        Joining? joining = null;
        try
        {
            int pending = CoalescedReads.CollectPending(requests, order, scratch.Keys);
            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            // The plaintext's length bounds the segments as a file's would.
            int runCount = CoalescedReads.PlanRuns(requests, order, scratch.Keys, sorted, runs, pending, _plainLength, _options);
            int groupCount = Group(runs, runCount, groups);

            // The groups whose every frame the cache keeps come from the disk, those whose every frame another reader is
            // fetching wait for it, and the rest come from the store in one batch: stored[g] is group g's slot among the
            // store's ranges, or Served, or Waiting.
            bool waits = false;
            for (int g = 0; g < groupCount; g++)
            {
                if (_cache is not null && ServeKept(requests, runs, groups[g], groups[g + 1], sorted, order, stretches.AsSpan()))
                {
                    stored[g] = Served;
                }
                else if (_cache is not null && Joins(runs, groups[g], groups[g + 1], sorted, stretches.AsSpan(), ref joining))
                {
                    stored[g] = Waiting;
                    waits = true;
                }
                else
                {
                    stored[g] = Register(ciphertext, FrameStart(runs[groups[g]].Start), FrameEnd(runs[groups[g + 1] - 1].End - 1));
                }
            }

            try
            {
                if (ciphertext.Count > 0)
                {
                    await _inner.ReadManyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                }

                PublishStored(requests, ciphertext, stored.AsSpan(), groupCount, runs, groups, sorted, order, stretches.AsSpan());
            }
            finally
            {
                // The frames this read fetched are kept: the readers that wait for them read them from the disk. Ended before
                // this read waits for another's, as a plain object's reader releases its own, so that two readers whose groups
                // cross never wait for each other.
                joining?.End(_cache!);
            }

            if (waits)
            {
                await joining!.WaitAsync(cancellationToken).ConfigureAwait(false);

                // What the other read left on the disk is read from it, and what it did not, a frame its cache refused or a
                // read that failed, from the store.
                late = RentSet();
                late.Account = requests.Account;
                for (int g = 0; g < groupCount; g++)
                {
                    if (stored[g] == Waiting)
                    {
                        stored[g] = ServeKept(requests, runs, groups[g], groups[g + 1], sorted, order, stretches.AsSpan())
                            ? Served
                            : Register(late, FrameStart(runs[groups[g]].Start), FrameEnd(runs[groups[g + 1] - 1].End - 1));
                    }
                }

                if (late.Count > 0)
                {
                    await _inner.ReadManyAsync(late, cancellationToken).ConfigureAwait(false);
                    PublishStored(requests, late, stored.AsSpan(), groupCount, runs, groups, sorted, order, stretches.AsSpan());
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
            ReturnSet(ciphertext);
            if (late is not null)
            {
                ReturnSet(late);
            }

            Blocks.Return(stretches);
            Blocks.Return(stored);
            Blocks.Return(groups);
            scratch.Return();
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
    private static BlockOwner Decrypt(
        SealedLayout layout, EpochCipher[] ciphers, ReadOnlySpan<byte> ciphertext, long cipherBase, long frameStart, long frameEnd,
        MemoryAccount account)
    {
        BlockOwner block = RentPlain(frameStart, frameEnd, account);
        Span<byte> plain = block.WritableSpan[(int)(frameStart & (VortexLimits.MaxAlignment - 1))..];
        int current = -1;
        AesGcm? aes = null;
        try
        {
            for (long at = frameStart; at < frameEnd;)
            {
                (long cipherOffset, int length) = FrameOf(layout, at);
                OpenFrame(layout, ciphers, ref current, ref aes, at, ciphertext.Slice(checked((int)(cipherOffset - cipherBase)), length), plain[(int)(at - frameStart)..]);
                at += length - SealedFormat.TagBytes;
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

    /// <summary>
    /// A 64-byte aligned block for the plaintext <c>[frameStart, frameEnd)</c>, which keeps each offset's alignment: the
    /// plaintext starts <c>frameStart % 64</c> bytes into it.
    /// </summary>
    private static BlockOwner RentPlain(long frameStart, long frameEnd, MemoryAccount account)
    {
        int pad = (int)(frameStart & (VortexLimits.MaxAlignment - 1));
        int plainBytes = checked((int)(frameEnd - frameStart));
        return plainBytes + pad <= SegmentReadOptions.DefaultMaxPooledBytes
            ? account.Rent(plainBytes + pad, VortexLimits.MaxAlignment)
            : BlockOwner.Allocate(plainBytes + pad, VortexLimits.MaxAlignment);
    }

    /// <summary>
    /// Decrypts the frame that starts at plaintext <paramref name="at"/>, <paramref name="frame"/> its bytes and its tag as
    /// the store holds them, into the start of <paramref name="plain"/>: <paramref name="aes"/> is the instance of epoch
    /// <paramref name="current"/>'s cipher the caller holds, rented anew when the frame's epoch is another.
    /// </summary>
    /// <exception cref="VortexEncryptionException">The frame does not authenticate.</exception>
    private static void OpenFrame(
        SealedLayout layout, EpochCipher[] ciphers, ref int current, ref AesGcm? aes, long at, ReadOnlySpan<byte> frame, Span<byte> plain)
    {
        int e = layout.EpochOf(at);
        SealedEpoch epoch = layout.Epochs[e];
        if (e != current)
        {
            if (aes is not null)
            {
                ciphers[current].Return(aes);
                aes = null;
            }

            aes = ciphers[e].Rent();
            current = e;
        }

        long index = (at - epoch.PlainStart) / layout.FrameSize;
        int length = frame.Length - SealedFormat.TagBytes;
        bool final = at + length == epoch.PlainEnd;
        EpochCipher.Open(
            aes!, index, final, frame[..length], frame.Slice(length, SealedFormat.TagBytes), plain[..length],
            epoch.FramesStart + (index * (layout.FrameSize + SealedFormat.TagBytes)));
    }

    /// <summary>
    /// Reads plaintext <c>[offset, offset + length)</c> on its own: its frames from the disk when the cache keeps data and holds
    /// them all, otherwise in one ciphertext range from the store, handed then to such a cache; decrypted into a block.
    /// </summary>
    private async ValueTask<SegmentOwner> ReadPlainAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        long frameStart = FrameStart(offset);
        long frameEnd = FrameEnd(offset + length - 1);
        BlockOwner? block = _cache is { KeepsData: true } kept ? ReadKeptRange(kept, frameStart, frameEnd, MemoryAccount.Kept) : null;
        if (block is null)
        {
            (long cipherStart, long cipherEnd) = CipherRange(frameStart, frameEnd);
            int cipherLength = checked((int)(cipherEnd - cipherStart));
            SegmentOwner cipher = await _inner.ReadRangeAsync(cipherStart, cipherLength, 1, cancellationToken).ConfigureAwait(false);
            try
            {
                if (cipher.Length != cipherLength)
                {
                    SegmentIo.ThrowTruncatedRead(cipherStart, cipherLength, cipher.Length);
                }

                block = Decrypt(_layout, _ciphers, cipher.Buffer.Span, cipherStart, frameStart, frameEnd, MemoryAccount.Kept);
                if (_cache is { KeepsData: true } keeping)
                {
                    KeepRange(keeping, cipher, cipher.Buffer, cipherStart, frameStart, frameEnd);
                }
            }
            finally
            {
                cipher.Release();
            }
        }

        try
        {
            if (Slice(block, frameStart, offset, length, alignment, out VortexBuffer view))
            {
                return new SliceSegmentOwner(block, view);
            }

            // An offset that is not a multiple of the alignment asked for: copied to a block's base.
            BlockOwner copy = MemoryAccount.Kept.Rent(length, VortexLimits.MaxAlignment);
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

    /// <summary>
    /// Groups the runs whose frames touch, so that no frame is read or decrypted twice: group <c>g</c> is
    /// the runs <c>[groups[g], groups[g + 1])</c>.
    /// </summary>
    private int Group(ReadOnlySpan<CoalescedRun> runs, int runCount, Span<int> groups)
    {
        int groupCount = 0;
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

            groups[groupCount++] = r;
            groupEnd = end;
        }

        groups[groupCount] = runCount;
        return groupCount;
    }

    /// <summary>
    /// Publishes every group whose frames <paramref name="read"/> holds, <paramref name="stored"/> giving its slot there, and
    /// marks it served.
    /// </summary>
    private void PublishStored(
        SegmentRequestSet requests, SegmentRequestSet read, Span<int> stored, int groupCount, ReadOnlySpan<CoalescedRun> runs, ReadOnlySpan<int> groups,
        ReadOnlySpan<SegmentSpec> sorted, ReadOnlySpan<int> order, Span<long> stretches)
    {
        for (int g = 0; g < groupCount; g++)
        {
            if (stored[g] >= 0)
            {
                Publish(requests, read.GetOwner(stored[g]), read.GetBuffer(stored[g]), runs, groups[g], groups[g + 1], sorted, order, stretches);
                stored[g] = Served;
            }
        }
    }

    /// <summary>
    /// Whether the group of runs <c>[firstRun, endRun)</c>, which the disk did not serve, waits for other readers' reads of its
    /// frames: the cache keeps them once read, a declared region's or, when it keeps data, any, and other readers fetch every
    /// one. Otherwise this reader reads the group, its frames no one else fetches registered as its own in
    /// <paramref name="joining"/>, made at the first.
    /// </summary>
    /// <remarks>
    /// The frames are those the group's segments lie in, those a gap between them holds left out, as the disk serves them. A
    /// group that shares some frames with another reader's read is read whole: the frames another reader fetches are read
    /// twice, those of two questions asking the same segments at once never.
    /// </remarks>
    private bool Joins(
        ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun, ReadOnlySpan<SegmentSpec> sorted, Span<long> stretches, ref Joining? joining)
    {
        ISealedFrameCache cache = _cache!;
        if (!cache.KeepsData && !AllDeclared(cache, runs, firstRun, endRun, sorted))
        {
            // A frame of the group that the cache would not keep: a reader that waited for it would read it again.
            return false;
        }

        joining ??= new Joining();
        List<long> frames = joining.Frames;
        frames.Clear();
        int count = Stretches(runs, firstRun, endRun, sorted, stretches, skipDeclared: null);
        for (int s = 0; s < count; s += 2)
        {
            for (long at = stretches[s]; at < stretches[s + 1]; at = FrameEnd(at))
            {
                frames.Add(FrameAt(at).Offset);
            }
        }

        int before = joining.Registered.Count;
        List<Task> theirs = joining.Group;
        theirs.Clear();
        cache.BeginReadFrames(CollectionsMarshal.AsSpan(frames), joining.Mine, joining.Registered, theirs);
        if (joining.Registered.Count > before || theirs.Count == 0)
        {
            return false;
        }

        joining.Theirs.AddRange(theirs);
        return true;
    }

    /// <summary>Whether every segment of the runs <c>[firstRun, endRun)</c> is a region the object's table of contents declares.</summary>
    private static bool AllDeclared(ISealedFrameCache cache, ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun, ReadOnlySpan<SegmentSpec> sorted)
    {
        for (int r = firstRun; r < endRun; r++)
        {
            for (int k = runs[r].FirstIndex; k < runs[r].FirstIndex + runs[r].Count; k++)
            {
                if (sorted[k].Length > 0 && !cache.Declares(sorted[k].Offset, sorted[k].Length))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// What a batch that missed the disk holds of the cache's reads in flight: the frames it reads for others, and the reads of
    /// others it waits for. Made only by a batch whose frames the store sends, so a batch the disk serves allocates none.
    /// </summary>
    private sealed class Joining
    {
        private bool _ended;

        /// <summary>This batch's read, which the readers that wait for its frames wait on.</summary>
        internal TaskCompletionSource Mine { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The frames this batch registered as its read's, by their place in the object.</summary>
        internal List<long> Registered { get; } = [];

        /// <summary>The other readers' reads the groups that wait wait for.</summary>
        internal List<Task> Theirs { get; } = [];

        /// <summary>The frames of the group being planned, reused from group to group.</summary>
        internal List<long> Frames { get; } = [];

        /// <summary>The reads the group being planned would wait for, reused from group to group.</summary>
        internal List<Task> Group { get; } = [];

        /// <summary>Ends this batch's reads, once: the readers that wait for its frames look again.</summary>
        internal void End(ISealedFrameCache cache)
        {
            if (!_ended)
            {
                _ended = true;
                cache.EndReadFrames(CollectionsMarshal.AsSpan(Registered), Mine);
            }
        }

        /// <summary>Waits for the other readers' reads of the frames this batch's waiting groups lie in.</summary>
        internal Task WaitAsync(CancellationToken cancellationToken) => Task.WhenAll(Theirs).WaitAsync(cancellationToken);
    }

    /// <summary>Registers the ciphertext of the frames covering plaintext <c>[frameStart, frameEnd)</c>: its slot.</summary>
    private int Register(SegmentRequestSet ciphertext, long frameStart, long frameEnd)
    {
        (long start, long end) = CipherRange(frameStart, frameEnd);
        return ciphertext.Add(new SegmentSpec((ulong)start, checked((uint)(end - start)), 0, 0, 0));
    }

    /// <summary>
    /// Decrypts the group of runs <c>[firstRun, endRun)</c> from <paramref name="cipher"/>, the store's bytes of its
    /// frames, which <paramref name="owner"/> holds, and publishes the segments of its runs; the frames of each declared
    /// region go to the cache, and, when it keeps data, those of the other segments, written behind the read.
    /// </summary>
    private void Publish(
        SegmentRequestSet requests, SegmentOwner owner, VortexBuffer cipher, ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun,
        ReadOnlySpan<SegmentSpec> sorted, ReadOnlySpan<int> order, Span<long> stretches)
    {
        long frameStart = FrameStart(runs[firstRun].Start);
        long frameEnd = FrameEnd(runs[endRun - 1].End - 1);
        long cipherStart = CipherOffset(_layout, frameStart);
        BlockOwner block = Decrypt(_layout, _ciphers, cipher.Span, cipherStart, frameStart, frameEnd, requests.Account);
        try
        {
            SetResults(requests, block, frameStart, runs, firstRun, endRun, sorted, order, kept: false);
            if (_cache is not null)
            {
                Keep(_cache, cipher.Span, cipherStart, block, frameStart, runs, firstRun, endRun, sorted);
                if (_cache.KeepsData)
                {
                    // The frames the declared regions share with the data's segments are the regions', kept just before.
                    int count = Stretches(runs, firstRun, endRun, sorted, stretches, skipDeclared: _cache);
                    for (int s = 0; s < count; s += 2)
                    {
                        KeepRange(_cache, owner, cipher, cipherStart, stretches[s], stretches[s + 1]);
                    }
                }
            }
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// Publishes the group of runs <c>[firstRun, endRun)</c> from the frames the cache keeps, when it keeps every one its
    /// segments lie in, decrypted as the store's are, each declared region's plaintext checked against its checksum. A frame
    /// of a gap the group's runs read across is not asked: the cache keeps only frames a segment lay in. False when a frame is
    /// not kept; when a frame or a region fails, the frames are dropped, and the store sends them again.
    /// </summary>
    private bool ServeKept(
        SegmentRequestSet requests, ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun, ReadOnlySpan<SegmentSpec> sorted,
        ReadOnlySpan<int> order, Span<long> stretches)
    {
        ISealedFrameCache cache = _cache!;
        long frameStart = FrameStart(runs[firstRun].Start);
        long frameEnd = FrameEnd(runs[endRun - 1].End - 1);
        ReadOnlySpan<long> needed = stretches[..Stretches(runs, firstRun, endRun, sorted, stretches, skipDeclared: null)];

        // The scan's, as the plaintext read from the store is.
        if (ReadKept(cache, needed, frameStart, frameEnd, requests.Account) is not { } block)
        {
            return false;
        }

        try
        {
            if (!DeclaredHold(cache, block, frameStart, runs, firstRun, endRun, sorted))
            {
                DropKept(cache, needed);
                return false;
            }

            SetResults(requests, block, frameStart, runs, firstRun, endRun, sorted, order, kept: true);
            return true;
        }
        finally
        {
            block.Release();
        }
    }

    /// <summary>
    /// The plaintext <c>[frameStart, frameEnd)</c> from the frames the cache keeps, in a block as <see cref="Decrypt"/> makes
    /// one, those of <paramref name="stretches"/> decrypted and the rest of the block left as it came: null when the cache
    /// does not hold every one, or one does not authenticate, the stretches' frames then dropped.
    /// </summary>
    /// <param name="cache">The cache.</param>
    /// <param name="stretches">The plaintext stretches to read, a start and an end each at frame bounds, in order, within the range.</param>
    /// <param name="frameStart">The plaintext start of the block, at a frame's start.</param>
    /// <param name="frameEnd">The plaintext end of the block, at a frame's end.</param>
    /// <param name="account">What the block is rented under.</param>
    private BlockOwner? ReadKept(ISealedFrameCache cache, ReadOnlySpan<long> stretches, long frameStart, long frameEnd, MemoryAccount account)
    {
        for (int s = 0; s < stretches.Length; s += 2)
        {
            for (long at = stretches[s]; at < stretches[s + 1]; at = FrameEnd(at))
            {
                (long offset, int length) = FrameAt(at);
                if (!cache.HoldsFrame(offset, length))
                {
                    return null;
                }
            }
        }

        BlockOwner block = RentPlain(frameStart, frameEnd, account);
        Span<byte> plain = block.WritableSpan[(int)(frameStart & (VortexLimits.MaxAlignment - 1))..];
        int current = -1;
        AesGcm? aes = null;
        bool read = false;
        try
        {
            for (int s = 0; s < stretches.Length; s += 2)
            {
                for (long at = stretches[s]; at < stretches[s + 1]; at = FrameEnd(at))
                {
                    // A frame whose file is torn is dropped by the cache itself.
                    (long offset, int length) = FrameAt(at);
                    if (!cache.TryReadFrame(offset, length, out SegmentOwner frame))
                    {
                        return null;
                    }

                    try
                    {
                        OpenFrame(_layout, _ciphers, ref current, ref aes, at, frame.Buffer.Span, plain[(int)(at - frameStart)..]);
                    }
                    catch (VortexEncryptionException)
                    {
                        DropKept(cache, stretches);
                        return null;
                    }
                    finally
                    {
                        frame.Release();
                    }
                }
            }

            read = true;
            return block;
        }
        finally
        {
            if (aes is not null)
            {
                _ciphers[current].Return(aes);
            }

            // No decoder sees a byte that was not authenticated: a block that failed is wiped.
            if (!read)
            {
                CryptographicOperations.ZeroMemory(block.WritableSpan);
                block.Release();
            }
        }
    }

    /// <summary>As <see cref="ReadKept"/>, for the one stretch <c>[frameStart, frameEnd)</c>.</summary>
    private BlockOwner? ReadKeptRange(ISealedFrameCache cache, long frameStart, long frameEnd, MemoryAccount account)
    {
        Span<long> stretch = [frameStart, frameEnd];
        return ReadKept(cache, stretch, frameStart, frameEnd, account);
    }

    /// <summary>
    /// The stretches of frames the segments of the runs <c>[firstRun, endRun)</c> lie in, written into
    /// <paramref name="into"/> as a start and an end each at frame bounds, in order, those that touch joined; how many
    /// longs it wrote. A frame of a gap the runs read across lies in none. The segments <paramref name="skipDeclared"/>
    /// declares as regions are left out.
    /// </summary>
    private int Stretches(
        ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun, ReadOnlySpan<SegmentSpec> sorted, Span<long> into, ISealedFrameCache? skipDeclared)
    {
        int count = 0;
        for (int r = firstRun; r < endRun; r++)
        {
            for (int k = runs[r].FirstIndex; k < runs[r].FirstIndex + runs[r].Count; k++)
            {
                SegmentSpec spec = sorted[k];
                if (spec.Length == 0 || (skipDeclared is not null && skipDeclared.Declares(spec.Offset, spec.Length)))
                {
                    continue;
                }

                // The segments come by their offsets: a stretch starts no earlier than the last.
                long start = FrameStart((long)spec.Offset);
                long end = FrameEnd((long)spec.Offset + spec.Length - 1);
                if (count > 0 && start <= into[count - 1])
                {
                    into[count - 1] = Math.Max(into[count - 1], end);
                    continue;
                }

                into[count] = start;
                into[count + 1] = end;
                count += 2;
            }
        }

        return count;
    }

    /// <summary>Publishes the segments of the runs <c>[firstRun, endRun)</c> from <paramref name="block"/>, their plaintext from <paramref name="frameStart"/> on.</summary>
    private void SetResults(
        SegmentRequestSet requests, BlockOwner block, long frameStart, ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun,
        ReadOnlySpan<SegmentSpec> sorted, ReadOnlySpan<int> order, bool kept)
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
                if (kept)
                {
                    requests.NoteCacheHit();
                }

                ReadOnlySpan<byte> segment = bytes.Slice((int)((long)spec.Offset - frameStart + pad), (int)spec.Length);
                if (slice)
                {
                    requests.SetSharedResult(order[k], block, VortexBuffer.FromPinned(segment, spec.AlignmentExponent));
                    continue;
                }

                // A sparse run would pin its whole block for a few segments: each is copied out.
                BlockOwner owner = spec.Length <= _options.MaxPooledBytes
                    ? requests.Account.Rent((int)spec.Length, VortexLimits.MaxAlignment)
                    : BlockOwner.Allocate((int)spec.Length, VortexLimits.MaxAlignment);
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

    /// <summary>Whether every declared region among the runs <c>[firstRun, endRun)</c> holds its checksum in <paramref name="block"/>.</summary>
    private static bool DeclaredHold(
        ISealedFrameCache cache, BlockOwner block, long frameStart, ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun,
        ReadOnlySpan<SegmentSpec> sorted)
    {
        ReadOnlySpan<byte> bytes = block.Buffer.Span;
        int pad = (int)(frameStart & (VortexLimits.MaxAlignment - 1));
        for (int r = firstRun; r < endRun; r++)
        {
            for (int k = runs[r].FirstIndex; k < runs[r].FirstIndex + runs[r].Count; k++)
            {
                SegmentSpec spec = sorted[k];
                if (cache.Declares(spec.Offset, spec.Length)
                    && !cache.Holds(spec.Offset, spec.Length, bytes.Slice((int)((long)spec.Offset - frameStart + pad), (int)spec.Length)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Hands the cache the frames of each declared region among the runs <c>[firstRun, endRun)</c>, from
    /// <paramref name="cipher"/>, the store's bytes from <paramref name="cipherStart"/> on, once the frames
    /// authenticated into <paramref name="block"/> and the region's plaintext holds its checksum.
    /// </summary>
    private void Keep(
        ISealedFrameCache cache, ReadOnlySpan<byte> cipher, long cipherStart, BlockOwner block, long frameStart,
        ReadOnlySpan<CoalescedRun> runs, int firstRun, int endRun, ReadOnlySpan<SegmentSpec> sorted)
    {
        ReadOnlySpan<byte> bytes = block.Buffer.Span;
        int pad = (int)(frameStart & (VortexLimits.MaxAlignment - 1));
        for (int r = firstRun; r < endRun; r++)
        {
            for (int k = runs[r].FirstIndex; k < runs[r].FirstIndex + runs[r].Count; k++)
            {
                SegmentSpec spec = sorted[k];
                if (spec.Length == 0
                    || !cache.Declares(spec.Offset, spec.Length)
                    || !cache.Holds(spec.Offset, spec.Length, bytes.Slice((int)((long)spec.Offset - frameStart + pad), (int)spec.Length)))
                {
                    continue;
                }

                long end = (long)spec.Offset + spec.Length;
                for (long at = FrameStart((long)spec.Offset); at < end; at = FrameEnd(at))
                {
                    (long offset, int length) = FrameAt(at);
                    cache.AdmitFrame(offset, cipher.Slice((int)(offset - cipherStart), length));
                }
            }
        }
    }

    /// <summary>
    /// Hands the cache, as the data's, the frames of plaintext <c>[frameStart, frameEnd)</c>, authenticated, from
    /// <paramref name="cipher"/>, the store's bytes from <paramref name="cipherStart"/> on, which <paramref name="owner"/>
    /// holds until the cache's writer has written them; the cache keeps none it holds already.
    /// </summary>
    private void KeepRange(ISealedFrameCache cache, SegmentOwner owner, VortexBuffer cipher, long cipherStart, long frameStart, long frameEnd)
    {
        for (long at = frameStart; at < frameEnd; at = FrameEnd(at))
        {
            (long offset, int length) = FrameAt(at);
            cache.AdmitDataFrame(offset, owner, cipher.Slice(checked((int)(offset - cipherStart)), length));
        }
    }

    /// <summary>Drops every frame the cache keeps of the plaintext <paramref name="stretches"/>, a start and an end each.</summary>
    private void DropKept(ISealedFrameCache cache, ReadOnlySpan<long> stretches)
    {
        for (int s = 0; s < stretches.Length; s += 2)
        {
            for (long at = stretches[s]; at < stretches[s + 1]; at = FrameEnd(at))
            {
                (long offset, int length) = FrameAt(at);
                cache.DropFrame(offset, length);
            }
        }
    }

    /// <summary>Where the frame that starts at plaintext <paramref name="frameStart"/> lies in the object, and its bytes, its tag with them.</summary>
    private (long Offset, int Length) FrameAt(long frameStart) => FrameOf(_layout, frameStart);

    /// <summary>Where the frame that starts at plaintext <paramref name="frameStart"/> lies in an object of <paramref name="layout"/>, and its bytes, its tag with them.</summary>
    private static (long Offset, int Length) FrameOf(SealedLayout layout, long frameStart)
    {
        SealedEpoch epoch = layout.Epochs[layout.EpochOf(frameStart)];
        int frameSize = layout.FrameSize;
        long index = (frameStart - epoch.PlainStart) / frameSize;
        int length = (int)Math.Min(frameSize, epoch.PlainEnd - frameStart);
        return (epoch.FramesStart + (index * (frameSize + SealedFormat.TagBytes)), length + SealedFormat.TagBytes);
    }

    private static long Useful(in CoalescedRun run, ReadOnlySpan<SegmentSpec> sorted)
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
