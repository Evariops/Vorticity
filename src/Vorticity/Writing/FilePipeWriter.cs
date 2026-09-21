using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
/// byte of what it still held.
/// </remarks>
internal sealed class FilePipeWriter : PipeWriter
{
    private const int SegmentBytes = 256 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly MemoryPool<byte> _pool;
    private readonly long _origin;
    private readonly List<(IMemoryOwner<byte> Owner, int Length)> _filled = [];
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

    /// <summary>Creates <paramref name="path"/>, replacing a file already there.</summary>
    internal static FilePipeWriter Create(string path, MemoryPool<byte> pool) =>
        new FilePipeWriter(System.IO.File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous), 0, pool);

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

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        Seal();
        if (_filled.Count > 0)
        {
            _gather.Clear();
            long bytes = 0;
            foreach ((IMemoryOwner<byte> owner, int length) in _filled)
            {
                _gather.Add(owner.Memory[..length]);
                bytes += length;
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

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (!_completed && exception is null)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Close();
    }

    /// <summary>Drops what was not flushed, truncates the file back to where this writer started, and closes it.</summary>
    internal void Abandon()
    {
        if (_completed)
        {
            return;
        }

        try
        {
            RandomAccess.SetLength(_handle, _origin);
        }
        finally
        {
            Close();
        }
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
            _filled.Add((_current, _currentLength));
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
        foreach ((IMemoryOwner<byte> owner, int _) in _filled)
        {
            owner.Dispose();
        }

        _filled.Clear();
    }
}
