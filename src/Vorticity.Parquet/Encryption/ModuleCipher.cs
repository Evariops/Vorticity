using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vorticity.Parquet.Encryption;

/// <summary>The kinds of module the standard's AAD suffix names, by their byte.</summary>
internal enum ModuleType : byte
{
    Footer = 0,
    ColumnMetaData = 1,
    DataPage = 2,
    DictionaryPage = 3,
    DataPageHeader = 4,
    DictionaryPageHeader = 5,
    ColumnIndex = 6,
    OffsetIndex = 7,
    BloomFilterHeader = 8,
    BloomFilterBitset = 9,
}

/// <summary>
/// The ciphers of one key: AES-GCM for every module, and AES in counter mode for the pages of a file
/// encrypted <c>AES_GCM_CTR_V1</c>, each module as the standard serializes it.
/// </summary>
/// <remarks>
/// <para>
/// A GCM module is its length, four bytes little-endian, then a 12-byte nonce, the ciphertext and a
/// 16-byte tag; a CTR module the same without the tag. The length counts what follows it.
/// </para>
/// <para>
/// Counter mode is AES applied to counter blocks, a block of keystream each, XORed into the data:
/// the IV is the nonce then a 32-bit big-endian counter that starts at 1. .NET has no counter mode of
/// its own, so a run of counter blocks is encrypted in one ECB call, which AES-NI pipelines, and the
/// keystream XORed in a vector at a time.
/// </para>
/// <para>
/// One instance is used by one thread at a time: a column's reader holds its own.
/// </para>
/// </remarks>
internal sealed class ModuleCipher : IDisposable
{
    internal const int LengthBytes = 4;
    internal const int NonceLength = 12;
    internal const int TagLength = 16;

    /// <summary>The bytes a GCM module adds to its plaintext: its length, its nonce and its tag.</summary>
    internal const int GcmOverhead = LengthBytes + NonceLength + TagLength;

    /// <summary>The bytes a CTR module adds to its plaintext: its length and its nonce.</summary>
    internal const int CtrOverhead = LengthBytes + NonceLength;

    /// <summary>Counter blocks encrypted a call: 4 KiB of keystream.</summary>
    private const int Blocks = 256;

    private const int Block = 16;

    private readonly AesGcm _gcm;
    private readonly byte[] _key;
    private Aes? _aes;
    private byte[]? _counters;
    private byte[]? _stream;

    /// <summary>The ciphers of <paramref name="key"/>: 16, 24 or 32 bytes.</summary>
    /// <exception cref="ParquetUnsupportedException">The key is of another length.</exception>
    internal ModuleCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ParquetUnsupportedException("key", ParquetComponentKind.Encryption,
                $"A key of {key.Length} bytes is given; AES takes 16, 24 or 32.");
        }

        _key = key.ToArray();
        _gcm = new AesGcm(_key, TagLength);
    }

    /// <summary>The length a module at the start of <paramref name="bytes"/> declares, and the bytes it takes with it.</summary>
    /// <exception cref="ParquetFormatException">The module is cut short, or its length is less than <paramref name="overhead"/> allows.</exception>
    internal static int ModuleBytes(ReadOnlySpan<byte> bytes, int overhead)
    {
        if (bytes.Length < LengthBytes)
        {
            ParquetThrow.Format("An encrypted module is cut short of its length.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (length < (uint)(overhead - LengthBytes) || length > (uint)(bytes.Length - LengthBytes))
        {
            ParquetThrow.Format($"An encrypted module declares {length} bytes where {bytes.Length - LengthBytes} remain.");
        }

        return LengthBytes + (int)length;
    }

    /// <summary>Decrypts the GCM module that starts <paramref name="module"/> into <paramref name="plaintext"/>, its ciphertext's length.</summary>
    /// <exception cref="ParquetFormatException">The module fails its authentication: altered, or not under this key and AAD.</exception>
    internal void Decrypt(ReadOnlySpan<byte> module, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        ReadOnlySpan<byte> nonce = module.Slice(LengthBytes, NonceLength);
        ReadOnlySpan<byte> ciphertext = module.Slice(LengthBytes + NonceLength, plaintext.Length);
        ReadOnlySpan<byte> tag = module.Slice(LengthBytes + NonceLength + plaintext.Length, TagLength);
        try
        {
            _gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            ParquetThrow.Format("An encrypted module fails its authentication: the file was altered, or the key or the AAD prefix is not its own.");
        }
    }

    /// <summary>Encrypts <paramref name="plaintext"/> as a GCM module into <paramref name="module"/>, of <see cref="GcmOverhead"/> bytes more, under a fresh nonce.</summary>
    internal void Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, Span<byte> module)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(module, (uint)(plaintext.Length + NonceLength + TagLength));
        Span<byte> nonce = module.Slice(LengthBytes, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        _gcm.Encrypt(nonce, plaintext, module.Slice(LengthBytes + NonceLength, plaintext.Length), module.Slice(LengthBytes + NonceLength + plaintext.Length, TagLength), aad);
    }

    /// <summary>
    /// The tag GCM gives <paramref name="plaintext"/> under <paramref name="nonce"/> and
    /// <paramref name="aad"/>: what signs a plaintext footer, whose ciphertext is not kept.
    /// </summary>
    internal void Sign(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> tag)
    {
        byte[] scratch = new byte[plaintext.Length];
        _gcm.Encrypt(nonce, plaintext, scratch, tag, aad);
    }

    /// <summary>Decrypts the CTR module that starts <paramref name="module"/> into <paramref name="plaintext"/>, its ciphertext's length.</summary>
    internal void DecryptCtr(ReadOnlySpan<byte> module, Span<byte> plaintext) =>
        Ctr(module.Slice(LengthBytes, NonceLength), module.Slice(CtrOverhead, plaintext.Length), plaintext);

    /// <summary>Encrypts <paramref name="plaintext"/> as a CTR module into <paramref name="module"/>, of <see cref="CtrOverhead"/> bytes more, under a fresh nonce.</summary>
    internal void EncryptCtr(ReadOnlySpan<byte> plaintext, Span<byte> module)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(module, (uint)(plaintext.Length + NonceLength));
        Span<byte> nonce = module.Slice(LengthBytes, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        Ctr(nonce, plaintext, module.Slice(CtrOverhead, plaintext.Length));
    }

    /// <summary>XORs <paramref name="input"/> with the keystream of <paramref name="nonce"/> into <paramref name="output"/>.</summary>
    private void Ctr(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Aes aes = _aes ??= Create(_key);
        byte[] counters = _counters ??= new byte[Blocks * Block];
        byte[] stream = _stream ??= new byte[Blocks * Block];
        uint counter = 1;
        for (int at = 0; at < input.Length; at += counters.Length)
        {
            int length = Math.Min(input.Length - at, counters.Length);
            int blocks = (length + Block - 1) / Block;
            for (int b = 0; b < blocks; b++)
            {
                Span<byte> block = counters.AsSpan(b * Block, Block);
                nonce.CopyTo(block);
                BinaryPrimitives.WriteUInt32BigEndian(block[NonceLength..], counter++);
            }

            aes.EncryptEcb(counters.AsSpan(0, blocks * Block), stream.AsSpan(0, blocks * Block), PaddingMode.None);
            Xor(input.Slice(at, length), stream.AsSpan(0, length), output.Slice(at, length));
        }
    }

    private static void Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> into)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            ReadOnlySpan<Vector<byte>> a = MemoryMarshal.Cast<byte, Vector<byte>>(left);
            ReadOnlySpan<Vector<byte>> b = MemoryMarshal.Cast<byte, Vector<byte>>(right);
            Span<Vector<byte>> c = MemoryMarshal.Cast<byte, Vector<byte>>(into);
            for (int v = 0; v < c.Length; v++)
            {
                c[v] = a[v] ^ b[v];
            }

            i = c.Length * Vector<byte>.Count;
        }

        for (; i < into.Length; i++)
        {
            into[i] = (byte)(left[i] ^ right[i]);
        }
    }

    private static Aes Create(byte[] key)
    {
        Aes aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    public void Dispose()
    {
        _gcm.Dispose();
        _aes?.Dispose();
    }
}

/// <summary>
/// A module's AAD: the file's prefix, its unique part, the module's type, and the row group, column
/// and page ordinals the type takes, each two bytes little-endian.
/// </summary>
internal static class ModuleAad
{
    /// <summary>The most bytes an AAD's suffix past the file's own parts takes.</summary>
    internal const int SuffixBytes = 7;

    /// <summary>
    /// Writes the AAD of a module of <paramref name="type"/> into <paramref name="into"/>: the row
    /// group and column ordinals unless the module is the footer, the page ordinal for a data page and
    /// its header. An ordinal is cut to its low 16 bits, as the standard's 2-byte short.
    /// </summary>
    internal static int Write(Span<byte> into, ReadOnlySpan<byte> file, ModuleType type, int rowGroup, int column, int page)
    {
        file.CopyTo(into);
        int at = file.Length;
        into[at++] = (byte)type;
        if (type == ModuleType.Footer)
        {
            return at;
        }

        BinaryPrimitives.WriteInt16LittleEndian(into[at..], unchecked((short)rowGroup));
        BinaryPrimitives.WriteInt16LittleEndian(into[(at + 2)..], unchecked((short)column));
        at += 4;
        if (type is ModuleType.DataPage or ModuleType.DataPageHeader)
        {
            BinaryPrimitives.WriteInt16LittleEndian(into[at..], unchecked((short)page));
            at += 2;
        }

        return at;
    }
}
