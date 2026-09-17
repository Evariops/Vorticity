// Where the locating builders keep their chunk runs until the file ends - docs/13-dataset.md §6.1.
//
// A RUN PER CHUNK IS HOW THE BUILDERS WORK, AND ONE RUN PER ENTRY IS WHAT A READER NEEDS. A chunk's
// run is built in flux from the chunk's own table (11 §3.2.2), but a lookup on a key uncorrelated
// with row order would probe every chunk's run. So the chunk runs are not written to the file: they
// are laid here, raw, and merged into one run when the data ends. The sink stays forward-only.
//
// MEMORY FIRST, THEN A FILE. A chunk run is held as the builder's own arrays while the budget
// (`VortexWriteOptions.ScratchMemoryBytes`) admits it, and the merge reads those arrays in place;
// past the budget, runs are laid in this store, in rented one-mebibyte pages, and everything moves
// to a temporary file in `VortexWriteOptions.ScratchDirectory`, deleted when the scratch is
// disposed. A small file never touches the disk; a ten-gibibyte one does not hold its index in
// memory.
//
// SYNCHRONOUS, ON PURPOSE. The merge that reads the scratch runs in one call at the end of the data,
// over a local file this writer owns; a memory-mapped read path faults pages the same way.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Vorticity.Writing;

/// <summary>An append-only byte store, in memory up to a budget and on disk beyond it.</summary>
internal sealed class RunScratch : IDisposable
{
    /// <summary>The size of one memory page.</summary>
    internal const int PageBytes = 1 << 20;

    private readonly long _memoryBudget;
    private readonly string _directory;
    private readonly List<byte[]> _pages = [];
    private FileStream? _file;
    private long _length;
    private long _admitted;

    /// <summary>
    /// Admits <paramref name="bytes"/> of runs held in memory outside the store, under the same
    /// budget as its pages; once the store is on disk, nothing more is admitted.
    /// </summary>
    /// <param name="bytes">What the runs hold.</param>
    /// <returns>Whether the caller may keep them in memory; otherwise it lays them in the store.</returns>
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
    /// <param name="bytes">What was admitted.</param>
    internal void Release(long bytes) => _admitted = Math.Max(0, _admitted - bytes);

    /// <param name="memoryBudget">The bytes held in memory before the store moves to a file.</param>
    /// <param name="directory">Where the file goes; the system's temporary directory when null.</param>
    internal RunScratch(long memoryBudget, string? directory)
    {
        _memoryBudget = Math.Max(memoryBudget, 0);
        _directory = directory ?? Path.GetTempPath();
    }

    /// <summary>The bytes appended.</summary>
    internal long Length => _length;

    /// <summary>Whether the store has moved to its file.</summary>
    internal bool OnDisk => _file is not null;

    /// <summary>Appends <paramref name="bytes"/> and returns where they start.</summary>
    /// <param name="bytes">The bytes.</param>
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

    /// <summary>Appends the bytes of <paramref name="values"/> and returns where they start.</summary>
    /// <param name="values">The values, little-endian as the platform lays them.</param>
    internal long Append<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        Append(MemoryMarshal.AsBytes(values));

    /// <summary>Reads <paramref name="destination"/>'s length of bytes at <paramref name="offset"/>.</summary>
    /// <param name="offset">Where to read.</param>
    /// <param name="destination">Where the bytes go.</param>
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

    /// <summary>Reads values of <typeparamref name="T"/> at <paramref name="offset"/>.</summary>
    /// <param name="offset">Where to read.</param>
    /// <param name="destination">Where the values go.</param>
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

    /// <summary>Gives the pages back and deletes the file.</summary>
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
