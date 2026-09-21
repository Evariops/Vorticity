using System;
using System.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// A byte range of an object, with the object's token: bytes fetched in one call and a token
/// fetched in another would be no proof that the two describe the same object. The buffer is
/// rented, so a reader that disposes what it is given costs no allocation per request.
/// </summary>
internal sealed class ObjectRange : IDisposable
{
    private byte[]? _rented;
    private readonly int _length;

    private ObjectRange(byte[] bytes, int length, string token)
    {
        _rented = bytes;
        _length = length;
        Token = token;
    }

    /// <summary>The object's token, as <see cref="ObjectHead.Token"/> defines it.</summary>
    public string Token { get; }

    /// <summary>The bytes. Valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The range was disposed.</exception>
    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_rented is null, this);
            return _rented.AsMemory(0, _length);
        }
    }

    /// <summary>The bytes' count, which stays readable after disposal.</summary>
    public int Length => _length;

    /// <summary>Copies <paramref name="source"/> into a rented buffer.</summary>
    public static ObjectRange CopyOf(ReadOnlySpan<byte> source, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(source.Length, 1));
        source.CopyTo(rented);
        return new ObjectRange(rented, source.Length, token);
    }

    /// <summary>Returns the buffer to the pool. Idempotent.</summary>
    public void Dispose()
    {
        if (_rented is { } rented)
        {
            _rented = null;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
