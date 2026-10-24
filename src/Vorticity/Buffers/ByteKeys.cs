using System;
using System.Collections.Generic;

namespace Vorticity.Buffers;

/// <summary>Keys equal by their bytes, looked up by a span of them as well as by an array.</summary>
internal sealed class ByteKeys : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    internal static ByteKeys Instance { get; } = new ByteKeys();

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        HashCode hash = default;
        hash.AddBytes(alternate);
        return hash.ToHashCode();
    }

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
}
