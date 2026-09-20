using System;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.RowEncoding;

/// <summary>A column's per-row encoded width: a constant, or data-dependent.</summary>
internal readonly struct RowWidth
{
    private readonly int _width;

    private RowWidth(int width) => _width = width;

    /// <summary>Per-row width depends on the data (Utf8/Binary, or a composite containing one).</summary>
    internal static RowWidth Variable => new(-1);

    /// <summary>Every row encodes to exactly this many bytes, sentinels included.</summary>
    internal static RowWidth Fixed(int width) => new(width);

    /// <summary>Whether every row has the same width.</summary>
    internal bool IsFixed => _width >= 0;

    /// <summary>The constant width; meaningless unless <see cref="IsFixed"/>.</summary>
    internal int Width => _width;
}

/// <summary>
/// Classifies dtypes, and rejects the ones the format defines no ordering for. The width follows
/// from the dtype alone, never from the options or the data, so the size pass walks the schema
/// once instead of the data.
/// </summary>
internal static class RowWidths
{
    /// <summary>The 32 data bytes of one variable-length block.</summary>
    internal const int VarBlockData = 32;

    /// <summary>A whole variable-length block: 32 data bytes plus the marker.</summary>
    internal const int VarBlockTotal = VarBlockData + 1;

    /// <summary>The encoded size of a null variable-length value: the sentinel alone.</summary>
    internal const int VarNullSize = 1;

    /// <summary>The encoded size of an empty variable-length value: the sentinel alone.</summary>
    internal const int VarEmptySize = 1;

    /// <summary>Classifies <paramref name="dtype"/>'s per-row encoded width.</summary>
    internal static RowWidth For(DType dtype)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                // A sentinel and nothing else: there is no value to distinguish.
                return RowWidth.Fixed(1);

            case DTypeKind.Bool:
                return RowWidth.Fixed(2);

            case DTypeKind.Primitive:
                return RowWidth.Fixed(1 + dtype.PType.ByteWidth());

            case DTypeKind.Decimal:
            {
                DecimalStorageType key = KeyStorage(dtype.Precision);
                if (key == DecimalStorageType.I256)
                {
                    throw Unsupported("decimal256", "Row encoding is not defined for 256-bit decimals.");
                }

                return RowWidth.Fixed(1 + DecimalStorage.ByteWidth(key));
            }

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return RowWidth.Variable;

            case DTypeKind.FixedSizeList:
            {
                RowWidth element = For(dtype.ElementType);
                if (!element.IsFixed)
                {
                    return RowWidth.Variable;
                }

                // Nesting makes this product the one place where a schema alone can overflow an int.
                long total = 1 + ((long)element.Width * dtype.FixedSize);
                if (total > int.MaxValue)
                {
                    throw Overflow();
                }

                return RowWidth.Fixed((int)total);
            }

            case DTypeKind.Struct:
            {
                long total = 1;
                for (int i = 0; i < dtype.FieldCount; i++)
                {
                    RowWidth field = For(dtype.GetField(i));
                    if (!field.IsFixed)
                    {
                        return RowWidth.Variable;
                    }

                    total += field.Width;
                    if (total > int.MaxValue)
                    {
                        throw Overflow();
                    }
                }

                return RowWidth.Fixed((int)total);
            }

            case DTypeKind.List:
                throw Unsupported(
                    "list",
                    "Row encoding does not support variable-size lists: the format defines no ordering for them.");

            case DTypeKind.Map:
                throw Unsupported(
                    "map",
                    "Row encoding does not support maps: the format defines no ordering for them.");

            case DTypeKind.Variant:
                throw Unsupported(
                    "variant",
                    "Row encoding does not support variants: the format defines no ordering for them.");

            case DTypeKind.Union:
                throw Unsupported("union", "Row encoding does not support unions.");

            case DTypeKind.Extension:
                // Rejected rather than unwrapped: silently encoding the storage array would put
                // these bytes at odds with the day the format defines a temporal ordering.
                throw Unsupported(
                    dtype.ExtensionId,
                    "Row encoding does not support extension dtypes, so timestamps and dates " +
                    "cannot be row-encoded directly. Normalize the column to its storage type first.");

            default:
                throw Unsupported(dtype.Kind.ToString(), "Row encoding does not support this dtype.");
        }
    }

    /// <summary>
    /// The storage width a decimal's row key is written at, chosen from the declared precision and
    /// never from the chunk's physical values type: chunks of one column can compress to different
    /// physical widths, and keys taken from those would not compare across chunks.
    /// </summary>
    internal static DecimalStorageType KeyStorage(byte precision) => DecimalStorage.ForPrecision(precision);

    /// <summary>
    /// The encoded size of a non-empty variable-length value: the sentinel plus
    /// <c>ceil(length / 32)</c> whole blocks.
    /// </summary>
    internal static int NonEmptyVarSize(int length)
    {
        int blocks = ((length - 1) / VarBlockData) + 1;
        return 1 + (blocks * VarBlockTotal);
    }

    private static VortexUnsupportedException Unsupported(string id, string detail) =>
        new(id, VortexComponentKind.DType, detail);

    private static VortexFormatException Overflow() =>
        new("The row encoding of this schema exceeds 2 GiB per row.");
}
