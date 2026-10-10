using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vorticity.Sealing;

/// <summary>
/// The constants of the sealed format and the derivation of its keys: what every writer and reader
/// of a sealed object agrees on, version 1 (docs/design/18-encryption.md).
/// </summary>
/// <remarks>
/// The format version fixes the suite: AES-256-GCM with 96-bit nonces and 128-bit tags, keys derived
/// by HKDF-SHA-256 from the data key and each epoch's salt, and a commitment to that key checked
/// before any frame is decrypted. There is no suite field to edit.
/// </remarks>
internal static class SealedFormat
{
    /// <summary>The first eight bytes of a sealed object.</summary>
    internal static ReadOnlySpan<byte> HeaderMagic => "VXSEALED"u8;

    /// <summary>The last four bytes of a sealed object, where a Vortex file ends with <c>VTXF</c>.</summary>
    internal static ReadOnlySpan<byte> TrailerMagic => "VXSE"u8;

    /// <summary>The only version this build reads and writes.</summary>
    internal const ushort Version = 1;

    /// <summary>The magic, then the descriptor's length as a 32-bit integer.</summary>
    internal const int HeaderPrefixBytes = 12;

    /// <summary>The trailer's length, then its magic.</summary>
    internal const int TrailerSuffixBytes = 8;

    internal const int TagBytes = 16;
    internal const int NonceBytes = 12;
    internal const int KeyBytes = 32;
    internal const int SaltBytes = 32;
    internal const int CommitmentBytes = 32;
    internal const int ObjectIdBytes = 16;
    internal const int BindingBytes = 32;

    /// <summary>4 KiB, the smallest frame.</summary>
    internal const int MinFrameLog2 = 12;

    /// <summary>1 MiB, the largest frame.</summary>
    internal const int MaxFrameLog2 = 20;

    /// <summary>64 KiB, the frame size a writer uses unless told otherwise.</summary>
    internal const int DefaultFrameLog2 = 16;

    internal const int MaxKeyIdBytes = 255;
    internal const int MaxKeyContextBytes = 255;
    internal const int MaxWrappedKeyBytes = 1024;

    /// <summary>The epochs a trailer may list: far past any file's appends, and a bound on what a hostile trailer makes a reader parse.</summary>
    internal const int MaxEpochs = 1 << 16;

    /// <summary>The frames an epoch may hold, within every limit NIST SP 800-38D sets on one key.</summary>
    internal const long MaxFramesPerEpoch = 1L << 32;

    /// <summary>An epoch's entry in the trailer: where its frames start and its plaintext length.</summary>
    internal const int EpochEntryBytes = 16;

    /// <summary>An appended epoch's entry also carries its salt and its commitment.</summary>
    internal const int AppendedEpochEntryBytes = EpochEntryBytes + SaltBytes + CommitmentBytes;

    /// <summary>The bytes an open reads first from the end of a sealed file: a trailer of usual length and two frames of the default size.</summary>
    internal const int OpenReadBytes = 4096 + (2 * ((1 << DefaultFrameLog2) + TagBytes));

    /// <summary>The prefix of every derivation's info.</summary>
    private static ReadOnlySpan<byte> InfoPrefix => "vorticity/sealed/v1"u8;

    private static ReadOnlySpan<byte> KeyLabel => "key"u8;

    private static ReadOnlySpan<byte> CommitLabel => "commit"u8;

    /// <summary>The prefix, the descriptor's hash, the epoch's index and its first plaintext offset.</summary>
    private const int InfoBytes = 19 + 32 + 4 + 8;

    /// <summary>The frames of an epoch of <paramref name="plainLength"/> plaintext bytes: an empty epoch still has one, empty, so that it has a last frame.</summary>
    internal static long FrameCount(long plainLength, int frameSize) =>
        plainLength == 0 ? 1 : ((plainLength - 1) / frameSize) + 1;

    /// <summary>The ciphertext an epoch of <paramref name="plainLength"/> plaintext bytes occupies.</summary>
    internal static long CipherLength(long plainLength, int frameSize) =>
        plainLength + (FrameCount(plainLength, frameSize) * TagBytes);

    /// <summary>The plaintext an epoch whose frames occupy <paramref name="cipherLength"/> bytes holds, or -1 when no epoch occupies exactly that.</summary>
    internal static long PlainLength(long cipherLength, int frameSize)
    {
        if (cipherLength < TagBytes)
        {
            return -1;
        }

        long frames = ((cipherLength - 1) / (frameSize + TagBytes)) + 1;
        long plain = cipherLength - (frames * TagBytes);
        return plain >= 0 && CipherLength(plain, frameSize) == cipherLength ? plain : -1;
    }

    /// <summary>
    /// The length of an object of one epoch holding <paramref name="plainLength"/> bytes, whose
    /// descriptor is <paramref name="descriptorLength"/> bytes: the header, the frames, and a trailer
    /// with the descriptor again and one epoch.
    /// </summary>
    internal static long SealedLength(int descriptorLength, long plainLength, int frameSize) =>
        HeaderPrefixBytes + descriptorLength + CipherLength(plainLength, frameSize) + OneEpochTrailerBytes(descriptorLength);

    /// <summary>The trailer of an object of one epoch: the descriptor, the count, the entry, the length and the magic.</summary>
    internal static int OneEpochTrailerBytes(int descriptorLength) => descriptorLength + 4 + EpochEntryBytes + TrailerSuffixBytes;

    /// <summary>Writes the nonce of frame <paramref name="index"/>: the index as a little-endian 64-bit integer, then the flags, bit 0 marking the epoch's last frame.</summary>
    internal static void Nonce(long index, bool final, Span<byte> nonce)
    {
        BinaryPrimitives.WriteInt64LittleEndian(nonce, index);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], final ? 1u : 0u);
    }

    /// <summary>
    /// Derives the key and the commitment of epoch <paramref name="epoch"/>, whose plaintext starts at
    /// <paramref name="firstOffset"/>, from the data key, the epoch's salt and the descriptor's hash.
    /// </summary>
    internal static void Derive(
        ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> descriptorHash,
        uint epoch, long firstOffset, Span<byte> key, Span<byte> commitment)
    {
        Span<byte> prk = stackalloc byte[32];
        Span<byte> keyInfo = stackalloc byte[6 + InfoBytes];
        Span<byte> commitInfo = stackalloc byte[6 + InfoBytes];
        try
        {
            HKDF.Extract(HashAlgorithmName.SHA256, dataKey, salt, prk);
            int keyInfoLength = Info(KeyLabel, descriptorHash, epoch, firstOffset, keyInfo);
            int commitInfoLength = Info(CommitLabel, descriptorHash, epoch, firstOffset, commitInfo);
            HKDF.Expand(HashAlgorithmName.SHA256, prk, key[..KeyBytes], keyInfo[..keyInfoLength]);
            HKDF.Expand(HashAlgorithmName.SHA256, prk, commitment[..CommitmentBytes], commitInfo[..commitInfoLength]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private static int Info(ReadOnlySpan<byte> label, ReadOnlySpan<byte> descriptorHash, uint epoch, long firstOffset, Span<byte> info)
    {
        label.CopyTo(info);
        int at = label.Length;
        InfoPrefix.CopyTo(info[at..]);
        at += InfoPrefix.Length;
        descriptorHash[..32].CopyTo(info[at..]);
        at += 32;
        BinaryPrimitives.WriteUInt32LittleEndian(info[at..], epoch);
        at += 4;
        BinaryPrimitives.WriteInt64LittleEndian(info[at..], firstOffset);
        return at + 8;
    }
}
