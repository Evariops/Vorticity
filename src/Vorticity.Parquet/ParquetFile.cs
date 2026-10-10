using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Reading;
using Vorticity.Parquet.Schema;

namespace Vorticity.Parquet;

/// <summary>
/// An open Parquet file: its schema, its rows, and scans of them through the same surface as a
/// Vortex file's.
/// </summary>
/// <remarks>
/// The open reads the file's tail, the footer behind it when the tail does not hold it whole, and
/// compiles the schema; nothing else is read until a scan asks for its row groups. Thread-safe as a
/// <see cref="VortexFile"/> is: scans may run side by side, each with its own buffers.
/// </remarks>
public sealed class ParquetFile : IAsyncDisposable
{
    private readonly ParquetFooter _footer;

    /// <summary>
    /// The footer's bytes where the open read them, kept for the file's life and given back as it
    /// closes; null for a view of another file's.
    /// </summary>
    private readonly IDisposable? _footerBytes;

    private ParquetScanSource? _source;
    private int _disposed;
    private KeyValuePair<string, string?>[]? _keyValues;
    private ParquetMetadata? _metadata;

    private ParquetFile(ISegmentReader reader, VortexSession session, ParquetOpenOptions options, long length, ParquetFooter footer, ParquetSchema schema, IDisposable? footerBytes = null)
    {
        Reader = reader;
        Session = session;
        Options = options;
        Length = length;
        _footer = footer;
        _footerBytes = footerBytes;
        Compiled = schema;
    }

    /// <summary>The file's columns, as Vortex types.</summary>
    public VortexSchema Schema => Compiled.Vortex;

    /// <summary>The file's rows, as its footer counts them.</summary>
    public long RowCount => _footer.RowCount;

    /// <summary>The file's row groups.</summary>
    public int RowGroupCount => _footer.RowGroups.Length;

    /// <summary>
    /// What the footer says of the file, for inspection: its writer, its columns with their types as
    /// the standard spells them, its row groups and their column chunks, and its key-value pairs.
    /// </summary>
    public ParquetMetadata Metadata => _metadata ??= new ParquetMetadata(this);

    /// <summary>
    /// The key-value pairs the file's footer carries, in its order, a pair given no value with a null
    /// one: those of its writer and of its writer's caller. A file this package wrote holds
    /// <c>vorticity.schema</c>, the Vortex schema it was written from.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> KeyValueMetadata
    {
        get
        {
            if (_keyValues is { } known)
            {
                return known;
            }

            ReadOnlySpan<byte> bytes = Footer.Bytes;
            KeyValuePair<string, string?>[] pairs = new KeyValuePair<string, string?>[Footer.KeyValues.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                (ByteRange key, ByteRange value) = Footer.KeyValues[i];
                pairs[i] = new(Encoding.UTF8.GetString(key.Of(bytes)), value.IsPresent ? Encoding.UTF8.GetString(value.Of(bytes)) : null);
            }

            return _keyValues = pairs;
        }
    }

    internal ISegmentReader Reader { get; }

    internal VortexSession Session { get; }

    internal ParquetOpenOptions Options { get; }

    /// <summary>The file's bytes.</summary>
    internal long Length { get; }

    /// <summary>The footer, whose bytes lie where the open read them until the file closes; read after, it throws.</summary>
    internal ParquetFooter Footer
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _footer;
        }
    }

    /// <summary>The schema compiled to its leaves and levels.</summary>
    internal ParquetSchema Compiled { get; }

    internal ParquetScanSource Source => _source ??= new ParquetScanSource(this);

    /// <summary>Opens the file at <paramref name="path"/> in the default session.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The open file; the caller disposes it.</returns>
    /// <exception cref="ParquetFormatException">The file is not a well-formed Parquet file.</exception>
    /// <exception cref="ParquetUnsupportedException">The file is encrypted.</exception>
    public static ValueTask<ParquetFile> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, ParquetOpenOptions.Default, VortexSession.Default, cancellationToken);

    /// <summary>The rows, typed by <typeparamref name="TRecord"/>, whose members name the columns read.</summary>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <returns>The scan, which reads nothing until it is enumerated or asked a question.</returns>
    public Scan<TRecord> Scan<TRecord>()
        where TRecord : IVortexRecord<TRecord> => new Scan<TRecord>(Source);

    /// <summary>The rows, of the columns named; every column when none is.</summary>
    /// <param name="columns">The columns read.</param>
    /// <returns>The scan.</returns>
    public Scan Scan(params ReadOnlySpan<string> columns) => new Scan(Source, columns);

    /// <summary>
    /// What the file says of itself that its rows do not bear out: each flat column's chunk statistics
    /// recomputed from its values, its page index held to its pages, its Bloom filter asked for every
    /// value, its pages' rows and values counted, its encoding stats walked and its checksums
    /// verified. It reads the whole file; none for a file that says only what is true.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The findings, in the file's order.</returns>
    public async ValueTask<IReadOnlyList<ParquetFinding>> VerifyAsync(CancellationToken cancellationToken = default) =>
        await Verification.ParquetVerifier.VerifyAsync(this, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// This file read with its pages held to their checksums: itself when it is, else a view over its
    /// reader, which the view does not own and is never disposed.
    /// </summary>
    internal ParquetFile Checked() =>
        Options.VerifyChecksums ? this : new ParquetFile(Reader, Session, Options with { VerifyChecksums = true }, Length, Footer, Compiled);

    /// <summary>Closes the file.</summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? CloseAsync() : ValueTask.CompletedTask;

    private async ValueTask CloseAsync()
    {
        try
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _footerBytes?.Dispose();
        }
    }

    /// <summary>Opens the file at <paramref name="path"/> in <paramref name="session"/>: mapped, when the session maps files and a scan reads enough to pay for it.</summary>
    internal static async ValueTask<ParquetFile> OpenAsync(string path, ParquetOpenOptions options, VortexSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ISegmentReader reader = session.Options.MapFiles
            ? LocalFileSource.Open(path, session.Mappings)
            : SessionReader.Wrap(new FileSegmentSource(path), session);
        try
        {
            return await OpenAsync(reader, options, session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the tail, then the footer when the tail does not hold it whole, and compiles the schema.</summary>
    private static async ValueTask<ParquetFile> OpenAsync(ISegmentReader reader, ParquetOpenOptions options, VortexSession session, CancellationToken cancellationToken)
    {
        long length = await reader.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length < 12)
        {
            ParquetThrow.Format($"A Parquet file holds its two magics and a footer's length, 12 bytes at least; this one holds {length}.");
        }

        int tail = (int)Math.Min(length, Math.Max(reader.TailReadSize, 8));
        SegmentOwner? read = await reader.ReadRangeAsync(length - tail, tail, 1, cancellationToken).ConfigureAwait(false);
        SegmentOwner? held = null;
        VortexBuffer footerBytes = default;
        int footerLength;
        bool encryptedFooter;
        try
        {
            ReadOnlySpan<byte> bytes = read.Buffer.Span;
            ReadOnlySpan<byte> end = bytes[^8..];
            encryptedFooter = end[4..].SequenceEqual("PARE"u8);
            if (!encryptedFooter && !end[4..].SequenceEqual("PAR1"u8))
            {
                ParquetThrow.Format("The file does not end with the magic PAR1, nor PARE: it is not a Parquet file, or it is cut short.");
            }

            footerLength = BinaryPrimitives.ReadInt32LittleEndian(end);
            if (footerLength <= 0 || footerLength > length - 12)
            {
                ParquetThrow.Format($"The footer's length, {footerLength}, does not fit the file's {length} bytes.");
            }

            if (footerLength > options.MaxFooterBytes)
            {
                ParquetThrow.Format($"The footer's length, {footerLength}, passes the {options.MaxFooterBytes} bytes the open allows.");
            }

            if (tail == length && !bytes[..4].SequenceEqual(end[4..]))
            {
                ParquetThrow.Format("The file does not begin with the magic it ends with.");
            }

            if (footerLength <= tail - 8)
            {
                // The tail holds the footer, which stays where the read put it.
                footerBytes = read.Buffer.Slice(tail - 8 - footerLength, footerLength);
                held = read;
                read = null;
            }
        }
        finally
        {
            read?.Release();
        }

        if (held is null)
        {
            // The tail did not hold the footer: one more read, of exactly its bytes.
            held = await reader.ReadRangeAsync(length - 8 - footerLength, footerLength, 1, cancellationToken).ConfigureAwait(false);
            footerBytes = held.Buffer;
        }

        if (encryptedFooter)
        {
            return OpenEncrypted(reader, options, session, length, held, footerBytes);
        }

        // The footer is read where it lies, a mapped file's in the mapping, for the file's life: an
        // open copies nothing that grows with the footer, which grows with the chunks.
        SegmentOwnerMemory memory = new(held, footerBytes);
        try
        {
            ParquetFooter metadata = ParquetFooter.Read(memory.Memory);
            if (metadata.Algorithm is { } algorithm)
            {
                Signed(metadata, algorithm, options, footerBytes.Span);
            }

            ParquetSchema schema = Compile(metadata);
            return new ParquetFile(reader, session, options, length, metadata, schema, memory);
        }
        catch
        {
            ((IDisposable)memory).Dispose();
            throw;
        }
    }

    /// <summary>The schema a footer compiles to, its columns' paths given to it where it decrypts their keys.</summary>
    private static ParquetSchema Compile(ParquetFooter metadata)
    {
        ParquetSchema schema = ParquetSchema.Compile(metadata.Schema, metadata.ColumnOrders, metadata.Value).Restored(metadata.Value(ParquetSchema.VortexSchemaKey));
        if (metadata.Decryptor is not null || metadata.IsEncrypted)
        {
            string[] paths = new string[schema.Columns.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = schema.Columns[i].DottedPath;
            }

            metadata.ColumnPaths = paths;
        }

        return schema;
    }

    /// <summary>
    /// A plaintext footer of an encrypted file: its signature, the 28 bytes past its
    /// <c>FileMetaData</c>, held to the footer's key where the caller asks for decryption; the
    /// decryptor kept for the columns it reads. A caller who asks for none reads its plaintext columns.
    /// </summary>
    private static void Signed(ParquetFooter metadata, Encryption.FileCrypto algorithm, ParquetOpenOptions options, ReadOnlySpan<byte> footer)
    {
        if (footer.Length - metadata.StructLength != Encryption.ModuleCipher.NonceLength + Encryption.ModuleCipher.TagLength)
        {
            ParquetThrow.Format($"A plaintext footer of an encrypted file holds {footer.Length - metadata.StructLength} bytes past its metadata where its signature takes 28.");
        }

        if (options.Decryption is not { } decryption)
        {
            return;
        }

        Encryption.FileDecryptor decryptor = Encryption.FileDecryptor.Create(algorithm, decryption, footerKeyRequired: false);
        if (decryption.VerifyFooterSignature)
        {
            if (decryptor.FooterKey is null)
            {
                throw new ParquetUnsupportedException("footer key", ParquetComponentKind.Encryption,
                    $"The footer's signature needs its key, named by {Encryption.FileDecryptor.Describe(algorithm.KeyMetadata)}: give it, or set VerifyFooterSignature false.");
            }

            if (!decryptor.Signs(footer[..metadata.StructLength], footer[metadata.StructLength..]))
            {
                ParquetThrow.Format("The footer does not match its signature: it was altered, or the key or the AAD prefix is not its own.");
            }
        }

        metadata.Decryptor = decryptor;
    }

    /// <summary>
    /// A file whose footer is encrypted: its <c>FileCryptoMetaData</c>, then the footer's module,
    /// decrypted under the footer's key into bytes the file holds for its life.
    /// </summary>
    private static ParquetFile OpenEncrypted(ISegmentReader reader, ParquetOpenOptions options, VortexSession session, long length, SegmentOwner held, VortexBuffer tail)
    {
        try
        {
            if (options.Decryption is not { } decryption)
            {
                throw new ParquetUnsupportedException("PARE", ParquetComponentKind.Encryption,
                    "The file's footer is encrypted: give its keys as ParquetOpenOptions.Decryption.");
            }

            ReadOnlySpan<byte> bytes = tail.Span;
            Encryption.FileCrypto crypto = Encryption.FileCrypto.ReadFileCryptoMetaData(bytes, out int cryptoLength);
            Encryption.FileDecryptor decryptor = Encryption.FileDecryptor.Create(crypto, decryption, footerKeyRequired: true);
            byte[] plaintext = decryptor.Decrypt(decryptor.FooterKey!, bytes[cryptoLength..], Encryption.ModuleType.Footer, -1, -1);
            ParquetFooter metadata = ParquetFooter.Read(plaintext);
            metadata.Decryptor = decryptor;
            ParquetSchema schema = Compile(metadata);
            return new ParquetFile(reader, session, options, length, metadata, schema);
        }
        finally
        {
            held.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="length"/> bytes at <paramref name="offset"/> lie between the leading
    /// magic and the footer's length: where a structure the footer points to may be. A page index or
    /// a Bloom filter outside them is no index, which the reader does without.
    /// </summary>
    internal bool Holds(long offset, long length) =>
        offset >= 4 && length > 0 && length <= int.MaxValue && offset <= Length - 8 - length;

    /// <summary>
    /// The plaintext of the module <paramref name="module"/>, of <paramref name="type"/>, of the
    /// encrypted chunk <paramref name="chunk"/> of row group <paramref name="rowGroup"/> and column
    /// <paramref name="column"/>: an index or a Bloom filter. Null where the chunk's key is not given.
    /// </summary>
    internal byte[]? Decrypt(in ColumnChunkMetadata chunk, int rowGroup, int column, ReadOnlySpan<byte> module, Encryption.ModuleType type)
    {
        if (Footer.Decryptor is not { } decryptor || chunk.Hidden)
        {
            return null;
        }

        byte[]? key = decryptor.ColumnKey(Footer.ColumnPaths[column], chunk.KeyMetadata.Of(Footer.Bytes), chunk.Crypto == ChunkCrypto.FooterKey);
        return key is null ? null : decryptor.Decrypt(key, module, type, Footer.Ordinal(rowGroup), column);
    }

    /// <summary>Where a column chunk's pages lie, its dictionary page first; checked against the file's bytes.</summary>
    internal (long Start, int Length) ChunkRange(ColumnChunkMetadata chunk)
    {
        if (chunk.Hidden)
        {
            throw new ParquetUnsupportedException("column key", ParquetComponentKind.Encryption,
                "A column is encrypted with a key of its own, which is not given: set it in ParquetDecryption.ColumnKeys, or a KeyResolver that knows its key metadata.");
        }

        if (chunk.IsEncrypted && Footer.Decryptor is null)
        {
            throw new ParquetUnsupportedException("encrypted columns", ParquetComponentKind.Encryption,
                "A column is encrypted: give the file's keys as ParquetOpenOptions.Decryption.");
        }

        if (chunk.HasFilePath)
        {
            throw new ParquetUnsupportedException("file_path", ParquetComponentKind.Feature,
                "A column chunk lies in another file, which the standard does not count as part of Parquet.");
        }

        // Some writers set the dictionary page's offset to 0 when there is none.
        long start = chunk.DictionaryPageOffset > 0 && chunk.DictionaryPageOffset < chunk.DataPageOffset
            ? chunk.DictionaryPageOffset
            : chunk.DataPageOffset;
        long size = chunk.TotalCompressedSize;
        if (start < 4 || size <= 0 || size > int.MaxValue || start + size > Length - 8 - 4)
        {
            ParquetThrow.Format($"A column chunk declares {size} bytes at {start}, outside the {Length} bytes of the file's pages.");
        }

        return (start, (int)size);
    }
}
