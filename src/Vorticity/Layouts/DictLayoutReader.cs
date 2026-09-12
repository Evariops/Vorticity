// vortex.dict - vortex-layout-0.86.1/src/layouts/dict/mod.rs. Zero segments, exactly two children:
//
//     0 => Auxiliary("values"),  1 => Transparent("codes")
//
// CHILD 0 IS VALUES AND CHILD 1 IS CODES, which is THE OPPOSITE ORDER FROM THE vortex.dict ARRAY
// (contract §10.1: array child 0 = codes, child 1 = values). Two components with the same id and
// the reverse order is exactly the kind of thing that gets "fixed" into a bug, so it is written
// twice: here and in the parser.
//
// The gather itself mirrors the array decoder's
// (src/Vorticity/Arrays/Decoders/Compressed/DictDecoder.cs) and shares its plumbing rather than
// re-deriving the VarBinView buffer remap and the validity collapse.
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>Reads a <c>vortex.dict</c> layout by gathering its values through its codes.</summary>
public sealed class DictLayoutReader : LayoutReader
{
    private const string Id = "vortex.dict";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly DictLayoutReader Instance = new DictLayoutReader();

    private DictLayoutReader()
    {
    }

    /// <inheritdoc/>
    public override LayoutEncodingId EncodingId => LayoutEncodingId.Dict;

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.dict"u8;

    /// <inheritdoc/>
    public override void RegisterSegments(
        in LayoutNode node, RowRange rows, in FieldMask fields, SegmentRequestSet segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        CheckRange(in node, rows);

        LayoutNode values = node.GetChild(0);
        LayoutNode codes = node.GetChild(1);

        // The values child is always read WHOLE: a code anywhere in the requested rows may name any
        // dictionary entry. This is the one place a layout materializes something the row range did
        // not ask for, and it is deliberate (contract §11.3).
        RegisterChild(in values, RowRange.FromLength(0, values.RowCount), in fields, segments);

        FieldMask all = FieldMask.All;
        RegisterChild(in codes, rows, in all, segments);
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        int length = BatchLength(rows);
        LayoutNode valuesLayout = node.GetChild(0);
        LayoutNode codesLayout = node.GetChild(1);

        int valuesLength = NodeLength(in valuesLayout);
        int valuesIndex = ExecuteChild(
            in valuesLayout, RowRange.FromLength(0, valuesLength), in fields, context);

        FieldMask all = FieldMask.All;
        int codesIndex = ExecuteChild(in codesLayout, rows, in all, context);

        CanonicalNode codesNode = context.Canonical.GetNode(codesIndex);
        if (codesNode.Kind != CanonicalKind.Primitive)
        {
            LayoutsThrow.Format($"A {Id} layout's codes child decoded to {codesNode.Kind}, not a Primitive.");
        }

        if (codesNode.Length != length)
        {
            LayoutsThrow.Format(
                $"A {Id} layout's codes child produced {codesNode.Length} rows; {length} were asked for.");
        }

        DType dtype = context.Canonical.GetNode(valuesIndex).DType;
        return Gather(context, dtype, valuesIndex, valuesLength, codesIndex, length);
    }

    /// <summary>result[row] = values[codes[row]], with the codes bounds-checked unconditionally.</summary>
    private static int Gather(
        ScanContext context, DType dtype, int valuesIndex, int valuesLength, int codesIndex, int length)
    {
        ArrayDecodeContext decode = context.Decode;
        CanonicalArena arena = context.Canonical;

        ValueReader values = ValueReader.Of(arena, valuesIndex, Id);
        if (values.Length != valuesLength)
        {
            LayoutsThrow.Format(
                $"A {Id} layout's values child produced {values.Length} rows; the layout declares {valuesLength}.");
        }

        CanonicalNode codesNode = arena.GetNode(codesIndex);
        ReadOnlySpan<byte> codes = codesNode.Values.Span;
        PType codesPType = codesNode.PType;
        ValidityReader codesValidity = ValidityReader.Of(arena, codesNode.Validity);
        ValidityReader valuesValidity = ValidityReader.Of(arena, values.Validity);

        bool tracked = !codesNode.Validity.IsAllValid || !values.Validity.IsAllValid;

        DataBufferSet dataBuffers = DataBufferSet.Collect(arena, in values, false, default);
        try
        {
            ValueWriter writer = ValueWriter.Create(decode, in values, length, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(decode, length, tracked, Id);

            for (int row = 0; row < length; row++)
            {
                if (!codesValidity.IsValid(row))
                {
                    // A null code selects nothing; the row stays zeroed and invalid.
                    continue;
                }

                long code = CompressedValues.ReadInteger(codes, codesPType, row);

                // Class I: untrusted input, so the bound is checked on every row. The metadata's
                // `all_values_referenced` is a hint and never licenses skipping this.
                if ((ulong)code >= (ulong)(uint)valuesLength)
                {
                    LayoutsThrow.Format(
                        $"A {Id} layout's code {code} at row {row} is outside [0, {valuesLength}).");
                }

                int index = (int)code;
                writer.Copy(in values, index, row);
                if (tracked && valuesValidity.IsValid(index))
                {
                    validity.SetValid(row);
                }
            }

            return writer.Complete(
                decode, dtype, validity.Complete(decode, dtype, Id), dataBuffers.Buffers);
        }
        finally
        {
            dataBuffers.Dispose();
        }
    }
}
