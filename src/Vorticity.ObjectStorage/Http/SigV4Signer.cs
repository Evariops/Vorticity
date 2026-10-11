using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Vorticity.ObjectStorage.Credentials;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// Signature Version 4, as S3 and the clouds that speak its API take it: a SHA-256 of the canonical
/// request, a few hundred bytes, and one HMAC under a key derived once a day for the region and the
/// service.
/// </summary>
/// <remarks>
/// The canonical request is built in a pooled buffer, and the hashes and the HMAC run through the
/// platform's static span methods, so a signature allocates its <c>Authorization</c> header and
/// nothing else once the day's key is derived. A body of data is not hashed: its requests carry
/// <see cref="UnsignedPayload"/>, its integrity left to TLS and a checksum.
/// </remarks>
internal sealed class SigV4Signer
{
    /// <summary>The SHA-256 of nothing, the payload hash of a request without a body.</summary>
    internal const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>The payload hash of a body sent unsigned over TLS.</summary>
    internal const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    private const string Algorithm = "AWS4-HMAC-SHA256";

    private readonly string _region;
    private readonly string _service;
    private DayKey? _day;

    /// <summary>A signer for <paramref name="region"/> and <paramref name="service"/>.</summary>
    internal SigV4Signer(string region, string service = "s3")
    {
        ArgumentException.ThrowIfNullOrEmpty(region);
        _region = region;
        _service = service;
    }

    /// <summary>The region requests are signed for.</summary>
    internal string Region => _region;

    /// <summary><paramref name="utc"/> as <c>x-amz-date</c> writes it: <c>yyyyMMddTHHmmssZ</c>.</summary>
    internal static string AmzDate(DateTimeOffset utc)
    {
        DateTime t = utc.UtcDateTime;
        return string.Create(16, t, static (chars, t) =>
        {
            Digits(chars[..4], t.Year);
            Digits(chars.Slice(4, 2), t.Month);
            Digits(chars.Slice(6, 2), t.Day);
            chars[8] = 'T';
            Digits(chars.Slice(9, 2), t.Hour);
            Digits(chars.Slice(11, 2), t.Minute);
            Digits(chars.Slice(13, 2), t.Second);
            chars[15] = 'Z';
        });
    }

    /// <summary>
    /// The <c>Authorization</c> header of a request.
    /// </summary>
    /// <param name="keys">The keys to sign with.</param>
    /// <param name="amzDate">The request's <c>x-amz-date</c>, which <paramref name="headers"/> carries too.</param>
    /// <param name="method">The method, in upper case.</param>
    /// <param name="canonicalUri">The path exactly as sent, encoded by <see cref="UriEncoding.Path"/>.</param>
    /// <param name="canonicalQuery">The query's pairs, each encoded by <see cref="UriEncoding.Query"/>, sorted by name and joined by <c>&amp;</c>; empty for none.</param>
    /// <param name="headers">The headers signed, their names in lower case: <c>host</c> and every <c>x-amz-</c> one sent, at least.</param>
    /// <param name="payloadHash">The hex SHA-256 of the body, <see cref="EmptyPayloadHash"/> or <see cref="UnsignedPayload"/>.</param>
    /// <returns>The header's value.</returns>
    internal string Authorization(
        AwsKeys keys,
        string amzDate,
        string method,
        string canonicalUri,
        string canonicalQuery,
        ReadOnlySpan<(string Name, string Value)> headers,
        string payloadHash)
    {
        Span<int> order = headers.Length <= 32 ? stackalloc int[headers.Length] : new int[headers.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Insertion sort: a request signs a handful of headers.
        for (int i = 1; i < order.Length; i++)
        {
            int current = order[i];
            int j = i - 1;
            while (j >= 0 && string.CompareOrdinal(headers[order[j]].Name, headers[current].Name) > 0)
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = current;
        }

        Buffer request = new Buffer(1024);
        Buffer toSign = new Buffer(256);
        try
        {
            request.Append(method);
            request.Append('\n');
            request.Append(canonicalUri.Length == 0 ? "/" : canonicalUri);
            request.Append('\n');
            request.Append(canonicalQuery);
            request.Append('\n');
            foreach (int i in order)
            {
                request.Append(headers[i].Name);
                request.Append(':');
                request.AppendHeaderValue(headers[i].Value);
                request.Append('\n');
            }

            request.Append('\n');
            int signedStart = request.Length;
            for (int k = 0; k < order.Length; k++)
            {
                if (k > 0)
                {
                    request.Append(';');
                }

                request.Append(headers[order[k]].Name);
            }

            int signedEnd = request.Length;
            request.Append('\n');
            request.Append(payloadHash);

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(request.Written, hash);

            toSign.Append(Algorithm);
            toSign.Append('\n');
            toSign.Append(amzDate);
            toSign.Append('\n');
            int scopeStart = toSign.Length;
            toSign.Append(amzDate.AsSpan(0, 8));
            toSign.Append('/');
            toSign.Append(_region);
            toSign.Append('/');
            toSign.Append(_service);
            toSign.Append("/aws4_request");
            int scopeEnd = toSign.Length;
            toSign.Append('\n');
            toSign.AppendHex(hash);

            Span<byte> signature = stackalloc byte[32];
            HMACSHA256.HashData(DayKeyFor(keys, amzDate), toSign.Written, signature);

            string scope = Encoding.ASCII.GetString(toSign.Written[scopeStart..scopeEnd]);
            string signed = Encoding.ASCII.GetString(request.Written[signedStart..signedEnd]);
            return $"{Algorithm} Credential={keys.AccessKeyId}/{scope}, SignedHeaders={signed}, Signature={Convert.ToHexStringLower(signature)}";
        }
        finally
        {
            request.Dispose();
            toSign.Dispose();
        }
    }

    /// <summary>The day's signing key for <paramref name="keys"/>, derived on the day's first request.</summary>
    private byte[] DayKeyFor(AwsKeys keys, string amzDate)
    {
        DayKey? day = Volatile.Read(ref _day);
        if (day is not null && ReferenceEquals(day.Keys, keys) && amzDate.AsSpan(0, 8).SequenceEqual(day.Date))
        {
            return day.Key;
        }

        Span<byte> key = stackalloc byte[32];
        Span<byte> text = stackalloc byte[64];
        HMACSHA256.HashData(keys.SigningSecret.Span, text[..Encoding.ASCII.GetBytes(amzDate.AsSpan(0, 8), text)], key);
        HMACSHA256.HashData(key, text[..Encoding.UTF8.GetBytes(_region, text)], key);
        HMACSHA256.HashData(key, text[..Encoding.UTF8.GetBytes(_service, text)], key);
        HMACSHA256.HashData(key, "aws4_request"u8, key);
        DayKey derived = new DayKey(keys, amzDate[..8], key.ToArray());
        CryptographicOperations.ZeroMemory(key);
        Volatile.Write(ref _day, derived);
        return derived.Key;
    }

    private static void Digits(Span<char> destination, int value)
    {
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (char)('0' + (value % 10));
            value /= 10;
        }
    }

    /// <summary>A day's key, for one set of keys.</summary>
    private sealed record DayKey(AwsKeys Keys, string Date, byte[] Key);

    /// <summary>ASCII text in a pooled array.</summary>
    private struct Buffer(int capacity) : IDisposable
    {
        private byte[] _bytes = ArrayPool<byte>.Shared.Rent(capacity);

        internal int Length { get; private set; }

        internal readonly ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

        internal void Append(char c)
        {
            Ensure(1);
            _bytes[Length++] = (byte)c;
        }

        internal void Append(ReadOnlySpan<char> text)
        {
            Ensure(Encoding.UTF8.GetMaxByteCount(text.Length));
            Length += Encoding.UTF8.GetBytes(text, _bytes.AsSpan(Length));
        }

        /// <summary>A header's value as the canonical request takes it: trimmed, its runs of spaces made one.</summary>
        internal void AppendHeaderValue(string value)
        {
            ReadOnlySpan<char> rest = value.AsSpan().Trim(' ');
            while (!rest.IsEmpty)
            {
                int space = rest.IndexOf(' ');
                if (space < 0)
                {
                    Append(rest);
                    return;
                }

                Append(rest[..space]);
                Append(' ');
                rest = rest[space..].TrimStart(' ');
            }
        }

        internal void AppendHex(ReadOnlySpan<byte> bytes)
        {
            Ensure(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                _bytes[Length++] = (byte)"0123456789abcdef"[b >> 4];
                _bytes[Length++] = (byte)"0123456789abcdef"[b & 0xF];
            }
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = [];
        }

        private void Ensure(int more)
        {
            if (Length + more > _bytes.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_bytes.Length * 2, Length + more));
                _bytes.AsSpan(0, Length).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(_bytes);
                _bytes = larger;
            }
        }
    }
}
