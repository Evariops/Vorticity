using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.IO;

/// <summary>
/// The files a session keeps mapped once closed, so that opening one again finds its mapping and
/// the pages already mapped in it, instead of mapping the file again and faulting every page anew.
/// </summary>
/// <remarks>
/// <para>
/// A file is known by its <see cref="FileInode"/>, never by its name: a file replaced under the
/// same name is another file, mapped on its own. A mapping is shared with the file, so a file
/// rewritten in place reads as it now is; a file whose length changed is mapped again.
/// </para>
/// <para>
/// Past the capacity, the file opened longest ago leaves. A file whose name now names another
/// leaves at once, so that a replaced file is not kept allocated on disk by a mapping nobody will
/// ask for again. Leaving drops the cache's reference only: an open file keeps reading its mapping.
/// </para>
/// </remarks>
internal sealed class MappedFileCache
{
    private readonly Lock _gate = new();
    private readonly Entry[] _entries;
    private int _count;
    private long _clock;

    /// <param name="capacity">The most closed files kept mapped; positive.</param>
    internal MappedFileCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new Entry[capacity];
    }

    /// <summary>How many files are kept mapped.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The mapping of the file behind <paramref name="handle"/>, with a reference the caller releases.</summary>
    /// <param name="identity">The file's identity.</param>
    /// <param name="path">The name it was opened under.</param>
    /// <param name="handle">An open handle on it, to map it when it is not kept.</param>
    /// <param name="length">Its length now.</param>
    internal MappedFileOwner Acquire(FileInode identity, string path, SafeFileHandle handle, long length)
    {
        lock (_gate)
        {
            int kept = Find(identity);
            if (kept >= 0)
            {
                if (_entries[kept].Length == length)
                {
                    return Use(kept);
                }

                RemoveAt(kept);
            }
        }

        MappedFileOwner mapped = MappedFileOwner.Map(handle, length, ownsHandle: false);
        lock (_gate)
        {
            // Another open of the same file may have mapped it meanwhile.
            int kept = Find(identity);
            if (kept >= 0 && _entries[kept].Length == length)
            {
                mapped.Dispose();
                return Use(kept);
            }

            if (kept >= 0)
            {
                RemoveAt(kept);
            }

            for (int i = _count - 1; i >= 0; i--)
            {
                if (string.Equals(_entries[i].Path, path, StringComparison.Ordinal))
                {
                    RemoveAt(i);
                }
            }

            if (_count == _entries.Length)
            {
                RemoveAt(OldestIndex());
            }

            _entries[_count++] = new Entry(identity, path, length, mapped, ++_clock);
            return (MappedFileOwner)mapped.Retain();
        }
    }

    /// <summary>Lets every kept mapping go; the files still open keep reading theirs.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            while (_count > 0)
            {
                RemoveAt(_count - 1);
            }
        }
    }

    private MappedFileOwner Use(int index)
    {
        _entries[index].LastUse = ++_clock;
        return (MappedFileOwner)_entries[index].Mapping.Retain();
    }

    private int Find(FileInode identity)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_entries[i].Identity == identity)
            {
                return i;
            }
        }

        return -1;
    }

    private int OldestIndex()
    {
        int oldest = 0;
        for (int i = 1; i < _count; i++)
        {
            if (_entries[i].LastUse < _entries[oldest].LastUse)
            {
                oldest = i;
            }
        }

        return oldest;
    }

    private void RemoveAt(int index)
    {
        MappedFileOwner mapping = _entries[index].Mapping;
        _entries[index] = _entries[--_count];
        _entries[_count] = default;
        mapping.Dispose();
    }

    private struct Entry(FileInode identity, string path, long length, MappedFileOwner mapping, long lastUse)
    {
        internal readonly FileInode Identity = identity;
        internal readonly string Path = path;
        internal readonly long Length = length;
        internal readonly MappedFileOwner Mapping = mapping;
        internal long LastUse = lastUse;
    }
}
