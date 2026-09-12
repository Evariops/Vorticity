// Summarizing one written chunk, so the file it lands in can be pruned - F11.
//
// The rules are the ones docs/08-semantics.md §2 states for READING a zone map, applied in the
// direction that has to produce them:
//
//   * NULLS ARE EXCLUDED from min and max, and counted separately. A null is not a small value.
//   * NANS ARE EXCLUDED TOO. The reference computes min/max with `skip_nans()` and tracks NaN in
//     its own aggregate, which is what makes "a zone with max <= 10 may be pruned for x > 10 even
//     when it contains NaN" sound: the NaN rows would not have matched anyway. A writer that let a
//     NaN into `max` would poison every comparison against that zone, because NaN compares false
//     with everything and the bound would stop meaning anything.
//   * THE BOUNDS ARE EXACT, not bounded. We emit `vortex.min` / `vortex.max`, never the bounded_
//     variants, so a reader may use them for the equality shortcut Inexact forbids.
//
// A zone with no non-null, non-NaN value has no min and no max, and says so rather than inventing
// one -- which the reader in turn reads as "this bound licenses nothing".
using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>One chunk's summary: the bounds a reader prunes with, plus its null count.</summary>
internal readonly struct ZoneStatistics
{
    private ZoneStatistics(
        bool hasBounds, FilterLiteral min, FilterLiteral max, long nullCount, bool summarizable)
    {
        HasBounds = hasBounds;
        Min = min;
        Max = max;
        NullCount = nullCount;
        IsSummarizable = summarizable;
    }

    /// <summary>Whether the chunk held a value that could bound it.</summary>
    internal bool HasBounds { get; }

    /// <summary>The smallest non-null, non-NaN value.</summary>
    internal FilterLiteral Min { get; }

    /// <summary>The largest non-null, non-NaN value.</summary>
    internal FilterLiteral Max { get; }

    /// <summary>How many rows of the chunk are null.</summary>
    internal long NullCount { get; }

    /// <summary>
    /// Whether this column's canonical form has a min/max a zone map can carry at all.
    /// </summary>
    internal bool IsSummarizable { get; }

    /// <summary>Summarizes the canonical node at <paramref name="nodeIndex"/>.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <returns>Its summary.</returns>
    internal static ZoneStatistics Compute(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        long nulls = 0;
        if (mask.AllInvalid)
        {
            nulls = node.Length;
        }
        else if (!mask.AllValid)
        {
            for (int i = 0; i < node.Length; i++)
            {
                if (!mask.IsValid(i))
                {
                    nulls++;
                }
            }
        }

        // Only a primitive gets bounds here. Utf8 and binary have a perfectly good lexicographic
        // min/max and no place to put them without building a second varbinview per zone, which is
        // a size and complexity trade the null count alone does not need; every other kind has no
        // scalar bound at all. Such a column still gets a zone map -- with the null count only,
        // which is what IS NULL pruning runs on.
        if (node.Kind != CanonicalKind.Primitive || node.Length == 0)
        {
            return new ZoneStatistics(false, default, default, nulls, node.Kind == CanonicalKind.Primitive);
        }

        return node.PType.IsFloat()
            ? Floats(node, mask, nulls)
            : Integers(node, mask, nulls);
    }

    private static ZoneStatistics Integers(CanonicalNode node, ValidityMask mask, long nulls)
    {
        PType ptype = node.PType;
        ReadOnlySpan<byte> values = node.Values.Span;
        bool signed = ptype.IsSignedInteger();

        long minSigned = long.MaxValue;
        long maxSigned = long.MinValue;
        ulong minUnsigned = ulong.MaxValue;
        ulong maxUnsigned = ulong.MinValue;
        bool any = false;

        for (int i = 0; i < node.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                continue;
            }

            any = true;
            if (signed)
            {
                long value = CanonicalSupport.ReadInteger(values, ptype, i);
                minSigned = Math.Min(minSigned, value);
                maxSigned = Math.Max(maxSigned, value);
            }
            else
            {
                ulong value = CompressedValues.ReadUnsigned(values, ptype, i);
                minUnsigned = Math.Min(minUnsigned, value);
                maxUnsigned = Math.Max(maxUnsigned, value);
            }
        }

        if (!any)
        {
            return new ZoneStatistics(false, default, default, nulls, true);
        }

        return signed
            ? new ZoneStatistics(
                true, FilterLiteral.From(minSigned), FilterLiteral.From(maxSigned), nulls, true)
            : new ZoneStatistics(
                true, FilterLiteral.From(minUnsigned), FilterLiteral.From(maxUnsigned), nulls, true);
    }

    private static ZoneStatistics Floats(CanonicalNode node, ValidityMask mask, long nulls)
    {
        PType ptype = node.PType;
        ReadOnlySpan<byte> values = node.Values.Span;

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        bool any = false;

        for (int i = 0; i < node.Length; i++)
        {
            if (!mask.IsValid(i))
            {
                continue;
            }

            double value = ptype switch
            {
                PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(values.Slice(i * 2, 2)),
                PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(values.Slice(i * 4, 4)),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(values.Slice(i * 8, 8)),
            };

            // skip_nans, and it is load-bearing rather than tidy: see the header.
            if (double.IsNaN(value))
            {
                continue;
            }

            any = true;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return any
            ? new ZoneStatistics(true, FilterLiteral.From(min), FilterLiteral.From(max), nulls, true)
            : new ZoneStatistics(false, default, default, nulls, true);
    }
}
