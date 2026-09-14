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
    public override bool SelectsWithoutFullDecode => true;

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
            // A Bool value array is bit-packed, so there is no fixed-width row to move and the
            // typed kernel has nothing to specialize on; it keeps the row-at-a-time loop. Every
            // other kind is a gather, which is what the kernel is.
            bool bitPacked = values.Kind == CanonicalKind.Bool;
            ValueWriter writer = bitPacked
                ? ValueWriter.Create(context, in values, produced, 0, Id)
                : ValueWriter.CreateUninitialized(context, in values, produced, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, produced, tracked, Id);

            if (bitPacked)
            {
                GatherBits(
                    in values, codes, codesPType, valuesLength, produced, in codesValidity,
                    in valuesValidity, tracked, ref writer, in validity);
            }
            else if (!tracked)
            {
                // The dense path: no validity to read, no validity to write, one load and one
                // store per row with the physical types resolved before the loop starts.
                int bad = RowKernels.Gather(
                    codes, codesPType, values.Bytes, values.Width, valuesLength,
                    writer.Bytes, produced);
                if (bad >= 0)
                {
                    ThrowCode(codes, codesPType, bad, valuesLength);
                }
            }
            else
            {
                int bad = RowKernels.GatherMasked(
                    codes, codesPType, values.Bytes, values.Width, valuesLength,
                    writer.Bytes, produced,
                    codesValidity.Bits, codesValidity.BitOffset,
                    valuesValidity.Bits, valuesValidity.BitOffset, valuesValidity.IsAllValid,
                    validity.Bits);
                if (bad >= 0)
                {
                    ThrowCode(codes, codesPType, bad, valuesLength);
                }

                // An all-invalid codes child has no bitmap to mask with, so the kernel would treat
                // every row as valid. It cannot happen through the reader -- an all-invalid child
                // collapses to ValidityKind.AllInvalid -- so it is handled here rather than costing
                // a test per row inside the kernel.
                if (codesValidity.IsAllInvalid)
                {
                    writer.Bytes.Clear();
                    validity.Bits.Clear();
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

    /// <summary>The row-at-a-time path a bit-packed value array still needs.</summary>
    private static void GatherBits(
        in ValueReader values, ReadOnlySpan<byte> codes, PType codesPType, int valuesLength,
        int produced, in ValidityReader codesValidity, in ValidityReader valuesValidity,
        bool tracked, ref ValueWriter writer, in ValidityWriter validity)
    {
        for (int row = 0; row < produced; row++)
        {
            if (!codesValidity.IsValid(row))
            {
                writer.ClearRow(row);
                continue;
            }

            uint code = RowKernels.CodeAt(codes, codesPType, row);
            if (code >= (uint)valuesLength)
            {
                ThrowCode(codes, codesPType, row, valuesLength);
            }

            int index = (int)code;
            writer.Copy(in values, index, row);
            if (tracked && valuesValidity.IsValid(index))
            {
                validity.SetValid(row);
            }
        }
    }

    // Class I. Upstream leans on the compressor's invariant plus its `take` kernel; this is
    // untrusted input, so the check is unconditional -- what moved is where it is spelled, not
    // whether it runs.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowCode(
        ReadOnlySpan<byte> codes, PType codesPType, int row, int valuesLength) =>
        CompressedThrow.Format(
            $"{Id} code {CompressedValues.ReadInteger(codes, codesPType, row)} at row {row} is " +
            $"outside [0, {valuesLength}).");
}
