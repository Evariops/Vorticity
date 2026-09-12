// vortex.struct - vortex-array-0.86.1/src/arrays/struct_/vtable/mod.rs `deserialize`.
//
// This is the one encoding whose validity child comes FIRST. Every other encoding with validity
// puts it last, so the shared ArrayDecodeContext.DecodeValidity helper - which is written around a
// trailing child - cannot be used here (Phase 1 contract §9.1). The constant collapse of §2.6
// rule 3 still applies, so it is repeated explicitly rather than skipped: an all-null struct
// arrives as a vortex.constant(false) child and must report AllInvalid.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Decodes <c>vortex.struct</c>: one canonical child per field, validity first.</summary>
public sealed class StructDecoder : ArrayDecoder
{
    /// <summary>The wire id, UTF-8.</summary>
    public const string Id = "vortex.struct";

    private const int StackFields = 32;

    /// <summary>The shared, stateless instance.</summary>
    public static readonly StructDecoder Instance = new StructDecoder();

    private StructDecoder()
    {
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.struct"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Struct;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        EncodingMetadata.RequireEmpty(node.Metadata, Id);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        CanonicalSupport.RequireKind(dtype, DTypeKind.Struct, Id);

        int fieldCount = dtype.FieldCount;
        int childCount = node.ChildCount;

        // nfields -> no validity child; nfields + 1 -> validity at index 0. Nothing else.
        int fieldBase;
        Validity validity;
        if (childCount == fieldCount)
        {
            fieldBase = 0;
            validity = Validity.FromNullability(dtype.Nullability);
        }
        else if (childCount == fieldCount + 1)
        {
            fieldBase = 1;
            validity = DecodeLeadingValidity(context, in node, length);
        }
        else
        {
            ArrayDecodeContext.RequireChildCount(childCount, fieldCount, fieldCount + 1, Id);
            return -1;
        }

        Span<int> stack = stackalloc int[StackFields];
        Scratch<int> fields = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> indices = fields.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                indices[i] = context.DecodeChild(in node, fieldBase + i, dtype.GetField(i), length);
            }

            return context.Canonical.AddStruct(dtype, length, validity, indices);
        }
        finally
        {
            fields.Dispose();
        }
    }

    /// <summary>
    /// The body of <see cref="ArrayDecodeContext.DecodeValidity"/> for a validity child at index 0.
    /// The validity array may itself be encoded, so this goes through the normal dispatch and
    /// never a bitmap fast path (contract §2.6 rule 2).
    /// </summary>
    private static Validity DecodeLeadingValidity(
        ArrayDecodeContext context, in ArrayNode node, int length)
    {
        DType boolType = context.Types.Bool(Nullability.NonNullable);
        int decoded = context.DecodeChild(in node, 0, boolType, length);

        CanonicalNode bits = context.Canonical.GetNode(decoded);
        if (bits.Kind != CanonicalKind.Bool)
        {
            throw new VortexFormatException($"A {Id} validity child decoded to {bits.Kind}, not Bool.");
        }

        if (bits.Length != length)
        {
            throw new VortexFormatException(
                $"A {Id} validity child of {bits.Length} rows cannot describe an array of {length}.");
        }

        if (length == 0)
        {
            return Validity.AllValid;
        }

        return ArrayDecodeContext.ClassifyValidityBits(bits.Bits.Span, bits.BitOffset, length) switch
        {
            ValidityBitmapShape.AllClear => Validity.AllInvalid,
            ValidityBitmapShape.AllSet => Validity.AllValid,
            _ => Validity.Bitmap(decoded),
        };
    }
}
