using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Encryption;

/// <summary>
/// A file's encryption algorithm and what its AAD is made of, as its <c>FileCryptoMetaData</c> or its
/// plaintext footer gives them, with the metadata of the footer's key.
/// </summary>
internal sealed class FileCrypto
{
    /// <summary>Whether the pages are encrypted in counter mode: <c>AES_GCM_CTR_V1</c>.</summary>
    internal bool Ctr { get; init; }

    /// <summary>The AAD prefix the file stores, or null.</summary>
    internal byte[]? AadPrefix { get; init; }

    internal byte[] AadFileUnique { get; init; } = [];

    /// <summary>Whether a reader must supply the AAD prefix, which the file does not store.</summary>
    internal bool SupplyAadPrefix { get; init; }

    /// <summary>The metadata of the key that encrypts or signs the footer.</summary>
    internal byte[] KeyMetadata { get; set; } = [];

    /// <summary>Reads an <c>EncryptionAlgorithm</c> union, the reader at its first field.</summary>
    internal static FileCrypto ReadAlgorithm(ref ThriftCompactReader reader)
    {
        FileCrypto? crypto = null;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id is 1 or 2 && type == ThriftType.Struct && crypto is null)
            {
                crypto = ReadAes(ref reader, ctr: id == 2);
            }
            else
            {
                if (type == ThriftType.Struct)
                {
                    throw new ParquetUnsupportedException(id.ToString(System.Globalization.CultureInfo.InvariantCulture), ParquetComponentKind.Encryption,
                        "The file is encrypted by an algorithm the standard does not define; AES_GCM_V1 and AES_GCM_CTR_V1 are read.");
                }

                reader.Skip(type);
            }
        }

        reader.ExitStruct(saved);
        return crypto ?? ParquetThrow.Format<FileCrypto>("An encryption algorithm names no algorithm.");
    }

    /// <summary>Reads a <c>FileCryptoMetaData</c> at the start of <paramref name="bytes"/>; its length in <paramref name="length"/>.</summary>
    internal static FileCrypto ReadFileCryptoMetaData(ReadOnlySpan<byte> bytes, out int length)
    {
        ThriftCompactReader reader = new(bytes);
        FileCrypto? crypto = null;
        byte[] keyMetadata = [];
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1 when type == ThriftType.Struct:
                    crypto = ReadAlgorithm(ref reader);
                    break;
                case 2 when type == ThriftType.Binary:
                    keyMetadata = reader.ReadBinary().ToArray();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        length = reader.Position;
        if (crypto is null)
        {
            ParquetThrow.Format("The file's crypto metadata names no encryption algorithm.");
        }

        crypto.KeyMetadata = keyMetadata;
        return crypto;
    }

    private static FileCrypto ReadAes(ref ThriftCompactReader reader, bool ctr)
    {
        byte[]? prefix = null;
        byte[] unique = [];
        bool supply = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1 when type == ThriftType.Binary:
                    prefix = reader.ReadBinary().ToArray();
                    break;
                case 2 when type == ThriftType.Binary:
                    unique = reader.ReadBinary().ToArray();
                    break;
                case 3:
                    supply = ThriftCompactReader.BooleanField(type);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        return new FileCrypto { Ctr = ctr, AadPrefix = prefix, AadFileUnique = unique, SupplyAadPrefix = supply };
    }
}

/// <summary>
/// What decrypts a file: its algorithm, the AAD its modules share, and its keys, the footer's
/// resolved at the open and a column's the first time it is asked for.
/// </summary>
/// <remarks>
/// A key is the caller's: a column's from <see cref="ParquetDecryption.ColumnKeys"/>, the footer's
/// from <see cref="ParquetDecryption.FooterKey"/>, else what <see cref="ParquetDecryption.KeyResolver"/>
/// makes of the metadata the file stores for it. The ciphers made here decrypt the footer and the
/// columns' metadata, under a lock; a column's reader makes its own from <see cref="ColumnKey"/>.
/// </remarks>
internal sealed class FileDecryptor
{
    private readonly ParquetDecryption _options;
    private readonly Dictionary<string, byte[]> _columnKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleCipher> _ciphers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private FileDecryptor(FileCrypto crypto, ParquetDecryption options, byte[] fileAad, byte[]? footerKey)
    {
        Crypto = crypto;
        _options = options;
        FileAad = fileAad;
        FooterKey = footerKey;
    }

    internal FileCrypto Crypto { get; }

    /// <summary>The AAD's file parts: the prefix, then the file's unique bytes.</summary>
    internal byte[] FileAad { get; }

    /// <summary>The footer's key, and that of the columns it encrypts; null where the caller has none.</summary>
    internal byte[]? FooterKey { get; }

    /// <summary>
    /// The decryptor of a file of <paramref name="crypto"/>: the AAD prefix the file stores, held to
    /// the caller's when the caller gives one, or the caller's where the file asks for it.
    /// </summary>
    /// <param name="crypto">The file's encryption.</param>
    /// <param name="options">The caller's keys.</param>
    /// <param name="footerKeyRequired">Whether the footer cannot be read without its key: an encrypted footer.</param>
    internal static FileDecryptor Create(FileCrypto crypto, ParquetDecryption options, bool footerKeyRequired)
    {
        byte[] prefix;
        if (crypto.AadPrefix is { } stored)
        {
            if (!options.AadPrefix.IsEmpty && !options.AadPrefix.Span.SequenceEqual(stored))
            {
                ParquetThrow.Format("The file's AAD prefix is not the one its reader expects: it is another file, or another version of it.");
            }

            prefix = stored;
        }
        else
        {
            if (crypto.SupplyAadPrefix && options.AadPrefix.IsEmpty)
            {
                throw new ParquetUnsupportedException("aad_prefix", ParquetComponentKind.Encryption,
                    "The file was encrypted under an AAD prefix it does not store; give it as ParquetDecryption.AadPrefix.");
            }

            prefix = options.AadPrefix.ToArray();
        }

        byte[] footerKey = options.FooterKey.IsEmpty ? Resolve(options, null, crypto.KeyMetadata) : options.FooterKey.ToArray();
        if (footerKey.Length == 0)
        {
            if (footerKeyRequired)
            {
                throw new ParquetUnsupportedException("footer key", ParquetComponentKind.Encryption,
                    "The file's footer is encrypted, and no key is given for it: set ParquetDecryption.FooterKey, or a KeyResolver that knows its key metadata.");
            }
        }

        return new FileDecryptor(crypto, options, [.. prefix, .. crypto.AadFileUnique], footerKey.Length == 0 ? null : footerKey);
    }

    /// <summary>The AAD of a module of <paramref name="type"/> into <paramref name="into"/>, which holds <see cref="FileAad"/> and <see cref="ModuleAad.SuffixBytes"/> more.</summary>
    internal int Aad(Span<byte> into, ModuleType type, int rowGroup, int column, int page) =>
        ModuleAad.Write(into, FileAad, type, rowGroup, column, page);

    /// <summary>
    /// The key of the column at <paramref name="path"/>, which the footer's encrypts when
    /// <paramref name="footerKey"/>; null when the caller has none for it.
    /// </summary>
    internal byte[]? ColumnKey(string path, ReadOnlySpan<byte> keyMetadata, bool footerKey)
    {
        if (footerKey)
        {
            return FooterKey;
        }

        lock (_gate)
        {
            if (!_columnKeys.TryGetValue(path, out byte[]? key))
            {
                key = _options.ColumnKeys is { } keys && keys.TryGetValue(path, out ReadOnlyMemory<byte> given) && !given.IsEmpty
                    ? given.ToArray()
                    : Resolve(_options, path, keyMetadata.ToArray());
                _columnKeys[path] = key;
            }

            return key.Length == 0 ? null : key;
        }
    }

    /// <summary>
    /// Decrypts the GCM module <paramref name="module"/> under <paramref name="key"/>, a footer's or a
    /// column metadata's, into new bytes.
    /// </summary>
    internal byte[] Decrypt(byte[] key, ReadOnlySpan<byte> module, ModuleType type, int rowGroup, int column)
    {
        int bytes = ModuleCipher.ModuleBytes(module, ModuleCipher.GcmOverhead);
        if (bytes != module.Length)
        {
            ParquetThrow.Format($"An encrypted {type} of {module.Length} bytes holds a module of {bytes}.");
        }

        byte[] plaintext = new byte[bytes - ModuleCipher.GcmOverhead];
        Span<byte> aad = stackalloc byte[FileAad.Length + ModuleAad.SuffixBytes];
        int length = Aad(aad, type, rowGroup, column, -1);
        lock (_gate)
        {
            Cipher(key).Decrypt(module, aad[..length], plaintext);
        }

        return plaintext;
    }

    /// <summary>Whether <paramref name="signature"/>, a nonce and a tag, signs the plaintext footer <paramref name="footer"/> under the footer's key.</summary>
    internal bool Signs(ReadOnlySpan<byte> footer, ReadOnlySpan<byte> signature)
    {
        Span<byte> aad = stackalloc byte[FileAad.Length + ModuleAad.SuffixBytes];
        int length = Aad(aad, ModuleType.Footer, -1, -1, -1);
        Span<byte> tag = stackalloc byte[ModuleCipher.TagLength];
        lock (_gate)
        {
            Cipher(FooterKey!).Sign(footer, signature[..ModuleCipher.NonceLength], aad[..length], tag);
        }

        return CryptographicOperations.FixedTimeEquals(tag, signature.Slice(ModuleCipher.NonceLength, ModuleCipher.TagLength));
    }

    private ModuleCipher Cipher(byte[] key)
    {
        string id = Convert.ToBase64String(key);
        if (!_ciphers.TryGetValue(id, out ModuleCipher? cipher))
        {
            cipher = new ModuleCipher(key);
            _ciphers[id] = cipher;
        }

        return cipher;
    }

    private static byte[] Resolve(ParquetDecryption options, string? column, byte[] keyMetadata) =>
        options.KeyResolver is { } resolver ? resolver(new ParquetKeyRequest(column, keyMetadata)).ToArray() : [];

    /// <summary>The key metadata as text, where it is: what a message names a key by.</summary>
    internal static string Describe(ReadOnlySpan<byte> keyMetadata)
    {
        try
        {
            return keyMetadata.IsEmpty ? "no key metadata" : $"the key metadata '{new UTF8Encoding(false, true).GetString(keyMetadata)}'";
        }
        catch (DecoderFallbackException)
        {
            return $"{keyMetadata.Length} bytes of key metadata";
        }
    }
}
