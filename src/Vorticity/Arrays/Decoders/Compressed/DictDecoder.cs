using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.dict</c> by gathering the dictionary values through the codes, from a
/// <c>codes</c> child of the array's own length and a <c>values</c> child of the declared
/// dictionary length. The codes' nullability is not simply the array's: the metadata flag decides
/// it and its absence falls back to the array's dtype rather than meaning non-nullable, because
/// that dtype governs how the codes child's own validity is read. Every code is bounds-checked
/// against the dictionary length before it indexes anything, and dictionary keys are matched by
/// bit pattern, never canonicalized, so that the special float values stay distinct.
/// </summary>
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
    public override bool EvaluatesWithoutFullDecode => true;

    /// <summary>
    /// Compares the dictionary's values and expands the answer through the codes, so the column's
    /// rows are never built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictionary of a few labels over many rows answers an equality with one comparison per
    /// label and one pass of code lookups, rather than one comparison per row over values that had
    /// to be materialized first. The kernel that compares the values is the same one the evaluator
    /// would have run over the decoded column, so the answer is the same answer, three-valued and
    /// row for row.
    /// </para>
    /// <para>
    /// It gives up on a dictionary wider than the rows it covers: there is nothing to win by
    /// comparing more values than there are rows, and the codes still have to be walked.
    /// </para>
    /// <para>
    /// A code is bounds-checked before it indexes the answers, and the check is what stops a
    /// corrupt file reading past them.
    /// </para>
    /// <para>
    /// There is no extreme beside it, and that is a decision rather than an omission: the read path
    /// takes a column's bounds from the zone maps the writer left, so nothing here ever asks a
    /// dictionary for its smallest value. The one caller that computes an extreme works over a
    /// column already decoded, so an extreme on this path would be correct and unreachable.
    /// </para>
    /// <para>
    /// The chunk-level probe is the neighbour, not the duplicate: it prunes whole chunks that
    /// cannot hold the literal before they are read, while this answers the rows of a chunk that
    /// was read. The two compose, and share one decode of the values child.
    /// </para>
    /// </remarks>
    public override bool TryCompare(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        Expressions.ComparisonOp op, Expressions.FilterLiteral literal, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        DictMetadata metadata = DictMetadata.Read(node.Metadata);
        if (!metadata.CodesPType.IsInteger())
        {
            CompressedThrow.Format(
                $"{Id} codes must be an integer physical type, not {metadata.CodesPType.Name()}.");
        }

        int valuesLength = ArrayDecodeContext.CheckedLength(metadata.ValuesLength, $"{Id} values_len");
        if (valuesLength >= length)
        {
            return false;
        }

        Nullability codesNullability = metadata.IsNullableCodes switch
        {
            true => Nullability.Nullable,
            false => Nullability.NonNullable,
            null => dtype.Nullability,
        };

        int valuesIndex = context.DecodeChild(in node, 1, dtype, valuesLength);
        ValueReader values = ValueReader.Of(context.Canonical, valuesIndex, Id);
        if (values.Length != valuesLength)
        {
            CompressedThrow.ChildLength(Id, "values", values.Length, valuesLength);
        }

        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(valuesLength, 1));
        try
        {
            Span<byte> answers = rented.AsSpan(0, valuesLength);
            Compute.ComparisonKernels.Compare(context.Canonical, valuesIndex, op, literal, answers);

            DType codesType = context.Types.Primitive(metadata.CodesPType, codesNullability);
            int codesIndex = context.DecodeChild(in node, 0, codesType, length);
            CanonicalNode codesNode = context.Canonical.GetNode(codesIndex);
            if (codesNode.Kind != CanonicalKind.Primitive)
            {
                CompressedThrow.ChildKind(Id, "codes", codesNode.Kind, "a Primitive");
            }

            if (codesNode.Length != length)
            {
                CompressedThrow.ChildLength(Id, "codes", codesNode.Length, length);
            }

            ReadOnlySpan<byte> codes = codesNode.Values.Span;
            PType codesPType = metadata.CodesPType;
            ValidityReader codesValidity = ValidityReader.Of(context.Canonical, codesNode.Validity);
            for (int row = 0; row < length; row++)
            {
                if (!codesValidity.IsValid(row))
                {
                    destination[row] = Compute.Trilean.Unknown;
                    continue;
                }

                uint code = RowKernels.CodeAt(codes, codesPType, row);
                if (code >= (uint)valuesLength)
                {
                    ThrowCode(codes, codesPType, row, valuesLength);
                }

                destination[row] = answers[(int)code];
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Takes on the codes and leaves the values alone, which is the whole point of a dictionary.
    /// </summary>
    /// <remarks>
    /// The values child is shared by every row, so a take needs all of it whatever it asks for; the
    /// codes are one per row and are where the selection bites. The codes child is very often
    /// <c>fastlanes.for</c> over <c>fastlanes.bitpacked</c>, so this is also what lets the
    /// positional access underneath be reached at all.
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

        // Shared only on the selective path. The selection narrows the codes and never the values,
        // so a take that visits a hundred batches of one chunk would decode this child a hundred
        // times; on the whole-node path the reader above has already retained the node itself, and
        // asking again here would retain the same bytes twice.
        int valuesIndex = selective
            ? context.DecodeChildShared(in node, 1, dtype, valuesLength)
            : context.DecodeChild(in node, 1, dtype, valuesLength);
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

    // The bounds check that leads here is unconditional: the file is untrusted, so a code cannot be
    // assumed to respect the invariant the compressor kept.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowCode(
        ReadOnlySpan<byte> codes, PType codesPType, int row, int valuesLength) =>
        CompressedThrow.Format(
            $"{Id} code {CompressedValues.ReadInteger(codes, codesPType, row)} at row {row} is " +
            $"outside [0, {valuesLength}).");
}
