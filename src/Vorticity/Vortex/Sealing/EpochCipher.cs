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
/// platform's copies of it. A dispose that comes while a rent is creating an instance leaves the
/// key to that rent, which wipes it once the instance has its copy: a rent never reads a key
/// already freed.
/// </remarks>
internal sealed class EpochCipher : IDisposable
{
    /// <summary>The instances kept between reads: more lanes than this create and drop their own.</summary>
    private const int KeptInstances = 8;

    /// <summary>The bit of <see cref="_state"/> that says the cipher is disposed.</summary>
    private const int Disposed = int.MinValue;

    private readonly SecretBytes _key;
    private readonly AesGcm?[] _kept = new AesGcm?[KeptInstances];

    // Whether the cipher is disposed, its sign, and how many rents are creating an instance from the key, the rest: one
    // word, so that a rent comes in and a dispose closes the cipher in one step each. Once it is closed no rent comes in,
    // so the count only falls, and one party alone finds it at zero: the dispose, or the last rent out, which wipes the
    // key then. A flag and a count in two words let a rent come in between another's way out and its look at the flag,
    // which then wiped the key under it.
    private int _state;

    /// <summary>A cipher over <paramref name="key"/>, which it copies.</summary>
    internal EpochCipher(ReadOnlySpan<byte> key) => _key = new SecretBytes(key);

    private bool IsDisposed => Volatile.Read(ref _state) < 0;

    /// <summary>An instance for the calling thread alone, until <see cref="Return"/>.</summary>
    internal AesGcm Rent()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (int i = 0; i < _kept.Length; i++)
        {
            if (Interlocked.Exchange(ref _kept[i], null) is { } kept)
            {
                return kept;
            }
        }

        return Create();
    }

    /// <summary>A new instance over the key, which a dispose meanwhile leaves alone until it has its copy.</summary>
    private AesGcm Create()
    {
        // Counted in, unless the cipher is disposed.
        int state = Volatile.Read(ref _state);
        while (true)
        {
            ObjectDisposedException.ThrowIf(state < 0, this);
            int seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                break;
            }

            state = seen;
        }

        try
        {
            return new AesGcm(_key.Span, SealedFormat.TagBytes);
        }
        finally
        {
            // The last one out of a cipher disposed meanwhile wipes the key the dispose left it.
            if (Interlocked.Decrement(ref _state) == Disposed)
            {
                _key.Dispose();
            }
        }
    }

    /// <summary>Gives back an instance from <see cref="Rent"/>, kept for the next read or disposed.</summary>
    internal void Return(AesGcm instance)
    {
        if (!IsDisposed)
        {
            for (int i = 0; i < _kept.Length; i++)
            {
                if (Interlocked.CompareExchange(ref _kept[i], instance, null) is null)
                {
                    // A dispose that ran meanwhile may have swept the slots already: take it back.
                    if (IsDisposed && Interlocked.Exchange(ref _kept[i], null) is { } late)
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

    /// <summary>Wipes the key, or leaves it to the rents creating an instance from it, and disposes the kept instances. Idempotent.</summary>
    public void Dispose()
    {
        // Closed, with the count of the rents creating an instance as it stood at that step.
        int state = Volatile.Read(ref _state);
        while (true)
        {
            if (state < 0)
            {
                return;
            }

            int seen = Interlocked.CompareExchange(ref _state, state | Disposed, state);
            if (seen == state)
            {
                break;
            }

            state = seen;
        }

        for (int i = 0; i < _kept.Length; i++)
        {
            Interlocked.Exchange(ref _kept[i], null)?.Dispose();
        }

        // None was creating an instance: the key goes now. Otherwise the last of them out wipes it.
        if (state == 0)
        {
            _key.Dispose();
        }
    }
}
