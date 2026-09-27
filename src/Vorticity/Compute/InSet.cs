using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// The candidates of an <c>IN</c>, hashed once for the whole scan: built per batch, the set would
/// merely move the candidate count from the row loop to the setup. Signedness belongs to the set,
/// because a candidate above <see cref="long.MaxValue"/> shares its bit pattern with a negative
/// value and a set built for an unsigned column would make a signed column match a row it must not.
/// </summary>
internal sealed class InSet
{
    /// <summary>
    /// Fewer candidates than this and the equality kernel wins: a compare against a value already
    /// in a register beats a hash, a mask and a load, and the OR path pays it once per candidate.
    /// The set costs the same whatever the count, so the two paths cross here.
    /// </summary>
    private const int LeastCandidates = 4;

    /// <summary>
    /// An eighth full, which is emptier than a hash table is usually built. Almost every row misses,
    /// and an unsuccessful linear probe costs <c>(1 + 1/(1-a)^2)/2</c> slots: 2,5 at a half against
    /// 1,15 at an eighth, so the extra memory buys back most of the probe.
    /// </summary>
    private const int Emptiness = 8;

    /// <summary>
    /// Up to this many distinct keys, and where there are 512-bit vectors, a vector of rows is
    /// compared with each key in turn rather than each row hashed: a compare answers eight to
    /// sixty-four rows, where a probe is a multiply, a load and a branch for one.
    /// </summary>
    private const int FewKeys = 16;

    private readonly ulong[] _slots;
    private readonly int _mask;
    private readonly bool _hasZero;
    private readonly byte _miss;

    /// <summary>The distinct keys, zero included, when there are at most <see cref="FewKeys"/>; else null.</summary>
    private readonly ulong[]? _keys;

    private InSet(ulong[] slots, int mask, bool hasZero, byte miss, bool signed, ulong[]? keys)
    {
        _slots = slots;
        _mask = mask;
        _hasZero = hasZero;
        _miss = miss;
        Signed = signed;
        _keys = keys;
    }

    /// <summary>Whether this set was built for a signed column.</summary>
    internal bool Signed { get; }

    /// <summary>
    /// Hashes <paramref name="literals"/> for a column of the given signedness, or reports that
    /// they are not a set worth building.
    /// </summary>
    /// <param name="literals">The candidates.</param>
    /// <param name="signed">Whether the column is signed.</param>
    /// <returns><see langword="null"/> when the caller must keep to an OR of equalities.</returns>
    internal static InSet? TryBuild(ReadOnlySpan<FilterLiteral> literals, bool signed)
    {
        bool hasNull = false;
        int candidates = 0;
        for (int i = 0; i < literals.Length; i++)
        {
            switch (literals[i].Kind)
            {
                case FilterLiteralKind.Null:
                    hasNull = true;
                    break;

                case FilterLiteralKind.Signed:
                case FilterLiteralKind.Unsigned:
                    candidates++;
                    break;

                default:
                    // A float against an integer column has its own kernel and a bool is a type
                    // error the equality path reports. Neither belongs in a set of integers.
                    return null;
            }
        }

        if (candidates < LeastCandidates)
        {
            return null;
        }

        int slots = 64;
        while (slots < (long)candidates * Emptiness && slots < (1 << 16))
        {
            slots <<= 1;
        }

        // Past that ceiling the emptiness is a preference, but half empty is a requirement: the
        // insert and the miss both stop at the first empty slot, and a full table gives neither.
        while (slots < (long)candidates * 2)
        {
            slots <<= 1;
        }

        ulong[] table = new ulong[slots];
        int mask = slots - 1;

        // Zero is a legal key and it is also the empty slot, so it is held apart rather than given
        // a sentinel no value could take.
        bool hasZero = false;

        for (int i = 0; i < literals.Length; i++)
        {
            FilterLiteral literal = literals[i];
            ulong key;
            if (literal.Kind == FilterLiteralKind.Signed)
            {
                long value = literal.SignedValue;
                if (!signed && value < 0)
                {
                    continue;
                }

                key = unchecked((ulong)value);
            }
            else if (literal.Kind == FilterLiteralKind.Unsigned)
            {
                ulong value = literal.UnsignedValue;
                if (signed && value > long.MaxValue)
                {
                    continue;
                }

                key = value;
            }
            else
            {
                continue;
            }

            if (key == 0)
            {
                hasZero = true;
                continue;
            }

            int at = Slot(key, mask);
            while (table[at] != 0 && table[at] != key)
            {
                at = (at + 1) & mask;
            }

            table[at] = key;
        }

        ulong[]? keys = null;
        int distinct = hasZero ? 1 : 0;
        foreach (ulong slot in table)
        {
            distinct += slot != 0 ? 1 : 0;
        }

        if (distinct <= FewKeys)
        {
            keys = new ulong[distinct];
            int k = 0;
            foreach (ulong slot in table)
            {
                if (slot != 0)
                {
                    keys[k++] = slot;
                }
            }

            if (hasZero)
            {
                keys[k] = 0;
            }
        }

        // A null candidate cannot be put in a set -- nothing equals it -- but its whole effect is
        // to turn the no-match answer from false into unknown.
        return new InSet(table, mask, hasZero, hasNull ? Trilean.Unknown : Trilean.False, signed, keys);
    }

    /// <summary>Writes one state per row of <paramref name="destination"/>.</summary>
    /// <param name="ptype">The column's physical type.</param>
    /// <param name="values">The column's values.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="destination">Receives one state per row.</param>
    internal void Apply(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, Span<byte> destination)
    {
        if (_keys is not null && WordBytes.IsAccelerated)
        {
            switch (ptype)
            {
                case PType.I8:
                    Few<sbyte>(values, mask, destination);
                    break;
                case PType.I16:
                    Few<short>(values, mask, destination);
                    break;
                case PType.I32:
                    Few<int>(values, mask, destination);
                    break;
                case PType.I64:
                    Few<long>(values, mask, destination);
                    break;
                case PType.U8:
                    Few<byte>(values, mask, destination);
                    break;
                case PType.U16:
                    Few<ushort>(values, mask, destination);
                    break;
                case PType.U32:
                    Few<uint>(values, mask, destination);
                    break;
                default:
                    Few<ulong>(values, mask, destination);
                    break;
            }

            return;
        }

        switch (ptype)
        {
            case PType.I8:
                Core<sbyte>(values, mask, destination);
                break;
            case PType.I16:
                Core<short>(values, mask, destination);
                break;
            case PType.I32:
                Core<int>(values, mask, destination);
                break;
            case PType.I64:
                Core<long>(values, mask, destination);
                break;
            case PType.U8:
                Core<byte>(values, mask, destination);
                break;
            case PType.U16:
                Core<ushort>(values, mask, destination);
                break;
            case PType.U32:
                Core<uint>(values, mask, destination);
                break;
            default:
                Core<ulong>(values, mask, destination);
                break;
        }
    }

    /// <summary>Where <paramref name="key"/> starts looking.</summary>
    /// <param name="key">The key.</param>
    /// <param name="mask">One below the slot count, which is a power of two.</param>
    /// <remarks>
    /// Multiply-xorshift: consecutive keys are the common shape of an id column and land far apart,
    /// and the fold is what makes the low bits good, which is what the mask reads.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(ulong key, int mask)
    {
        ulong hash = key * 0x9E3779B97F4A7C15UL;
        hash ^= hash >> 29;
        return (int)(hash & (ulong)(uint)mask);
    }

    /// <summary>The rest of a probe that did not land on its first slot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AfterCollision(ref ulong table, int mask, ulong key, int at)
    {
        while (true)
        {
            at = (at + 1) & mask;
            ulong slot = Unsafe.Add(ref table, at);
            if (slot == key)
            {
                return true;
            }

            if (slot == 0)
            {
                return false;
            }
        }
    }

    /// <summary>Whether the table holds <paramref name="key"/>.</summary>
    /// <remarks>
    /// One probe, and the walk that follows a collision lives in its own method: a loop is what
    /// stops the jit inlining a body, and this one is called once a row.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Holds(ref ulong table, ulong key)
    {
        if (key == 0)
        {
            return _hasZero;
        }

        int at = Slot(key, _mask);
        ulong slot = Unsafe.Add(ref table, at);
        return slot == key || (slot != 0 && AfterCollision(ref table, _mask, key, at));
    }

    /// <summary>The membership loop, with the width and the validity resolved.</summary>
    private void Core<TValue>(ReadOnlySpan<byte> bytes, ValidityMask mask, Span<byte> destination)
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..destination.Length];
        ref ulong table = ref MemoryMarshal.GetArrayDataReference(_slots);
        ref TValue value = ref MemoryMarshal.GetReference(values);
        byte miss = _miss;

        if (mask.AllValid)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                ulong key = ulong.CreateTruncating(Unsafe.Add(ref value, i));
                destination[i] = Holds(ref table, key) ? Trilean.True : miss;
            }

            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            ulong key = ulong.CreateTruncating(Unsafe.Add(ref value, i));
            destination[i] = Holds(ref table, key) ? Trilean.True : miss;
        }
    }

    /// <summary>
    /// The membership of a set of few keys where there are 512-bit vectors: whole blocks of 64 rows
    /// compared with each key, the rest probed.
    /// </summary>
    /// <remarks>
    /// Its own method, reached from <see cref="Apply"/> rather than from <see cref="Core"/>: a
    /// branch to it inside the probing loop's method changes how the jit compiles that loop, and
    /// the probe of a set of many keys ran a fifth to a third slower for it.
    /// </remarks>
    private void Few<TValue>(ReadOnlySpan<byte> bytes, ValidityMask mask, Span<byte> destination)
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..destination.Length];
        Rest(values, mask, destination, Lanes(values, mask, destination));
    }

    /// <summary>The rows past the last whole block of <see cref="Lanes"/>, fewer than 64, probed one at a time.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Rest<TValue>(ReadOnlySpan<TValue> values, ValidityMask mask, Span<byte> destination, int from)
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        ref ulong table = ref MemoryMarshal.GetArrayDataReference(_slots);
        for (int i = from; i < destination.Length; i++)
        {
            destination[i] = !mask.IsValid(i) ? Trilean.Unknown
                : Holds(ref table, ulong.CreateTruncating(values[i])) ? Trilean.True
                : _miss;
        }
    }

    /// <summary>
    /// The states of whole blocks of 64 rows, each 512-bit vector of rows compared with each key:
    /// the matches' masks gathered into a word, the word spread back to a byte a row.
    /// </summary>
    /// <returns>The rows answered, a multiple of 64; the rest are the caller's.</returns>
    /// <remarks>
    /// The keys are compared in the column's type, so a key the type cannot hold is dropped: its
    /// truncation would match a row the key does not equal, and no row equals the key itself. A
    /// null row is compared like any other and its state replaced by unknown, which the no-match
    /// state may be too when a candidate was null.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Lanes<TValue>(ReadOnlySpan<TValue> values, ValidityMask mask, Span<byte> destination)
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        Span<TValue> keys = stackalloc TValue[FewKeys];
        int count = 0;
        foreach (ulong key in _keys!)
        {
            TValue narrow = TValue.CreateTruncating(key);
            if (ulong.CreateTruncating(narrow) == key)
            {
                keys[count++] = narrow;
            }
        }

        int rows = destination.Length;
        int lanes = Vector512<TValue>.Count;
        WordBytes spread = WordBytes.Create();
        ref TValue from = ref MemoryMarshal.GetReference(values);
        ref TValue key0 = ref MemoryMarshal.GetReference(keys);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        ulong missUnknown = _miss == Trilean.Unknown ? ulong.MaxValue : 0;
        ReadOnlySpan<byte> bits = mask.Bits;
        int offset = mask.BitOffset;
        bool allValid = mask.AllValid;
        int i = 0;
        for (; i <= rows - 64; i += 64)
        {
            ulong hits = 0;
            for (int row = 0; row < 64; row += lanes)
            {
                Vector512<TValue> block = Vector512.LoadUnsafe(ref from, (nuint)(i + row));
                Vector512<TValue> any = Vector512<TValue>.Zero;
                for (int k = 0; k < count; k++)
                {
                    any |= Vector512.Equals(block, Vector512.Create(Unsafe.Add(ref key0, k)));
                }

                hits |= any.ExtractMostSignificantBits() << row;
            }

            // A match is true and a valid miss the no-match state; a null row is unknown whatever
            // it held. Unknown is 2, so its ones are doubled.
            ulong valid = allValid ? ulong.MaxValue : BitWords.Load(bits, offset + i);
            Vector512<byte> unknown = spread.Ones(~valid | (~hits & missUnknown));
            (spread.Ones(hits & valid) | (unknown + unknown)).StoreUnsafe(ref into, (nuint)i);
        }

        return i;
    }
}
