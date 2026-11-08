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

    /// <summary>
    /// Whether <paramref name="utf8"/> holds <paramref name="text"/> in UTF-8, neither made into the other on the heap: an
    /// ASCII text compared byte for byte, any other encoded on the stack, or in a rented buffer past 256 bytes.
    /// </summary>
    internal static bool SameText(ReadOnlySpan<byte> utf8, string text)
    {
        if (System.Text.Ascii.IsValid(text))
        {
            return System.Text.Ascii.Equals(utf8, text);
        }

        int count = System.Text.Encoding.UTF8.GetByteCount(text);
        if (count != utf8.Length)
        {
            return false;
        }

        byte[]? rented = count > 256 ? System.Buffers.ArrayPool<byte>.Shared.Rent(count) : null;
        try
        {
            Span<byte> encoded = rented is null ? stackalloc byte[256] : rented;
            int written = System.Text.Encoding.UTF8.GetBytes(text, encoded);
            return encoded[..written].SequenceEqual(utf8);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
