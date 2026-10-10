using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.Writing;

/// <summary>
/// A <see cref="PipeWriter"/> over a file handle: writes land in pooled segments, and a flush
/// writes them at the file's next offset in one gathered call, so a flush is the only I/O.
/// </summary>
/// <remarks>
/// It owns the handle. <see cref="Abandon"/> drops what was not flushed, gives the file back its
/// length at open, and closes the handle, which is how a writer gives up a file without writing a
/// byte of what it still held. A file it creates is written under a name of its own beside the
/// destination and renamed over it once complete, so that until then, and after an abandon,
/// whatever was at the destination stays as it was.
/// </remarks>
internal class FilePipeWriter : PipeWriter
{
    private const int SegmentBytes = 256 * 1024;

    /// <summary>
    /// The file lengths the device flushes of each watched pipe covered, in order; null until a pipe
    /// is watched, so that a pipe nobody watches does not look.
    /// </summary>
    private static ConditionalWeakTable<FilePipeWriter, List<long>>? DiskFlushes;

    private readonly SafeFileHandle _handle;
    private readonly MemoryPool<byte> _pool;
    private readonly long _origin;

    /// <summary>What the next flush writes, in order: the pool's segments it owns, and the bytes callers lent.</summary>
    private readonly List<(IMemoryOwner<byte>? Owner, ReadOnlyMemory<byte> Bytes)> _filled = [];
    private readonly List<ReadOnlyMemory<byte>> _gather = [];
    private IMemoryOwner<byte>? _current;
    private int _currentLength;
    private long _offset;
    private long _unflushed;
    private bool _completed;

    /// <summary>Writes from <paramref name="offset"/> on, which is also the length an abandon truncates back to.</summary>
    internal FilePipeWriter(SafeFileHandle handle, long offset, MemoryPool<byte> pool)
    {
        _handle = handle;
        _pool = pool;
        _origin = offset;
        _offset = offset;
    }

    /// <summary>
    /// Starts a file that replaces <paramref name="path"/> once complete. A symbolic link is written
    /// through to its target, as opening the path would, and a file replaced passes its Unix
    /// permissions on to the new one.
    /// </summary>
    /// <remarks>
    /// The file is written as <c>.name.token.tmp</c> in the destination's directory, which needs to be
    /// writable: the rename that publishes it is atomic only within one file system. A process that
    /// dies before the rename leaves that file behind.
    /// </remarks>
    internal static FilePipeWriter Create(string path, MemoryPool<byte> pool, bool durable)
    {
        string destination = Path.GetFullPath(path);
        FileInfo named = new FileInfo(destination);
        if (named.LinkTarget is not null && named.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            destination = target.FullName;
        }

        string temporary = Path.Join(
            Path.GetDirectoryName(destination), $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        SafeFileHandle handle = System.IO.File.OpenHandle(
            temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        try
        {
            if (!OperatingSystem.IsWindows() && System.IO.File.Exists(destination))
            {
                System.IO.File.SetUnixFileMode(handle, System.IO.File.GetUnixFileMode(destination));
            }

            return new CreatedFile(handle, pool, temporary, destination) { Durable = durable };
        }
        catch
        {
            handle.Dispose();
            System.IO.File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Whether <see cref="FlushToDisk"/> puts the file on the device, and completing it does.</summary>
    internal bool Durable { get; init; }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes => _unflushed;

    public override void Advance(int bytes)
    {
        if (_current is null || (uint)bytes > (uint)(_current.Memory.Length - _currentLength))
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "More bytes than the last buffer handed out.");
        }

        _currentLength += bytes;
        _unflushed += bytes;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        int wanted = Math.Max(sizeHint, 1);
        if (_current is null || _current.Memory.Length - _currentLength < wanted)
        {
            Seal();
            _current = _pool.Rent(Math.Max(wanted, SegmentBytes));
            _currentLength = 0;
        }

        return _current.Memory[_currentLength..];
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>
    /// Appends <paramref name="data"/> where it lies, after what was written before it: the next flush
    /// writes it gathered with the rest, and the caller leaves it as it is until that flush completes.
    /// </summary>
    internal void Lend(ReadOnlyMemory<byte> data)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Seal();
        _filled.Add((null, data));
        _unflushed += data.Length;
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Seal();
        if (_filled.Count > 0)
        {
            _gather.Clear();
            long bytes = 0;
            foreach ((IMemoryOwner<byte>? _, ReadOnlyMemory<byte> filled) in _filled)
            {
                _gather.Add(filled);
                bytes += filled.Length;
            }

            await RandomAccess.WriteAsync(_handle, _gather, _offset, cancellationToken).ConfigureAwait(false);
            _offset += bytes;
            _gather.Clear();
            ReturnFilled();
        }

        _unflushed = 0;
        return new FlushResult(isCanceled: false, isCompleted: false);
    }

    public override void CancelPendingFlush()
    {
    }

    /// <summary>Closes the handle; what was not flushed is dropped, since a synchronous write here would be a blocking one.</summary>
    public override void Complete(Exception? exception = null) => Close();

    /// <summary>
    /// Flushes, closes, and puts a created file in place of its destination. With an exception, a
    /// created file is given up, and one written from an offset keeps what was flushed.
    /// </summary>
    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }

        if (exception is not null)
        {
            Fail();
            return;
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            FlushToDisk();
            Close();
            Publish();
        }
        catch
        {
            Close();
            Discard();
            throw;
        }
    }

    /// <summary>Puts what was flushed on the device, when the file is durable; nothing otherwise.</summary>
    internal void FlushToDisk()
    {
        if (!Durable)
        {
            return;
        }

        RandomAccess.FlushToDisk(_handle);
        if (DiskFlushes is { } watched && watched.TryGetValue(this, out List<long>? flushes))
        {
            flushes.Add(_offset);
        }
    }

    /// <summary>Records, from now on, the file length each device flush of <paramref name="pipe"/> covers.</summary>
    /// <param name="pipe">The pipe to watch.</param>
    /// <returns>The lengths, one per flush, in order.</returns>
    internal static List<long> WatchDiskFlushes(FilePipeWriter pipe) =>
        LazyInitializer.EnsureInitialized(ref DiskFlushes).GetValue(pipe, static _ => []);

    /// <summary>
    /// Drops what was not flushed and closes the file: a created one is deleted, and its destination
    /// keeps what it held; one written from an offset is truncated back to it.
    /// </summary>
    internal void Abandon()
    {
        if (_completed)
        {
            return;
        }

        try
        {
            Rewind();
        }
        finally
        {
            Close();
            Discard();
        }
    }

    /// <summary>What a completion with an error does: a file written from an offset keeps what was flushed.</summary>
    private protected virtual void Fail() => Close();

    /// <summary>Gives the file back its length at open, before an abandon closes it.</summary>
    private protected virtual void Rewind() => RandomAccess.SetLength(_handle, _origin);

    /// <summary>Puts a completed, closed file in place; one written from an offset already is.</summary>
    private protected virtual void Publish()
    {
    }

    /// <summary>Removes what a closed file that failed or was given up leaves behind; one written from an offset leaves nothing.</summary>
    private protected virtual void Discard()
    {
    }

    private void Close()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        ReturnFilled();
        _current?.Dispose();
        _current = null;
        _currentLength = 0;
        _unflushed = 0;
        _handle.Dispose();
    }

    private void Seal()
    {
        if (_current is null)
        {
            return;
        }

        if (_currentLength > 0)
        {
            _filled.Add((_current, _current.Memory[.._currentLength]));
        }
        else
        {
            _current.Dispose();
        }

        _current = null;
        _currentLength = 0;
    }

    private void ReturnFilled()
    {
        foreach ((IMemoryOwner<byte>? owner, ReadOnlyMemory<byte> _) in _filled)
        {
            owner?.Dispose();
        }

        _filled.Clear();
    }

    /// <summary>
    /// A file written under a name of its own beside its destination: renamed over it once complete,
    /// deleted when it fails or is given up. Only such a file carries the two names.
    /// </summary>
    private sealed class CreatedFile : FilePipeWriter
    {
        private readonly string _temporary;
        private readonly string _destination;

        internal CreatedFile(SafeFileHandle handle, MemoryPool<byte> pool, string temporary, string destination)
            : base(handle, 0, pool)
        {
            _temporary = temporary;
            _destination = destination;
        }

        private protected override void Fail() => Abandon();

        private protected override void Rewind()
        {
        }

        private protected override void Publish()
        {
            // The file being replaced may be kept mapped. NTFS and ReFS let the rename replace it all
            // the same; a file system without POSIX deletes would refuse to, and the old file is of no
            // use to a cache once its name names the new one.
            if (System.IO.File.Exists(_destination))
            {
                Vorticity.IO.MappedFileCache.ReleaseEverywhere(_destination);
            }

            System.IO.File.Move(_temporary, _destination, overwrite: true);
        }

        private protected override void Discard()
        {
            try
            {
                System.IO.File.Delete(_temporary);
            }
            catch (IOException)
            {
                // The file is this writer's alone, under a name no other writer takes: a failure here
                // leaves it behind, and is not worth hiding the error that led here.
            }
            catch (UnauthorizedAccessException)
            {
                // The same, for a directory that has become read-only.
            }
        }
    }
}
