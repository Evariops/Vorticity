using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Vorticity.Sealing;

/// <summary>
/// Key material in native memory: the garbage collector never moves or copies it, and it is wiped
/// before the memory goes back, by <see cref="Dispose"/> or, for a key nobody disposed, the finalizer.
/// </summary>
internal sealed unsafe class SecretBytes : IDisposable
{
    private nint _bytes;

    /// <summary>A copy of <paramref name="secret"/>.</summary>
    internal SecretBytes(ReadOnlySpan<byte> secret)
    {
        Length = secret.Length;
        byte* bytes = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(secret.Length, 1));
        secret.CopyTo(new Span<byte>(bytes, secret.Length));
        _bytes = (nint)bytes;
    }

    ~SecretBytes() => Free();

    /// <summary>How many bytes the secret holds.</summary>
    internal int Length { get; }

    /// <summary>The secret, valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The secret was wiped.</exception>
    internal ReadOnlySpan<byte> Span
    {
        get
        {
            nint bytes = Volatile.Read(ref _bytes);
            ObjectDisposedException.ThrowIf(bytes == 0, this);
            return new ReadOnlySpan<byte>((byte*)bytes, Length);
        }
    }

    /// <summary>Wipes the secret and frees its memory. Idempotent.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        nint bytes = Interlocked.Exchange(ref _bytes, 0);
        if (bytes != 0)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((byte*)bytes, Length));
            NativeMemory.Free((byte*)bytes);
        }
    }
}
