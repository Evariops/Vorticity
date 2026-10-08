using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of a key wider than a word, 16 or 32 bytes (a decimal, a UUID, a short text's word), in
/// slots of 8 bytes: the key's hash of 32 bits and its group, the key itself read from the keys of the
/// groups, which <see cref="FixedKeys{TValue}"/> holds anyway. Otherwise <see cref="KeyTable{TValue}"/>:
/// a key's home is its hash modulo a prime, the first free slot of the home's line past it, then the
/// line's chain, a seed past a chain of <see cref="MaxChain"/> links; every key lands where it would there.
/// </summary>
/// <remarks>
/// <para>
/// A slot of <see cref="KeyTable{TValue}"/> holds its key: 24 bytes for a short text's word, 32 for an
/// integer of 128 bits, which .NET aligns on 16, 40 for one of 256; two to a line of 64, or one; the key
/// held a second time in the groups' keys. With the table three tenths to six full, a group took 56 to
/// 96 bytes as a word, 69 to 123 as a UUID: what let a short text's words hold no more than a few
/// thousand groups (<see cref="ShortTextKeys"/>). Here a line holds eight slots, and a group takes 29 to
/// 43. A key found pays a second read, the key of the group its slot names, once the hash agrees: 32
/// bits, so that another key's group is read one time in four billion.
/// </para>
/// <para>
/// A growth places each slot again by the hash it keeps, reading no key; a seed hashes every key again,
/// read from the groups' keys.
/// </para>
/// </remarks>
internal struct WideKeyTable<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>The links of a chain past which the table takes a seed.</summary>
    private const int MaxChain = 64;

    /// <summary>The slots of a table's first growth: a prime.</summary>
    private const int FirstSlots = 31;

    /// <summary>The slots of a line of 64 bytes.</summary>
    private const int Width = 8;

    private const int WidthShift = 3;

    // The slots, from the first word of a line of 64 bytes of their store, as KeyTable lays them.
    private ulong[] _store;
    private int _base;
    private int _length;
    private int[] _chains;
    private Entry[] _overflow;
    private int _overflowed;
    private ulong _multiplier;
    private ulong _seed;
    private int _count;
    private int _growAt;
    private readonly ArrayShelf? _shelf;

    public WideKeyTable()
    {
        _store = [];
        _chains = [];
        _overflow = [];
    }

    /// <summary>A table that takes its arrays from a query's shelf and gives them back as it grows: a sub-table of the core.</summary>
    internal WideKeyTable(ArrayShelf shelf)
        : this() => _shelf = shelf;

    /// <summary>The slots, from the first line of their store.</summary>
    private readonly Span<Slot> Slots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, Slot>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_store), _base)), _length);
    }

    /// <summary>The keys the table holds.</summary>
    internal readonly int Count => _count;

    /// <summary>Whether a chain grew long enough for the table to take a seed.</summary>
    internal readonly bool Seeded => _seed != 0;

    /// <summary>The bytes of the slots, the chains' heads and their links.</summary>
    internal readonly long Footprint =>
        ((long)_store.Length * sizeof(ulong)) + ((long)_chains.Length * sizeof(int)) + ((long)_overflow.Length * Unsafe.SizeOf<Entry>());

    /// <summary>
    /// The bytes the table would take more were <paramref name="more"/> new keys to come: past six tenths
    /// full it doubles, its store and its chains twice what they hold. Its chains may make it grow sooner.
    /// </summary>
    internal readonly long GrowthFor(int more)
    {
        long fills = _overflowed > 0 ? Math.Min(_growAt, 3L * _length / 10) : _growAt;
        if (_count + (long)more <= fills)
        {
            return 0;
        }

        long bytes = Math.Max(2 * Footprint, (long)FirstSlots * Unsafe.SizeOf<Slot>());
        for (long fill = Math.Max(1, _growAt) * 2; _count + (long)more > fill; fill *= 2)
        {
            bytes *= 2;
        }

        return bytes;
    }

    /// <summary>
    /// The group of <paramref name="key"/>, the groups' keys in <paramref name="keys"/>; when the key is new,
    /// <paramref name="next"/>, which it then holds, and which the caller's keys hold from then on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetOrAdd(TValue key, int next, ReadOnlySpan<TValue> keys)
    {
        Span<Slot> slots = Slots;
        if (slots.Length != 0)
        {
            uint hash = HashOf(key, _seed);
            ref Slot slot = ref slots[(int)KeyTable<TValue>.FastMod(hash, (uint)slots.Length, _multiplier)];
            if (slot.Group != 0)
            {
                if (slot.Hash == hash && keys[slot.Group - 1].Equals(key))
                {
                    return slot.Group - 1;
                }
            }
            else if (_count < _growAt)
            {
                slot.Hash = hash;
                slot.Group = next + 1;
                _count++;
                return next;
            }
        }

        return Search(key, next, keys);
    }

    /// <summary>
    /// Each key's group where the key sits in its home slot, -1 where it does not, with no branch on the
    /// keys, as <see cref="KeyTable{TValue}.FindAtHome"/> finds them: the slot's hash compared, then the
    /// key of the group it names, the first group's read where it names none.
    /// </summary>
    /// <param name="keys">The keys of the batch.</param>
    /// <param name="groups">Each key's group, or -1.</param>
    /// <param name="hashes">Room for each key's hash, as many as the keys, when <paramref name="ahead"/> reads ahead.</param>
    /// <param name="ahead">How many rows on a slot is read before its row compares; 0 for none.</param>
    /// <param name="held">The keys of the groups.</param>
    /// <param name="missed">Whether a key found no group: the rows left to the lookup, none when false.</param>
    internal readonly int FindAtHome(ReadOnlySpan<TValue> keys, Span<int> groups, Span<uint> hashes, int ahead, ReadOnlySpan<TValue> held, out bool missed)
    {
        Span<Slot> slots = Slots;
        if (slots.Length == 0 || held.IsEmpty)
        {
            groups[..keys.Length].Fill(-1);
            missed = keys.Length > 0;
            return 0;
        }

        groups = groups[..keys.Length];
        ulong seed = _seed;
        ulong multiplier = _multiplier;
        uint length = (uint)slots.Length;
        ref Slot first = ref MemoryMarshal.GetReference(slots);
        ref TValue firstKey = ref MemoryMarshal.GetReference(held);
        int any = 0;
        if (ahead <= 0)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                int group = Found(ref first, ref firstKey, HashOf(keys[i], seed), keys[i], length, multiplier);
                groups[i] = group;
                any |= group;
            }

            missed = any < 0;
            return 0;
        }

        hashes = hashes[..keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            hashes[i] = HashOf(keys[i], seed);
        }

        int sink = 0;
        int row = 0;
        for (int touched = keys.Length - ahead; row < touched; row++)
        {
            sink ^= Unsafe.Add(ref first, (nint)KeyTable<TValue>.FastMod(hashes[row + ahead], length, multiplier)).Group;
            int group = Found(ref first, ref firstKey, hashes[row], keys[row], length, multiplier);
            groups[row] = group;
            any |= group;
        }

        for (; row < keys.Length; row++)
        {
            int group = Found(ref first, ref firstKey, hashes[row], keys[row], length, multiplier);
            groups[row] = group;
            any |= group;
        }

        missed = any < 0;
        return sink;
    }

    /// <summary>
    /// The group of <paramref name="key"/> if it sits in its home slot, else -1: the slot's group where its
    /// hash agrees, then that group's key compared, both as masks rather than branches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Found(ref Slot first, ref TValue firstKey, uint hash, TValue key, uint length, ulong multiplier)
    {
        ref Slot home = ref Unsafe.Add(ref first, (nint)KeyTable<TValue>.FastMod(hash, length, multiplier));
        int candidate = (home.Group & -Unsafe.BitCast<bool, byte>(home.Hash == hash)) - 1;
        int same = Unsafe.BitCast<bool, byte>(Unsafe.Add(ref firstKey, (nint)(uint)(candidate & ~(candidate >> 31))).Equals(key));
        return ((candidate + 1) & -same) - 1;
    }

    /// <summary>Makes room for <paramref name="keys"/> keys without growing on the way.</summary>
    internal void Reserve(int keys)
    {
        long slots = ((10L * keys) / 6) + 1;
        if (slots > _length)
        {
            Resize(KeyTable<TValue>.PrimeAtLeast((int)Math.Min(int.MaxValue / 2, slots)));
        }
    }

    /// <summary>
    /// Forgets every key, keeping the slots and the seed, with room for <paramref name="room"/> keys: a
    /// table about to take as many keys as it held, beside those kept, doubles now, empty, rather than in
    /// the middle of its next batch with every key placed again.
    /// </summary>
    /// <remarks>
    /// A streaming key keeps its open groups at each batch closed (<see cref="FixedKeys{TValue}.Keep"/>):
    /// a day's few names, then the next batch's. Batches of 164 names, five kept, passed the growth point
    /// of 277 slots in the second batch, past the first read: an allocation once the stream was warm.
    /// </remarks>
    internal void Clear(int room)
    {
        Array.Clear(_store);
        Array.Clear(_chains);
        _overflowed = 0;
        _count = 0;
        if (_length > 0 && room > _growAt)
        {
            int length = _length;
            while ((int)(6L * length / 10) < room)
            {
                length = KeyTable<TValue>.PrimeAtLeast(GroupKeys.Doubled(length));
            }

            Resize(length);
        }
    }

    /// <summary>Gives the table's arrays back to its shelf: a sub-table split, whose groups another holds.</summary>
    internal void Release()
    {
        _shelf?.Give(_store);
        _shelf?.Give(_chains);
        _shelf?.Give(_overflow);
        _store = [];
        _base = 0;
        _length = 0;
        _chains = [];
        _overflow = [];
        _overflowed = 0;
        _count = 0;
        _growAt = 0;
    }

    /// <summary>A key whose home is taken: the rest of the home's line, then the line's chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Search(TValue key, int next, ReadOnlySpan<TValue> keys)
    {
        if (_length == 0)
        {
            Resize(FirstSlots);
        }

        Span<Slot> slots = Slots;
        uint hash = HashOf(key, _seed);
        uint at = KeyTable<TValue>.FastMod(hash, (uint)slots.Length, _multiplier);
        uint first = at & ~(uint)(Width - 1);
        uint end = Math.Min(first + (uint)Width, (uint)slots.Length);
        uint probe = at;
        do
        {
            ref Slot slot = ref slots[(int)probe];
            if (slot.Group == 0)
            {
                if (_count >= _growAt)
                {
                    Resize(KeyTable<TValue>.PrimeAtLeast(GroupKeys.Doubled(slots.Length)));
                    return Search(key, next, keys);
                }

                slot.Hash = hash;
                slot.Group = next + 1;
                _count++;
                return next;
            }

            if (slot.Hash == hash && keys[slot.Group - 1].Equals(key))
            {
                return slot.Group - 1;
            }

            probe = probe + 1 == end ? first : probe + 1;
        }
        while (probe != at);

        int links = 0;
        for (int link = _chains[at >> WidthShift]; link != 0; link = _overflow[link - 1].Next)
        {
            ref Entry entry = ref _overflow[link - 1];
            if (entry.Hash == hash && keys[entry.Group - 1].Equals(key))
            {
                return entry.Group - 1;
            }

            links++;
        }

        // A new key past a full line: the table grows if it is full, or if its chains hold one key in
        // eight while it is past three tenths full, or takes a seed if the chain is long.
        if (_count >= _growAt || (8L * _overflowed > _count && 10L * _count > 3L * slots.Length))
        {
            Resize(KeyTable<TValue>.PrimeAtLeast(GroupKeys.Doubled(slots.Length)));
            return Search(key, next, keys);
        }

        if (links >= MaxChain && _seed == 0)
        {
            Reseed((ulong)Random.Shared.NextInt64(1, long.MaxValue) | 1, keys);
            return Search(key, next, keys);
        }

        Chain(hash, next + 1, at);
        _count++;
        return next;
    }

    /// <summary>
    /// The table under <paramref name="seed"/>: every key hashed again from the groups' keys, then placed
    /// again. Internal for the tests, which force a seed.
    /// </summary>
    internal void Reseed(ulong seed, ReadOnlySpan<TValue> keys)
    {
        _seed = seed;
        Span<Slot> slots = Slots;
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot slot = ref slots[i];
            if (slot.Group != 0)
            {
                slot.Hash = HashOf(keys[slot.Group - 1], seed);
            }
        }

        for (int i = 0; i < _overflowed; i++)
        {
            ref Entry entry = ref _overflow[i];
            entry.Hash = HashOf(keys[entry.Group - 1], seed);
        }

        Resize(Math.Max(_length, FirstSlots));
    }

    /// <summary>
    /// The 32 bits a key is homed by and its slot keeps: as <see cref="KeyTable{TValue}"/> homes a key wider
    /// than a word, its two words folded, a short text's by CRC32C, mixed under the seed once there is one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashOf(TValue key, ulong seed)
    {
        (ulong low, ulong high) = KeyWords.Of(key);
        if (typeof(TValue) == typeof(TextWord))
        {
            return TextWord.Home(low, high, seed);
        }

        if (seed != 0)
        {
            return (uint)(MergeHash.Of(low, high, seed) >> 32);
        }

        ulong folded = low ^ high;
        return (uint)(folded ^ (folded >> 32));
    }

    /// <summary>Places every slot again in <paramref name="length"/> slots, a prime, by the hash each keeps.</summary>
    private void Resize(int length)
    {
        ulong[] oldStore = _store;
        Span<Slot> old = Slots;
        int[] chains = _chains;
        Entry[] overflow = _overflow;
        int overflowed = _overflowed;

        // The store a line longer than the slots, which start at its first line.
        _store = NewArray<ulong>(checked(length + (GroupRecords.Line / sizeof(ulong)) - 1));
        _base = KeyTable<TValue>.LineStart(_store);
        _length = length;
        _chains = NewArray<int>((length >> WidthShift) + 1);

        // Links to place again in an array of their own; none, the array kept, which the next chains
        // reuse: a cleared table's, else a doubling empty grew it again past the first read.
        bool kept = overflowed == 0;
        _overflow = kept ? overflow : NewArray<Entry>(overflowed);
        _overflowed = 0;
        _multiplier = (ulong.MaxValue / (uint)length) + 1;
        _growAt = (int)(6L * length / 10);
        foreach (Slot slot in old)
        {
            if (slot.Group != 0)
            {
                Place(slot.Hash, slot.Group);
            }
        }

        for (int i = 0; i < overflowed; i++)
        {
            Place(overflow[i].Hash, overflow[i].Group);
        }

        _shelf?.Give(oldStore);
        _shelf?.Give(chains);
        if (!kept)
        {
            _shelf?.Give(overflow);
        }
    }

    /// <summary>A slot placed again: the first free slot of its line from its home, or its line's chain.</summary>
    private void Place(uint hash, int group)
    {
        Span<Slot> slots = Slots;
        uint at = KeyTable<TValue>.FastMod(hash, (uint)slots.Length, _multiplier);
        uint first = at & ~(uint)(Width - 1);
        uint end = Math.Min(first + (uint)Width, (uint)slots.Length);
        uint probe = at;
        do
        {
            ref Slot slot = ref slots[(int)probe];
            if (slot.Group == 0)
            {
                slot.Hash = hash;
                slot.Group = group;
                return;
            }

            probe = probe + 1 == end ? first : probe + 1;
        }
        while (probe != at);

        Chain(hash, group, at);
    }

    /// <summary>Links a key at the head of the chain of the line of slot <paramref name="at"/>.</summary>
    private void Chain(uint hash, int group, uint at)
    {
        if (_overflowed == _overflow.Length)
        {
            Entry[] grown = NewArray<Entry>(Math.Max(16, 2 * _overflow.Length));
            _overflow.CopyTo(grown, 0);
            _shelf?.Give(_overflow);
            _overflow = grown;
        }

        ref int head = ref _chains[at >> WidthShift];
        _overflow[_overflowed] = new Entry { Hash = hash, Group = group, Next = head };
        head = ++_overflowed;
    }

    /// <summary>An array of the table's, zeroed: from its shelf when it has one.</summary>
    private readonly T[] NewArray<T>(int length) => _shelf is null ? new T[length] : _shelf.Take<T>(length, zeroed: true);

    /// <summary>A key's hash and its group plus one: zero, the slots' first value, an empty slot.</summary>
    private struct Slot
    {
        public uint Hash;
        public int Group;
    }

    /// <summary>A key past a full line: its hash, its group plus one, and the next link of its line's chain plus one.</summary>
    private struct Entry
    {
        public uint Hash;
        public int Group;
        public int Next;
    }
}
