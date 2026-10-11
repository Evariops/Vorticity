using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Types.Numerics;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// What a scan reads of a file's row groups: those its rows reach, less those the footer's
/// statistics prove its filter selects no row of; and the rows the statistics prove it selects of
/// the others, where they decide it, which a count takes without reading them.
/// </summary>
/// <remarks>
/// <para>
/// Each row group is one zone of the core's pruner, its bounds the chunk's statistics of every column
/// the filter reads that is not under a list: a top-level column, or a struct's field, whose null
/// count is the rows where it is null. The pruner proves only what the bounds prove; a bound it
/// cannot read leaves the row group read.
/// </para>
/// <para>
/// A bound is believed only where the standard makes it one: <c>min_value</c> and <c>max_value</c>
/// under a column order this build knows, on a logical type it knows; the legacy <c>min</c> and
/// <c>max</c>, without them, on the types whose legacy order was signed, which is theirs; neither
/// on a NaN, nor on the types no order covers.
/// </para>
/// </remarks>
internal sealed class RowGroupPlan
{
    private RowGroupPlan(bool[] read, long[] proven, int prunedBlocks, int blocks)
    {
        Read = read;
        Proven = proven;
        PrunedBlocks = prunedBlocks;
        Blocks = blocks;
    }

    /// <summary>Per row group, whether the scan reads it.</summary>
    internal bool[] Read { get; }

    /// <summary>Per row group the scan reads, the rows its filter selects when the statistics decide it; else -1.</summary>
    internal long[] Proven { get; }

    /// <summary>The batches of the row groups the statistics pruned.</summary>
    internal int PrunedBlocks { get; }

    /// <summary>The batches of every row group the scan's rows reach.</summary>
    internal int Blocks { get; }

    /// <summary>The plan of <paramref name="spec"/> over <paramref name="file"/>.</summary>
    internal static RowGroupPlan For(ParquetFile file, ScanSpec spec)
    {
        ParquetFooter footer = file.Footer;
        int groups = footer.RowGroups.Length;
        bool[] read = new bool[groups];
        long[] proven = new long[groups];
        Array.Fill(proven, -1);
        FilterColumns? pruning = FilterColumns.For(file, spec);
        int batchRows = ParquetBatches.RowsOf(spec);
        int prunedBlocks = 0;
        int blocks = 0;
        for (int group = 0; group < groups; group++)
        {
            RowGroupEntry entry = footer.RowGroups[group];
            long first = entry.FirstRow;
            long end = first + entry.RowCount;
            if (entry.RowCount == 0 || (spec.Rows is { } range && (first >= range.End || end <= range.Start)))
            {
                continue;
            }

            int groupBlocks = (int)((entry.RowCount + batchRows - 1) / batchRows);
            blocks += groupBlocks;
            read[group] = true;
            if (pruning is null)
            {
                continue;
            }

            ZonePruner zones = pruning.Of(group, entry.RowCount);
            RowRange whole = new(0, entry.RowCount);
            if (!zones.MayMatch(whole))
            {
                read[group] = false;
                prunedBlocks += groupBlocks;
                continue;
            }

            // A count proven of the whole group is the scan's only when the scan takes the whole group.
            bool inside = spec.Rows is not { } rows || (rows.Start <= first && rows.End >= end);
            if (inside && spec.Take is null && zones.TryCount(whole, out long count))
            {
                proven[group] = count;
            }
        }

        return new RowGroupPlan(read, proven, prunedBlocks, blocks);
    }
}

/// <summary>A column chunk's statistics as the bounds the core's pruner compares a filter's literals with.</summary>
internal static class ColumnBounds
{
    /// <summary>Whether the column's bounds are decimals, unscaled at its scale.</summary>
    internal static bool IsDecimal(ParquetColumn column) => column.Logical.Kind == LogicalTypeKind.Decimal;

    /// <summary>The order of two values of a column, decimals at their scale; false where they do not compare.</summary>
    internal static bool TryOrder(FilterLiteral a, FilterLiteral b, bool decimals, out int order)
    {
        if (decimals)
        {
            order = 0;
            if (!ComparisonKernels.TryDecimal(a, out Int256 x) || !ComparisonKernels.TryDecimal(b, out Int256 y))
            {
                return false;
            }

            order = x.CompareTo(y);
            return true;
        }

        return ZonePruner.TryCompare(a, b, out order);
    }

    /// <summary>The bounds and counts <paramref name="statistics"/> give <paramref name="column"/>, those it does not believe left out.</summary>
    internal static ZoneBounds Of(ParquetColumn column, in ColumnStatistics statistics, ReadOnlySpan<byte> footer)
    {
        if (!statistics.IsPresent)
        {
            return ZoneBounds.Unknown;
        }

        ByteRange min = ByteRange.None;
        ByteRange max = ByteRange.None;
        bool exact = false;
        if (column.Logical.Kind is LogicalTypeKind.Geometry or LogicalTypeKind.Geography)
        {
            // No order covers them: their bounds are a bounding box's business.
            return ZoneBounds.Create(default, false, default, false, false, statistics.NullCount, statistics.HasNullCount);
        }

        if (statistics.MinValue.IsPresent || statistics.MaxValue.IsPresent)
        {
            // Without a column order this build knows, the standard gives them no meaning; the total
            // order of IEEE 754 is a float's alone.
            bool floats = column.Physical is PhysicalType.Float or PhysicalType.Double || column.Form == LeafForm.Float16;
            if (column.Order == ColumnOrderKind.TypeDefined || (column.Order == ColumnOrderKind.Ieee754TotalOrder && floats))
            {
                min = statistics.MinValue;
                max = statistics.MaxValue;
                exact = statistics.IsMinValueExact && statistics.IsMaxValueExact;
            }
        }
        else if (SignedLegacyOrder(column))
        {
            min = statistics.LegacyMin;
            max = statistics.LegacyMax;
        }

        FilterLiteral low = default;
        FilterLiteral high = default;
        bool hasMin = min.IsPresent && TryLiteral(column, min.Of(footer), out low);
        bool hasMax = max.IsPresent && TryLiteral(column, max.Of(footer), out high);
        return ZoneBounds.Create(
            low, hasMin, high, hasMax, exact && hasMin && hasMax,
            statistics.NullCount, statistics.HasNullCount, statistics.NanCount, statistics.HasNanCount);
    }

    /// <summary>
    /// A plain-encoded bound as a literal of the column's domain: the order its type is compared in
    /// read into the literal's kind. A bound of the wrong size, a NaN, or a type no order covers is none.
    /// </summary>
    internal static bool TryLiteral(ParquetColumn column, ReadOnlySpan<byte> bytes, out FilterLiteral literal)
    {
        literal = default;
        bool unsigned = column.Logical.Kind == LogicalTypeKind.Integer && !column.Logical.IsSigned;
        switch (column.Physical)
        {
            case PhysicalType.Boolean:
                if (bytes.Length != 1)
                {
                    return false;
                }

                literal = FilterLiteral.From(bytes[0] != 0);
                return true;

            case PhysicalType.Int32:
                if (bytes.Length != sizeof(int))
                {
                    return false;
                }

                int value32 = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                literal = unsigned ? FilterLiteral.From((ulong)(uint)value32) : FilterLiteral.From((long)value32);
                return true;

            case PhysicalType.Int64:
                if (bytes.Length != sizeof(long))
                {
                    return false;
                }

                long value64 = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                literal = unsigned ? FilterLiteral.From((ulong)value64) : FilterLiteral.From(value64);
                return true;

            case PhysicalType.Float:
                if (bytes.Length != sizeof(float))
                {
                    return false;
                }

                float single = BinaryPrimitives.ReadSingleLittleEndian(bytes);
                literal = FilterLiteral.From(single);
                return !float.IsNaN(single);

            case PhysicalType.Double:
                if (bytes.Length != sizeof(double))
                {
                    return false;
                }

                double wide = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
                literal = FilterLiteral.From(wide);
                return !double.IsNaN(wide);

            case PhysicalType.ByteArray:
            case PhysicalType.FixedLenByteArray:
                switch (column.Form)
                {
                    case LeafForm.BigEndianDecimal:
                        return TryDecimal(bytes, out literal);
                    case LeafForm.Binary:
                    case LeafForm.Utf8:
                        // Byte arrays order as unsigned bytes, as the literal's bytes compare.
                        literal = FilterLiteral.From(bytes);
                        return true;
                    case LeafForm.Float16:
                        if (bytes.Length != 2)
                        {
                            return false;
                        }

                        Half half = BinaryPrimitives.ReadHalfLittleEndian(bytes);
                        literal = FilterLiteral.From((double)half);
                        return !Half.IsNaN(half);
                    default:
                        return false;
                }

            default:
                // INT96 is ordered only by an order this build does not apply.
                return false;
        }
    }

    /// <summary>Big-endian two's complement, sign-extended to 256 bits, as the unscaled decimal's literal.</summary>
    private static bool TryDecimal(ReadOnlySpan<byte> bytes, out FilterLiteral literal)
    {
        literal = default;
        if (bytes.IsEmpty || bytes.Length > 32)
        {
            return false;
        }

        Span<byte> extended = stackalloc byte[32];
        extended[..(32 - bytes.Length)].Fill((bytes[0] & 0x80) != 0 ? (byte)0xFF : (byte)0);
        bytes.CopyTo(extended[(32 - bytes.Length)..]);
        literal = LiteralReader.Decimal(Int256.FromBigEndianBytes(extended));
        return true;
    }

    /// <summary>
    /// Whether the legacy <c>min</c> and <c>max</c>, which writers compared as signed values, bound
    /// the column: INT32 and INT64 but the unsigned ones, FLOAT and DOUBLE.
    /// </summary>
    private static bool SignedLegacyOrder(ParquetColumn column) => column.Physical switch
    {
        PhysicalType.Int32 or PhysicalType.Int64 => !(column.Logical.Kind == LogicalTypeKind.Integer && !column.Logical.IsSigned),
        PhysicalType.Float or PhysicalType.Double => true,
        _ => false,
    };
}
