using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vorticity.Keys;

/// <summary>
/// What a dictionary probe found (<see cref="SortedRunsSource.ClaimDictionariesAsync"/>): each chunk it read whose
/// dictionary it could ask, with its first row, its rows and the keys its dictionary holds; and what it asked of the
/// file's source.
/// </summary>
internal sealed class DictionaryClaims(int keys)
{
    private readonly List<(long FirstRow, long Rows, int First)> _chunks = [];
    private readonly List<int> _held = [];

    // Whether the chunk last opened holds each key, cleared for the keys it held when the next one opens.
    private readonly bool[] _holds = new bool[keys];

    /// <summary>The chunks asked.</summary>
    internal int Count => _chunks.Count;

    /// <summary>The segments asked of the source, those the file's tail holds not among them.</summary>
    internal int Read { get; set; }

    /// <summary>Their bytes.</summary>
    internal long Bytes { get; set; }

    /// <summary>Chunk <paramref name="chunk"/>'s first row and its rows.</summary>
    internal (long FirstRow, long Rows) Extent(int chunk) => (_chunks[chunk].FirstRow, _chunks[chunk].Rows);

    /// <summary>The keys chunk <paramref name="chunk"/>'s dictionary holds, by their places among those asked.</summary>
    internal ReadOnlySpan<int> HeldBy(int chunk)
    {
        int first = _chunks[chunk].First;
        int end = chunk + 1 < _chunks.Count ? _chunks[chunk + 1].First : _held.Count;
        return CollectionsMarshal.AsSpan(_held)[first..end];
    }

    /// <summary>Starts the next chunk, of <paramref name="rows"/> rows from <paramref name="firstRow"/>.</summary>
    internal void Open(long firstRow, long rows)
    {
        if (_chunks.Count > 0)
        {
            foreach (int key in HeldBy(_chunks.Count - 1))
            {
                _holds[key] = false;
            }
        }

        _chunks.Add((firstRow, rows, _held.Count));
    }

    /// <summary>Records that the chunk last opened holds key <paramref name="key"/>; false when it was recorded already.</summary>
    internal bool Hold(int key)
    {
        if (_holds[key])
        {
            return false;
        }

        _holds[key] = true;
        _held.Add(key);
        return true;
    }
}
