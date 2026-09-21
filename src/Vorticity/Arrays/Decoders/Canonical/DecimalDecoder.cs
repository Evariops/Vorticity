using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.decimal</c>: little-endian two's-complement unscaled values.</summary>
/// <remarks>
/// The values buffer's stride is both stated in the node's metadata and derivable from the dtype's
/// precision, and the two have to agree closely enough to be safe: a storage narrower than the
/// precision needs would truncate every value, so it is refused here.
/// </remarks>
internal sealed class DecimalDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.decimal";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DecimalDecoder Instance = new DecimalDecoder();

    private DecimalDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.decimal"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Decimal;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        DecimalMetadata metadata = DecimalMetadata.Read(node.Metadata);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Decimal, Id);

        DecimalStorageType storage = metadata.ValuesType;
        DecimalStorageType required = DecimalStorage.ForPrecision(dtype.Precision);
        int width = DecimalStorage.ByteWidth(storage);
        if (width < DecimalStorage.ByteWidth(required))
        {
            throw new VortexFormatException(
                $"{Id} stores {storage} values, which cannot hold a decimal of precision " +
                $"{dtype.Precision}; {required} is the narrowest legal storage.");
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);

        VortexBuffer values = node.GetBuffer(0);
        CanonicalSupport.RequireExactBuffer(values, length, width, Id + " values");

        return context.Canonical.AddDecimal(
            dtype, length, validity, storage, dtype.Precision, dtype.Scale, values);
    }
}
