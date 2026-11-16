using System;
using System.Security.Cryptography;
using System.Threading;

namespace Vorticity.Sealing;

/// <summary>
/// Key material in a managed array the runtime pins for its whole life: the collector never moves it, so it leaves no
/// copy of the key behind in the heap, and the array is wiped before the collector takes it back, by
/// <see cref="Dispose"/> or, for a key nobody disposed, by the finalizer.
/// </summary>
/// <remarks>
/// The finalizer frees no memory, which the collector takes back alone: it only wipes a key its holder dropped, which
/// would otherwise lie in the heap's free space until something else was allocated over it.
/// </remarks>
internal sealed class SecretBytes : IDisposable
{
    private byte[]? _bytes;

    /// <summary>A copy of <paramref name="secret"/>.</summary>
    internal SecretBytes(ReadOnlySpan<byte> secret)
    {
        Length = secret.Length;
        byte[] bytes = GC.AllocateArray<byte>(Math.Max(secret.Length, 1), pinned: true);
        secret.CopyTo(bytes);
        _bytes = bytes;
    }

    ~SecretBytes() => Wipe();

    /// <summary>How many bytes the secret holds.</summary>
    internal int Length { get; }

    /// <summary>The secret, valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The secret was wiped.</exception>
    internal ReadOnlySpan<byte> Span
    {
        get
        {
            byte[]? bytes = Volatile.Read(ref _bytes);
            ObjectDisposedException.ThrowIf(bytes is null, this);
            return new ReadOnlySpan<byte>(bytes, 0, Length);
        }
    }

    /// <summary>Wipes the secret and lets its array go to the collector. Idempotent.</summary>
    public void Dispose()
    {
        Wipe();
        GC.SuppressFinalize(this);
    }

    private void Wipe()
    {
        if (Interlocked.Exchange(ref _bytes, null) is { } bytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
