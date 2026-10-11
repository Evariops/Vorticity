using System;
using System.Security.Cryptography;
using System.Threading;

namespace Vorticity.Sealing;

/// <summary>
/// The key of one epoch of one sealed object, and the platform's <see cref="AesGcm"/> instances that
/// seal or open its frames.
/// </summary>
/// <remarks>
/// An <see cref="AesGcm"/> imports its key once into a native context and holds no lock, so one
/// instance serves one thread at a time. The lanes that read an object at once each rent their own,
/// created when first needed and kept for the next read, so a scan allocates none per frame or per
/// batch. Disposing the cipher wipes the key and disposes every instance, which frees the
/// platform's copies of it.
/// </remarks>
internal sealed class EpochCipher : IDisposable
{
    /// <summary>The instances kept between reads: more lanes than this create and drop their own.</summary>
    private const int KeptInstances = 8;

    private readonly SecretBytes _key;
    private readonly AesGcm?[] _kept = new AesGcm?[KeptInstances];
    private int _disposed;

    /// <summary>A cipher over <paramref name="key"/>, which it copies.</summary>
    internal EpochCipher(ReadOnlySpan<byte> key) => _key = new SecretBytes(key);

    /// <summary>An instance for the calling thread alone, until <see cref="Return"/>.</summary>
    internal AesGcm Rent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (int i = 0; i < _kept.Length; i++)
        {
            if (Interlocked.Exchange(ref _kept[i], null) is { } kept)
            {
                return kept;
            }
        }

        return new AesGcm(_key.Span, SealedFormat.TagBytes);
    }

    /// <summary>Gives back an instance from <see cref="Rent"/>, kept for the next read or disposed.</summary>
    internal void Return(AesGcm instance)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            for (int i = 0; i < _kept.Length; i++)
            {
                if (Interlocked.CompareExchange(ref _kept[i], instance, null) is null)
                {
                    // A dispose that ran meanwhile may have swept the slots already: take it back.
                    if (Volatile.Read(ref _disposed) != 0 && Interlocked.Exchange(ref _kept[i], null) is { } late)
                    {
                        late.Dispose();
                    }

                    return;
                }
            }
        }

        instance.Dispose();
    }

    /// <summary>Seals one frame: <paramref name="plaintext"/> into <paramref name="ciphertext"/>, then its tag.</summary>
    internal static void Seal(AesGcm aes, long index, bool final, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag)
    {
        Span<byte> nonce = stackalloc byte[SealedFormat.NonceBytes];
        SealedFormat.Nonce(index, final, nonce);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
    }

    /// <summary>Opens one frame into <paramref name="plaintext"/>, or throws when its tag does not authenticate it.</summary>
    /// <exception cref="VortexEncryptionException">The tag fails: the frame was altered, moved, truncated or taken from another object.</exception>
    internal static void Open(AesGcm aes, long index, bool final, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, long objectOffset)
    {
        Span<byte> nonce = stackalloc byte[SealedFormat.NonceBytes];
        SealedFormat.Nonce(index, final, nonce);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException mismatch)
        {
            throw VortexEncryptionException.Unauthenticated(
                $"The frame at offset {objectOffset} of the sealed object does not authenticate: the object was altered, truncated, or assembled from another.",
                mismatch);
        }
    }

    /// <summary>Wipes the key and disposes the kept instances. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int i = 0; i < _kept.Length; i++)
        {
            Interlocked.Exchange(ref _kept[i], null)?.Dispose();
        }

        _key.Dispose();
    }
}
