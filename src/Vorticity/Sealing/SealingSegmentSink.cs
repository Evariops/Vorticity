using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Diagnostics;
using Vorticity.Writing;

namespace Vorticity.Sealing;

/// <summary>What a sealed object is bound to and how it is cut, fixed before its first byte.</summary>
/// <param name="FrameLog2">The frame size's base-2 logarithm, 12 to 20.</param>
/// <param name="ObjectId">16 bytes: random for a file, the uid of a dataset's data object.</param>
/// <param name="Binding">32 bytes: the SHA-256 of what the writer binds the object to, or zeros.</param>
/// <param name="KeyContext">What the data key was generated with, which a reader hands back to unwrap it.</param>
/// <param name="Salt">The first epoch's salt, or empty for a random one: pinned only by known-answer tests.</param>
internal sealed record SealParameters(int FrameLog2, ReadOnlyMemory<byte> ObjectId, ReadOnlyMemory<byte> Binding, ReadOnlyMemory<byte> KeyContext, ReadOnlyMemory<byte> Salt)
{
    /// <summary>A file's parameters: the default frame size, a random object id, no binding, no context.</summary>
    internal static SealParameters ForFile(int frameLog2 = SealedFormat.DefaultFrameLog2)
    {
        byte[] objectId = new byte[SealedFormat.ObjectIdBytes];
        RandomNumberGenerator.Fill(objectId);
        return new SealParameters(frameLog2, objectId, new byte[SealedFormat.BindingBytes], ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }
}

/// <summary>
/// The stage between a writer and its pipe that seals what the writer writes: the header first, then
/// each frame encrypted into the pipe's buffer as soon as the bytes after it arrive, then at
/// <see cref="FinishAsync"/> the last frame and the trailer.
/// </summary>
/// <remarks>
/// <para>
/// A frame waits for the byte after it because the last frame of an epoch carries the final flag in
/// its nonce, and a full frame may turn out to be the last. A frame the writer hands over whole, with
/// more bytes behind it, is encrypted straight from the writer's memory, so a large blob is never
/// copied: the cipher's pass is the only one. Smaller writes gather in the frame buffer, which takes
/// the copy a plain pipe would make.
/// </para>
/// <para>
/// <see cref="Position"/> counts plaintext, which is what every offset the writer records means: the
/// file inside the envelope is byte for byte the one a plain write produces.
/// </para>
/// <para>
/// An append continues a sealed object with an epoch of its own: no header, frames sealed under a key
/// derived from a fresh salt, and a trailer that lists the old epochs again before the new one. The
/// pipe then starts at the old object's end, and <see cref="Position"/> at its plaintext's.
/// </para>
/// </remarks>
internal sealed class SealingSegmentSink : ISegmentSink
{
    private readonly PipeWriter _pipe;
    private readonly SealParameters? _parameters;
    private readonly SealedLayout? _continued;
    private readonly Func<CancellationToken, ValueTask<DataKey>> _dataKey;
    private readonly int _frameSize;
    private readonly long _plainStart;
    private SealDescriptor? _descriptor;
    private EpochCipher? _cipher;
    private AesGcm? _aes;
    private NativeSegmentOwner? _frame;
    private byte[]? _appendedSaltAndCommitment;
    private long _framesStart;
    private int _filled;
    private long _position;
    private long _frameIndex;
    private long _unflushed;
    private bool _finished;

    /// <summary>A stage over <paramref name="pipe"/>, which takes the data key from <paramref name="dataKey"/> when the first byte arrives.</summary>
    /// <param name="pipe">Where the sealed bytes go; the writer completes it.</param>
    /// <param name="parameters">The frame size and the ids.</param>
    /// <param name="dataKey">The data key, handed over: the stage derives the object's key from it and disposes it.</param>
    internal SealingSegmentSink(PipeWriter pipe, SealParameters parameters, Func<CancellationToken, ValueTask<DataKey>> dataKey)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(dataKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.FrameLog2, SealedFormat.MinFrameLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.FrameLog2, SealedFormat.MaxFrameLog2);
        _pipe = pipe;
        _parameters = parameters;
        _dataKey = dataKey;
        _frameSize = 1 << parameters.FrameLog2;
    }

    /// <summary>A stage that appends an epoch to the sealed object <paramref name="continued"/> describes, through a pipe positioned at its end.</summary>
    /// <param name="pipe">Where the sealed bytes go, from the old object's last byte on; the writer completes it.</param>
    /// <param name="continued">The old object's layout: its descriptor, which the epoch keeps, and its epochs.</param>
    /// <param name="dataKey">The data key the descriptor names, handed over: the stage derives the epoch's key from it and disposes it.</param>
    /// <exception cref="VortexUnsupportedException">The object already holds as many epochs as a trailer may list.</exception>
    internal SealingSegmentSink(PipeWriter pipe, SealedLayout continued, Func<CancellationToken, ValueTask<DataKey>> dataKey)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(continued);
        ArgumentNullException.ThrowIfNull(dataKey);
        if (continued.Epochs.Length >= SealedFormat.MaxEpochs)
        {
            throw new VortexUnsupportedException(
                "append",
                ComponentKind.Feature,
                $"This sealed file holds {continued.Epochs.Length} epochs, the most a trailer lists, and cannot be appended to. Rewrite it instead.");
        }

        _pipe = pipe;
        _continued = continued;
        _dataKey = dataKey;
        _frameSize = continued.FrameSize;
        _plainStart = continued.PlainLength;
        _position = continued.PlainLength;
        _framesStart = continued.ObjectLength;
    }

    /// <summary>The plaintext bytes written so far, the next write's offset in the file inside the envelope.</summary>
    public long Position => _position;

    /// <summary>Sealed bytes written into the pipe and not yet flushed.</summary>
    internal long UnflushedBytes => _unflushed;

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        if (_aes is null)
        {
            return StartThenWriteAsync(data, cancellationToken);
        }

        Write(data.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        FlushResult result = await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        _unflushed = 0;
        if (result.IsCanceled)
        {
            throw new OperationCanceledException("The pipe's flush was canceled.");
        }

        if (result.IsCompleted)
        {
            throw new InvalidOperationException("The pipe's reader completed before the file did.");
        }
    }

    /// <summary>Seals the last frame, writes the trailer, and lets go of the key: the object is whole once the pipe is flushed.</summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        if (_aes is null)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        SealFrame(_frame!.Buffer.Span[.._filled], final: true);
        _filled = 0;
        WriteTrailer();
        _finished = true;
        Release();
    }

    /// <summary>Wipes the key and gives the frame buffer back, whether or not the object was finished. Idempotent.</summary>
    internal void Release()
    {
        if (_aes is { } aes)
        {
            _aes = null;
            _cipher!.Return(aes);
        }

        _cipher?.Dispose();
        _cipher = null;
        if (_frame is { } frame)
        {
            _frame = null;
            CryptographicOperations.ZeroMemory(frame.WritableSpan);
            frame.Release();
        }
    }

    private async ValueTask StartThenWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        Write(data.Span);
    }

    /// <summary>
    /// Takes the data key and derives the epoch's key and commitment: for a new object the first
    /// epoch's, and the header written; for an append the next epoch's, from a fresh salt.
    /// </summary>
    private async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        using DataKey dataKey = await _dataKey(cancellationToken).ConfigureAwait(false);
        Span<byte> salt = stackalloc byte[SealedFormat.SaltBytes];
        if (_parameters is null || _parameters.Salt.IsEmpty)
        {
            RandomNumberGenerator.Fill(salt);
        }
        else
        {
            _parameters.Salt.Span.CopyTo(salt);
        }

        SealDescriptor descriptor = _continued?.Descriptor ?? SealDescriptor.Create(
            _parameters!.FrameLog2, _parameters.ObjectId.Span, _parameters.Binding.Span, dataKey.KeyId,
            _parameters.KeyContext.Span, dataKey.WrappedKey.Span, salt);
        uint epoch = (uint)(_continued?.Epochs.Length ?? 0);
        Span<byte> key = stackalloc byte[SealedFormat.KeyBytes];
        Span<byte> commitment = stackalloc byte[SealedFormat.CommitmentBytes];
        try
        {
            if (_continued is not null)
            {
                // An epoch sealed under another data key than the object's would make the whole object
                // unreadable: the key handed over is checked against the first epoch's commitment first.
                SealedFormat.Derive(dataKey.Key, descriptor.Salt, descriptor.Hash, 0, 0, key, commitment);
                if (!CryptographicOperations.FixedTimeEquals(commitment, descriptor.Commitment))
                {
                    throw VortexEncryptionException.Unauthenticated(
                        "The data key handed to the append is not the one the sealed object was sealed under: no epoch is written.");
                }
            }

            SealedFormat.Derive(dataKey.Key, salt, descriptor.Hash, epoch, _plainStart, key, commitment);
            if (_continued is null)
            {
                descriptor.SetCommitment(commitment);
            }
            else
            {
                // An appended epoch's salt and commitment go in its entry of the trailer.
                _appendedSaltAndCommitment = new byte[SealedFormat.SaltBytes + SealedFormat.CommitmentBytes];
                salt.CopyTo(_appendedSaltAndCommitment);
                commitment.CopyTo(_appendedSaltAndCommitment.AsSpan(SealedFormat.SaltBytes));
            }

            _cipher = new EpochCipher(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _descriptor = descriptor;
        _aes = _cipher.Rent();
        _frame = AlignedBufferPool.Shared.Rent(_frameSize, VortexLimits.MaxAlignment);
        if (_continued is not null)
        {
            return;
        }

        Span<byte> header = _pipe.GetSpan(SealedFormat.HeaderPrefixBytes + descriptor.Length);
        SealedFormat.HeaderMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], descriptor.Length);
        descriptor.Bytes.CopyTo(header[SealedFormat.HeaderPrefixBytes..]);
        Advance(SealedFormat.HeaderPrefixBytes + descriptor.Length);
        _framesStart = SealedFormat.HeaderPrefixBytes + descriptor.Length;
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _position += data.Length;
        Span<byte> frame = _frame!.WritableSpan[.._frameSize];
        while (!data.IsEmpty)
        {
            // A full frame is sealed only now that a byte after it has arrived: it is not the last.
            if (_filled == _frameSize)
            {
                SealFrame(frame, final: false);
                _filled = 0;
            }

            // A whole frame with more behind it goes straight from the writer's memory.
            if (_filled == 0 && data.Length > _frameSize)
            {
                SealFrame(data[.._frameSize], final: false);
                data = data[_frameSize..];
                continue;
            }

            int take = Math.Min(_frameSize - _filled, data.Length);
            data[..take].CopyTo(frame[_filled..]);
            _filled += take;
            data = data[take..];
        }
    }

    private void SealFrame(ReadOnlySpan<byte> plaintext, bool final)
    {
        if (_frameIndex >= SealedFormat.MaxFramesPerEpoch)
        {
            throw new InvalidOperationException($"An epoch holds at most {SealedFormat.MaxFramesPerEpoch} frames; write the file with larger frames.");
        }

        Span<byte> sealedFrame = _pipe.GetSpan(plaintext.Length + SealedFormat.TagBytes);
        EpochCipher.Seal(_aes!, _frameIndex, final, plaintext, sealedFrame[..plaintext.Length], sealedFrame.Slice(plaintext.Length, SealedFormat.TagBytes));
        Advance(plaintext.Length + SealedFormat.TagBytes);
        _frameIndex++;
    }

    /// <summary>The descriptor again, every epoch, the trailer's length, and the magic.</summary>
    private void WriteTrailer()
    {
        SealDescriptor descriptor = _descriptor!;
        SealedEpoch[] old = _continued?.Epochs ?? [];
        int length = checked(descriptor.Length + 4 + SealedFormat.EpochEntryBytes
            + (old.Length * SealedFormat.AppendedEpochEntryBytes) + SealedFormat.TrailerSuffixBytes);
        Span<byte> trailer = _pipe.GetSpan(length);
        descriptor.Bytes.CopyTo(trailer);
        int at = descriptor.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[at..], (uint)(old.Length + 1));
        at += 4;
        for (int e = 0; e < old.Length; e++)
        {
            at = WriteEpoch(trailer, at, old[e].FramesStart, old[e].PlainLength);
            if (e > 0)
            {
                old[e].Salt.Span.CopyTo(trailer[at..]);
                old[e].Commitment.Span.CopyTo(trailer[(at + SealedFormat.SaltBytes)..]);
                at += SealedFormat.SaltBytes + SealedFormat.CommitmentBytes;
            }
        }

        // The epoch written now; its salt and commitment when it is not the first.
        at = WriteEpoch(trailer, at, _framesStart, _position - _plainStart);
        ReadOnlySpan<byte> appended = _appendedSaltAndCommitment;
        appended.CopyTo(trailer[at..]);
        at += appended.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[at..], (uint)length);
        SealedFormat.TrailerMagic.CopyTo(trailer[(at + 4)..]);
        Advance(length);
    }

    /// <summary>The fixed part of an epoch's entry: where its frames start and its plaintext length.</summary>
    private static int WriteEpoch(Span<byte> trailer, int at, long framesStart, long plainLength)
    {
        BinaryPrimitives.WriteInt64LittleEndian(trailer[at..], framesStart);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[(at + 8)..], plainLength);
        return at + SealedFormat.EpochEntryBytes;
    }

    private void Advance(int bytes)
    {
        _pipe.Advance(bytes);
        _unflushed += bytes;
    }
}
