using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
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

        // The pushed predicate, judged once on the values and asked of the codes.
        if (context.PredicateAtNode && context.PushedPredicate is { } predicate)
        {
            int answered = TryAnswer(in node, rows, context, predicate);
            if (answered >= 0)
            {
                return answered;
            }
        }

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
            valuesIndex = context.Canonical.LendFrom(held, retainedValues);
        }

        FieldMask all = FieldMask.All;
        int codesIndex = ExecuteChild(in codesLayout, rows, in all, context);

        // A chunk of one code, which a writer spells as a constant, read as the codes it stands for.
        if (context.Canonical.GetNode(codesIndex).Kind == CanonicalKind.Constant)
        {
            codesIndex = context.Canonical.MaterializeConstant(codesIndex);
        }

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

    /// <summary>Distinguishes the retained verdicts on a dictionary's values from its retained values.</summary>
    private const long VerdictKey = 0x4000_0000_0000_0000;

    /// <summary>The most codes a set of them is pushed as, past which the verdicts are spread over the codes one by one.</summary>
    private const int MostCodeMembers = 4_096;

    /// <summary>
    /// The pushed predicate's answer over the layout's rows: judged once a scan on the dictionary's values, then asked of
    /// the codes as the range or the set of codes it holds for, which their encoding answers as it unpacks them, or spread
    /// over the codes a row at a time; -1 when the values cannot be judged, and the layout reads its values.
    /// </summary>
    /// <remarks>
    /// The verdicts are retained under the values' layout, so every batch and every lane of the scan reads them; the
    /// codes' predicate is worked out from them once a context. A dictionary whose values are sorted holds a range of
    /// values as a range of codes, so a comparison, a range or a prefix asks its codes one range; one of a value that is
    /// unknown, a null, spreads.
    /// </remarks>
    private static int TryAnswer(in LayoutNode node, RowRange rows, ScanContext context, Compute.EncodedPredicate predicate)
    {
        LayoutNode valuesLayout = node.GetChild(0);
        LayoutNode codesLayout = node.GetChild(1);
        if (!TryVerdicts(in valuesLayout, context, predicate, out CanonicalArena held, out int verdicts))
        {
            return -1;
        }

        ReadOnlySpan<byte> states = held.GetNode(verdicts).Values.Span;
        (LayoutTree? tree, int layout, Compute.EncodedPredicate? source, Compute.EncodedPredicate? codes) = context.DictionaryCodes;
        if (!ReferenceEquals(tree, node.Tree) || layout != valuesLayout.Index || !ReferenceEquals(source, predicate))
        {
            codes = CodesPredicate(states);
            context.DictionaryCodes = (node.Tree, valuesLayout.Index, predicate, codes);
        }

        bool answered = context.PredicateAnswered;
        context.PredicateAnswered = false;
        int result;
        if (codes is not null)
        {
            // The codes' chunks asked their predicate in the column's place: an answer, or the codes when one declines.
            (byte[]? Field, Compute.EncodedPredicate? Predicate) saved = context.ExchangePushedPredicate(null, null);
            context.ExchangePushedPredicate(saved.Field, codes);
            bool encoded = context.KeepEncodings;
            context.KeepEncodings = false;
            try
            {
                result = ExecuteRowChild(in codesLayout, rows, FieldMask.All, context);
            }
            finally
            {
                context.KeepEncodings = encoded;
                context.ExchangePushedPredicate(saved.Field, saved.Predicate);
            }

            if (context.PredicateAnswered)
            {
                return result;
            }
        }
        else
        {
            result = ExecuteChild(in codesLayout, rows, FieldMask.All, context);
        }

        context.PredicateAnswered = answered;
        return Spread(context, states, result);
    }

    /// <summary>
    /// The verdict of <paramref name="predicate"/> on each value of the dictionary, a state each, retained for the scan;
    /// false when the predicate has no meaning on these values, a prefix of numbers.
    /// </summary>
    private static bool TryVerdicts(
        in LayoutNode valuesLayout, ScanContext context, Compute.EncodedPredicate predicate, out CanonicalArena held, out int verdicts)
    {
        long key = ScanContext.LayoutKey(valuesLayout.Index) ^ VerdictKey;
        if (context.TryPeekRetained(key, out held, out verdicts))
        {
            return true;
        }

        int length = NodeLength(in valuesLayout);
        ScanContext.SavedSelection saved = context.ExchangeSelection(null, 0);
        int values;
        try
        {
            values = ExecuteChild(in valuesLayout, RowRange.FromLength(0, length), FieldMask.All, context);
        }
        finally
        {
            context.RestoreSelection(in saved);
        }

        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(2 * length, 1));
        try
        {
            Span<byte> states = rented.AsSpan(0, length);
            if (!Compute.ValueVerdicts.TryJudge(context.Canonical, values, predicate, states, rented.AsSpan(length, length)))
            {
                held = default!;
                verdicts = -1;
                return false;
            }

            // Another lane may have published them since the look; otherwise this one does.
            if (!context.TryGetRetained(key, out held, out verdicts))
            {
                held = context.BeginRetainedDecode();
                verdicts = -1;
                try
                {
                    VortexBuffer buffer = held.AllocateUninitialized(length, 1, out Span<byte> into);
                    states.CopyTo(into);
                    verdicts = held.AddPrimitive(context.Types.Primitive(PType.U8, Nullability.NonNullable), length, Validity.NonNullable, PType.U8, buffer);
                }
                finally
                {
                    context.EndRetainedDecode(verdicts);
                }
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The predicate on the codes the verdicts hold for: none, every one, one range of them, every one but one, or a set
    /// of at most <see cref="MostCodeMembers"/>; null when a value is unknown or the codes are too scattered, and the
    /// verdicts are spread one by one.
    /// </summary>
    private static Compute.EncodedPredicate? CodesPredicate(ReadOnlySpan<byte> states)
    {
        int trues = 0;
        int first = -1;
        int last = -1;
        int falseCode = -1;
        for (int code = 0; code < states.Length; code++)
        {
            switch (states[code])
            {
                case Compute.Trilean.True:
                    trues++;
                    first = first < 0 ? code : first;
                    last = code;
                    break;
                case Compute.Trilean.False:
                    falseCode = code;
                    break;
                default:
                    return null;
            }
        }

        if (trues == 0)
        {
            return Compute.EncodedPredicate.Compare(Expressions.ComparisonOp.Less, Expressions.FilterLiteral.From(0L));
        }

        if (trues == states.Length)
        {
            return Compute.EncodedPredicate.Compare(Expressions.ComparisonOp.GreaterOrEqual, Expressions.FilterLiteral.From(0L));
        }

        if (last - first + 1 == trues)
        {
            return first == last
                ? Compute.EncodedPredicate.Compare(Expressions.ComparisonOp.Equal, Expressions.FilterLiteral.From((long)first))
                : Compute.EncodedPredicate.Between(
                    Expressions.FilterLiteral.From((long)first), true, Expressions.FilterLiteral.From((long)last), true);
        }

        if (trues == states.Length - 1)
        {
            return Compute.EncodedPredicate.Compare(Expressions.ComparisonOp.NotEqual, Expressions.FilterLiteral.From((long)falseCode));
        }

        if (trues > MostCodeMembers)
        {
            return null;
        }

        Expressions.FilterLiteral[] members = new Expressions.FilterLiteral[trues];
        int next = 0;
        for (int code = first; code <= last; code++)
        {
            if (states[code] == Compute.Trilean.True)
            {
                members[next++] = Expressions.FilterLiteral.From((long)code);
            }
        }

        return Compute.EncodedPredicate.In(members);
    }

    /// <summary>
    /// The verdicts spread over the codes <paramref name="codes"/>: each row's is its code's, a null row's unknown; the
    /// answer column, its known rows its validity.
    /// </summary>
    private static int Spread(ScanContext context, ReadOnlySpan<byte> states, int codes)
    {
        CanonicalArena arena = context.Canonical;
        codes = arena.Decoded(codes);
        if (arena.GetNode(codes).Kind == CanonicalKind.Constant)
        {
            codes = arena.MaterializeConstant(codes);
        }

        CanonicalNode codesNode = arena.GetNode(codes);
        if (codesNode.Kind != CanonicalKind.Primitive || !codesNode.PType.IsInteger())
        {
            LayoutsThrow.Format($"A {Id} layout's codes child decoded to {codesNode.Kind}, not integer codes.");
        }

        int rows = codesNode.Length;
        ValidityReader validity = ValidityReader.Of(arena, codesNode.Validity);
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(rows, 1));
        ulong[] words = System.Buffers.ArrayPool<ulong>.Shared.Rent(2 * Compute.PredicateBits.WordsOf(Math.Max(rows, 1)));
        try
        {
            Span<byte> spread = rented.AsSpan(0, rows);
            int bad = Compute.CodeAnswers.Expand(
                states, codesNode.Values.Span, codesNode.PType, validity.Bits, validity.BitOffset, validity.IsAllValid, spread);
            if (bad >= 0)
            {
                ThrowCode(codesNode.Values.Span, codesNode.PType, bad, states.Length);
            }

            int count = Compute.PredicateBits.WordsOf(rows);
            Compute.PredicateBits bits = new Compute.PredicateBits(words.AsSpan(0, count), words.AsSpan(count, count), rows);
            bits.FromStates(spread);
            context.PredicateAnswered = true;
            return FlatLayoutReader.Answer(context, ref bits);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            System.Buffers.ArrayPool<ulong>.Shared.Return(words);
        }
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
    internal static int Gather(
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
