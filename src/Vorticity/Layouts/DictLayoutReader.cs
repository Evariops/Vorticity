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

        // The selection applies to the CODES and not to the values: this layout's whole point is
        // that the values are shared across the column, so a take reads every one of them and picks
        // a subset of the codes. That is the `vortex.dict` row of the take table, at the layout
        // level rather than the array level.
        int length = context.HasSelection ? context.Selection.Length : BatchLength(rows);
        LayoutNode valuesLayout = node.GetChild(0);
        LayoutNode codesLayout = node.GetChild(1);

        int valuesLength = NodeLength(in valuesLayout);

        // THE VALUES CHILD IS DECODED ONCE PER SCAN, NOT ONCE PER BATCH. It is asked for WHOLE
        // every time -- that is what a dict layout is, one shared set of values behind every row --
        // so a column split into N batches used to decode all of its values N times to serve them
        // once each. PERF-AUDIT names it P8.
        //
        // The mechanism is the one `FlatLayoutReader` already uses for a chunk larger than a batch,
        // under a key in the layout-node namespace rather than the segment one. Its eviction rule
        // carries over unchanged, and it is the load-bearing part: a batch BORROWS the retained
        // arena, so an entry may be freed only once no live batch can be looking at it.
        // NOTHING IS RETAINED WHEN THERE IS NO SECOND BATCH TO SERVE, for the reason the retention
        // cache is lazy in the first place: it costs an arena, and on a file whose rows are one
        // batch that arena is pure loss. `WriteAllocationTests` priced it at 6.9 kB on
        // types/utf8_nullable_r1025 -- one batch, one use -- the moment this retained
        // unconditionally. The test is on the layout's own range, not on the selection: a take
        // reads every value whatever it selects.
        bool wholeLayout = rows.Start == 0 && BatchLength(rows) == NodeLength(in node);
        long valuesKey = ScanContext.LayoutKey(valuesLayout.Index);
        int valuesIndex;

        if (wholeLayout)
        {
            (int[]? Buffer, int Count) once = context.ExchangeSelection(null, 0);
            try
            {
                valuesIndex = ExecuteChild(
                    in valuesLayout, RowRange.FromLength(0, valuesLength), in fields, context);
            }
            finally
            {
                context.ExchangeSelection(once.Buffer, once.Count);
            }
        }
        else
        {
            if (!context.TryGetRetained(valuesKey, out CanonicalArena held, out int retainedValues))
            {
                (int[]? Buffer, int Count) saved = context.ExchangeSelection(null, 0);
                held = context.BeginRetainedDecode();
                retainedValues = -1;
                try
                {
                    retainedValues = ExecuteChild(
                        in valuesLayout, RowRange.FromLength(0, valuesLength), in fields, context);
                }
                finally
                {
                    context.EndRetainedDecode(valuesKey, retainedValues);
                    context.ExchangeSelection(saved.Buffer, saved.Count);
                }
            }

            // Records only: the batch's node points at the retained arena's buffers rather than
            // owning a copy of them, which is the same borrow `CanonicalSlice.SliceAcross` makes.
            valuesIndex = context.Canonical.ReferenceFrom(held, retainedValues);
        }

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
