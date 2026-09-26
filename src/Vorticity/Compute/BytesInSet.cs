using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Expressions;
using Vorticity.Types.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// The candidates of an <c>IN</c> as byte strings: utf8 or binary values, the rows of a fixed-size
/// list of bytes, or a wide decimal's unscaled values in the little-endian bytes the column stores.
/// A row is hashed once and compared with the candidates its hash leads to, where an OR of
/// equalities compares it with every candidate.
/// </summary>
internal sealed class BytesInSet : CandidateSet
{
    /// <summary>
    /// An eighth full. A miss, which is what almost every row is, walks <c>(1 + 1/(1-a)^2)/2</c>
    /// slots, 1,15 at an eighth, and the tag each slot carries settles most of them without reading
    /// a candidate's bytes.
    /// </summary>
    private const int Emptiness = 8;

    /// <summary>The high half of a slot, which holds the high half of its candidate's hash.</summary>
    private const ulong TagMask = 0xFFFF_FFFF_0000_0000UL;

    /// <summary>
    /// A seed a query cannot know, so that no list of candidates can be written to share one run of
    /// slots that every row would walk.
    /// </summary>
    private static readonly long Seed = DrawSeed();

    private readonly byte[] _bytes;
    private readonly int[] _starts;
    private readonly ulong[] _slots;
    private readonly int _mask;
    private readonly int _shortest;
    private readonly int _spread;
    private readonly byte _miss;

    private BytesInSet(
        byte[] bytes, int[] starts, ulong[] slots, int shortest, int spread, byte miss, CandidateKind kind)
    {
        _bytes = bytes;
        _starts = starts;
        _slots = slots;
        _mask = slots.Length - 1;
        _shortest = shortest;
        _spread = spread;
        _miss = miss;
        Kind = kind;
    }

    /// <inheritdoc/>
    internal override CandidateKind Kind { get; }

    /// <summary>
    /// Hashes <paramref name="literals"/> for a column of the given kind, or reports that they are
    /// not a set worth building.
    /// </summary>
    /// <param name="literals">The candidates.</param>
    /// <param name="kind">The column's kind: bytes, or a decimal of sixteen or thirty-two bytes.</param>
    /// <returns><see langword="null"/> when the caller must keep to an OR of equalities.</returns>
    internal static BytesInSet? TryBuild(ReadOnlySpan<FilterLiteral> literals, CandidateKind kind)
    {
        int width = kind switch
        {
            CandidateKind.Decimal128 => 16,
            CandidateKind.Decimal256 => Int256.ByteCount,
            _ => 0,
        };

        bool hasNull = false;
        int candidates = 0;
        long total = 0;
        for (int i = 0; i < literals.Length; i++)
        {
            FilterLiteral literal = literals[i];
            if (literal.Kind == FilterLiteralKind.Null)
            {
                hasNull = true;
                continue;
            }

            // A literal the equality would refuse leaves the refusal to it.
            if (width == 0 ? literal.Kind != FilterLiteralKind.Bytes : !ComparisonKernels.TryDecimal(literal, out _))
            {
                return null;
            }

            total += width == 0 ? literal.BytesValue.Length : width;
            candidates++;
        }

        if (candidates < LeastCandidates(kind))
        {
            return null;
        }

        int size = 64;
        while (size < (long)candidates * Emptiness && size < (1 << 16))
        {
            size <<= 1;
        }

        while (size < (long)candidates * 2)
        {
            size <<= 1;
        }

        byte[] bytes = new byte[checked((int)total)];
        int[] starts = new int[candidates + 1];
        ulong[] slots = new ulong[size];
        int mask = size - 1;
        int count = 0;
        int used = 0;
        int shortest = int.MaxValue;
        int longest = 0;
        for (int i = 0; i < literals.Length; i++)
        {
            FilterLiteral literal = literals[i];
            if (literal.Kind == FilterLiteralKind.Null)
            {
                continue;
            }

            Span<byte> key = bytes.AsSpan(used);
            int length = width;
            if (width == 0)
            {
                ReadOnlySpan<byte> value = literal.BytesValue;
                value.CopyTo(key);
                length = value.Length;
            }
            else
            {
                ComparisonKernels.TryDecimal(literal, out Int256 value);
                if (!TryWrite(value, width, key))
                {
                    // Beyond what the storage holds, so beyond every row.
                    continue;
                }
            }

            ReadOnlySpan<byte> candidate = key[..length];
            ulong hash = Hash(candidate);
            int at = (int)hash & mask;
            if (Find(bytes, starts, slots, mask, candidate, hash, ref at))
            {
                continue;
            }

            slots[at] = (hash & TagMask) | (uint)(count + 1);
            used += length;
            starts[++count] = used;
            shortest = Math.Min(shortest, length);
            longest = Math.Max(longest, length);
        }

        // With no candidate left, a shortest length no row can have makes every row miss.
        return new BytesInSet(
            bytes, starts, slots, shortest, count == 0 ? 0 : longest - shortest,
            hasNull ? Trilean.Unknown : Trilean.False, kind);
    }

    /// <summary>Writes one state per row of a utf8 or binary column into <paramref name="destination"/>.</summary>
    /// <param name="node">The column, a varbinview node.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="destination">Receives one state per row.</param>
    internal void Apply(CanonicalNode node, ValidityMask mask, Span<byte> destination)
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        ViewValues values = new ViewValues(node);
        byte miss = _miss;
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            destination[i] = Holds(values.At(i)) ? Trilean.True : miss;
        }
    }

    /// <summary>Writes one state per row of <paramref name="width"/> bytes into <paramref name="destination"/>.</summary>
    /// <param name="values">The rows, back to back.</param>
    /// <param name="width">The bytes of one row.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="destination">Receives one state per row.</param>
    internal void Apply(ReadOnlySpan<byte> values, int width, ValidityMask mask, Span<byte> destination)
    {
        if (mask.AllInvalid)
        {
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (width == 16)
        {
            Apply16(values[..(destination.Length * 16)], mask, destination);
            return;
        }

        byte miss = _miss;
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            destination[i] = Holds(values.Slice(i * width, width)) ? Trilean.True : miss;
        }
    }

    /// <summary>
    /// The rows of sixteen bytes -- a uuid, a 128-bit decimal -- read as two words each, hashed and
    /// compared as words rather than as a span.
    /// </summary>
    private void Apply16(ReadOnlySpan<byte> values, ValidityMask mask, Span<byte> destination)
    {
        byte miss = _miss;
        if ((uint)(16 - _shortest) > (uint)_spread)
        {
            // No candidate is sixteen bytes long, so no row is one.
            FillMisses(mask, miss, destination);
            return;
        }

        ref byte rows = ref MemoryMarshal.GetReference(values);
        ref ulong slots = ref MemoryMarshal.GetArrayDataReference(_slots);
        int tableMask = _mask;
        bool allValid = mask.AllValid;
        for (int i = 0; i < destination.Length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                destination[i] = Trilean.Unknown;
                continue;
            }

            ref byte row = ref Unsafe.Add(ref rows, (nint)i * 16);
            ulong low = Unsafe.ReadUnaligned<ulong>(ref row);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref row, 8));
            ulong hash = Mix16(low, high);
            int at = (int)hash & tableMask;
            ulong slot = Unsafe.Add(ref slots, at);
            bool holds = slot != 0 &&
                (IsCandidate16(slot, hash, low, high) || AfterCollision(values.Slice(i * 16, 16), hash, at));
            destination[i] = holds ? Trilean.True : miss;
        }
    }

    /// <summary>Whether the candidate in <paramref name="slot"/> is the sixteen bytes of <paramref name="low"/> and <paramref name="high"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCandidate16(ulong slot, ulong hash, ulong low, ulong high)
    {
        if (((slot ^ hash) & TagMask) != 0)
        {
            return false;
        }

        int index = (int)(uint)slot - 1;
        int start = _starts[index];
        if (_starts[index + 1] - start != 16)
        {
            return false;
        }

        ReadOnlySpan<byte> candidate = _bytes.AsSpan(start, 16);
        return MemoryMarshal.Read<ulong>(candidate) == low && MemoryMarshal.Read<ulong>(candidate[8..]) == high;
    }

    /// <summary>A miss for every valid row and unknown for a null one.</summary>
    private static void FillMisses(ValidityMask mask, byte miss, Span<byte> destination)
    {
        if (mask.AllValid)
        {
            Trilean.Fill(destination, miss);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = mask.IsValid(i) ? miss : Trilean.Unknown;
        }
    }

    /// <summary>Whether <paramref name="value"/> is a candidate.</summary>
    /// <remarks>
    /// A value of a length no candidate has is answered before it is hashed, which is most of the
    /// rows when the candidates are codes of one length. The first slot is read here and the rest
    /// of the run out of line: a loop is what stops the jit inlining a body, and this one is called
    /// once a row.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Holds(ReadOnlySpan<byte> value)
    {
        if ((uint)(value.Length - _shortest) > (uint)_spread)
        {
            return false;
        }

        ulong hash = Hash(value);
        int at = (int)hash & _mask;
        ulong slot = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_slots), at);
        return slot != 0 && (IsCandidate(slot, hash, value) || AfterCollision(value, hash, at));
    }

    /// <summary>Whether the candidate in <paramref name="slot"/> is <paramref name="value"/>, its tag asked first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCandidate(ulong slot, ulong hash, ReadOnlySpan<byte> value) =>
        ((slot ^ hash) & TagMask) == 0 && Candidate(_bytes, _starts, (int)(uint)slot - 1).SequenceEqual(value);

    /// <summary>The rest of a run whose first slot held another candidate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool AfterCollision(ReadOnlySpan<byte> value, ulong hash, int at)
    {
        at = (at + 1) & _mask;
        return Find(_bytes, _starts, _slots, _mask, value, hash, ref at);
    }

    private static ReadOnlySpan<byte> Candidate(byte[] bytes, int[] starts, int index)
    {
        int start = starts[index];
        return bytes.AsSpan(start, starts[index + 1] - start);
    }

    /// <summary>The hash of a candidate or of a row.</summary>
    /// <remarks>
    /// Sixteen bytes -- a uuid, a 128-bit decimal -- are two words, and two multiplications mix them
    /// for a fraction of what the general hash spends deciding how to read them.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(ReadOnlySpan<byte> value) => value.Length == 16
        ? Mix16(MemoryMarshal.Read<ulong>(value), MemoryMarshal.Read<ulong>(value[8..]))
        : XxHash3.HashToUInt64(value, Seed);

    /// <summary>The hash of sixteen bytes, as their two little-endian words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix16(ulong low, ulong high)
    {
        ulong hash = ((low ^ (ulong)Seed) * 0x9E3779B97F4A7C15UL) ^ (high * 0xC2B2AE3D27D4EB4FUL);
        return hash ^ (hash >> 32);
    }

    /// <summary>
    /// Walks the slots from <paramref name="at"/> to <paramref name="value"/> or to the empty slot
    /// that ends its run, where <paramref name="at"/> is left.
    /// </summary>
    private static bool Find(
        byte[] bytes, int[] starts, ulong[] slots, int mask, ReadOnlySpan<byte> value, ulong hash, ref int at)
    {
        ulong tag = hash & TagMask;
        ref ulong slot0 = ref MemoryMarshal.GetArrayDataReference(slots);
        while (true)
        {
            ulong slot = Unsafe.Add(ref slot0, at);
            if (slot == 0)
            {
                return false;
            }

            if ((slot & TagMask) == tag && Candidate(bytes, starts, (int)(uint)slot - 1).SequenceEqual(value))
            {
                return true;
            }

            at = (at + 1) & mask;
        }
    }

    /// <summary>A decimal's unscaled value in the bytes a column of <paramref name="width"/> stores it in.</summary>
    /// <returns>False when the storage cannot hold the value.</returns>
    private static bool TryWrite(Int256 value, int width, Span<byte> destination)
    {
        if (width == Int256.ByteCount)
        {
            value.WriteLittleEndianBytes(destination);
            return true;
        }

        if (!value.TryToInt128(out Int128 narrow))
        {
            return false;
        }

        BinaryPrimitives.WriteInt128LittleEndian(destination, narrow);
        return true;
    }

    private static long DrawSeed()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes);
    }
}
