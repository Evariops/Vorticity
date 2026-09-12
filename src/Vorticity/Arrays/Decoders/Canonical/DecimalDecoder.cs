// vortex.decimal - vortex-array-0.86.1/src/arrays/decimal/vtable/mod.rs `deserialize`.
// Phase 1 contract §9.1: one buffer (`values`), an optional validity child at index 0, and a
// DecimalMetadata whose `values_type` is the buffer's stride.
//
// The stride is on the wire AND derivable from the dtype's precision, and the two must agree well
// enough to be safe: prost would coerce an unknown enumeration to DecimalType::I8 = 0 and read the
// buffer one byte at a time. DecimalMetadata.Read already rejects an out-of-domain tag; this
// decoder additionally rejects a storage narrower than the precision needs, which would truncate
// every value.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.decimal</c>: little-endian two's-complement unscaled values.</summary>
public sealed class DecimalDecoder : ArrayDecoder
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
