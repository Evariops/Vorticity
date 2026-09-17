// What a ranged read hands back - docs/13-dataset.md §11, "GetRange(key, offset, length) -> bytes
// + token".
//
// THE TOKEN TRAVELS WITH THE BYTES, and that is the point of the type. §7 binds what a reader
// believes about an object to the object's identity, and a reader that got the bytes in one call
// and the token in another has no proof the two describe the same object. One call, one answer.
//
// THE BYTES ARE RENTED. A store's reads are the hot path of every open and every lookup, and a
// range is copied out of the store's own storage anyway; renting means a reader that disposes what
// it is given -- which the contract requires -- costs no allocation per request.
using System;
using System.Buffers;

namespace Vorticity.Dataset;

/// <summary>A byte range of an object, with the object's token.</summary>
public sealed class ObjectRange : IDisposable
{
    private byte[]? _rented;
    private readonly int _length;

    /// <summary>Wraps bytes the caller owns and will not reuse.</summary>
    /// <param name="bytes">The rented buffer holding the range.</param>
    /// <param name="length">How many of its bytes are the range.</param>
    /// <param name="token">The object's token.</param>
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
    /// <param name="source">The bytes to copy.</param>
    /// <param name="token">The object's token.</param>
    /// <returns>The range.</returns>
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
