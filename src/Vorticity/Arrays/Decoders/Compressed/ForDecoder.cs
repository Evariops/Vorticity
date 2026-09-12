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
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Frame of reference is transparent to a take: it adds a constant to every row, so the rows it
    /// is asked for are the rows its child is asked for.
    /// </summary>
    /// <remarks>
    /// The `fastlanes.for` row of the take table - "offset add over the child's strategy" - and the
    /// reason it matters far more than its own cost suggests: the child is almost always
    /// `fastlanes.bitpacked`, so without this the positional access underneath is never reached.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {

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
        int encoded = selective
            ? context.DecodeChildSelected(in node, 0, dtype, length, wanted)
            : context.DecodeChild(in node, 0, dtype, length);

        // Everything below is expressed in the number of rows PRODUCED, which the selection
        // shortens; the child's own bound checks still use the node's declared length.
        int produced = selective ? wanted.Length : length;

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

        if (child.Length != produced)
        {
            CompressedThrow.ChildLength(Id, "encoded", child.Length, produced);
        }

        // Upstream returns the child untouched when the reference is zero. It is free and exactly
        // equivalent: same buffer, same validity, same dtype.
        if (referenceBits == 0)
        {
            return encoded;
        }

        int total = ArrayDecodeContext.CheckedMultiply(produced, width, "FoR values");
        if (total == 0)
        {
            return context.Canonical.AddPrimitive(
                dtype, produced, child.Validity, ptype, VortexBuffer.Empty);
        }

        // UNINITIALIZED: AddWrapping writes all `total` bytes, and the zero-length case returned
        // above it.
        VortexBuffer output = CompressedValues.AllocateUninitialized(
            context, total, width, Id, out Span<byte> destination);
        IntegerKernels.AddWrapping(child.Values.Span[..total], destination, width, referenceBits);
        return context.Canonical.AddPrimitive(dtype, produced, child.Validity, ptype, output);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(value),
    };
}
