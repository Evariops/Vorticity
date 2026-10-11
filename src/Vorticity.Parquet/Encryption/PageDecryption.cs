using System;
using Vorticity.Buffers;
using Vorticity.Parquet.Metadata;

namespace Vorticity.Parquet.Encryption;

/// <summary>
/// The pages of an encrypted column chunk, a header and a body each a module of its own: the header
/// decrypted to be read, the body when the page is.
/// </summary>
/// <remarks>
/// <para>
/// A module's AAD names the row group, the column and, for a data page and its header, the page's
/// ordinal among the chunk's data pages. A chunk with a dictionary starts with its page, whose
/// modules take no ordinal. The ordinal follows the pages' places, not the reads: a header read
/// twice, as a skip that looks at a page and leaves it does, is the same page, kept the first time.
/// </para>
/// <para>
/// Every header and every module of <c>AES_GCM_V1</c> is GCM; the bodies of an <c>AES_GCM_CTR_V1</c>
/// file are in counter mode, which authenticates nothing: their headers, GCM, say what they hold.
/// </para>
/// </remarks>
internal sealed class PageDecryption : IDisposable
{
    private readonly ModuleCipher _cipher;
    private readonly byte[] _key;
    private readonly byte[] _fileAad;
    private readonly byte[] _aad;
    private readonly bool _ctr;
    private byte[] _plain = new byte[256];
    private int _rowGroup;
    private int _column;
    private bool _dictionary;

    /// <summary>The place of the last header decrypted, and what it held.</summary>
    private int _seen;
    private PageHeader _header;
    private int _headerBytes;
    private int _ordinal;
    private int _next;

    internal PageDecryption(byte[] key, FileDecryptor file)
    {
        _key = key;
        _cipher = new ModuleCipher(key);
        _fileAad = file.FileAad;
        _aad = new byte[file.FileAad.Length + ModuleAad.SuffixBytes];
        _ctr = file.Crypto.Ctr;
    }

    /// <summary>Whether this decrypts under <paramref name="key"/>.</summary>
    internal bool Under(ReadOnlySpan<byte> key) => key.SequenceEqual(_key);

    /// <summary>The bytes a body's module adds to its page's.</summary>
    internal int BodyOverhead => _ctr ? ModuleCipher.CtrOverhead : ModuleCipher.GcmOverhead;

    /// <summary>Starts the chunk of the row group of ordinal <paramref name="rowGroup"/> and the column <paramref name="column"/>, which begins with a dictionary page when <paramref name="dictionary"/>.</summary>
    internal void Start(int rowGroup, int column, bool dictionary)
    {
        _rowGroup = rowGroup;
        _column = column;
        _dictionary = dictionary;
        _seen = -1;
        _next = 0;
    }

    /// <summary>The header of the page at <paramref name="position"/> of the chunk, whose module starts <paramref name="module"/>; the module's bytes in <paramref name="moduleBytes"/>.</summary>
    internal PageHeader Header(ReadOnlySpan<byte> module, int position, out int moduleBytes)
    {
        if (position != _seen)
        {
            if (position < _seen)
            {
                throw new InvalidOperationException("An encrypted chunk's pages are read in their order.");
            }

            bool dictionary = position == 0 && _dictionary;
            _headerBytes = ModuleCipher.ModuleBytes(module, ModuleCipher.GcmOverhead);
            int plain = _headerBytes - ModuleCipher.GcmOverhead;
            if (_plain.Length < plain)
            {
                _plain = new byte[Math.Max(plain, 2 * _plain.Length)];
            }

            _ordinal = dictionary ? -1 : _next++;
            int aad = ModuleAad.Write(_aad, _fileAad, dictionary ? ModuleType.DictionaryPageHeader : ModuleType.DataPageHeader, _rowGroup, _column, _ordinal);
            _cipher.Decrypt(module[.._headerBytes], _aad.AsSpan(0, aad), _plain.AsSpan(0, plain));
            _header = PageHeader.Read(_plain.AsSpan(0, plain));
            if (dictionary != (_header.Type == PageType.DictionaryPage))
            {
                ParquetThrow.Format(dictionary
                    ? "An encrypted chunk said to start with its dictionary page starts with another."
                    : "An encrypted chunk holds a dictionary page past its first.");
            }

            _seen = position;
        }

        moduleBytes = _headerBytes;
        return _header;
    }

    /// <summary>
    /// The body of the page whose header <see cref="Header"/> read last, decrypted from its module
    /// <paramref name="module"/> into a block of <paramref name="pool"/>'s, which the caller owns.
    /// </summary>
    internal NativeSegmentOwner Body(in PageHeader header, ReadOnlySpan<byte> module, AlignedBufferPool pool)
    {
        int plain = module.Length - BodyOverhead;
        if (plain != header.CompressedPageSize)
        {
            ParquetThrow.Format($"An encrypted page holds {plain} bytes where its header, less the module's, declares {header.CompressedPageSize}.");
        }

        NativeSegmentOwner owner = pool.Rent(Math.Max(plain, 1), 64);
        try
        {
            if (_ctr)
            {
                _cipher.DecryptCtr(module, owner.WritableSpan[..plain]);
            }
            else
            {
                bool dictionary = _ordinal < 0;
                int aad = ModuleAad.Write(_aad, _fileAad, dictionary ? ModuleType.DictionaryPage : ModuleType.DataPage, _rowGroup, _column, _ordinal);
                _cipher.Decrypt(module, _aad.AsSpan(0, aad), owner.WritableSpan[..plain]);
            }
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        return owner;
    }

    public void Dispose() => _cipher.Dispose();
}
