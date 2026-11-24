using System;
using System.Collections.Generic;

namespace Vorticity;

/// <summary>
/// The arrays the code the generator emits fills a member's values in before it builds the records, held by what reads or
/// writes the records from one batch to the next: a typed scan's enumeration, a batch read alone, a builder.
/// </summary>
/// <remarks>
/// A member's values may be references, which no block of the pool holds, so the arrays are managed and owned rather than
/// rented from the runtime's shared pool, whose arrays a thread and a core keep apart from any account. Each array is kept
/// for its member, its nesting depth and its use, and grows by doubling to the longest batch met.
/// </remarks>
internal sealed class RecordScratch
{
    private readonly Dictionary<(Type Type, int Depth, int Slot), Array> _arrays = [];

    /// <summary>An array of <paramref name="count"/> elements at least, kept for <paramref name="depth"/> and <paramref name="slot"/>.</summary>
    internal T[] Take<T>(int depth, int slot, int count)
    {
        (Type, int, int) key = (typeof(T), depth, slot);
        if (_arrays.TryGetValue(key, out Array? held) && held.Length >= count)
        {
            return (T[])held;
        }

        T[] made = new T[Math.Max(count, 2 * (held?.Length ?? 0))];
        _arrays[key] = made;
        return made;
    }
}
