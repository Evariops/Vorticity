using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Vorticity.Writing;

/// <summary>An append-only byte store, in memory up to a budget and on disk beyond it.</summary>
internal sealed class RunScratch : IDisposable
{
    internal const int PageBytes = 1 << 20;

    private readonly long _memoryBudget;
    private readonly string _directory;
    private readonly List<byte[]> _pages = [];
    private FileStream? _file;
    private long _length;
    private long _admitted;

    /// <summary>
    /// Whether runs held outside the store may stay in memory, charged against the same budget as
    /// its pages; once the store is on disk, nothing more is admitted.
    /// </summary>
    internal bool Admit(long bytes)
    {
        if (_file is null && _admitted + _length + bytes <= _memoryBudget)
        {
            _admitted += bytes;
            return true;
        }

        if (_file is null)
        {
            Spill();
        }

        return false;
    }

    /// <summary>Gives back bytes <see cref="Admit"/> granted.</summary>
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

    /// <summary>Appends the bytes and returns where they start.</summary>
    internal long Append(ReadOnlySpan<byte> bytes)
    {
        long offset = _length;
        if (bytes.IsEmpty)
        {
            return offset;
        }

        if (_file is null && _admitted + _length + bytes.Length > _memoryBudget)
        {
            Spill();
        }

        if (_file is not null)
        {
            RandomAccess.Write(_file.SafeFileHandle, bytes, _length);
            _length += bytes.Length;
            return offset;
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

    /// <summary>Appends the values in the platform's byte order and returns where they start.</summary>
    internal long Append<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        Append(MemoryMarshal.AsBytes(values));

    internal void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"[{offset}, {offset + destination.Length}) is outside the scratch's {_length} bytes");
        }

        if (_file is not null)
        {
            while (!destination.IsEmpty)
            {
                int read = RandomAccess.Read(_file.SafeFileHandle, destination, offset);
                if (read <= 0)
                {
                    throw new IOException($"the scratch file ended at {offset}");
                }

                destination = destination[read..];
                offset += read;
            }

            return;
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

    internal void Read<T>(long offset, Span<T> destination)
        where T : unmanaged =>
        Read(offset, MemoryMarshal.AsBytes(destination));

    private void Spill()
    {
        string path = Path.Combine(_directory, $"vorticity-runs-{Guid.NewGuid():N}.tmp");
        _file = new FileStream(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        long position = 0;
        foreach (byte[] page in _pages)
        {
            int length = (int)Math.Min(PageBytes, _length - position);
            RandomAccess.Write(_file.SafeFileHandle, page.AsSpan(0, length), position);
            position += length;
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
