using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.Writing;

/// <summary>
/// An append-only byte store, in memory up to a budget and on disk beyond it. The synchronous
/// members serve it while it is in memory; its file is read and written asynchronously.
/// </summary>
internal sealed class RunScratch : IDisposable
{
    internal const int PageBytes = 1 << 20;

    private readonly long _memoryBudget;
    private readonly string _directory;
    private readonly List<byte[]> _pages = [];
    private SafeFileHandle? _file;
    private long _length;
    private long _admitted;

    /// <summary>
    /// Whether runs held outside the store may stay in memory, charged against the same budget as
    /// its pages. A refusal moves the store to disk, and once it is there, nothing more is admitted.
    /// </summary>
    internal async ValueTask<bool> AdmitAsync(long bytes, CancellationToken cancellationToken)
    {
        if (Fits(bytes))
        {
            _admitted += bytes;
            return true;
        }

        await SpillAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Gives back bytes <see cref="AdmitAsync"/> granted.</summary>
    internal void Release(long bytes) => _admitted = Math.Max(0, _admitted - bytes);

    /// <summary>A null directory spills to the system's temporary directory.</summary>
    internal RunScratch(long memoryBudget, string? directory)
    {
        _memoryBudget = Math.Max(memoryBudget, 0);
        _directory = directory ?? Path.GetTempPath();
    }

    internal long Length => _length;

    /// <summary>Whether the store has moved to its file.</summary>
    internal bool OnDisk => _file is not null;

    /// <summary>Whether bytes appended now stay in memory.</summary>
    internal bool Fits(long bytes) => _file is null && _admitted + _length + bytes <= _memoryBudget;

    /// <summary>Appends bytes that <see cref="Fits"/> accepts and returns where they start.</summary>
    internal long Append(ReadOnlySpan<byte> bytes)
    {
        long offset = _length;
        if (bytes.IsEmpty)
        {
            return offset;
        }

        if (!Fits(bytes.Length))
        {
            throw new InvalidOperationException("The scratch cannot keep these bytes in memory; they go to its file asynchronously.");
        }

        while (!bytes.IsEmpty)
        {
            int page = (int)(_length / PageBytes);
            int at = (int)(_length % PageBytes);
            if (page == _pages.Count)
            {
                _pages.Add(ArrayPool<byte>.Shared.Rent(PageBytes));
            }

            int take = Math.Min(PageBytes - at, bytes.Length);
            bytes[..take].CopyTo(_pages[page].AsSpan(at));
            bytes = bytes[take..];
            _length += take;
        }

        return offset;
    }

    /// <summary>Appends values that <see cref="Fits"/> accepts, in the platform's byte order, and returns where they start.</summary>
    internal long Append<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        Append(MemoryMarshal.AsBytes(values));

    /// <summary>
    /// Appends the bytes and returns where they start: in memory while they fit, completing at
    /// once, and in the file past that, the store moving there first.
    /// </summary>
    internal ValueTask<long> AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        bytes.IsEmpty || Fits(bytes.Length)
            ? new ValueTask<long>(Append(bytes.Span))
            : AppendToFileAsync(bytes, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> AppendToFileAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await SpillAsync(cancellationToken).ConfigureAwait(false);
        long offset = _length;
        await RandomAccess.WriteAsync(_file!, bytes, offset, cancellationToken).ConfigureAwait(false);
        _length += bytes.Length;
        return offset;
    }

    /// <summary>Reads bytes of the store while it is in memory.</summary>
    internal void Read(long offset, Span<byte> destination)
    {
        CheckRange(offset, destination.Length);
        if (_file is not null)
        {
            throw new InvalidOperationException("The scratch is on disk; its file is read asynchronously.");
        }

        while (!destination.IsEmpty)
        {
            int page = (int)(offset / PageBytes);
            int at = (int)(offset % PageBytes);
            int take = Math.Min(PageBytes - at, destination.Length);
            _pages[page].AsSpan(at, take).CopyTo(destination);
            destination = destination[take..];
            offset += take;
        }
    }

    /// <summary>Reads values of the store while it is in memory.</summary>
    internal void Read<T>(long offset, Span<T> destination)
        where T : unmanaged =>
        Read(offset, MemoryMarshal.AsBytes(destination));

    /// <summary>Reads bytes of the store: from memory, completing at once, or from its file.</summary>
    internal ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_file is null)
        {
            Read(offset, destination.Span);
            return ValueTask.CompletedTask;
        }

        CheckRange(offset, destination.Length);
        return ReadFileAsync(offset, destination, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadFileAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            int read = await RandomAccess.ReadAsync(_file!, destination, offset, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException($"the scratch file ended at {offset}");
            }

            destination = destination[read..];
            offset += read;
        }
    }

    private void CheckRange(long offset, int length)
    {
        if (offset < 0 || offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"[{offset}, {offset + length}) is outside the scratch's {_length} bytes");
        }
    }

    /// <summary>Moves the store to its file, page by page; nothing when it is there already.</summary>
    private async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        if (_file is not null)
        {
            return;
        }

        string path = Path.Combine(_directory, $"vorticity-runs-{Guid.NewGuid():N}.tmp");
        SafeFileHandle file = System.IO.File.OpenHandle(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            long position = 0;
            for (int i = 0; i < _pages.Count; i++)
            {
                int length = (int)Math.Min(PageBytes, _length - position);
                await RandomAccess.WriteAsync(file, _pages[i].AsMemory(0, length), position, cancellationToken).ConfigureAwait(false);
                position += length;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }

        // The pages go only once the file holds them: a spill that fails leaves the store in memory.
        _file = file;
        foreach (byte[] page in _pages)
        {
            ArrayPool<byte>.Shared.Return(page);
        }

        _pages.Clear();
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        foreach (byte[] page in _pages)
        {
            ArrayPool<byte>.Shared.Return(page);
        }

        _pages.Clear();
        _length = 0;
    }
}
