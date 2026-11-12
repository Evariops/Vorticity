using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// The object entries a dataset handle parsed out of its tree pages, by where their bytes lie: the page's array, the
/// page's offset in it and the entry's place in the page, so that a walk of every version that holds the page parses each
/// entry once.
/// </summary>
/// <remarks>
/// <para>
/// A page's bytes are an array of their own that nothing writes again: read from the store, held from a commit object,
/// or built by a commit. The same array and offset are therefore the same page for as long as the array lives, and a
/// page the handle's cache lets go takes its entries with it. A warm question over 48 objects parsed each of them again
/// at every walk, a fifth of the bytes it allocated.
/// </para>
/// <para>
/// A walk looks a page up once (<see cref="SlotsOf"/>) and each entry by its place in it (<see cref="Of"/>): no lock and
/// no hashing an entry, where a dictionary of a page's entries by their offset was locked twice for each entry parsed and
/// once for each found.
/// </para>
/// </remarks>
internal sealed class EntryMemo
{
    private readonly ConditionalWeakTable<byte[], Pages> _pages = [];

    // The entries a reader met outside a walk of pages, by where their values lie in their arrays (OfValue).
    private readonly ConditionalWeakTable<byte[], System.Collections.Concurrent.ConcurrentDictionary<int, ObjectEntry>> _values = [];

    /// <summary>
    /// The places of the entries of the leaf page <paramref name="page"/>, which holds <paramref name="count"/>; null when
    /// its bytes are not an array's, whose entries are parsed at every walk.
    /// </summary>
    internal ObjectEntry?[]? SlotsOf(ReadOnlyMemory<byte> page, int count)
    {
        if (!MemoryMarshal.TryGetArray(page, out ArraySegment<byte> segment) || segment.Array is not { } array)
        {
            return null;
        }

        Pages pages = _pages.GetValue(array, static _ => new Pages());
        while (true)
        {
            PageSlots? head = Volatile.Read(ref pages.Head);
            for (PageSlots? at = head; at is not null; at = at.Next)
            {
                if (at.Offset == segment.Offset && at.Length == segment.Count)
                {
                    return at.Slots;
                }
            }

            // A page says how many entries it holds, and each takes at least three bytes: a count past that is a page that
            // fails to read, not an array to make.
            PageSlots made = new PageSlots(segment.Offset, segment.Count, new ObjectEntry?[Math.Clamp(count, 0, segment.Count / 3)], head);
            if (Interlocked.CompareExchange(ref pages.Head, made, head) == head)
            {
                return made.Slots;
            }
        }
    }

    /// <summary>
    /// The entry <paramref name="value"/> holds, the <paramref name="index"/>-th of a page whose places
    /// <paramref name="slots"/> are: parsed the first time a walk meets it.
    /// </summary>
    internal static ObjectEntry Of(ObjectEntry?[]? slots, int index, ReadOnlyMemory<byte> value)
    {
        if (slots is null || (uint)index >= (uint)slots.Length)
        {
            return ObjectEntry.FromBytes(value);
        }

        if (Volatile.Read(ref slots[index]) is { } known)
        {
            return known;
        }

        ObjectEntry parsed = ObjectEntry.FromBytes(value);
        return Interlocked.CompareExchange(ref slots[index], parsed, null) ?? parsed;
    }

    /// <summary>
    /// The entry <paramref name="value"/> holds, met outside a walk of its page, as the plan of the compactions meets the
    /// last entries of a level: parsed once for where it lies in its array, which nothing writes again; at every asking
    /// for bytes that are not an array's.
    /// </summary>
    internal ObjectEntry OfValue(ReadOnlyMemory<byte> value)
    {
        if (!MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) || segment.Array is not { } array)
        {
            return ObjectEntry.FromBytes(value);
        }

        System.Collections.Concurrent.ConcurrentDictionary<int, ObjectEntry> held = _values.GetValue(array, static _ => new());
        return held.TryGetValue(segment.Offset, out ObjectEntry? known) ? known : held.GetOrAdd(segment.Offset, ObjectEntry.FromBytes(value));
    }

    /// <summary>The pages one array holds, most often one.</summary>
    private sealed class Pages
    {
        internal PageSlots? Head;
    }

    /// <summary>One page of an array: where it lies, and the places of its entries.</summary>
    private sealed record PageSlots(int Offset, int Length, ObjectEntry?[] Slots, PageSlots? Next);
}
