using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of a fixed-width key by open addressing, in a table the engine owns
/// (PLAN-HIGH-CARDINALITY, H15): a slot holds a key and its group, a key's slot is its hash modulo a
/// prime, by the fast modulo the runtime's dictionary takes, and the slots after it in turn. An
/// integer is its own hash, folded to 32 bits, so that keys in a row land in slots in a row and a
/// regular stride spreads over the prime; a float its bits, one pattern a value. At most six slots
/// in ten hold a key: some 2.2 slots a key on average as the table doubles, where a dictionary takes
/// an entry and a bucket.
/// </summary>
/// <remarks>
/// Keys can be built to share their slot modulo any prime: a key that lands <see cref="MaxProbes"/>
/// slots past its own makes the table take a seed of its own and place every key again by a mix of
/// it, which keys built against the identity cannot follow. It runs at every insertion, so a table
/// presized for a merge sees it too.
/// </remarks>
internal struct KeyTable<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>
    /// How far past its own slot a key lands before the table takes a seed: at six tenths, the runs of
    /// keys in no order stay under some 150 slots at ten million keys.
    /// </summary>
    private const int MaxProbes = 256;

    /// <summary>The slots of a table's first growth: a prime.</summary>
    private const int FirstSlots = 31;

    private Slot[] _slots;
    private ulong _multiplier;
    private ulong _seed;
    private int _count;

    public KeyTable()
    {
        _slots = [];
    }

    /// <summary>The keys the table holds.</summary>
    internal readonly int Count => _count;

    /// <summary>Whether a key landed far enough from its own slot for the table to take a seed.</summary>
    internal readonly bool Seeded => _seed != 0;

    /// <summary>The bytes of the slots.</summary>
    internal readonly long Footprint => (long)_slots.Length * Unsafe.SizeOf<Slot>();

    /// <summary>The group of <paramref name="key"/>; when the key is new, <paramref name="next"/>, which it then holds.</summary>
    internal int GetOrAdd(TValue key, int next)
    {
        Slot[] slots = _slots;
        if (slots.Length == 0)
        {
            Resize(FirstSlots);
            slots = _slots;
        }

        uint at = Home(key);
        int probes = 0;
        while (true)
        {
            ref Slot slot = ref slots[at];
            if (slot.Group == 0)
            {
                // A new key: the table grows first if it would hold more than six slots in ten, or
                // takes a seed if the key lands too far from its own slot.
                if (10L * (_count + 1) > 6L * slots.Length)
                {
                    Resize(PrimeAtLeast(2 * slots.Length));
                    return GetOrAdd(key, next);
                }

                if (probes >= MaxProbes && _seed == 0)
                {
                    _seed = (ulong)Random.Shared.NextInt64(1, long.MaxValue) | 1;
                    Resize(slots.Length);
                    return GetOrAdd(key, next);
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

            at = at + 1 == (uint)slots.Length ? 0 : at + 1;
            probes++;
        }
    }

    /// <summary>Makes room for <paramref name="keys"/> keys without growing on the way.</summary>
    internal void Reserve(int keys)
    {
        long slots = ((10L * keys) / 6) + 1;
        if (slots > _slots.Length)
        {
            Resize(PrimeAtLeast((int)Math.Min(int.MaxValue / 2, slots)));
        }
    }

    /// <summary>Forgets every key, keeping the slots and the seed.</summary>
    internal void Clear()
    {
        Array.Clear(_slots);
        _count = 0;
    }

    /// <summary>The slot <paramref name="key"/> starts from: its folded bits, mixed under the seed once there is one, modulo the slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint Home(TValue key)
    {
        (ulong low, ulong high) = KeyWords.Of(key);
        uint hash;
        if (_seed != 0)
        {
            hash = (uint)(MergeHash.Of(low, high, _seed) >> 32);
        }
        else if (Unsafe.SizeOf<TValue>() <= sizeof(uint))
        {
            hash = (uint)low;
        }
        else
        {
            ulong folded = low ^ high;
            hash = (uint)(folded ^ (folded >> 32));
        }

        return FastMod(hash, (uint)_slots.Length, _multiplier);
    }

    /// <summary>Places every key again in <paramref name="length"/> slots, a prime, under the seed the table has.</summary>
    private void Resize(int length)
    {
        Slot[] old = _slots;
        _slots = new Slot[length];
        _multiplier = (ulong.MaxValue / (uint)length) + 1;
        Slot[] slots = _slots;
        foreach (Slot slot in old)
        {
            if (slot.Group == 0)
            {
                continue;
            }

            uint at = Home(slot.Key);
            while (slots[at].Group != 0)
            {
                at = at + 1 == (uint)slots.Length ? 0 : at + 1;
            }

            slots[at] = slot;
        }
    }

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
}
