// vortex.dict - vortex-array-0.86.1/src/arrays/dict/vtable/mod.rs and .../dict/array.rs.
//
// Two children, no buffers: `codes` of the array's own length and `values` of `values_len`.
// Contract §10.5 fixes the codes' nullability, which is NOT simply the array's:
//
//     is_nullable_codes = true  -> Nullable
//     is_nullable_codes = false -> NonNullable
//     is_nullable_codes absent  -> the array dtype's nullability   (back-compat fallback)
//
// The absent case is not the same as `false`; getting it wrong changes the codes child's dtype,
// which changes how ITS validity child is read, which silently changes values.
//
// Class I: every code is bounds-checked against `values_len` before it indexes anything.
// `all_values_referenced` is a pure hint - absent or false means "unknown" - and must never let a
// bounds check be skipped. Float dictionary keys are compared by bit pattern upstream, never by
// IEEE equality, so a decoder must copy the key bytes and never canonicalize them (corpus
// manifest caveat 2: distributions/float_specials_f64_* hold 15 distinct keys including both
// zeros and several NaNs).
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.dict</c> by gathering the dictionary values through the codes.</summary>
public sealed class DictDecoder : ArrayDecoder
{
    private const string Id = "vortex.dict";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DictDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.dict"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Dict;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Takes on the CODES and leaves the values alone, which is the whole point of a dictionary.
    /// </summary>
    /// <remarks>
    /// The `vortex.dict` row of the take table, verbatim: "take on codes, values untouched". The
    /// values child is shared by every row, so a take has to have all of it whatever it asks for;
    /// the codes are one per row and are where the selection bites. The codes child is very often
    /// `fastlanes.for` over `fastlanes.bitpacked` - our own writer cascades them there - so this is
    /// also what lets the positional access underneath be reached at all.
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
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        DictMetadata metadata = DictMetadata.Read(node.Metadata);

        if (!metadata.CodesPType.IsInteger())
        {
            CompressedThrow.Format(
                $"{Id} codes must be an integer physical type, not {metadata.CodesPType.Name()}.");
        }

        int valuesLength = ArrayDecodeContext.CheckedLength(metadata.ValuesLength, $"{Id} values_len");

        Nullability codesNullability = metadata.IsNullableCodes switch
        {
            true => Nullability.Nullable,
            false => Nullability.NonNullable,
            null => dtype.Nullability,
        };

        DType codesType = context.Types.Primitive(metadata.CodesPType, codesNullability);
        int codesIndex = selective
            ? context.DecodeChildSelected(in node, 0, codesType, length, wanted)
            : context.DecodeChild(in node, 0, codesType, length);
        int valuesIndex = context.DecodeChild(in node, 1, dtype, valuesLength);
        int produced = selective ? wanted.Length : length;

        CanonicalNode codesNode = context.Canonical.GetNode(codesIndex);
        if (codesNode.Kind != CanonicalKind.Primitive)
        {
            CompressedThrow.ChildKind(Id, "codes", codesNode.Kind, "a Primitive");
        }

        if (codesNode.PType != metadata.CodesPType)
        {
            CompressedThrow.Format(
                $"{Id}'s codes child decoded as {codesNode.PType.Name()}; " +
                $"{metadata.CodesPType.Name()} was declared.");
        }

        if (codesNode.Length != produced)
        {
            CompressedThrow.ChildLength(Id, "codes", codesNode.Length, produced);
        }

        ValueReader values = ValueReader.Of(context.Canonical, valuesIndex, Id);
        if (values.Length != valuesLength)
        {
            CompressedThrow.ChildLength(Id, "values", values.Length, valuesLength);
        }

        ReadOnlySpan<byte> codes = codesNode.Values.Span;
        PType codesPType = metadata.CodesPType;
        ValidityReader codesValidity = ValidityReader.Of(context.Canonical, codesNode.Validity);
        ValidityReader valuesValidity = ValidityReader.Of(context.Canonical, values.Validity);

        bool tracked = !codesNode.Validity.IsAllValid || !values.Validity.IsAllValid;

        DataBufferSet dataBuffers = DataBufferSet.Collect(context.Canonical, in values, false, default);
        try
        {
            ValueWriter writer = ValueWriter.Create(context, in values, produced, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, produced, tracked, Id);

            for (int row = 0; row < produced; row++)
            {
                if (!codesValidity.IsValid(row))
                {
                    // A null code selects nothing; the row stays zeroed and invalid.
                    continue;
                }

                long code = CompressedValues.ReadInteger(codes, codesPType, row);

                // Class I. Upstream leans on the compressor's invariant plus its `take` kernel;
                // this is untrusted input, so the check is unconditional.
                if ((ulong)code >= (ulong)(uint)valuesLength)
                {
                    CompressedThrow.Format(
                        $"{Id} code {code} at row {row} is outside [0, {valuesLength}).");
                }

                int index = (int)code;
                writer.Copy(in values, index, row);
                if (tracked && valuesValidity.IsValid(index))
                {
                    validity.SetValid(row);
                }
            }

            return writer.Complete(
                context, dtype, validity.Complete(context, dtype, Id), dataBuffers.Buffers);
        }
        finally
        {
            dataBuffers.Dispose();
        }
    }
}
