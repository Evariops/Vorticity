using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Sealing;

namespace Vorticity;

/// <summary>
/// A data key, unwrapped: 256 random bits that seal objects, the id of the key that wrapped them, and
/// the wrapped bytes a sealed object records so that a keyring can unwrap them again.
/// </summary>
/// <remarks>
/// The key lives in native memory, which the garbage collector never moves or copies, and
/// <see cref="Dispose"/> wipes it. The library never hands it out: a sealed object derives keys of
/// its own from it, a fresh one per object and per append. A key a keyring returns belongs to the
/// library from then on, which disposes it.
/// </remarks>
public sealed class DataKey : IDisposable
{
    private readonly SecretBytes _key;
    private readonly byte[] _wrapped;

    /// <summary>The holders of the key: the one that created it, and those the library lent it to.</summary>
    private int _holders = 1;

    /// <summary>A data key a keyring generated or unwrapped.</summary>
    /// <param name="keyId">The id of the key that wrapped it, which a sealed object records and hands back to unwrap it.</param>
    /// <param name="key">The 32 bytes of the key, copied into native memory; the caller wipes its own copy.</param>
    /// <param name="wrappedKey">The key as the keyring wrapped it, 1 to 1 024 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is empty or longer than 255 bytes of UTF-8, <paramref name="key"/> is not 32 bytes, or <paramref name="wrappedKey"/> is empty or too long.</exception>
    public DataKey(string keyId, ReadOnlySpan<byte> key, ReadOnlySpan<byte> wrappedKey)
    {
        VortexKey.CheckId(keyId, nameof(keyId));
        if (key.Length != SealedFormat.KeyBytes)
        {
            throw new ArgumentException($"A data key is {SealedFormat.KeyBytes} bytes; this one is {key.Length}.", nameof(key));
        }

        if (wrappedKey.IsEmpty || wrappedKey.Length > SealedFormat.MaxWrappedKeyBytes)
        {
            throw new ArgumentException($"A wrapped data key is 1 to {SealedFormat.MaxWrappedKeyBytes} bytes; this one is {wrappedKey.Length}.", nameof(wrappedKey));
        }

        KeyId = keyId;
        _key = new SecretBytes(key);
        _wrapped = wrappedKey.ToArray();
    }

    /// <summary>The id of the key that wrapped this one.</summary>
    public string KeyId { get; }

    /// <summary>The key as its keyring wrapped it, which every object it seals records.</summary>
    public ReadOnlyMemory<byte> WrappedKey => _wrapped;

    /// <summary>The key itself, until the data key is disposed.</summary>
    internal ReadOnlySpan<byte> Key => _key.Span;

    /// <summary>Lends the key to one more holder, which disposes it once done; the key is wiped when the last one does.</summary>
    /// <exception cref="ObjectDisposedException">The key is already wiped.</exception>
    internal DataKey Retain()
    {
        int holders = Volatile.Read(ref _holders);
        while (true)
        {
            ObjectDisposedException.ThrowIf(holders <= 0, this);
            int seen = Interlocked.CompareExchange(ref _holders, holders + 1, holders);
            if (seen == holders)
            {
                return this;
            }

            holders = seen;
        }
    }

    /// <summary>Wipes the key, once every holder the library lent it to has let it go.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _holders) == 0)
        {
            _key.Dispose();
        }
    }
}

/// <summary>A key the application holds, which a keyring of <see cref="VortexKeyring.FromKeys"/> wraps data keys with.</summary>
public readonly struct VortexKey
{
    /// <summary>A key named <paramref name="id"/>.</summary>
    /// <param name="id">Its name, which every object sealed under it records: 1 to 255 bytes of UTF-8, and never a secret.</param>
    /// <param name="key">Its 32 bytes, which <see cref="VortexKeyring.FromKeys"/> copies into native memory.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or too long, or <paramref name="key"/> is not 32 bytes.</exception>
    public VortexKey(string id, ReadOnlyMemory<byte> key)
    {
        CheckId(id, nameof(id));
        if (key.Length != SealedFormat.KeyBytes)
        {
            throw new ArgumentException($"A key is {SealedFormat.KeyBytes} bytes, for AES-256; this one is {key.Length}.", nameof(key));
        }

        Id = id;
        Key = key;
    }

    /// <summary>The key's name.</summary>
    public string Id { get; }

    /// <summary>The key's bytes, which stay the caller's.</summary>
    public ReadOnlyMemory<byte> Key { get; }

    internal static void CheckId(string id, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(id, paramName);
        if (Encoding.UTF8.GetByteCount(id) > SealedFormat.MaxKeyIdBytes)
        {
            throw new ArgumentException($"A key id is at most {SealedFormat.MaxKeyIdBytes} bytes of UTF-8.", paramName);
        }
    }
}

/// <summary>
/// Generates data keys and unwraps the ones sealed objects record: a key service such as AWS KMS or
/// Azure Key Vault, or keys the application holds (<see cref="FromKeys"/>).
/// </summary>
/// <remarks>
/// <para>
/// A keyring is called rarely: a session generates one data key for the files it seals, a dataset
/// keeps its own, and an unwrapped key is cached, so no call sits on the path of a scan or of a
/// write. Implementations are thread-safe.
/// </para>
/// <para>
/// The context is what a key service binds the data key to and records in its audit log: a dataset's
/// random id, or nothing for a single file. It never carries a secret, and a sealed object keeps it
/// in clear so that a reader can hand it back.
/// </para>
/// </remarks>
public abstract class VortexKeyring : IDisposable
{
    /// <summary>A keyring.</summary>
    protected VortexKeyring()
    {
    }

    /// <summary>Generates a data key, wrapped by the current key, bound to <paramref name="context"/>.</summary>
    /// <param name="context">What the key is bound to, up to 255 bytes; empty for a single file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The key; the caller disposes it.</returns>
    public abstract ValueTask<DataKey> GenerateAsync(ReadOnlyMemory<byte> context, CancellationToken cancellationToken);

    /// <summary>Unwraps a data key a sealed object records.</summary>
    /// <param name="keyId">The id of the key that wrapped it.</param>
    /// <param name="wrappedKey">The wrapped bytes.</param>
    /// <param name="context">The context it was generated with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The key; the caller disposes it.</returns>
    /// <exception cref="VortexEncryptionException">No key of the ring has that id, or the wrapped bytes do not unwrap under it.</exception>
    public abstract ValueTask<DataKey> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);

    /// <summary>
    /// A keyring over keys the application holds, the first one current: data keys are wrapped with
    /// AES-256-GCM under it, a random nonce per wrap, the key id and the context as associated data.
    /// The others unwrap what they wrapped before, which is how such a keyring rotates.
    /// </summary>
    /// <param name="keys">The keys, the current one first; each id once.</param>
    /// <returns>The keyring; disposing it wipes its copies of the keys.</returns>
    /// <exception cref="ArgumentException">No key is given, or two have the same id.</exception>
    public static VortexKeyring FromKeys(params ReadOnlySpan<VortexKey> keys) => new HeldKeyring(keys);

    /// <summary>Releases what the keyring holds.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the keyring holds: its keys, its clients.</summary>
    /// <param name="disposing">True from <see cref="Dispose()"/>, false from a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>The keyring of <see cref="VortexKeyring.FromKeys"/>.</summary>
internal sealed class HeldKeyring : VortexKeyring
{
    /// <summary>The nonce, the wrapped key, and the tag.</summary>
    private const int WrappedBytes = SealedFormat.NonceBytes + SealedFormat.KeyBytes + SealedFormat.TagBytes;

    private readonly string _current;
    private readonly Dictionary<string, SecretBytes> _keys = new Dictionary<string, SecretBytes>(StringComparer.Ordinal);

    internal HeldKeyring(ReadOnlySpan<VortexKey> keys)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("A keyring needs at least one key, the current one first.", nameof(keys));
        }

        foreach (VortexKey key in keys)
        {
            if (key.Id is null)
            {
                throw new ArgumentException("A key was default-constructed; build each with its id and its bytes.", nameof(keys));
            }

            if (_keys.ContainsKey(key.Id))
            {
                throw new ArgumentException($"Two keys are named '{key.Id}'; a key id names one key.", nameof(keys));
            }

            _keys.Add(key.Id, new SecretBytes(key.Key.Span));
        }

        _current = keys[0].Id;
    }

    public override ValueTask<DataKey> GenerateAsync(ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckContext(context);
        Span<byte> key = stackalloc byte[SealedFormat.KeyBytes];
        Span<byte> wrapped = stackalloc byte[WrappedBytes];
        try
        {
            RandomNumberGenerator.Fill(key);
            RandomNumberGenerator.Fill(wrapped[..SealedFormat.NonceBytes]);
            using (AesGcm aes = new AesGcm(Held(_current).Span, SealedFormat.TagBytes))
            {
                aes.Encrypt(
                    wrapped[..SealedFormat.NonceBytes],
                    key,
                    wrapped.Slice(SealedFormat.NonceBytes, SealedFormat.KeyBytes),
                    wrapped[(SealedFormat.NonceBytes + SealedFormat.KeyBytes)..],
                    AssociatedData(_current, context.Span));
            }

            return new ValueTask<DataKey>(new DataKey(_current, key, wrapped));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public override ValueTask<DataKey> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        cancellationToken.ThrowIfCancellationRequested();
        CheckContext(context);
        if (!_keys.TryGetValue(keyId, out SecretBytes? held))
        {
            throw VortexEncryptionException.NoKey(keyId, $"No key of the keyring is named '{keyId}', the key the object's data key is wrapped with.");
        }

        ReadOnlySpan<byte> wrapped = wrappedKey.Span;
        if (wrapped.Length != WrappedBytes)
        {
            throw VortexEncryptionException.Unauthenticated($"The data key wrapped under '{keyId}' is {wrapped.Length} bytes, where this keyring wraps {WrappedBytes}.");
        }

        Span<byte> key = stackalloc byte[SealedFormat.KeyBytes];
        try
        {
            using (AesGcm aes = new AesGcm(held.Span, SealedFormat.TagBytes))
            {
                aes.Decrypt(
                    wrapped[..SealedFormat.NonceBytes],
                    wrapped.Slice(SealedFormat.NonceBytes, SealedFormat.KeyBytes),
                    wrapped[(SealedFormat.NonceBytes + SealedFormat.KeyBytes)..],
                    key,
                    AssociatedData(keyId, context.Span));
            }

            return new ValueTask<DataKey>(new DataKey(keyId, key, wrapped));
        }
        catch (AuthenticationTagMismatchException mismatch)
        {
            throw VortexEncryptionException.Unauthenticated(
                $"The data key wrapped under '{keyId}' does not unwrap: the key named so is not the one that wrapped it, or the object was altered.",
                mismatch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (SecretBytes key in _keys.Values)
            {
                key.Dispose();
            }
        }
    }

    private SecretBytes Held(string id) => _keys[id];

    private static void CheckContext(ReadOnlyMemory<byte> context)
    {
        if (context.Length > SealedFormat.MaxKeyContextBytes)
        {
            throw new ArgumentException($"A key context is at most {SealedFormat.MaxKeyContextBytes} bytes.", nameof(context));
        }
    }

    /// <summary>The key id's length and bytes, then the context: what a wrap is bound to.</summary>
    private static byte[] AssociatedData(string keyId, ReadOnlySpan<byte> context)
    {
        int idLength = Encoding.UTF8.GetByteCount(keyId);
        byte[] data = new byte[1 + idLength + context.Length];
        data[0] = (byte)idLength;
        Encoding.UTF8.GetBytes(keyId, data.AsSpan(1));
        context.CopyTo(data.AsSpan(1 + idLength));
        return data;
    }
}
