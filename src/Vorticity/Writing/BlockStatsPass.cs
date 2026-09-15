// The fused pass of docs/11-write-strategy.md §3.2, at the size stage 1 of its §8 asks for: one
// loop per (column, block) that produces everything the zone map needs, run on the batch's own
// arena while the decode that produced it is still in cache.
//
// THREE THINGS IT DOES THAT `ZoneStatistics` DID NOT:
//
//   * IT RUNS AT INGEST, over a row RANGE of the batch, so a block is summarized from the batches
//     that cover it rather than from the chunk it lands in. That is what decouples the zone map from
//     the chunk shape.
//   * IT RESOLVES THE PHYSICAL TYPE ONCE, before the loop, into a generic instantiation the JIT
//     monomorphizes - the form of R7, W-9 and W-33. `ZoneStatistics` called `ReadInteger` per row
//     and paid a switch on `PType` for every value; those are the two calls PerRowDispatchTests
//     charged it under W-12.
//   * IT COUNTS NULLS BY WORDS. `BitmapKernels.CountSet` popcounts the range; the old loop asked
//     `IsValid` once per row to add up the same number.
//
// A CONSTANT BLOCK IS ANSWERED WITHOUT READING A VALUE. The canonical constant form (PERF-AUDIT-v2
// Z1b) holds one element and a count, and its bounds are that element whatever the row count; going
// through `node.Values` here would materialize the very column the form exists to not build. The
// emitted chunk still materializes today - `ArrayBlobWriter.Materialize` does it at the boundary -
// but the summary no longer forces it.
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>Fills a <see cref="BlockStats"/> from a row range of a canonical column.</summary>
internal static class BlockStatsPass
{
    /// <summary>
    /// Folds rows <c>[start, start + count)</c> of <paramref name="nodeIndex"/> into
    /// <paramref name="stats"/>.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="start">First row of the range, inside the node.</param>
    /// <param name="count">How many rows; zero is a no-op.</param>
    /// <param name="stats">The block's accumulator, continued rather than replaced.</param>
    internal static void Accumulate(
        CanonicalArena arena, int nodeIndex, int start, int count, ref BlockStats stats)
    {
        if (count <= 0)
        {
            return;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        stats.Rows += count;
        int valid = Nulls(in mask, start, count, ref stats);

        switch (node.Kind)
        {
            case CanonicalKind.Constant:
                // One element, `count` rows, and its dtype is Primitive by construction
                // (ConstantCanonicalizer builds the form for no other kind). A block of it is
                // bounded by the element itself as soon as one row of it is valid.
                stats.IsSummarizable = true;
                if (valid > 0)
                {
                    Element(node.DType.PType, node.ConstantElement, ref stats);
                }

                return;

            case CanonicalKind.Primitive:
                stats.IsSummarizable = true;
                if (valid > 0)
                {
                    Values(node, in mask, start, count, ref stats);
                }

                return;

            default:
                // Utf8 and binary have a perfectly good lexicographic min/max and no place to put it
                // without a second varbinview per zone; every other kind has no scalar bound at all.
                // Such a column still gets a zone map, with the null count alone, which is what
                // IS NULL pruning runs on.
                return;
        }
    }

    /// <summary>Adds the range's nulls to <paramref name="stats"/> and returns its valid rows.</summary>
    private static int Nulls(in ValidityMask mask, int start, int count, ref BlockStats stats)
    {
        if (mask.AllValid)
        {
            return count;
        }

        if (mask.AllInvalid)
        {
            stats.NullCount += count;
            return 0;
        }

        int valid = BitmapKernels.CountSet(mask.Bits, mask.BitOffset + start, count);
        stats.NullCount += count - valid;
        return valid;
    }

    /// <summary>Bounds a block by the single element a constant column repeats.</summary>
    private static void Element(PType ptype, ReadOnlySpan<byte> element, ref BlockStats stats)
    {
        if (ptype.IsFloat())
        {
            double value = ElementFloat(element, ptype);
            if (!double.IsNaN(value))
            {
                stats.MergeFloat(value, value);
            }

            return;
        }

        if (ptype.IsSignedInteger())
        {
            long value = ElementSigned(element, ptype);
            stats.MergeSigned(value, value);
            return;
        }

        ulong bits = ElementUnsigned(element, ptype);
        stats.MergeUnsigned(bits, bits);
    }

    /// <summary>Dispatches on the physical type ONCE, then runs a monomorphic loop.</summary>
    private static void Values(
        CanonicalNode node, in ValidityMask mask, int start, int count, ref BlockStats stats)
    {
        ReadOnlySpan<byte> values = node.Values.Span;
        switch (node.PType)
        {
            case PType.I8: Signed<sbyte>(values, in mask, start, count, ref stats); return;
            case PType.I16: Signed<short>(values, in mask, start, count, ref stats); return;
            case PType.I32: Signed<int>(values, in mask, start, count, ref stats); return;
            case PType.I64: Signed<long>(values, in mask, start, count, ref stats); return;
            case PType.U8: Unsigned<byte>(values, in mask, start, count, ref stats); return;
            case PType.U16: Unsigned<ushort>(values, in mask, start, count, ref stats); return;
            case PType.U32: Unsigned<uint>(values, in mask, start, count, ref stats); return;
            case PType.U64: Unsigned<ulong>(values, in mask, start, count, ref stats); return;
            case PType.F16: Halves(values, in mask, start, count, ref stats); return;
            case PType.F32: Floats<float>(values, in mask, start, count, ref stats); return;
            default: Floats<double>(values, in mask, start, count, ref stats); return;
        }
    }

    private static void Signed<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>, ISignedNumber<T>, IMinMaxValue<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.MaxValue;
        T max = T.MinValue;

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
            return;
        }

        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.IsValid(start + i))
            {
                continue;
            }

            any = true;
            T value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        if (any)
        {
            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
        }
    }

    private static void Unsigned<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>, IMinMaxValue<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.MaxValue;
        T max = T.MinValue;

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
            return;
        }

        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.IsValid(start + i))
            {
                continue;
            }

            any = true;
            T value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        if (any)
        {
            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
        }
    }

    /// <summary>
    /// F32 and F64, NaN excluded, ordered by <c>Math.Min</c>'s sign-aware rule rather than by
    /// <c>&lt;</c>.
    /// </summary>
    /// <remarks>
    /// SKIPPING NaN IS LOAD-BEARING, not tidy: the reference computes min/max with `skip_nans()` and
    /// counts NaN in its own aggregate, which is what makes "a zone with max &lt;= 10 may be pruned
    /// for x &gt; 10 even when it contains NaN" sound. A NaN that reached `max` would compare false
    /// with everything and the bound would stop meaning anything.
    /// <para>
    /// The accumulation stays in <typeparamref name="T"/> and widens once at the end. Widening is
    /// monotone and exact for F32 to F64, so the answer is the one a double accumulator gives, at
    /// half the register traffic.
    /// </para>
    /// </remarks>
    private static void Floats<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.PositiveInfinity;
        T max = T.NegativeInfinity;
        bool any = false;

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (T.IsNaN(value))
                {
                    continue;
                }

                any = true;
                min = Smaller(min, value);
                max = Larger(max, value);
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!mask.IsValid(start + i))
                {
                    continue;
                }

                T value = values[i];
                if (T.IsNaN(value))
                {
                    continue;
                }

                any = true;
                min = Smaller(min, value);
                max = Larger(max, value);
            }
        }

        if (any)
        {
            stats.MergeFloat(double.CreateTruncating(min), double.CreateTruncating(max));
        }
    }

    /// <summary>F16, widened to <see cref="float"/> - exactly, since every Half has one.</summary>
    /// <remarks>
    /// Its own loop rather than an instantiation of <see cref="Floats{T}"/>: <see cref="Half"/>
    /// arithmetic is emulated, so comparing in it would be slower than the conversion it saves.
    /// </remarks>
    private static void Halves(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
    {
        ReadOnlySpan<Half> values = MemoryMarshal.Cast<byte, Half>(bytes).Slice(start, count);
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        bool any = false;

        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.AllValid && !mask.IsValid(start + i))
            {
                continue;
            }

            float value = (float)values[i];
            if (float.IsNaN(value))
            {
                continue;
            }

            any = true;
            min = Smaller(min, value);
            max = Larger(max, value);
        }

        if (any)
        {
            stats.MergeFloat(min, max);
        }
    }

    /// <summary><c>Math.Min</c>'s rule, generically: <c>-0.0</c> sorts below <c>+0.0</c>.</summary>
    private static T Smaller<T>(T a, T b)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a != b)
        {
            return b < a ? b : a;
        }

        return T.IsNegative(a) ? a : b;
    }

    /// <summary><c>Math.Max</c>'s rule, generically: <c>+0.0</c> sorts above <c>-0.0</c>.</summary>
    private static T Larger<T>(T a, T b)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a != b)
        {
            return b > a ? b : a;
        }

        return T.IsNegative(a) ? b : a;
    }

    // The three below read ONE value, the element of a constant column. They switch on the physical
    // type, which is what `CanonicalSupport.ReadInteger` does and what PerRowDispatchTests counts --
    // written out here rather than called so that the ratchet's table stays a list of loops.

    private static double ElementFloat(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(element),
        PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(element),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(element),
    };

    private static long ElementSigned(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.I8 => (sbyte)element[0],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(element),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(element),
        _ => BinaryPrimitives.ReadInt64LittleEndian(element),
    };

    private static ulong ElementUnsigned(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.U8 => element[0],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(element),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(element),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(element),
    };
}
