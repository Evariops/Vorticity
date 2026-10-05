using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The groups of fixed-width key values, under the default hash while their keys spread over its
/// buckets. The default hash of a 64-bit integer or a float folds its halves together, so a column
/// can be built to pile into one bucket and make a group-by quadratic: each time the keys double
/// past <see cref="CheckFrom"/>, a sample of them is checked for how many buckets they share, and
/// keys crowded into few are rehashed under a comparer seeded per process.
/// </summary>
/// <remarks>
/// The owner calls <see cref="Doubled"/> where its own storage of the keys grows, so a lookup pays
/// for no check. A mutable struct, kept in a field of the owner so that a lookup reaches the
/// dictionary in one load: a copy would harden a dictionary its owner no longer reads.
/// </remarks>
internal struct GroupIndex<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    /// <summary>The fewest keys whose spread is checked.</summary>
    private const int CheckFrom = 1_024;

    /// <summary>The keys a check looks at.</summary>
    private const int Sample = 256;

    private Dictionary<TValue, int> _groups;

    /// <summary>An index of no key.</summary>
    public GroupIndex()
    {
        _groups = [];
    }

    /// <summary>Whether the keys were found crowded, and are hashed under the seeded comparer.</summary>
    internal bool Hardened { readonly get; private set; }

    /// <summary>How many keys there are.</summary>
    internal readonly int Count => _groups.Count;

    /// <summary>Forgets every key.</summary>
    internal readonly void Clear() => _groups.Clear();

    /// <summary>Makes room for <paramref name="count"/> keys at once, which then come without a growth.</summary>
    internal readonly void Reserve(int count) => _groups.EnsureCapacity(count);

    /// <summary>The group slot of <paramref name="value"/>, added when it is new; valid until <see cref="Doubled"/>.</summary>
    /// <param name="value">The key.</param>
    /// <param name="exists">Whether it was there.</param>
    internal readonly ref int Slot(TValue value, out bool exists) =>
        ref CollectionsMarshal.GetValueRefOrAddDefault(_groups, value, out exists);

    /// <summary>
    /// Follows a doubling of the keys, their slots filled: past <see cref="CheckFrom"/> keys, rehashes
    /// them under the seeded comparer when a sample of them is crowded.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Doubled()
    {
        int count = _groups.Count;
        if (Hardened || count < CheckFrom || !Crowded())
        {
            return;
        }

        Dictionary<TValue, int> seeded = new Dictionary<TValue, int>(count, SeededComparer.Instance);
        foreach (KeyValuePair<TValue, int> entry in _groups)
        {
            seeded.Add(entry.Key, entry.Value);
        }

        _groups = seeded;
        Hardened = true;
    }

    /// <summary>Whether a sample of the keys shares fewer than a quarter as many buckets as it holds keys.</summary>
    private readonly bool Crowded()
    {
        uint buckets = (uint)_groups.EnsureCapacity(0);
        Span<uint> seen = stackalloc uint[Sample];
        int taken = 0;
        foreach (TValue key in _groups.Keys)
        {
            seen[taken++] = (uint)key.GetHashCode() % buckets;
            if (taken == Sample)
            {
                break;
            }
        }

        Span<uint> sample = seen[..taken];
        sample.Sort();
        int distinct = 1;
        for (int i = 1; i < sample.Length; i++)
        {
            distinct += sample[i] != sample[i - 1] ? 1 : 0;
        }

        return distinct * 4 < taken;
    }

    /// <summary>
    /// Equality as the key type has it, and a hash of every bit of the key under the process's own
    /// seed: equal floats (all NaNs, both zeros) are given one pattern of bits first.
    /// </summary>
    private sealed class SeededComparer : IEqualityComparer<TValue>
    {
        internal static readonly SeededComparer Instance = new SeededComparer();

        public bool Equals(TValue x, TValue y) => x.Equals(y);

        public int GetHashCode(TValue value)
        {
            if (typeof(TValue) == typeof(double))
            {
                double d = Unsafe.As<TValue, double>(ref value);
                return Mix(double.IsNaN(d) ? 0x7FF8_0000_0000_0000UL : d == 0 ? 0 : BitConverter.DoubleToUInt64Bits(d), 0);
            }

            if (typeof(TValue) == typeof(float))
            {
                float f = Unsafe.As<TValue, float>(ref value);
                return Mix(float.IsNaN(f) ? 0x7FC0_0000U : f == 0 ? 0 : BitConverter.SingleToUInt32Bits(f), 0);
            }

            if (typeof(TValue) == typeof(Half))
            {
                Half h = Unsafe.As<TValue, Half>(ref value);
                ushort bits = Half.IsNaN(h) ? (ushort)0x7E00 : h == Half.Zero ? (ushort)0 : BitConverter.HalfToUInt16Bits(h);
                return Mix(bits, 0);
            }

            return Unsafe.SizeOf<TValue>() switch
            {
                1 => Mix(Unsafe.As<TValue, byte>(ref value), 0),
                2 => Mix(Unsafe.As<TValue, ushort>(ref value), 0),
                4 => Mix(Unsafe.As<TValue, uint>(ref value), 0),
                8 => Mix(Unsafe.As<TValue, ulong>(ref value), 0),
                16 => Mix((ulong)Unsafe.As<TValue, UInt128>(ref value), (ulong)(Unsafe.As<TValue, UInt128>(ref value) >> 64)),
                _ => Wide(ref Unsafe.As<TValue, ulong>(ref value)),
            };
        }

        private static int Mix(ulong low, ulong high) =>
            HashCode.Combine((uint)low, (uint)(low >> 32), (uint)high, (uint)(high >> 32));

        /// <summary>A key of four words, a decimal of 256 bits: every word hashed, two rounds of the seeded combine.</summary>
        private static int Wide(ref ulong words) =>
            HashCode.Combine(Mix(words, Unsafe.Add(ref words, 1)), Mix(Unsafe.Add(ref words, 2), Unsafe.Add(ref words, 3)));
    }
}
