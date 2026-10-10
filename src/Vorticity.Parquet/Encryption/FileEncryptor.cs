using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Encryption;

/// <summary>
/// What encrypts a file as the writer makes it: its algorithm, the AAD its modules share, the
/// footer's cipher, and a column's cipher where the caller encrypts it.
/// </summary>
/// <remarks>
/// The AAD's unique part is eight random bytes a file, so that no module of one file authenticates
/// in another under the same key. Every module is encrypted under a fresh random nonce.
/// </remarks>
internal sealed class FileEncryptor : IDisposable
{
    /// <summary>The bytes of the AAD's unique part.</summary>
    private const int UniqueBytes = 8;

    private readonly ParquetEncryption _options;
    private readonly ModuleCipher _footer;
    private readonly Dictionary<string, ColumnEncryptor> _columns = new(StringComparer.Ordinal);

    internal FileEncryptor(ParquetEncryption options)
    {
        _options = options;
        _footer = new ModuleCipher(options.FooterKey.Span);
        Unique = RandomNumberGenerator.GetBytes(UniqueBytes);
        FileAad = [.. options.AadPrefix.Span, .. Unique];
    }

    /// <summary>Whether the footer is in plaintext, signed.</summary>
    internal bool PlaintextFooter => _options.PlaintextFooter;

    /// <summary>The AAD's file parts: the prefix, then <see cref="Unique"/>.</summary>
    internal byte[] FileAad { get; }

    internal byte[] Unique { get; }

    internal bool Ctr => _options.Algorithm == ParquetEncryptionAlgorithm.AesGcmCtr;

    /// <summary>The encryptor of the column at <paramref name="path"/>, whose ordinal is <paramref name="ordinal"/>; null for a column written in plaintext.</summary>
    internal ColumnEncryptor? Column(string path, string[] names, int ordinal)
    {
        ParquetColumnKey key = default;
        if (_options.ColumnKeys is { } keys && !keys.TryGetValue(path, out key))
        {
            return null;
        }

        if (!_columns.TryGetValue(path, out ColumnEncryptor? column))
        {
            bool footer = key.Key.IsEmpty;
            column = new ColumnEncryptor(this, footer ? _footer : new ModuleCipher(key.Key.Span), footer, key.KeyMetadata.ToArray(), names, ordinal);
            _columns[path] = column;
        }

        return column;
    }

    /// <summary>Writes the <c>EncryptionAlgorithm</c> union as field <paramref name="id"/>.</summary>
    internal void WriteAlgorithm(ref ThriftCompactWriter writer, short id)
    {
        short union = writer.BeginStructField(id);
        short member = writer.BeginStructField(Ctr ? (short)2 : (short)1);
        if (_options.StoreAadPrefix && !_options.AadPrefix.IsEmpty)
        {
            writer.WriteBinaryField(1, _options.AadPrefix.Span);
        }

        writer.WriteBinaryField(2, Unique);
        if (!_options.StoreAadPrefix && !_options.AadPrefix.IsEmpty)
        {
            writer.WriteBooleanField(3, true);
        }

        writer.EndStruct(member);
        writer.EndStruct(union);
    }

    /// <summary>Writes a plaintext footer's fields of encryption: the algorithm, and the metadata of the key that signs it.</summary>
    internal void WriteFooterFields(ref ThriftCompactWriter writer)
    {
        WriteAlgorithm(ref writer, 8);
        if (!_options.FooterKeyMetadata.IsEmpty)
        {
            writer.WriteBinaryField(9, _options.FooterKeyMetadata.Span);
        }
    }

    /// <summary>
    /// The tail of a file whose footer is encrypted: its <c>FileCryptoMetaData</c>, then
    /// <paramref name="footer"/>'s module.
    /// </summary>
    internal void WriteEncryptedFooter(IBufferWriter<byte> into, ReadOnlySpan<byte> footer)
    {
        ThriftCompactWriter writer = new(into);
        short saved = writer.BeginStruct();
        WriteAlgorithm(ref writer, 1);
        if (!_options.FooterKeyMetadata.IsEmpty)
        {
            writer.WriteBinaryField(2, _options.FooterKeyMetadata.Span);
        }

        writer.EndStruct(saved);
        writer.Flush();
        Span<byte> aad = stackalloc byte[FileAad.Length + ModuleAad.SuffixBytes];
        int length = ModuleAad.Write(aad, FileAad, ModuleType.Footer, -1, -1, -1);
        Span<byte> module = into.GetSpan(footer.Length + ModuleCipher.GcmOverhead)[..(footer.Length + ModuleCipher.GcmOverhead)];
        _footer.Encrypt(footer, aad[..length], module);
        into.Advance(module.Length);
    }

    /// <summary>The 28 bytes that sign a plaintext <paramref name="footer"/>: a fresh nonce, and the tag GCM gives the footer under it.</summary>
    internal void Sign(ReadOnlySpan<byte> footer, Span<byte> signature)
    {
        Span<byte> aad = stackalloc byte[FileAad.Length + ModuleAad.SuffixBytes];
        int length = ModuleAad.Write(aad, FileAad, ModuleType.Footer, -1, -1, -1);
        Span<byte> nonce = signature[..ModuleCipher.NonceLength];
        RandomNumberGenerator.Fill(nonce);
        _footer.Sign(footer, nonce, aad[..length], signature.Slice(ModuleCipher.NonceLength, ModuleCipher.TagLength));
    }

    public void Dispose()
    {
        foreach (ColumnEncryptor column in _columns.Values)
        {
            column.Dispose();
        }

        _footer.Dispose();
    }
}

/// <summary>
/// What encrypts one column's modules: its pages and their headers, its metadata, its page index and
/// its Bloom filter, each under the AAD of its row group, the column's ordinal and, for a data page
/// and its header, the page's.
/// </summary>
internal sealed class ColumnEncryptor : IDisposable
{
    private readonly FileEncryptor _file;
    private readonly ModuleCipher _cipher;
    private readonly byte[] _aad;

    internal ColumnEncryptor(FileEncryptor file, ModuleCipher cipher, bool footerKey, byte[] keyMetadata, string[] path, int ordinal)
    {
        _file = file;
        _cipher = cipher;
        FooterKey = footerKey;
        KeyMetadata = keyMetadata;
        Path = path;
        Ordinal = ordinal;
        _aad = new byte[file.FileAad.Length + ModuleAad.SuffixBytes];
    }

    /// <summary>Whether the column is encrypted with the footer's key.</summary>
    internal bool FooterKey { get; }

    internal byte[] KeyMetadata { get; }

    internal string[] Path { get; }

    /// <summary>The column's ordinal in the file's leaves.</summary>
    internal int Ordinal { get; }

    /// <summary>Whether the file's footer is in plaintext, which its encrypted columns' metadata then is not.</summary>
    internal bool PlaintextFooter => _file.PlaintextFooter;

    /// <summary>The bytes a module of <paramref name="type"/> adds to its plaintext.</summary>
    internal int Overhead(ModuleType type) =>
        _file.Ctr && type is ModuleType.DataPage or ModuleType.DictionaryPage ? ModuleCipher.CtrOverhead : ModuleCipher.GcmOverhead;

    /// <summary>
    /// Appends <paramref name="plaintext"/> to <paramref name="into"/> as a module of
    /// <paramref name="type"/> of row group <paramref name="rowGroup"/> and page <paramref name="page"/>;
    /// the module's bytes.
    /// </summary>
    internal int Encrypt(ReadOnlySpan<byte> plaintext, ModuleType type, int rowGroup, int page, IBufferWriter<byte> into)
    {
        int bytes = plaintext.Length + Overhead(type);
        Span<byte> module = into.GetSpan(bytes)[..bytes];
        if (_file.Ctr && type is ModuleType.DataPage or ModuleType.DictionaryPage)
        {
            _cipher.EncryptCtr(plaintext, module);
        }
        else
        {
            int aad = ModuleAad.Write(_aad, _file.FileAad, type, rowGroup, Ordinal, page);
            _cipher.Encrypt(plaintext, _aad.AsSpan(0, aad), module);
        }

        into.Advance(bytes);
        return bytes;
    }

    /// <summary>Writes the chunk's <c>ColumnCryptoMetaData</c> as field 8 of its <c>ColumnChunk</c>.</summary>
    internal void WriteCryptoMetadata(ref ThriftCompactWriter writer)
    {
        short union = writer.BeginStructField(8);
        if (FooterKey)
        {
            short empty = writer.BeginStructField(1);
            writer.EndStruct(empty);
        }
        else
        {
            short member = writer.BeginStructField(2);
            writer.WriteListField(1, ThriftType.Binary, Path.Length);
            foreach (string name in Path)
            {
                writer.WriteStringElement(name);
            }

            if (KeyMetadata.Length > 0)
            {
                writer.WriteBinaryField(2, KeyMetadata);
            }

            writer.EndStruct(member);
        }

        writer.EndStruct(union);
    }

    public void Dispose()
    {
        if (!FooterKey)
        {
            _cipher.Dispose();
        }
    }
}
