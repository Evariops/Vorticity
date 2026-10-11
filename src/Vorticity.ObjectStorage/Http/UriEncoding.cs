using System;
using System.Buffers;
using System.Text;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The percent-encoding the services sign and route by: every byte of a key's UTF-8 but the unreserved
/// ones (<c>A-Z a-z 0-9 - . _ ~</c>) as <c>%XY</c> in upper case, and <c>/</c> kept in a path. The
/// request carries the path exactly so, which is what makes the signed path and the sent one the same.
/// </summary>
internal static class UriEncoding
{
    private static readonly SearchValues<byte> Unreserved =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"u8);

    /// <summary>A key or a path encoded for a URI's path: <c>/</c> kept.</summary>
    internal static string Path(string value) => Encode(value, keepSlash: true);

    /// <summary>A name or a value encoded for a query: <c>/</c> encoded too.</summary>
    internal static string Query(string value) => Encode(value, keepSlash: false);

    /// <summary>
    /// The decoding of a key a listing returned URL-encoded (<c>encoding-type=url</c>): <c>%XY</c> as
    /// its byte and <c>+</c> as a space, the bytes then read as UTF-8.
    /// </summary>
    internal static string DecodeForm(string value)
    {
        if (value.AsSpan().IndexOfAny('%', '+') < 0)
        {
            return value;
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            int written = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '+')
                {
                    bytes[written++] = (byte)' ';
                }
                else if (c == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
                {
                    bytes[written++] = (byte)((Hex(value[i + 1]) << 4) | Hex(value[i + 2]));
                    i += 2;
                }
                else if (c < 0x80)
                {
                    bytes[written++] = (byte)c;
                }
                else
                {
                    written += Encoding.UTF8.GetBytes(value.AsSpan(i, char.IsHighSurrogate(c) && i + 1 < value.Length ? 2 : 1), bytes.AsSpan(written));
                    i += char.IsHighSurrogate(c) && i + 1 < value.Length ? 1 : 0;
                }
            }

            return Encoding.UTF8.GetString(bytes, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static string Encode(string value, bool keepSlash)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[] utf8 = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int length = Encoding.UTF8.GetBytes(value, utf8);
            ReadOnlySpan<byte> bytes = utf8.AsSpan(0, length);
            int encoded = 0;
            foreach (byte b in bytes)
            {
                encoded += Unreserved.Contains(b) || (keepSlash && b == (byte)'/') ? 1 : 3;
            }

            if (encoded == length && length == value.Length)
            {
                return value;
            }

            return string.Create(encoded, (utf8, length, keepSlash), static (chars, state) =>
            {
                int at = 0;
                foreach (byte b in state.utf8.AsSpan(0, state.length))
                {
                    if (Unreserved.Contains(b) || (state.keepSlash && b == (byte)'/'))
                    {
                        chars[at++] = (char)b;
                    }
                    else
                    {
                        chars[at++] = '%';
                        chars[at++] = "0123456789ABCDEF"[b >> 4];
                        chars[at++] = "0123456789ABCDEF"[b & 0xF];
                    }
                }
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    private static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
