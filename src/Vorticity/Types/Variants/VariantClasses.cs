namespace Vorticity.Types.Variants;

/// <summary>
/// The class a variant value is of, as a shredding types it: from the kind of a scalar read, or from the value's header
/// byte alone, for a writer that routes a value without reading it. A short string or the string primitive is a string,
/// the four integers are an integer, the double a double, the two booleans' primitives a boolean; anything else, a float
/// among them, which <c>AsDouble</c> reads but no typed column of doubles holds, is of none.
/// </summary>
internal static class VariantClasses
{
    // The type ids of the variant encoding's primitives a typed column holds.
    private const int TrueType = 1;
    private const int FalseType = 2;
    private const int IntegerFirst = 3;
    private const int IntegerLast = 6;
    private const int DoubleType = 7;
    private const int StringType = 16;

    /// <summary>The class of a scalar of <paramref name="kind"/>; null for one of none.</summary>
    internal static VariantClass? Of(VariantKind kind) => kind switch
    {
        VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => VariantClass.Integer,
        VariantKind.Double => VariantClass.Double,
        VariantKind.String => VariantClass.String,
        VariantKind.Bool => VariantClass.Boolean,
        _ => null,
    };

    /// <summary>The class of the value whose header byte is <paramref name="header"/>; null for one of none, an object or a list among them.</summary>
    internal static VariantClass? OfHeader(byte header)
    {
        int basic = header & 0x03;
        if (basic == ParquetVariant.BasicShortString)
        {
            return VariantClass.String;
        }

        return basic != ParquetVariant.BasicPrimitive ? null : (header >> 2) switch
        {
            TrueType or FalseType => VariantClass.Boolean,
            >= IntegerFirst and <= IntegerLast => VariantClass.Integer,
            DoubleType => VariantClass.Double,
            StringType => VariantClass.String,
            _ => null,
        };
    }
}
