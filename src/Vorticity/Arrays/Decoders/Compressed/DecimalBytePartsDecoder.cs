// vortex.decimal_byte_parts - vortex-decimal-byte-parts-0.86.1/src/decimal_byte_parts/mod.rs,
// `deserialize` and `to_canonical_decimal`.
//
// The name promises more than the encoding currently delivers: the design reserves 1-4 primitive
// children ("most significant part" plus lower parts), but `lower_part_count` is pinned to zero
// upstream and a non-zero value is rejected by DecimalBytePartsMetadata.Read before we get here.
// So the decode is a REINTERPRETATION, not an arithmetic recomposition: one signed primitive child
// whose buffer already holds the unscaled values, re-labelled as a decimal.
//
// That makes it zero-copy - the child's values buffer becomes the decimal's, untouched - and it
// makes the storage width the CHILD's width, not the width the precision would imply. Upstream is
// explicit about this: `to_canonical_decimal` calls `DecimalArray::new_unchecked` with
// `prim.to_buffer::<P>()`, so a decimal(18,4) whose values all fit in i32 canonicalizes to I32
// storage. vortex.decimal's "storage must be wide enough for the precision" check is therefore
// deliberately NOT repeated here: it would reject files the reference writes.
using System;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.decimal_byte_parts</c> into a canonical decimal.</summary>
public sealed class DecimalBytePartsDecoder : ArrayDecoder
{
    /// <summary>The wire id.</summary>
    public const string Id = "vortex.decimal_byte_parts";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DecimalBytePartsDecoder Instance = new DecimalBytePartsDecoder();

    private DecimalBytePartsDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.decimal_byte_parts"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.DecimalByteParts;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Decimal, Id);

        DecimalBytePartsMetadata metadata = DecimalBytePartsMetadata.Read(node.Metadata);
        PType msp = metadata.ZerothChildPType;

        // `DecimalBytePartsData::validate`: "decimal bytes parts, first part must be a signed
        // array". An unsigned msp would reinterpret the sign bit as magnitude, so this is a
        // correctness check, not a taste one.
        if (!msp.IsSignedInteger())
        {
            CompressedThrow.Format(
                $"{Id}'s zeroth_child_ptype is {msp.Name()}; a signed integer is required.");
        }

        // The child carries the array's own nullability: upstream derives it as
        // `DType::Primitive(metadata.zeroth_child_ptype(), dtype.nullability())` and the validity
        // of the whole array is the child's (ValidityChild -> msp).
        DType childType = context.Types.Primitive(msp, dtype.Nullability);
        int childIndex = context.DecodeChild(in node, 0, childType, length);
        CanonicalNode child = context.Canonical.GetNode(childIndex);

        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "msp", child.Kind, "a Primitive");
        }

        if (child.PType != msp)
        {
            CompressedThrow.Format(
                $"{Id}'s msp child decoded as {child.PType.Name()}; {msp.Name()} was declared.");
        }

        if (child.Length != length)
        {
            CompressedThrow.ChildLength(Id, "msp", child.Length, length);
        }

        DecimalStorageType storage = DecimalStorage.FromByteWidth(msp.ByteWidth());
        return context.Canonical.AddDecimal(
            dtype, length, child.Validity, storage, dtype.Precision, dtype.Scale, child.Values);
    }
}
