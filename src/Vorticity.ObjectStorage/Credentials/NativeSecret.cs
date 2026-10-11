using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// A secret's bytes in native memory, wiped when disposed. The string the secret first came in, from
/// an environment variable or a file, stays on the managed heap until it is collected, which no .NET
/// code can prevent: this copy narrows the window rather than closing it.
/// </summary>
internal sealed unsafe class NativeSecret : IDisposable
{
    private nint _bytes;
    private readonly int _length;

    /// <summary>The UTF-8 of <paramref name="prefix"/> then <paramref name="secret"/>.</summary>
    internal NativeSecret(string prefix, string secret)
    {
        _length = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(secret);
        _bytes = (nint)NativeMemory.Alloc((nuint)Math.Max(_length, 1));
        Span<byte> span = new Span<byte>((void*)_bytes, _length);
        int written = Encoding.UTF8.GetBytes(prefix, span);
        Encoding.UTF8.GetBytes(secret, span[written..]);
    }

    /// <summary>A copy of <paramref name="bytes"/>.</summary>
    internal NativeSecret(ReadOnlySpan<byte> bytes)
    {
        _length = bytes.Length;
        _bytes = (nint)NativeMemory.Alloc((nuint)Math.Max(_length, 1));
        bytes.CopyTo(new Span<byte>((void*)_bytes, _length));
    }

    ~NativeSecret() => Free();

    /// <summary>The bytes, valid until disposal.</summary>
    /// <exception cref="ObjectDisposedException">The secret was wiped.</exception>
    internal ReadOnlySpan<byte> Span
    {
        get
        {
            nint bytes = Volatile.Read(ref _bytes);
            ObjectDisposedException.ThrowIf(bytes == 0, this);
            return new ReadOnlySpan<byte>((void*)bytes, _length);
        }
    }

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
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)bytes, _length));
            NativeMemory.Free((void*)bytes);
        }
    }
}
