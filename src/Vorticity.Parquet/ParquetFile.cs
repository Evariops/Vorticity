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
    private ParquetScanSource? _source;
    private int _disposed;
    private KeyValuePair<string, string?>[]? _keyValues;

    private ParquetFile(ISegmentReader reader, VortexSession session, ParquetOpenOptions options, long length, ParquetFooter footer, ParquetSchema schema)
    {
        Reader = reader;
        Session = session;
        Options = options;
        Length = length;
        Footer = footer;
        Compiled = schema;
    }

    /// <summary>The file's columns, as Vortex types.</summary>
    public VortexSchema Schema => Compiled.Vortex;

    /// <summary>The file's rows, as its footer counts them.</summary>
    public long RowCount => Footer.RowCount;

    /// <summary>The file's row groups.</summary>
    public int RowGroupCount => Footer.RowGroups.Length;

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

    internal ParquetFooter Footer { get; }

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

    /// <summary>Closes the file.</summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? Reader.DisposeAsync() : ValueTask.CompletedTask;

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
        byte[] footerBytes;
        SegmentOwner read = await reader.ReadRangeAsync(length - tail, tail, 1, cancellationToken).ConfigureAwait(false);
        int footerLength;
        try
        {
            ReadOnlySpan<byte> bytes = read.Buffer.Span;
            ReadOnlySpan<byte> end = bytes[^8..];
            if (end[4..].SequenceEqual("PARE"u8))
            {
                throw new ParquetUnsupportedException("PARE", ParquetComponentKind.Encryption,
                    "The file's footer is encrypted; this version of the reader reads plaintext footers.");
            }

            if (!end[4..].SequenceEqual("PAR1"u8))
            {
                ParquetThrow.Format("The file does not end with the magic PAR1: it is not a Parquet file, or it is cut short.");
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

            if (tail == length && !bytes[..4].SequenceEqual("PAR1"u8))
            {
                ParquetThrow.Format("The file does not begin with the magic PAR1.");
            }

            footerBytes = footerLength <= tail - 8 ? bytes.Slice(tail - 8 - footerLength, footerLength).ToArray() : [];
        }
        finally
        {
            read.Release();
        }

        if (footerBytes.Length == 0)
        {
            // The tail did not hold the footer: one more read, of exactly its bytes.
            SegmentOwner footer = await reader.ReadRangeAsync(length - 8 - footerLength, footerLength, 1, cancellationToken).ConfigureAwait(false);
            try
            {
                footerBytes = footer.Buffer.Span.ToArray();
            }
            finally
            {
                footer.Release();
            }
        }

        ParquetFooter metadata = ParquetFooter.Read(footerBytes);
        if (metadata.IsEncrypted)
        {
            throw new ParquetUnsupportedException("encrypted columns", ParquetComponentKind.Encryption,
                "The file encrypts its columns; this version of the reader reads plaintext files.");
        }

        ParquetSchema schema = ParquetSchema.Compile(metadata.Schema, metadata.ColumnOrders).Restored(metadata.Value(ParquetSchema.VortexSchemaKey));
        return new ParquetFile(reader, session, options, length, metadata, schema);
    }

    /// <summary>
    /// Whether <paramref name="length"/> bytes at <paramref name="offset"/> lie between the leading
    /// magic and the footer's length: where a structure the footer points to may be. A page index or
    /// a Bloom filter outside them is no index, which the reader does without.
    /// </summary>
    internal bool Holds(long offset, long length) =>
        offset >= 4 && length > 0 && length <= int.MaxValue && offset <= Length - 8 - length;

    /// <summary>Where a column chunk's pages lie, its dictionary page first; checked against the file's bytes.</summary>
    internal (long Start, int Length) ChunkRange(ColumnChunkMetadata chunk)
    {
        if (chunk.IsEncrypted)
        {
            throw new ParquetUnsupportedException("encrypted columns", ParquetComponentKind.Encryption,
                "A column chunk is encrypted; this version of the reader reads plaintext files.");
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
