using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The whole content of a put held on disk, in the spool directory a store was given, so that every
/// attempt, of a part, of the whole object, or of the upload itself, sends it again without holding it
/// in memory. It is written as the content is read, then mapped read-only and handed out as a sequence,
/// and the file is deleted when the spool is disposed, its put ended.
/// </summary>
/// <remarks>
/// The mapped pages are the file's, in the system's cache: the operating system reads them back as a
/// request sends them and drops them under pressure, which no pooled buffer of the process does.
/// </remarks>
internal sealed unsafe class SpoolFile : IDisposable
{
    /// <summary>The most one segment of the sequence spans: a span's length is an <see cref="int"/>.</summary>
    private const long SegmentBytes = 1L << 30;

    private readonly FileStream _file;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _acquired;

    private SpoolFile(FileStream file) => _file = file;

    /// <summary>The content, valid until the spool is disposed.</summary>
    internal ReadOnlySequence<byte> Bytes { get; private set; } = ReadOnlySequence<byte>.Empty;

    /// <summary>Writes <paramref name="content"/> to a new file of <paramref name="directory"/>, then maps it.</summary>
    /// <param name="directory">The spool directory.</param>
    /// <param name="content">The content, read to its end; never completed here.</param>
    /// <param name="length">The length announced, or -1.</param>
    /// <param name="key">The object's key, for the messages.</param>
    /// <param name="cancellationToken">Cancels the reads and the writes.</param>
    /// <returns>The spool, which the caller disposes once the put has ended.</returns>
    /// <exception cref="ObjectStoreException">The content ended before its announced length or ran past it.</exception>
    internal static async ValueTask<SpoolFile> FillAsync(string directory, PipeReader content, long length, string key, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, $"vorticity-put-{Guid.NewGuid():N}.spool");
        FileStream file = new FileStream(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        SpoolFile spool = new SpoolFile(file);
        try
        {
            long written = 0;
            while (true)
            {
                ReadResult result = await content.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    await file.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                written += result.Buffer.Length;
                content.AdvanceTo(result.Buffer.End);
                if (length >= 0 && written > length)
                {
                    throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content runs past them.");
                }

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException("The content was cancelled.");
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }

            if (length >= 0 && written != length)
            {
                throw new ObjectStoreException($"'{key}' was announced as {length} bytes and its content ended after {written}.");
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            spool.Map(written);
            return spool;
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Bytes = ReadOnlySequence<byte>.Empty;
        if (_acquired)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _acquired = false;
        }

        _view?.Dispose();
        _map?.Dispose();

        // The file was opened to be deleted on close.
        _file.Dispose();
    }

    /// <summary>Maps the <paramref name="length"/> bytes written and builds the sequence over them, a gibibyte a segment at most.</summary>
    private void Map(long length)
    {
        if (length == 0)
        {
            return;
        }

        _map = MemoryMappedFile.CreateFromFile(_file, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _view = _map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _acquired = true;
        byte* start = pointer + _view.PointerOffset;

        SequenceSegment first = new SequenceSegment(new Mapped(start, (int)Math.Min(length, SegmentBytes)).Memory, 0);
        SequenceSegment last = first;
        for (long offset = SegmentBytes; offset < length; offset += SegmentBytes)
        {
            last = last.Append(new Mapped(start + offset, (int)Math.Min(length - offset, SegmentBytes)).Memory);
        }

        Bytes = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    /// <summary>A stretch of the mapped file as memory, valid while the view is mapped.</summary>
    private sealed class Mapped : MemoryManager<byte>
    {
        private readonly byte* _start;
        private readonly int _length;

        internal Mapped(byte* start, int length)
        {
            _start = start;
            _length = length;
        }

        public override Span<byte> GetSpan() => new Span<byte>(_start, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_start + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
