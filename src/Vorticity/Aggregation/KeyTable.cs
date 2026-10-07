using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of a fixed-width key, in a table the engine owns: a slot
/// holds a key and its group, a key's home slot is its hash modulo a prime, by the fast modulo the
/// runtime's dictionary takes. An integer is its own hash, folded to 32 bits, so that keys in a row
/// land in slots in a row and a regular stride spreads over the prime; a float its bits, one pattern
/// a value. A key whose home is taken goes to the first free slot of the home's line (64 bytes of
/// slots), and past a full line to the line's chain, in an array of its own.
/// </summary>
/// <remarks>
/// <para>
/// A key of eight bytes at most loses, before its fold, the low bits that every key the table held when
/// it was last placed had zero: keys at a stride of a power of two are then numbers in a row. Folded
/// whole, keys at a stride of 2²² past 2³² made runs of ten homes in a row, which a prime laid over
/// each other or apart by chance: 12 % of 10⁴ keys away from home under 17 929 slots, 73 % under 17 989,
/// none under 17 959.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// The line bounds the probe: a run of keys in a row fills its lines, and a key that lands in it costs
/// a link of a chain, where a probe past the line would walk the run. Linear probing over the whole
/// table, measured on 2026-10-06, read hot keys in a row among keys spread over a range folded on
/// them (the bench's <c>drift</c>) three times slower than the dictionary, and keys at a stride of
/// 2²² or a permutation of the integers twice, both after a probe of 256 slots made it take a seed.
/// </para>
/// <para>
/// Keys can still be built to share their home modulo any prime: a chain of <see cref="MaxChain"/>
/// links makes the table take a seed of its own and place every key again by a mix of it, which keys
/// built against the identity cannot follow. Before the seed, a table that sends one key in eight to
/// its chains while it is past three tenths full doubles: two runs folded onto each other modulo a
/// prime a little smaller than their range lie apart modulo the next.
/// </para>
/// </remarks>
internal struct KeyTable<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>The links of a chain past which the table takes a seed.</summary>
    private const int MaxChain = 64;

    /// <summary>The slots of a table's first growth: a prime.</summary>
    private const int FirstSlots = 31;

    // The slots, from the first word of a line of 64 bytes of their store: a line of slots is then
    // one of cache, where the data of an array of slots starts anywhere a word starts, and a slot of
    // 32 bytes lay across two lines one time in three.
    private ulong[] _store;
    private int _base;
    private int _length;
    private int[] _chains;
    private Entry[] _overflow;
    private int _overflowed;
    private ulong _multiplier;
    private ulong _seed;

    // The low bits no key had set when the table was last placed, which its homes leave out.
    private int _shift;
    private int _count;
    private int _growAt;
    private readonly ArrayShelf? _shelf;

    public KeyTable()
    {
        _store = [];
        _chains = [];
        _overflow = [];
    }

    /// <summary>The slots, from the first line of their store.</summary>
    private readonly Span<Slot> Slots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, Slot>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_store), _base)), _length);
    }

    /// <summary>A table that takes its arrays from a query's shelf and gives them back as it grows: a sub-table of the core.</summary>
    internal KeyTable(ArrayShelf shelf)
        : this() => _shelf = shelf;

    /// <summary>The keys the table holds.</summary>
    internal readonly int Count => _count;

    /// <summary>Whether a chain grew long enough for the table to take a seed.</summary>
    internal readonly bool Seeded => _seed != 0;

    /// <summary>The bytes of the slots, the chains' heads and their links.</summary>
    internal readonly long Footprint =>
        ((long)_store.Length * sizeof(ulong)) + ((long)_chains.Length * sizeof(int)) + ((long)_overflow.Length * Unsafe.SizeOf<Entry>());

    /// <summary>The slots of a line: as many as 64 bytes hold, a power of two.</summary>
    /// <remarks>Inlined, a constant: the native compiler left it a call, a twentieth of the cycles of a group by of unique keys.</remarks>
    private static int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.SizeOf<Slot>() switch
        {
            <= 8 => 8,
            <= 16 => 4,
            <= 32 => 2,
            _ => 1,
        };
    }

    private static int WidthShift
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Width switch
        {
            8 => 3,
            4 => 2,
            2 => 1,
            _ => 0,
        };
    }

    /// <summary>The group of <paramref name="key"/>; when the key is new, <paramref name="next"/>, which it then holds.</summary>
    /// <remarks>
    /// A key whose home is free is in no line and no chain: it would have taken its home, which no
    /// key leaves. So a free home takes the key at once, and only a taken one searches.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetOrAdd(TValue key, int next)
    {
        Span<Slot> slots = Slots;
        if (slots.Length != 0)
        {
            ref Slot slot = ref slots[(int)Home(key)];
            if (slot.Group != 0)
            {
                if (slot.Key.Equals(key))
                {
                    return slot.Group - 1;
                }
            }
            else if (_count < _growAt)
            {
                slot.Key = key;
                slot.Group = next + 1;
                _count++;
                return next;
            }
        }

        return Search(key, next);
    }

    /// <summary>
    /// Each key's group where the key sits in its home slot, -1 where it does not (past its home, or
    /// absent), with no branch on the keys: the first pass over a batch,
    /// whose rows left go through <see cref="GetOrAdd"/> in their order. With <paramref name="ahead"/>
    /// rows, the slot of the row that far on is read first, its miss on its way while the rows before
    /// it compare; what those reads find goes to the sink returned, so that the JIT keeps them.
    /// </summary>
    /// <param name="keys">The keys of the batch.</param>
    /// <param name="groups">Each key's group, or -1.</param>
    /// <param name="homes">Room for each key's home slot, as many as the keys, when <paramref name="ahead"/> reads ahead.</param>
    /// <param name="ahead">How many rows on a slot is read before its row compares; 0 for none.</param>
    /// <param name="missed">Whether a key found no group: the rows left to the lookup, none when false.</param>
    internal readonly int FindAtHome(ReadOnlySpan<TValue> keys, Span<int> groups, Span<uint> homes, int ahead, out bool missed)
    {
        Span<Slot> slots = Slots;
        if (slots.Length == 0)
        {
            groups[..keys.Length].Fill(-1);
            missed = keys.Length > 0;
            return 0;
        }

        groups = groups[..keys.Length];

        // The table's fields in locals: a store to the homes could alias them for all the JIT knows.
        ulong seed = _seed;
        ulong multiplier = _multiplier;
        uint length = (uint)slots.Length;
        int shift = _shift;

        // Every home is below the slots' length, by the fast modulo: no bound to check. A match is a
        // bit, and the group its mask over the slot's group plus one, less one: the JIT branched on
        // the choice of the group or -1, even with both at hand.
        ref Slot first = ref MemoryMarshal.GetReference(slots);
        int any = 0;
        if (ahead <= 0)
        {
            // No read ahead: each home probed as it is computed, none stored to be read back; with no
            // bit left out, a loop without the shift, a cycle a row.
            if (shift == 0)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    ref Slot home = ref Unsafe.Add(ref first, (nint)HomeOf(keys[i], seed, multiplier, length, 0));
                    int match = Unsafe.BitCast<bool, byte>(home.Key.Equals(keys[i]));
                    int group = (home.Group & -match) - 1;
                    groups[i] = group;
                    any |= group;
                }
            }
            else
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    ref Slot home = ref Unsafe.Add(ref first, (nint)HomeOf(keys[i], seed, multiplier, length, shift));
                    int match = Unsafe.BitCast<bool, byte>(home.Key.Equals(keys[i]));
                    int group = (home.Group & -match) - 1;
                    groups[i] = group;
                    any |= group;
                }
            }

            missed = any < 0;
            return 0;
        }

        homes = homes[..keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            homes[i] = HomeOf(keys[i], seed, multiplier, length, shift);
        }

        int sink = 0;
        int row = 0;
        for (int touched = keys.Length - ahead; row < touched; row++)
        {
            sink ^= Unsafe.Add(ref first, (nint)homes[row + ahead]).Group;
            ref Slot slot = ref Unsafe.Add(ref first, (nint)homes[row]);
            int match = Unsafe.BitCast<bool, byte>(slot.Key.Equals(keys[row]));
            int group = (slot.Group & -match) - 1;
            groups[row] = group;
            any |= group;
        }

        for (; row < keys.Length; row++)
        {
            ref Slot slot = ref Unsafe.Add(ref first, (nint)homes[row]);
            int match = Unsafe.BitCast<bool, byte>(slot.Key.Equals(keys[row]));
            int group = (slot.Group & -match) - 1;
            groups[row] = group;
            any |= group;
        }

        missed = any < 0;
        return sink;
    }

    /// <summary>Makes room for <paramref name="keys"/> keys without growing on the way.</summary>
    internal void Reserve(int keys)
    {
        long slots = ((10L * keys) / 6) + 1;
        if (slots > _length)
        {
            Resize(PrimeAtLeast((int)Math.Min(int.MaxValue / 2, slots)));
        }
    }

    /// <summary>Forgets every key, keeping the slots and the seed.</summary>
    internal void Clear()
    {
        Array.Clear(_store);
        Array.Clear(_chains);
        _overflowed = 0;
        _count = 0;
    }

    /// <summary>A key whose home is taken: the rest of the home's line, then the line's chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Search(TValue key, int next)
    {
        if (_length == 0)
        {
            Resize(FirstSlots);
        }

        Span<Slot> slots = Slots;
        uint at = Home(key);
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
                    Resize(PrimeAtLeast(GroupKeys.Doubled(slots.Length)));
                    return Search(key, next);
                }

                slot.Key = key;
                slot.Group = next + 1;
                _count++;
                return next;
            }

            if (slot.Key.Equals(key))
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
            if (entry.Key.Equals(key))
            {
                return entry.Group - 1;
            }

            links++;
        }

        // A new key past a full line: the table grows if it is full, or if its chains hold one key in
        // eight while it is past three tenths full, or takes a seed if the chain is long.
        if (_count >= _growAt || (8L * _overflowed > _count && 10L * _count > 3L * slots.Length))
        {
            Resize(PrimeAtLeast(GroupKeys.Doubled(slots.Length)));
            return Search(key, next);
        }

        if (links >= MaxChain && _seed == 0)
        {
            _seed = (ulong)Random.Shared.NextInt64(1, long.MaxValue) | 1;
            Resize(slots.Length);
            return Search(key, next);
        }

        Chain(key, next + 1, at);
        _count++;
        return next;
    }

    /// <summary>
    /// The low bits no key of a sample of <paramref name="slots"/> and of <paramref name="overflow"/>
    /// sets, which the homes leave out; the sample ends at its first key with its lowest bit set. A key
    /// past the sample that sets one shares its home with its neighbour, which costs it a probe and no
    /// more: the shift only places keys. Counted at each key it came, an OR into the table made each
    /// insertion of 2×10⁷ unique keys wait on the one before, 1.06 times as long a group by; every
    /// key read at each growth, keys at a stride of 2²² that sat at home already took 1.02 times as long.
    /// </summary>
    private readonly int Shift(ReadOnlySpan<Slot> slots, ReadOnlySpan<Entry> overflow)
    {
        if (_seed != 0 || Unsafe.SizeOf<TValue>() > sizeof(ulong))
        {
            return 0;
        }

        ulong bits = 0;
        int step = Math.Max(1, slots.Length / SampledSlots);
        for (int at = 0; at < slots.Length && (bits & 1) == 0; at += step)
        {
            bits |= slots[at].Group != 0 ? KeyWords.Of(slots[at].Key).Low : 0;
        }

        foreach (Entry entry in overflow)
        {
            bits |= KeyWords.Of(entry.Key).Low;
        }

        return bits == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>The slots <see cref="Shift"/> samples, spread over the table: every slot of a smaller one.</summary>
    private const int SampledSlots = 2_048;

    /// <summary>The slot <paramref name="key"/> starts from: its folded bits, mixed under the seed once there is one, modulo the slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint Home(TValue key) => HomeOf(key, _seed, _multiplier, (uint)_length, _shift);

    /// <summary>As <see cref="Home"/>, the table's seed, multiplier, length and shift given.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HomeOf(TValue key, ulong seed, ulong multiplier, uint length, int shift)
    {
        (ulong low, ulong high) = KeyWords.Of(key);
        uint hash;
        if (seed != 0)
        {
            hash = (uint)((Unsafe.SizeOf<TValue>() <= sizeof(ulong) ? MergeHash.Of(low, seed) : MergeHash.Of(low, high, seed)) >> 32);
        }
        else if (Unsafe.SizeOf<TValue>() <= sizeof(uint))
        {
            hash = (uint)(low >> shift);
        }
        else
        {
            ulong folded = (low >> shift) ^ high;
            hash = (uint)(folded ^ (folded >> 32));
        }

        return FastMod(hash, length, multiplier);
    }

    /// <summary>Places every key again in <paramref name="length"/> slots, a prime, under the seed the table has.</summary>
    private void Resize(int length)
    {
        ulong[] oldStore = _store;
        Span<Slot> old = Slots;
        int[] chains = _chains;
        Entry[] overflow = _overflow;
        int overflowed = _overflowed;

        // The store a line longer than the slots, which start at its first line.
        _store = NewArray<ulong>(checked((int)((((long)length * Unsafe.SizeOf<Slot>()) + GroupRecords.Line - sizeof(ulong)) / sizeof(ulong))));
        _base = LineStart(_store);
        _length = length;
        _chains = NewArray<int>((length >> WidthShift) + 1);
        _overflow = NewArray<Entry>(overflowed);
        _overflowed = 0;
        _multiplier = (ulong.MaxValue / (uint)length) + 1;
        _growAt = (int)(6L * length / 10);

        // The low bits no key has set, left out of the homes; under a seed, every bit goes to the mix.
        _shift = Shift(old, overflow.AsSpan(0, overflowed));
        foreach (Slot slot in old)
        {
            if (slot.Group != 0)
            {
                Place(slot.Key, slot.Group);
            }
        }

        for (int i = 0; i < overflowed; i++)
        {
            Place(overflow[i].Key, overflow[i].Group);
        }

        _shelf?.Give(oldStore);
        _shelf?.Give(chains);
        _shelf?.Give(overflow);
    }

    /// <summary>The word of <paramref name="store"/> a line of 64 bytes starts at.</summary>
    private static unsafe int LineStart(ulong[] store)
    {
        nint address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(store));
        return (int)((-address & (GroupRecords.Line - 1)) / sizeof(ulong));
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
        _shift = 0;
    }

    /// <summary>A key of the table placed again: the first free slot of its line from its home, or its line's chain.</summary>
    private void Place(TValue key, int group)
    {
        Span<Slot> slots = Slots;
        uint at = Home(key);
        uint first = at & ~(uint)(Width - 1);
        uint end = Math.Min(first + (uint)Width, (uint)slots.Length);
        uint probe = at;
        do
        {
            ref Slot slot = ref slots[(int)probe];
            if (slot.Group == 0)
            {
                slot.Key = key;
                slot.Group = group;
                return;
            }

            probe = probe + 1 == end ? first : probe + 1;
        }
        while (probe != at);

        Chain(key, group, at);
    }

    /// <summary>Links a key at the head of the chain of the line of slot <paramref name="at"/>.</summary>
    private void Chain(TValue key, int group, uint at)
    {
        if (_overflowed == _overflow.Length)
        {
            Entry[] grown = NewArray<Entry>(Math.Max(16, 2 * _overflow.Length));
            _overflow.CopyTo(grown, 0);
            _shelf?.Give(_overflow);
            _overflow = grown;
        }

        ref int head = ref _chains[at >> WidthShift];
        _overflow[_overflowed] = new Entry { Key = key, Group = group, Next = head };
        head = ++_overflowed;
    }

    /// <summary>An array of the table's, zeroed: from its shelf when it has one.</summary>
    private readonly T[] NewArray<T>(int length) => _shelf is null ? new T[length] : _shelf.Take<T>(length, zeroed: true);

    /// <summary><paramref name="value"/> modulo <paramref name="divisor"/> by a multiplication, as the runtime's dictionary takes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FastMod(uint value, uint divisor, ulong multiplier) =>
        (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);

    /// <summary>The least prime at or above <paramref name="least"/>: a stride of keys shares no factor with the slots.</summary>
    private static int PrimeAtLeast(int least)
    {
        for (int candidate = least | 1; candidate < int.MaxValue; candidate += 2)
        {
            bool prime = true;
            for (int divisor = 3; (long)divisor * divisor <= candidate; divisor += 2)
            {
                if (candidate % divisor == 0)
                {
                    prime = false;
                    break;
                }
            }

            if (prime)
            {
                return candidate;
            }
        }

        return least;
    }

    /// <summary>A key and its group plus one: zero, the slots' first value, an empty slot.</summary>
    private struct Slot
    {
        public TValue Key;
        public int Group;
    }

    /// <summary>A key past a full line, its group plus one, and the next link of its line's chain plus one.</summary>
    private struct Entry
    {
        public TValue Key;
        public int Group;
        public int Next;
    }
}
