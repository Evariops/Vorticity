using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Parquet;

/// <summary>What Vorticity.Parquet adds to a session.</summary>
public static class ParquetSessionExtensions
{
    extension(VortexSession session)
    {
        /// <summary>Opens the Parquet file at <paramref name="path"/> in this session, whose pool, mapping and caches its scans use.</summary>
        /// <param name="path">The file.</param>
        /// <param name="options">How the file is read; null for the defaults.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The open file; the caller disposes it.</returns>
        /// <exception cref="ParquetFormatException">The file is not a well-formed Parquet file.</exception>
        /// <exception cref="ParquetUnsupportedException">The file is encrypted.</exception>
        public ValueTask<ParquetFile> OpenParquetAsync(string path, ParquetOpenOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.ThrowIfDisposed();
            return ParquetFile.OpenAsync(path, options ?? ParquetOpenOptions.Default, session, cancellationToken);
        }

        /// <summary>
        /// Starts a Parquet file at <paramref name="path"/>, which replaces whatever is there once the
        /// file completes; until then the path holds what it held, and a file given up leaves it so.
        /// </summary>
        /// <param name="path">The destination.</param>
        /// <param name="schema">The file's columns.</param>
        /// <param name="options">What the file looks like; null for the defaults.</param>
        /// <returns>The writer; the caller completes and disposes it. Disposed without completing, it deletes what it wrote.</returns>
        /// <exception cref="ArgumentException">The options are out of range.</exception>
        /// <exception cref="ParquetUnsupportedException">A column is of a type this writer does not write yet.</exception>
        public ParquetFileWriter CreateParquetWriter(string path, VortexSchema schema, ParquetWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.ThrowIfDisposed();
            return ParquetFileWriter.Create(path, schema, options ?? ParquetWriteOptions.Default, session);
        }

        /// <summary>Starts a Parquet file at <paramref name="path"/> whose columns are the members of <typeparamref name="TRecord"/>.</summary>
        /// <typeparam name="TRecord">The record type; its schema is the file's.</typeparam>
        /// <param name="path">The destination, replaced once the file completes.</param>
        /// <param name="options">What the file looks like; null for the defaults.</param>
        /// <returns>The writer; the caller completes and disposes it.</returns>
        public ParquetFileWriter CreateParquetWriter<TRecord>(string path, ParquetWriteOptions? options = null)
            where TRecord : IVortexRecord<TRecord>
        {
            ParquetFileWriter writer = session.CreateParquetWriter(path, TRecord.Schema, options);
            try
            {
                writer.Builder<TRecord>();
            }
            catch
            {
                writer.Abandon();
                throw;
            }

            return writer;
        }

        /// <summary>Starts a Parquet file written to <paramref name="sink"/>: a file, a socket, an upload.</summary>
        /// <param name="sink">
        /// Where the bytes go; a row group's close waits for it to accept them. The writer completes
        /// the pipe when the file is whole, and completes it with an error when the file is abandoned.
        /// </param>
        /// <param name="schema">The file's columns.</param>
        /// <param name="options">What the file looks like; null for the defaults.</param>
        /// <returns>The writer; the caller completes and disposes it.</returns>
        public ParquetFileWriter CreateParquetWriter(PipeWriter sink, VortexSchema schema, ParquetWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.ThrowIfDisposed();
            return ParquetFileWriter.Create(sink, schema, options ?? ParquetWriteOptions.Default, session);
        }
    }
}
