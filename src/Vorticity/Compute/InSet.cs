// The candidates of one IN, hashed once and read by every batch of a scan.
//
// An OR of equalities walks the whole column once per candidate, so its cost is rows times
// candidates. Membership against a set is one walk whatever the count -- but only if the set itself
// is built once. Built per batch it merely moves the candidate count from the row loop to the
// setup, and a scan of a million rows in eight-thousand-row batches runs that setup a hundred and
// twenty times over.
//
// SIGNEDNESS IS PART OF THE SET, not a detail of how it is read. A candidate above i64::MaxValue
// shares its bit pattern with a negative value, so a set built for an unsigned column would make a
// signed column match a row it must not. Each set therefore knows which kind of column it was built
// for and says so.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>The candidates of an <c>IN</c>, hashed once for the whole scan.</summary>
internal sealed class InSet
{
    /// <summary>
    /// Fewer candidates than this and the equality kernel wins: a compare against a value already
    /// in a register beats a hash, a mask and a load, and the OR path pays it once per candidate.
    /// Measured on a million rows, an extra candidate costs that path about 0,28 ms while a set
    /// costs about 1,3 ms whatever the count, which puts the crossing just under four.
    /// </summary>
    private const int LeastCandidates = 4;

    /// <summary>
    /// An eighth full, which is emptier than a hash table is usually built and is what separates a
    /// line from a floor. Almost every row misses, and an unsuccessful linear probe costs
    /// <c>(1 + 1/(1-a)^2)/2</c> slots: 2,5 at a half and 1,15 at an eighth. Measured on a million
    /// rows against 512 candidates, a half reads 8,2 ms and an eighth 2,4.
    /// </summary>
    private const int Emptiness = 8;

    private readonly ulong[] _slots;
    private readonly int _mask;
    private readonly bool _hasZero;
    private readonly byte _miss;

    private InSet(ulong[] slots, int mask, bool hasZero, byte miss, bool signed)
    {
        _slots = slots;
        _mask = mask;
        _hasZero = hasZero;
        _miss = miss;
        Signed = signed;
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

        // A null candidate cannot be put in a set -- nothing equals it -- but its whole effect is
        // to turn the no-match answer from false into unknown.
        return new InSet(table, mask, hasZero, hasNull ? Trilean.Unknown : Trilean.False, signed);
    }

    /// <summary>Writes one state per row of <paramref name="destination"/>.</summary>
    /// <param name="ptype">The column's physical type.</param>
    /// <param name="values">The column's values.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="destination">Receives one state per row.</param>
    internal void Apply(
        PType ptype, ReadOnlySpan<byte> values, ValidityMask mask, Span<byte> destination)
    {
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
}
