using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Dataset;

/// <summary>
/// The object entries a dataset handle parsed out of its tree pages, by where their bytes lie: the page's array
/// and the entry's offset in it, so that a walk of every version that holds the page parses each entry once.
/// </summary>
/// <remarks>
/// A page's bytes are an array of their own that nothing writes again: read from the store, held from a commit
/// object, or built by a commit. The same array and offset are therefore the same entry for as long as the array
/// lives, and a page the handle's cache lets go takes its entries with it. A warm question over 48 objects parsed
/// each of them again at every walk, a fifth of the bytes it allocated.
/// </remarks>
internal sealed class EntryMemo
{
    private readonly ConditionalWeakTable<byte[], Dictionary<int, ObjectEntry>> _pages = [];

    /// <summary>The entry <paramref name="value"/> holds, parsed the first time a walk meets its bytes.</summary>
    internal ObjectEntry Of(ReadOnlyMemory<byte> value)
    {
        if (!MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) || segment.Array is not { } array)
        {
            return ObjectEntry.FromBytes(value);
        }

        Dictionary<int, ObjectEntry> entries = _pages.GetValue(array, static _ => []);
        lock (entries)
        {
            if (entries.TryGetValue(segment.Offset, out ObjectEntry? known))
            {
                return known;
            }
        }

        ObjectEntry parsed = ObjectEntry.FromBytes(value);
        lock (entries)
        {
            return entries.TryAdd(segment.Offset, parsed) ? parsed : entries[segment.Offset];
        }
    }
}
