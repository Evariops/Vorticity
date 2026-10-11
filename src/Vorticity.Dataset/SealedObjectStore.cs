using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Sealing;

namespace Vorticity.Dataset;

/// <summary>
/// A dataset's store in a session that holds a keyring: the dataset's own objects sealed on their
/// way in, and its commit objects opened on their way out, so that the commit protocol, the tree and
/// vacuum read plaintext as they do on a plain dataset.
/// </summary>
/// <remarks>
/// <para>
/// A dataset is encrypted when its commit objects are sealed: the first commit object read tells this
/// store which, and from then on one of the other kind is refused, since a dataset that mixed sealed
/// and plain objects would be a downgrade. A dataset created with <see cref="DatasetOptions.Encrypted"/>
/// starts sealed, under a data key drawn for it and a dataset id of 16 random bytes, which every sealed
/// object carries as its key context.
/// </para>
/// <para>
/// What it seals: the commit objects, each bound to the dataset's id and its version, and the data
/// objects, each bound to the uid its key and its entry name. Leases hold a version number and are
/// never read, so they stay plain, and so does anything else under the store. A writer seals under the
/// data key of the newest commit object it has read, so the writers of a dataset share one, and
/// <see cref="RekeyAsync"/> draws another for the next commit.
/// </para>
/// <para>
/// What it opens: the commit objects only, by their head, in the one request a plain one costs when the
/// store gives the object's length with its answer. A data object is opened by the dataset's object
/// cache through a sealed reader over the store's own bytes (<see cref="OpenDataAsync(string, UInt128, CancellationToken)"/>),
/// which serves the Vortex open from the read that brought the trailer. A head says what the store
/// holds: the sealed length.
/// </para>
/// </remarks>
internal sealed class SealedObjectStore : IObjectStore
{
    /// <summary>The frame size of what a dataset seals: the format's default.</summary>
    internal const int FrameLog2 = SealedFormat.DefaultFrameLog2;

    /// <summary>The room a first read of a commit object leaves for its header, which a usual descriptor fits.</summary>
    private const int HeaderAllowance = 4096;

    /// <summary>
    /// How far into a commit object a first read reaches to bring a range back with the header in one
    /// request; a range past it costs a read of the header first.
    /// </summary>
    private const long SpeculativeBytes = 1L << 20;

    /// <summary>The commit objects whose layout is kept: a starting point, each is a descriptor and a cipher.</summary>
    private const int MaxLayouts = 4096;

    private const int Unknown = 0;
    private const int Plain = 1;
    private const int Sealed = 2;

    private static ReadOnlySpan<byte> CommitMagic => "VXCOMMIT"u8;

    private readonly IObjectStore _inner;
    private readonly SessionKeys _keys;
    private readonly object _gate = new object();
    private readonly Dictionary<string, CommitLayout> _layouts = new Dictionary<string, CommitLayout>(StringComparer.Ordinal);
    private readonly Queue<string> _order = new Queue<string>();
    private int _kind;
    private byte[]? _datasetId;
    private KeyName? _current;
    private ulong _currentVersion;
    private KeyName? _pending;
    private int _disposed;

    internal SealedObjectStore(IObjectStore inner, SessionKeys keys)
    {
        _inner = inner;
        _keys = keys;
    }

    /// <summary>The store under this one.</summary>
    internal IObjectStore Inner => _inner;

    /// <summary>Whether the dataset is encrypted, which the first commit object read, or the creation, decides.</summary>
    internal bool Seals => Volatile.Read(ref _kind) == Sealed;

    /// <summary>Whether the dataset is known to be plain.</summary>
    internal bool KnownPlain => Volatile.Read(ref _kind) == Plain;

    /// <summary>
    /// The store a dataset of <paramref name="session"/> reads and writes through: this one over
    /// <paramref name="store"/> when the session holds a keyring, <paramref name="store"/> otherwise.
    /// </summary>
    internal static IObjectStore Over(IObjectStore store, VortexSession session) =>
        store is SealedObjectStore || session.Keys is not { } keys ? store : new SealedObjectStore(store, keys);

    /// <summary>Starts an encrypted dataset: a dataset id, and a data key drawn for it, for its first commit.</summary>
    /// <exception cref="InvalidOperationException">The store has read a commit object already.</exception>
    internal async ValueTask StartSealedAsync(CancellationToken cancellationToken)
    {
        byte[] id = new byte[SealedFormat.ObjectIdBytes];
        RandomNumberGenerator.Fill(id);
        using DataKey key = await _keys.GenerateAsync(id, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_kind != Unknown)
            {
                throw new InvalidOperationException("The store has read a commit object already; a dataset is encrypted when it is created.");
            }

            _datasetId = id;
            _current = new KeyName(key.KeyId, key.WrappedKey.ToArray());
            Volatile.Write(ref _kind, Sealed);
        }
    }

    /// <summary>Draws the data key the next commit object, and every object after it, is sealed under.</summary>
    /// <exception cref="InvalidOperationException">The dataset is not encrypted.</exception>
    internal async ValueTask RekeyAsync(CancellationToken cancellationToken)
    {
        byte[] id = DatasetId();
        using DataKey key = await _keys.GenerateAsync(id, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _pending = new KeyName(key.KeyId, key.WrappedKey.ToArray());
        }
    }

    /// <summary>Forgets a rekey whose commit did not land.</summary>
    internal void AbandonRekey()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }

    // ---- IObjectStore ----------------------------------------------------------------------

    /// <inheritdoc/>
    public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken)
    {
        if (!CommitKey.TryParse(key, out ulong version))
        {
            return _inner.GetRangeAsync(key, offset, length, cancellationToken);
        }

        if (KnownPlain)
        {
            return offset == 0 ? PlainCommitAsync(key, length, cancellationToken) : _inner.GetRangeAsync(key, offset, length, cancellationToken);
        }

        return ReadCommitAsync(key, version, offset, length, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) => _inner.HeadAsync(key, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        (PutOutcome outcome, _) = await PutAsync(key, content, length, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (string key in keys)
        {
            Forget(key);
        }

        return _inner.DeleteAsync(keys, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        _inner.ListAsync(prefix, startAfter, cancellationToken);

    /// <summary>Wipes the keys of the commit objects it opened; the store under it stays the caller's.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_gate)
            {
                foreach (CommitLayout layout in _layouts.Values)
                {
                    layout.Dispose();
                }

                _layouts.Clear();
                _order.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    // ---- puts ------------------------------------------------------------------------------

    /// <summary>
    /// Creates an object, sealed when it is one of an encrypted dataset's commit or data objects and as
    /// given otherwise, and answers what the store answered and the bytes it now holds.
    /// </summary>
    internal async ValueTask<(PutOutcome Outcome, long StoredLength)> PutAsync(
        string key, PipeReader content, long length, CancellationToken cancellationToken)
    {
        bool commit = CommitKey.TryParse(key, out ulong version);
        bool data = !commit && TryUid(key, out _);
        if (commit)
        {
            // The first commit of a dataset created plain in a session that holds a keyring.
            Interlocked.CompareExchange(ref _kind, Plain, Unknown);
        }

        if (!Seals || (!commit && !data))
        {
            return (await _inner.PutIfAbsentAsync(key, content, length, cancellationToken).ConfigureAwait(false), length);
        }

        byte[] id = DatasetId();
        KeyName name;
        lock (_gate)
        {
            name = (commit ? _pending : null) ?? _current
                ?? throw new InvalidOperationException("The dataset's data key is not known yet: its latest commit object is read before anything is put.");
        }

        byte[] objectId = new byte[SealedFormat.ObjectIdBytes];
        if (commit)
        {
            RandomNumberGenerator.Fill(objectId);
        }
        else
        {
            _ = TryUid(key, out Guid uid);
            uid.TryWriteBytes(objectId);
        }

        SealParameters parameters = new SealParameters(FrameLog2, objectId, Binding(id, commit ? version : null), id, ReadOnlyMemory<byte>.Empty);
        int descriptorLength = SealDescriptor.LengthOf(name.KeyId, id.Length, name.Wrapped.Length);
        long sealedLength = SealedFormat.SealedLength(descriptorLength, length, 1 << FrameLog2);
        DataKey dataKey = await _keys.UnwrapAsync(name.KeyId, name.Wrapped, id, cancellationToken).ConfigureAwait(false);

        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19, useSynchronizationContext: false));
        Task<Exception?> produce = ProduceAsync(pipe.Writer, content, length, parameters, dataKey, cancellationToken);
        PutOutcome outcome;
        try
        {
            outcome = await _inner.PutIfAbsentAsync(key, pipe.Reader, sealedLength, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await produce.ConfigureAwait(false);
            throw;
        }

        Exception? failure = await produce.ConfigureAwait(false);
        if (outcome == PutOutcome.Created && failure is not null)
        {
            throw failure;
        }

        if (commit && outcome == PutOutcome.Created)
        {
            lock (_gate)
            {
                if (version > _currentVersion)
                {
                    _current = name;
                    _currentVersion = version;
                }

                if (ReferenceEquals(name, _pending))
                {
                    _pending = null;
                }
            }
        }

        return (outcome, sealedLength);
    }

    /// <summary>
    /// Seals <paramref name="content"/> into <paramref name="sealedBytes"/> as the put reads it, a
    /// mebibyte flushed at a time. The failure is returned rather than thrown: a put that finds the
    /// key taken lets go of the bytes before their end, and that is no failure of the put.
    /// </summary>
    private static async Task<Exception?> ProduceAsync(
        PipeWriter sealedBytes, PipeReader content, long length, SealParameters parameters, DataKey dataKey, CancellationToken cancellationToken)
    {
        bool handedOver = false;
        SealingSegmentSink sink = new SealingSegmentSink(sealedBytes, parameters, _ =>
        {
            handedOver = true;
            return new ValueTask<DataKey>(dataKey);
        });
        Exception? failure = null;
        try
        {
            long taken = 0;
            while (true)
            {
                ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    await sink.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                taken += result.Buffer.Length;
                content.AdvanceTo(result.Buffer.End);
                if (taken > length)
                {
                    throw new ObjectStoreException($"The content was announced as {length} bytes and runs past them.");
                }

                if (sink.UnflushedBytes >= 1 << 20)
                {
                    await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }

            if (taken != length)
            {
                throw new ObjectStoreException($"The content was announced as {length} bytes and ended after {taken}.");
            }

            await sink.FinishAsync(cancellationToken).ConfigureAwait(false);
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            failure = caught;
        }
        finally
        {
            sink.Release();
            if (!handedOver)
            {
                dataKey.Dispose();
            }

            await content.CompleteAsync(failure).ConfigureAwait(false);
            await sealedBytes.CompleteAsync(failure).ConfigureAwait(false);
        }

        return failure;
    }

    // ---- commit objects --------------------------------------------------------------------

    /// <summary>The plaintext length of a commit object, which a head does not say: the stored length less the envelope, for a sealed one.</summary>
    internal async ValueTask<long> CommitLengthAsync(string key, CancellationToken cancellationToken)
    {
        if (!Seals || !CommitKey.TryParse(key, out ulong version))
        {
            ObjectHead head = await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
            return head.Length;
        }

        if (Kept(key) is { } known)
        {
            return known.PlainLength;
        }

        Resolved resolved = await ResolveAsync(key, version, 1, cancellationToken).ConfigureAwait(false);
        resolved.Head.Dispose();
        return resolved.Layout!.PlainLength;
    }

    /// <summary>The head of a commit object of a plain dataset, whose magic is still checked: a sealed one among them is refused.</summary>
    private async ValueTask<ObjectRange> PlainCommitAsync(string key, int length, CancellationToken cancellationToken)
    {
        ObjectRange range = await _inner.GetRangeAsync(key, 0, length, cancellationToken).ConfigureAwait(false);
        if (StartsWith(range, SealedFormat.HeaderMagic))
        {
            range.Dispose();
            throw Mixed(key, sealedAmongPlain: true);
        }

        return range;
    }

    private async ValueTask<ObjectRange> ReadCommitAsync(string key, ulong version, long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        for (int attempt = 0; ; attempt++)
        {
            CommitLayout? layout = Kept(key);
            Resolved resolved = default;
            if (layout is null)
            {
                resolved = await ResolveAsync(key, version, offset + (long)length, cancellationToken).ConfigureAwait(false);
                if (resolved.Layout is null)
                {
                    // The dataset turned out plain: the bytes asked for, cut out of the read.
                    using (resolved.Head)
                    {
                        return Cut(resolved.Head, offset, length);
                    }
                }

                layout = resolved.Layout;
            }

            try
            {
                long plainLength = layout.PlainLength;
                if (offset >= plainLength && !(offset == 0 && plainLength == 0))
                {
                    throw new ArgumentOutOfRangeException(nameof(offset), offset, $"'{key}' holds {plainLength} bytes, so the range starts past its end.");
                }

                int wanted = (int)Math.Min(length, plainLength - offset);
                if (wanted == 0)
                {
                    return new ObjectRange(new SegmentLease(ReadOnlyMemory<byte>.Empty), layout.Token, plainLength);
                }

                (long frameStart, long frameEnd) = layout.Frames(offset, wanted);
                (long cipherStart, long cipherEnd) = layout.Cipher(frameStart, frameEnd);
                if (resolved.Layout is not null && cipherEnd <= resolved.Head.Length)
                {
                    return layout.Open(resolved.Head.Contiguous().Span, 0, frameStart, frameEnd, offset, wanted, layout.Token);
                }

                using ObjectRange cipher = await _inner
                    .GetRangeAsync(key, cipherStart, checked((int)(cipherEnd - cipherStart)), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(layout.Token, cipher.Token, StringComparison.Ordinal))
                {
                    // Removed and created again under its key since its layout was read.
                    Forget(key);
                    if (attempt == 0)
                    {
                        continue;
                    }

                    throw new VortexFormatException($"'{key}' changed under this reader: it was {layout.Token} and is now {cipher.Token}.");
                }

                if (cipher.Length != cipherEnd - cipherStart)
                {
                    throw new CommitFormatException($"'{key}' ends before the frames its layout names.");
                }

                return layout.Open(cipher.Contiguous().Span, cipherStart, frameStart, frameEnd, offset, wanted, cipher.Token);
            }
            finally
            {
                resolved.Head.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads a commit object's head, with as much of its frames as a range ending at
    /// <paramref name="plainEnd"/> needs, and makes its layout: the descriptor, the length the store
    /// gave, the binding to the dataset and the version, and the key, its commitment checked. The
    /// layout is null when the object turns out plain on a dataset whose kind was not known yet, the
    /// read handed back as it is.
    /// </summary>
    private async ValueTask<Resolved> ResolveAsync(string key, ulong version, long plainEnd, CancellationToken cancellationToken)
    {
        int frameSize = 1 << FrameLog2;
        long reach = Math.Clamp(plainEnd, 1, SpeculativeBytes);
        long frames = ((reach - 1) / frameSize) + 1;
        int window = checked((int)(HeaderAllowance + (frames * (frameSize + SealedFormat.TagBytes))));
        ObjectRange head = await _inner.GetRangeAsync(key, 0, window, cancellationToken).ConfigureAwait(false);
        bool kept = false;
        try
        {
            if (!StartsWith(head, SealedFormat.HeaderMagic))
            {
                if (Seals || !StartsWith(head, CommitMagic))
                {
                    throw Seals
                        ? Mixed(key, sealedAmongPlain: false)
                        : new CommitFormatException($"'{key}' starts with neither a commit object's magic nor a sealed object's.");
                }

                Interlocked.CompareExchange(ref _kind, Plain, Unknown);
                kept = true;
                return new Resolved(null, head);
            }

            ReadOnlySpan<byte> bytes = head.Contiguous().Span;
            if (bytes.Length < SealedFormat.HeaderPrefixBytes)
            {
                throw new CommitFormatException($"'{key}' is {bytes.Length} bytes, too few for a sealed header.");
            }

            int descriptorLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            if (descriptorLength <= 0 || descriptorLength > SealedFormat.MaxDescriptorBytes)
            {
                throw new CommitFormatException($"'{key}' claims a descriptor of {descriptorLength} bytes.");
            }

            SealDescriptor descriptor;
            if (SealedFormat.HeaderPrefixBytes + descriptorLength <= bytes.Length)
            {
                descriptor = Descriptor(bytes.Slice(SealedFormat.HeaderPrefixBytes, descriptorLength), descriptorLength, key);
            }
            else
            {
                using ObjectRange whole = await _inner
                    .GetRangeAsync(key, 0, SealedFormat.HeaderPrefixBytes + descriptorLength, cancellationToken).ConfigureAwait(false);
                ReadOnlySpan<byte> header = whole.Contiguous().Span;
                if (header.Length != SealedFormat.HeaderPrefixBytes + descriptorLength)
                {
                    throw new CommitFormatException($"'{key}' ends inside its sealed header.");
                }

                descriptor = Descriptor(header[SealedFormat.HeaderPrefixBytes..], descriptorLength, key);
            }

            long objectLength = head.ObjectLength >= 0
                ? head.ObjectLength
                : head.Length < window
                    ? head.Length
                    : (await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key)).Length;
            long framesStart = SealedFormat.HeaderPrefixBytes + descriptorLength;
            long plainLength = SealedFormat.PlainLength(
                objectLength - SealedFormat.OneEpochTrailerBytes(descriptorLength) - framesStart, descriptor.FrameSize);
            if (plainLength < 0)
            {
                throw new CommitFormatException($"'{key}' is {objectLength} bytes, which no whole sealed commit object of its descriptor measures: it is torn.");
            }

            CommitLayout layout = await LayoutAsync(key, version, descriptor, framesStart, plainLength, head.Token, cancellationToken).ConfigureAwait(false);
            Remember(key, version, layout);
            kept = true;
            return new Resolved(layout, head);
        }
        finally
        {
            if (!kept)
            {
                head.Dispose();
            }
        }
    }

    /// <summary>The cipher of a commit object, once its descriptor is checked against the dataset and the version, and its commitment against the key.</summary>
    private async ValueTask<CommitLayout> LayoutAsync(
        string key, ulong version, SealDescriptor descriptor, long framesStart, long plainLength, string token, CancellationToken cancellationToken)
    {
        if (descriptor.KeyContext.Length != SealedFormat.ObjectIdBytes)
        {
            throw VortexEncryptionException.Unauthenticated($"'{key}' names no dataset in its key context: it is not one of this dataset's commit objects.");
        }

        byte[] id;
        lock (_gate)
        {
            // The first commit object read names the dataset, as a handle trusts the latest version it lists.
            _datasetId ??= descriptor.KeyContext.ToArray();
            id = _datasetId;
            Volatile.Write(ref _kind, Sealed);
        }

        if (!descriptor.KeyContext.Span.SequenceEqual(id) || !descriptor.Binding.SequenceEqual(Binding(id, version)))
        {
            throw VortexEncryptionException.Unauthenticated(
                $"'{key}' is bound to another dataset or another version: it was copied or moved under this key.");
        }

        DataKey dataKey = await _keys.UnwrapAsync(descriptor, cancellationToken).ConfigureAwait(false);
        try
        {
            return new CommitLayout(descriptor, framesStart, plainLength, token, Cipher(dataKey, descriptor, key));
        }
        finally
        {
            dataKey.Dispose();
        }
    }

    /// <summary>The first epoch's cipher, its commitment checked in constant time.</summary>
    private static EpochCipher Cipher(DataKey dataKey, SealDescriptor descriptor, string key)
    {
        Span<byte> epochKey = stackalloc byte[SealedFormat.KeyBytes];
        Span<byte> commitment = stackalloc byte[SealedFormat.CommitmentBytes];
        try
        {
            SealedFormat.Derive(dataKey.Key, descriptor.Salt, descriptor.Hash, 0, 0, epochKey, commitment);
            if (!CryptographicOperations.FixedTimeEquals(commitment, descriptor.Commitment))
            {
                throw VortexEncryptionException.Unauthenticated(
                    $"The key of '{key}' is not the one its commitment names: the object was altered, or its data key is another's.");
            }

            return new EpochCipher(epochKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(epochKey);
        }
    }

    private CommitLayout? Kept(string key)
    {
        lock (_gate)
        {
            return _layouts.GetValueOrDefault(key);
        }
    }

    /// <summary>Keeps a layout, and makes the data key of the newest commit read the one writers seal under.</summary>
    private void Remember(string key, ulong version, CommitLayout layout)
    {
        lock (_gate)
        {
            if (_layouts.Remove(key, out CommitLayout? replaced))
            {
                replaced.Dispose();
            }

            _layouts[key] = layout;
            _order.Enqueue(key);
            while (_layouts.Count > MaxLayouts && _order.TryDequeue(out string? oldest))
            {
                if (_layouts.Remove(oldest, out CommitLayout? evicted))
                {
                    evicted.Dispose();
                }
            }

            if (version > _currentVersion)
            {
                _current = new KeyName(layout.Descriptor.KeyId, layout.Descriptor.WrappedKey.ToArray());
                _currentVersion = version;
            }
        }
    }

    private void Forget(string key)
    {
        lock (_gate)
        {
            if (_layouts.Remove(key, out CommitLayout? layout))
            {
                layout.Dispose();
            }
        }
    }

    // ---- data objects ----------------------------------------------------------------------

    /// <summary>
    /// A reader over the data object under <paramref name="key"/>: a sealed reader on an encrypted
    /// dataset, its object id checked against <paramref name="uid"/> when the entry names one; the
    /// object as it lies otherwise.
    /// </summary>
    /// <exception cref="VortexEncryptionException">The object is plain in an encrypted dataset, or not the one its entry names.</exception>
    internal async ValueTask<ISegmentReader> OpenDataAsync(string key, UInt128 uid, CancellationToken cancellationToken)
    {
        ObjectSegmentSource source = new ObjectSegmentSource(_inner, key);
        if (!Seals)
        {
            return source;
        }

        try
        {
            return await SealedSegmentReader.OpenAsync(
                source,
                ownsInner: true,
                (descriptor, token) =>
                {
                    if (uid != UInt128.Zero && !descriptor.ObjectId.SequenceEqual(UidBytes(uid)))
                    {
                        throw VortexEncryptionException.Unauthenticated(
                            $"'{key}' is not the object its entry names: its envelope binds another object id.");
                    }

                    return _keys.UnwrapAsync(descriptor, token);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (VortexFormatException)
        {
            bool plain = await EndsPlainAsync(source, cancellationToken).ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
            if (plain)
            {
                throw Mixed(key, sealedAmongPlain: false);
            }

            throw;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The reader over a data object of the dataset behind <paramref name="store"/>, sealed or plain as the dataset is.</summary>
    internal static ValueTask<ISegmentReader> OpenDataAsync(IObjectStore store, string key, UInt128 uid, CancellationToken cancellationToken) =>
        store is SealedObjectStore sealedStore
            ? sealedStore.OpenDataAsync(key, uid, cancellationToken)
            : new ValueTask<ISegmentReader>(new ObjectSegmentSource(store, key));

    /// <summary>
    /// What verification finds wrong with a sealed object's envelope beyond its frames: two copies of
    /// the descriptor that are not the same bytes. Null when they are, or when the dataset is plain.
    /// </summary>
    internal async ValueTask<string?> CheckEnvelopeAsync(string key, CancellationToken cancellationToken)
    {
        if (!Seals)
        {
            return null;
        }

        using ObjectRange prefix = await _inner.GetRangeAsync(key, 0, HeaderAllowance, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> head = prefix.Contiguous();
        if (head.Length < SealedFormat.HeaderPrefixBytes || !head.Span.StartsWith(SealedFormat.HeaderMagic))
        {
            return $"'{key}' does not start as a sealed object";
        }

        int descriptorLength = BinaryPrimitives.ReadInt32LittleEndian(head.Span[8..]);
        if (descriptorLength <= 0 || SealedFormat.HeaderPrefixBytes + descriptorLength > head.Length)
        {
            return string.Create(CultureInfo.InvariantCulture, $"'{key}' claims a descriptor of {descriptorLength} bytes");
        }

        ObjectHead stored = await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
        int tail = (int)Math.Min(stored.Length, SealedFormat.OneEpochTrailerBytes(descriptorLength) + HeaderAllowance);
        using ObjectRange end = await _inner.GetRangeAsync(key, stored.Length - tail, tail, cancellationToken).ConfigureAwait(false);
        return Compare(key, head.Span.Slice(SealedFormat.HeaderPrefixBytes, descriptorLength), end.Contiguous().Span, stored.Length);

        static string? Compare(string key, ReadOnlySpan<byte> descriptor, ReadOnlySpan<byte> trailer, long objectLength)
        {
            int trailerLength;
            try
            {
                trailerLength = SealedLayout.TrailerLength(trailer, objectLength);
            }
            catch (VortexFormatException malformed)
            {
                return $"'{key}': {malformed.Message}";
            }

            if (trailerLength > trailer.Length || trailerLength < descriptor.Length)
            {
                return string.Create(CultureInfo.InvariantCulture, $"'{key}' has a trailer of {trailerLength} bytes, which cannot hold its descriptor");
            }

            return trailer[^trailerLength..][..descriptor.Length].SequenceEqual(descriptor)
                ? null
                : $"'{key}' carries two different descriptors, at its head and in its trailer";
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private byte[] DatasetId()
    {
        lock (_gate)
        {
            return _kind == Sealed && _datasetId is { } id
                ? id
                : throw new InvalidOperationException("The dataset is not encrypted, or its latest commit object is not read yet.");
        }
    }

    /// <summary>What an object of the dataset is bound to: the SHA-256 of the dataset's id, and of a commit object's version after it.</summary>
    private static byte[] Binding(ReadOnlySpan<byte> datasetId, ulong? version)
    {
        Span<byte> input = stackalloc byte[SealedFormat.ObjectIdBytes + 8];
        datasetId.CopyTo(input);
        int length = SealedFormat.ObjectIdBytes;
        if (version is { } committed)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input[length..], committed);
            length += 8;
        }

        return SHA256.HashData(input[..length]);
    }

    /// <summary>The uid a data object's key names: <c>data/&lt;uid&gt;.vortex</c>, the uid in 32 hexadecimal digits.</summary>
    private static bool TryUid(string key, out Guid uid)
    {
        uid = Guid.Empty;
        return key.Length == CommitKey.DataPrefix.Length + 32 + CommitKey.DataSuffix.Length
            && key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal)
            && key.EndsWith(CommitKey.DataSuffix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(CommitKey.DataPrefix.Length, 32), "N", out uid);
    }

    /// <summary>The 16 bytes of an entry's uid as a sealed object's id holds them: the identity's own bytes.</summary>
    internal static byte[] UidBytes(UInt128 uid)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)(uid >> 64));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)uid);
        return bytes;
    }

    private static SealDescriptor Descriptor(ReadOnlySpan<byte> bytes, int length, string key)
    {
        SealDescriptor descriptor = SealDescriptor.Read(bytes, out int consumed);
        if (consumed != length)
        {
            throw new CommitFormatException($"The descriptor of '{key}' takes {consumed} bytes and its header says {length}.");
        }

        return descriptor;
    }

    private static bool StartsWith(in ObjectRange range, ReadOnlySpan<byte> magic)
    {
        if (range.Length < magic.Length)
        {
            return false;
        }

        Span<byte> first = stackalloc byte[8];
        range.Bytes.Slice(0, magic.Length).CopyTo(first);
        return first[..magic.Length].SequenceEqual(magic);
    }

    /// <summary>The bytes <c>[offset, offset + length)</c> of a read that began at offset zero, copied out, as the store would have answered them.</summary>
    private static ObjectRange Cut(in ObjectRange read, long offset, int length)
    {
        if (offset >= read.Length && !(offset == 0 && read.Length == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The range starts past the end of the object.");
        }

        int available = (int)Math.Min(length, read.Length - offset);
        byte[] copy = read.Bytes.Slice(offset, available).ToArray();
        return new ObjectRange(new SegmentLease(copy), read.Token, read.ObjectLength);
    }

    private static async ValueTask<bool> EndsPlainAsync(ObjectSegmentSource source, CancellationToken cancellationToken)
    {
        try
        {
            long length = await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length < 4)
            {
                return false;
            }

            Buffers.SegmentOwner end = await source.ReadRangeAsync(length - 4, 4, 1, cancellationToken).ConfigureAwait(false);
            try
            {
                return end.Buffer.Span.SequenceEqual("VTXF"u8);
            }
            finally
            {
                end.Release();
            }
        }
        catch (Exception)
        {
            // The object cannot even be read back to tell: the format error says more.
            return false;
        }
    }

    private static VortexEncryptionException Mixed(string key, bool sealedAmongPlain) =>
        VortexEncryptionException.Refused(sealedAmongPlain
            ? $"'{key}' is sealed and the dataset's commit objects are plain: a dataset is sealed whole or not at all."
            : $"'{key}' is plain and the dataset is encrypted: a dataset is sealed whole or not at all, and a plain object in it is refused.");

    /// <summary>What a first read of a commit object made of it: its layout, null for a plain one, and the read itself.</summary>
    private readonly record struct Resolved(CommitLayout? Layout, ObjectRange Head);

    /// <summary>A data key as a sealed object names it: the key that wrapped it and the wrapped bytes.</summary>
    private sealed record KeyName(string KeyId, byte[] Wrapped);

    /// <summary>What a commit object's head says, and its key: everything a range of it needs to be read in one request.</summary>
    private sealed class CommitLayout : IDisposable
    {
        private readonly long _framesStart;
        private readonly EpochCipher _cipher;

        internal CommitLayout(SealDescriptor descriptor, long framesStart, long plainLength, string token, EpochCipher cipher)
        {
            Descriptor = descriptor;
            _framesStart = framesStart;
            PlainLength = plainLength;
            Token = token;
            _cipher = cipher;
        }

        internal SealDescriptor Descriptor { get; }

        internal long PlainLength { get; }

        internal string Token { get; }

        private int FrameSize => Descriptor.FrameSize;

        private long FrameCount => SealedFormat.FrameCount(PlainLength, FrameSize);

        /// <summary>The plaintext bounds of the frames that hold <c>[offset, offset + length)</c>.</summary>
        internal (long Start, long End) Frames(long offset, int length)
        {
            long start = offset / FrameSize * FrameSize;
            long end = Math.Min((((offset + length - 1) / FrameSize) + 1) * FrameSize, PlainLength);
            return (start, end);
        }

        /// <summary>The object range of the frames between plaintext <paramref name="frameStart"/> and <paramref name="frameEnd"/>.</summary>
        internal (long Start, long End) Cipher(long frameStart, long frameEnd)
        {
            long first = frameStart / FrameSize;
            long last = (frameEnd - 1) / FrameSize;
            long stride = FrameSize + SealedFormat.TagBytes;
            return (_framesStart + (first * stride), _framesStart + (last * stride) + (frameEnd - (last * FrameSize)) + SealedFormat.TagBytes);
        }

        /// <summary>Decrypts the frames out of <paramref name="cipher"/>, whose first byte is object offset <paramref name="cipherBase"/>, and hands out the range asked for.</summary>
        internal ObjectRange Open(ReadOnlySpan<byte> cipher, long cipherBase, long frameStart, long frameEnd, long offset, int length, string token)
        {
            int plainBytes = checked((int)(frameEnd - frameStart));
            WipedBuffer buffer = WipedBuffer.Rent(plainBytes);
            try
            {
                Span<byte> plain = buffer.Array.AsSpan(0, plainBytes);
                AesGcm aes = _cipher.Rent();
                try
                {
                    for (long at = frameStart; at < frameEnd; at += FrameSize)
                    {
                        long index = at / FrameSize;
                        int frame = (int)Math.Min(FrameSize, PlainLength - at);
                        long cipherOffset = _framesStart + (index * (FrameSize + SealedFormat.TagBytes));
                        int relative = checked((int)(cipherOffset - cipherBase));
                        EpochCipher.Open(
                            aes, index, index == FrameCount - 1, cipher.Slice(relative, frame), cipher.Slice(relative + frame, SealedFormat.TagBytes),
                            plain.Slice((int)(at - frameStart), frame), cipherOffset);
                    }
                }
                finally
                {
                    _cipher.Return(aes);
                }

                return new ObjectRange(new SegmentLease(buffer.Array.AsMemory((int)(offset - frameStart), length), buffer), token, PlainLength);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        public void Dispose() => _cipher.Dispose();
    }

    /// <summary>An array from the shared pool, wiped as it goes back: a commit object's plaintext does not outlive its lease there.</summary>
    private sealed class WipedBuffer : IDisposable
    {
        private byte[]? _array;

        private WipedBuffer(byte[] array) => _array = array;

        internal byte[] Array => _array ?? throw new ObjectDisposedException(nameof(WipedBuffer));

        internal static WipedBuffer Rent(int length) => new WipedBuffer(ArrayPool<byte>.Shared.Rent(Math.Max(length, 1)));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _array, null) is { } array)
            {
                ArrayPool<byte>.Shared.Return(array, clearArray: true);
            }
        }
    }
}
