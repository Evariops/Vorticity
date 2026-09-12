// fastlanes.for - vortex-fastlanes-0.86.1/src/for/vtable/mod.rs and
// src/for/array/for_decompress.rs.
//
// Phase 1 contract §0a C2: the metadata is NOT empty, as spec/METADATA.md once claimed. It is a
// bare protobuf ScalarValue - the reference, without its dtype - and an empty metadata decodes to
// a null reference, which upstream's `validate_parts` rejects ("Reference value cannot be null").
// 75 corpus files carry a FoR node, and reading it as empty-metadata produces silently wrong
// values on every one of them.
using System;
using System.Buffers.Binary;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>fastlanes.for</c>: <c>value = encoded + reference</c>, wrapping.</summary>
public sealed class ForDecoder : ArrayDecoder
{
    private const string Id = "fastlanes.for";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly ForDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "fastlanes.for"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.FastLanesFor;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 1, Id);

        // `vortex_ensure!(dtype.is_int(), "FoR requires an integer dtype")`.
        PType ptype = CompressedValues.RequireIntegerPrimitive(dtype, Id);

        // The reference is interpreted against the node's OWN dtype, exactly as
        // `ScalarValue::from_proto_bytes(metadata, dtype, session)` does; that is also what makes
        // "reference dtype == array dtype" automatic rather than a separate check.
        TypedScalar reference = TypedScalarReader.Read(node.Metadata, dtype, context.Scalars, context.Types);
        if (reference.IsNull)
        {
            CompressedThrow.Format("Reference value cannot be null.");
        }

        int width = ptype.ByteWidth();
        Span<byte> referenceBytes = stackalloc byte[8];
        referenceBytes = referenceBytes[..width];
        reference.WriteTo(referenceBytes, ptype);
        ulong referenceBits = ReadBits(referenceBytes);

        // The encoded child carries the array's dtype unchanged, including its nullability, and
        // the FoR node has no validity of its own (ValidityVTableFromChild).
        int encoded = context.DecodeChild(in node, 0, dtype, length);

        // The child is checked BEFORE the zero-reference shortcut, so a child that decoded to the
        // wrong shape is rejected whatever the reference happens to be.
        CanonicalNode child = context.Canonical.GetNode(encoded);
        if (child.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "encoded", child.Kind, "a Primitive");
        }

        if (child.PType != ptype)
        {
            CompressedThrow.Format(
                $"{Id}'s encoded child decoded as {child.PType.Name()}; {ptype.Name()} was required.");
        }

        if (child.Length != length)
        {
            CompressedThrow.ChildLength(Id, "encoded", child.Length, length);
        }

        // Upstream returns the child untouched when the reference is zero. It is free and exactly
        // equivalent: same buffer, same validity, same dtype.
        if (referenceBits == 0)
        {
            return encoded;
        }

        int total = ArrayDecodeContext.CheckedMultiply(length, width, "FoR values");
        if (total == 0)
        {
            return context.Canonical.AddPrimitive(
                dtype, length, child.Validity, ptype, VortexBuffer.Empty);
        }

        VortexBuffer output = CompressedValues.Allocate(
            context, total, width, Id, out Span<byte> destination);
        IntegerKernels.AddWrapping(child.Values.Span[..total], destination, width, referenceBits);
        return context.Canonical.AddPrimitive(dtype, length, child.Validity, ptype, output);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(value),
    };
}
