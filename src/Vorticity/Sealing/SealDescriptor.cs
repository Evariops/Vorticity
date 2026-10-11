using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vorticity.Sealing;

/// <summary>
/// What a sealed object says about itself in clear, at its head and again in its trailer: the
/// format version, the frame size, the ids that bind it, the key that wraps its data key and that
/// key wrapped, the first epoch's salt, and the commitment to the first epoch's key.
/// </summary>
/// <remarks>
/// <para>
/// Little-endian throughout: the version (16 bits), the frame size's base-2 logarithm (8), the
/// object id (16 bytes), the binding (32), the key id's length (8) and its UTF-8, the key context's
/// length (8) and its bytes, the wrapped key's length (16) and its bytes, the salt (32), and the
/// commitment (32), last so that the hash taken without it is the descriptor's first bytes.
/// </para>
/// <para>
/// Nothing here carries a tag. The hash of these bytes, commitment aside, enters the derivation of
/// every key, so a descriptor changed in one bit derives other keys, and the commitment refuses
/// them before a frame is decrypted.
/// </para>
/// </remarks>
internal sealed class SealDescriptor
{
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The fixed fields: everything but the key id, the context and the wrapped key.</summary>
    private const int FixedBytes = 2 + 1 + SealedFormat.ObjectIdBytes + SealedFormat.BindingBytes + 1 + 1 + 2
        + SealedFormat.SaltBytes + SealedFormat.CommitmentBytes;

    private SealDescriptor(byte[] bytes, int frameLog2, string keyId, int keyIdAt, int keyIdLength, int contextAt, int contextLength, int wrappedAt, int wrappedLength)
    {
        Bytes = bytes;
        FrameLog2 = frameLog2;
        KeyId = keyId;
        _keyIdAt = keyIdAt;
        _keyIdLength = keyIdLength;
        _contextAt = contextAt;
        _contextLength = contextLength;
        _wrappedAt = wrappedAt;
        _wrappedLength = wrappedLength;
        Hash = SHA256.HashData(bytes.AsSpan(0, bytes.Length - SealedFormat.CommitmentBytes));
    }

    private readonly int _keyIdAt;
    private readonly int _keyIdLength;
    private readonly int _contextAt;
    private readonly int _contextLength;
    private readonly int _wrappedAt;
    private readonly int _wrappedLength;

    /// <summary>The encoded descriptor, as both ends of the object carry it.</summary>
    internal byte[] Bytes { get; }

    /// <summary>The SHA-256 of <see cref="Bytes"/> without the commitment, which every key's derivation takes.</summary>
    internal byte[] Hash { get; }

    /// <summary>The frame size's base-2 logarithm.</summary>
    internal int FrameLog2 { get; }

    /// <summary>The frame size in bytes.</summary>
    internal int FrameSize => 1 << FrameLog2;

    /// <summary>The keyring's name for the key that wraps the data key.</summary>
    internal string KeyId { get; }

    internal ReadOnlySpan<byte> ObjectId => Bytes.AsSpan(3, SealedFormat.ObjectIdBytes);

    internal ReadOnlySpan<byte> Binding => Bytes.AsSpan(3 + SealedFormat.ObjectIdBytes, SealedFormat.BindingBytes);

    internal ReadOnlyMemory<byte> KeyIdBytes => Bytes.AsMemory(_keyIdAt, _keyIdLength);

    /// <summary>What the keyring passed to the key service with the data key, which unwrapping it needs back.</summary>
    internal ReadOnlyMemory<byte> KeyContext => Bytes.AsMemory(_contextAt, _contextLength);

    /// <summary>The data key as the keyring wrapped it.</summary>
    internal ReadOnlyMemory<byte> WrappedKey => Bytes.AsMemory(_wrappedAt, _wrappedLength);

    /// <summary>The first epoch's salt.</summary>
    internal ReadOnlySpan<byte> Salt => Bytes.AsSpan(_wrappedAt + _wrappedLength, SealedFormat.SaltBytes);

    /// <summary>The commitment to the first epoch's key.</summary>
    internal ReadOnlySpan<byte> Commitment => Bytes.AsSpan(Bytes.Length - SealedFormat.CommitmentBytes);

    /// <summary>The descriptor's length in bytes.</summary>
    internal int Length => Bytes.Length;

    /// <summary>The length of a descriptor naming <paramref name="keyId"/>, a context of <paramref name="contextLength"/> bytes and a wrapped key of <paramref name="wrappedLength"/>.</summary>
    internal static int LengthOf(string keyId, int contextLength, int wrappedLength) =>
        FixedBytes + StrictUtf8.GetByteCount(keyId) + contextLength + wrappedLength;

    /// <summary>A descriptor whose commitment is still zeros; <see cref="SetCommitment"/> fills it once the hash has derived the first key.</summary>
    /// <exception cref="ArgumentException">A field is longer than the format allows.</exception>
    internal static SealDescriptor Create(
        int frameLog2, ReadOnlySpan<byte> objectId, ReadOnlySpan<byte> binding, string keyId,
        ReadOnlySpan<byte> keyContext, ReadOnlySpan<byte> wrappedKey, ReadOnlySpan<byte> salt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameLog2, SealedFormat.MinFrameLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameLog2, SealedFormat.MaxFrameLog2);
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        int keyIdLength = StrictUtf8.GetByteCount(keyId);
        if (keyIdLength > SealedFormat.MaxKeyIdBytes)
        {
            throw new ArgumentException($"A key id is at most {SealedFormat.MaxKeyIdBytes} bytes of UTF-8; '{keyId}' takes {keyIdLength}.", nameof(keyId));
        }

        if (keyContext.Length > SealedFormat.MaxKeyContextBytes)
        {
            throw new ArgumentException($"A key context is at most {SealedFormat.MaxKeyContextBytes} bytes.", nameof(keyContext));
        }

        if (wrappedKey.IsEmpty || wrappedKey.Length > SealedFormat.MaxWrappedKeyBytes)
        {
            throw new ArgumentException($"A wrapped data key is 1 to {SealedFormat.MaxWrappedKeyBytes} bytes; this one is {wrappedKey.Length}.", nameof(wrappedKey));
        }

        byte[] bytes = new byte[FixedBytes + keyIdLength + keyContext.Length + wrappedKey.Length];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt16LittleEndian(span, SealedFormat.Version);
        span[2] = (byte)frameLog2;
        objectId[..SealedFormat.ObjectIdBytes].CopyTo(span[3..]);
        binding[..SealedFormat.BindingBytes].CopyTo(span[(3 + SealedFormat.ObjectIdBytes)..]);
        int at = 3 + SealedFormat.ObjectIdBytes + SealedFormat.BindingBytes;
        span[at++] = (byte)keyIdLength;
        int keyIdAt = at;
        at += StrictUtf8.GetBytes(keyId, span[at..]);
        span[at++] = (byte)keyContext.Length;
        int contextAt = at;
        keyContext.CopyTo(span[at..]);
        at += keyContext.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)wrappedKey.Length);
        at += 2;
        int wrappedAt = at;
        wrappedKey.CopyTo(span[at..]);
        at += wrappedKey.Length;
        salt[..SealedFormat.SaltBytes].CopyTo(span[at..]);
        return new SealDescriptor(bytes, frameLog2, keyId, keyIdAt, keyIdLength, contextAt, keyContext.Length, wrappedAt, wrappedKey.Length);
    }

    /// <summary>Writes the commitment, the one field the hash leaves out.</summary>
    internal void SetCommitment(ReadOnlySpan<byte> commitment) =>
        commitment[..SealedFormat.CommitmentBytes].CopyTo(Bytes.AsSpan(Bytes.Length - SealedFormat.CommitmentBytes));

    /// <summary>Reads a descriptor from the start of <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The descriptor and possibly what follows it.</param>
    /// <param name="consumed">How many bytes the descriptor took.</param>
    /// <returns>The descriptor.</returns>
    /// <exception cref="VortexFormatException">The bytes are not a descriptor.</exception>
    /// <exception cref="VortexUnsupportedException">The descriptor is of a version this build does not know.</exception>
    internal static SealDescriptor Read(ReadOnlySpan<byte> bytes, out int consumed)
    {
        if (bytes.Length < FixedBytes + 2)
        {
            throw Malformed($"{bytes.Length} bytes are too few for a descriptor");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (version != SealedFormat.Version)
        {
            throw new VortexUnsupportedException(
                string.Create(CultureInfo.InvariantCulture, $"vorticity.sealed.v{version}"),
                ComponentKind.Encryption,
                $"This build reads sealed objects of version {SealedFormat.Version} only.");
        }

        int frameLog2 = bytes[2];
        if (frameLog2 is < SealedFormat.MinFrameLog2 or > SealedFormat.MaxFrameLog2)
        {
            throw Malformed($"a frame of 2^{frameLog2} bytes is outside 2^{SealedFormat.MinFrameLog2} to 2^{SealedFormat.MaxFrameLog2}");
        }

        int at = 3 + SealedFormat.ObjectIdBytes + SealedFormat.BindingBytes;
        int keyIdLength = bytes[at++];
        if (keyIdLength == 0)
        {
            throw Malformed("its key id is empty");
        }

        int keyIdAt = at;
        at += keyIdLength;
        if (at + 1 > bytes.Length)
        {
            throw Malformed("its key id runs past its end");
        }

        int contextLength = bytes[at++];
        int contextAt = at;
        at += contextLength;
        if (at + 2 > bytes.Length)
        {
            throw Malformed("its key context runs past its end");
        }

        int wrappedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        at += 2;
        if (wrappedLength is 0 or > SealedFormat.MaxWrappedKeyBytes)
        {
            throw Malformed($"its wrapped key is {wrappedLength} bytes, outside 1 to {SealedFormat.MaxWrappedKeyBytes}");
        }

        int wrappedAt = at;
        at += wrappedLength + SealedFormat.SaltBytes + SealedFormat.CommitmentBytes;
        if (at > bytes.Length)
        {
            throw Malformed("its wrapped key, salt or commitment runs past its end");
        }

        string keyId;
        try
        {
            keyId = StrictUtf8.GetString(bytes.Slice(keyIdAt, keyIdLength));
        }
        catch (DecoderFallbackException invalid)
        {
            throw new VortexFormatException("Malformed sealed object: its key id is not UTF-8.", invalid);
        }

        consumed = at;
        return new SealDescriptor(bytes[..at].ToArray(), frameLog2, keyId, keyIdAt, keyIdLength, contextAt, contextLength, wrappedAt, wrappedLength);
    }

    /// <summary>Whether <paramref name="other"/> holds exactly these bytes, as the two copies of one object must.</summary>
    internal bool SameAs(ReadOnlySpan<byte> other) => Bytes.AsSpan().SequenceEqual(other);

    private static VortexFormatException Malformed(string why) => new VortexFormatException($"Malformed sealed object: {why}.");
}
