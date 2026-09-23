using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// Reads a <c>vortex.dict</c> layout by gathering its values through its codes. The layout owns no
/// segment and has exactly two children: child 0 is the values and child 1 the codes, the opposite
/// order from the <c>vortex.dict</c> array, whose child 0 is the codes. The parser states that
/// order too, deliberately twice, because two components sharing an id with reversed children is
/// the kind of thing that gets "fixed" into a bug. The gather shares the array decoder's kernels
/// rather than re-deriving the buffer remap and the validity collapse.
/// </summary>
internal sealed class DictLayoutReader : LayoutReader
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

        // The values child is always read whole: a code anywhere in the requested rows may name any
        // dictionary entry. This is the one place a layout materializes something the row range did
        // not ask for, and it is deliberate.
        RegisterChild(in values, RowRange.FromLength(0, values.RowCount), in fields, segments);

        FieldMask all = FieldMask.All;
        RegisterChild(in codes, rows, in all, segments);
    }

    /// <inheritdoc/>
    public override int Execute(in LayoutNode node, RowRange rows, in FieldMask fields, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckRange(in node, rows);

        // The selection applies to the codes and not to the values: this layout's whole point is
        // that the values are shared across the column, so a take reads every one of them and picks
        // a subset of the codes.
        int length = context.HasSelection ? context.SelectionCount : BatchLength(rows);
        LayoutNode valuesLayout = node.GetChild(0);
        LayoutNode codesLayout = node.GetChild(1);

        int valuesLength = NodeLength(in valuesLayout);

        // The values child is decoded once per scan, not once per batch. It is asked for whole
        // every time -- that is what a dict layout is, one shared set of values behind every row --
        // so without retention a column split into several batches would decode all of its values
        // again for each one.
        //
        // The mechanism is the one `FlatLayoutReader` already uses for a chunk larger than a batch,
        // under a key in the layout-node namespace rather than the segment one. Its eviction rule
        // carries over unchanged, and it is the load-bearing part: a batch borrows the retained
        // arena, so an entry may be freed only once no live batch can be looking at it.
        //
        // Nothing is retained when there is no second batch to serve, for the reason the retention
        // cache is lazy in the first place: it costs an arena, and on a file whose rows are one
        // batch that arena is pure loss. The test is on the layout's own range and not on the
        // selection, because a take reads every value whatever it selects.
        bool wholeLayout = rows.Start == 0 && BatchLength(rows) == NodeLength(in node);
        long valuesKey = ScanContext.LayoutKey(valuesLayout.Index);
        int valuesIndex;

        if (wholeLayout)
        {
            ScanContext.SavedSelection once = context.ExchangeSelection(null, 0);
            try
            {
                valuesIndex = ExecuteChild(
                    in valuesLayout, RowRange.FromLength(0, valuesLength), in fields, context);
            }
            finally
            {
                context.RestoreSelection(in once);
            }
        }
        else
        {
            if (!context.TryGetRetained(valuesKey, out CanonicalArena held, out int retainedValues))
            {
                ScanContext.SavedSelection saved = context.ExchangeSelection(null, 0);
                held = context.BeginRetainedDecode();
                retainedValues = -1;
                try
                {
                    retainedValues = ExecuteChild(
                        in valuesLayout, RowRange.FromLength(0, valuesLength), in fields, context);
                }
                finally
                {
                    context.EndRetainedDecode(retainedValues);
                    context.RestoreSelection(in saved);
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
        if (context.KeepEncodings)
        {
            // Both children were read with encoded delivery cleared, so they are plain values; the
            // node keeps them as they are and nothing is gathered.
            int produced = context.Canonical.GetNode(valuesIndex).Length;
            if (produced != valuesLength)
            {
                LayoutsThrow.Format(
                    $"A {Id} layout's values child produced {produced} rows; the layout declares {valuesLength}.");
            }

            return EncodedNodes.Dictionary(
                context.Decode, dtype, codesIndex, valuesIndex, valuesLength, Id, CodeOwner);
        }

        return Gather(context, dtype, valuesIndex, valuesLength, codesIndex, length);
    }

    /// <summary>What an out-of-range code is attributed to, in the message that reports it.</summary>
    private const string CodeOwner = "A " + Id + " layout's";

    /// <summary>The one message for an out-of-range code, from whichever path found it.</summary>
    /// <remarks>
    /// It reads the offending code itself rather than taking one as an argument, so none of the
    /// three call sites carries a `ReadInteger` for an error path; `DictDecoder.ThrowCode` is
    /// written the same way.
    /// </remarks>
    private static void ThrowCode(
        ReadOnlySpan<byte> codes, PType codesPType, int row, int valuesLength) =>
        LayoutsThrow.Format(
            $"A {Id} layout's code {CompressedValues.ReadInteger(codes, codesPType, row)} at " +
            $"row {row} is outside [0, {valuesLength}).");

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
            // The same three paths as `DictDecoder`, and for the same reason: a row loop pays a
            // switch on the code type and a switch on the value kind per row, where the kernels
            // resolve both once and then move fixed-width rows.
            //
            // A Bool value array keeps the row loop, exactly as it does there: bit-packed values
            // have no fixed-width row to move, so a typed kernel has nothing to specialize on.
            bool bitPacked = values.Kind == CanonicalKind.Bool;
            ValueWriter writer = bitPacked
                ? ValueWriter.Create(decode, in values, length, 0, Id)
                : ValueWriter.CreateUninitialized(decode, in values, length, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(decode, length, tracked, Id);

            if (bitPacked)
            {
                for (int row = 0; row < length; row++)
                {
                    if (!codesValidity.IsValid(row))
                    {
                        // A null code selects nothing; the row stays zeroed and invalid.
                        continue;
                    }

                    // `RowKernels.CodeAt` and not `ReadInteger`: the same read without the general
                    // switch, which is what `DictDecoder.GatherBits` uses too.
                    uint code = RowKernels.CodeAt(codes, codesPType, row);

                    // The codes come off the wire, so the bound is checked on every row. The
                    // metadata's `all_values_referenced` is a hint and never licenses skipping it.
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
            else if (!tracked)
            {
                // The dense path: no validity to read, no validity to write, one load and one
                // store per row with the physical types resolved before the loop starts.
                int bad = RowKernels.Gather(
                    codes, codesPType, values.Bytes, values.Width, valuesLength,
                    writer.Bytes, length);
                if (bad >= 0)
                {
                    ThrowCode(codes, codesPType, bad, valuesLength);
                }
            }
            else
            {
                int bad = RowKernels.GatherMasked(
                    codes, codesPType, values.Bytes, values.Width, valuesLength,
                    writer.Bytes, length,
                    codesValidity.Bits, codesValidity.BitOffset,
                    valuesValidity.Bits, valuesValidity.BitOffset, valuesValidity.IsAllValid,
                    validity.Bits);
                if (bad >= 0)
                {
                    ThrowCode(codes, codesPType, bad, valuesLength);
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
