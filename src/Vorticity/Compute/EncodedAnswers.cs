using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Compute;

/// <summary>
/// A predicate over a dictionary or run-end column, answered once per distinct value or per run
/// and spread over the rows, so the column is never decoded to answer it.
/// </summary>
/// <remarks>
/// The value kernel is the one the decoded column would have run, so the answer is the same answer,
/// three-valued and row for row: a null value answers unknown for every row that names it, and the
/// node's own validity, which is the row validity, turns a null code unknown too.
/// </remarks>
internal static class EncodedAnswers
{
    /// <summary>Whether <paramref name="nodeIndex"/> is a dictionary or run-end node.</summary>
    internal static bool IsEncoded(CanonicalArena arena, int nodeIndex) =>
        arena.RecordRef(nodeIndex).Kind is CanonicalKind.Dictionary or CanonicalKind.RunEnd;

    /// <summary>
    /// The values child to answer instead of the rows, when that is the cheaper route: always for
    /// runs, and for a dictionary with fewer entries than the batch has rows.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">An encoded node.</param>
    /// <param name="rows">The rows to answer.</param>
    /// <param name="values">The values child.</param>
    /// <param name="count">Its length.</param>
    /// <returns>False when the dictionary is as large as the batch, which then answers its decoded rows.</returns>
    internal static bool TryValues(CanonicalArena arena, int nodeIndex, int rows, out int values, out int count)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        values = node.EncodedValuesIndex;
        count = arena.RecordRef(values).Length;
        return node.Kind == CanonicalKind.RunEnd || count < rows;
    }

    /// <summary>Spreads one answer per value, or per run, over the rows of the node.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The encoded node.</param>
    /// <param name="answers">One <see cref="Trilean"/> state per entry of the values child.</param>
    /// <param name="destination">One state per row.</param>
    internal static void Expand(
        CanonicalArena arena, int nodeIndex, ReadOnlySpan<byte> answers, Span<byte> destination)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllInvalid)
        {
            // Every code may be the 0 a null row carries, into a dictionary that may be empty.
            Trilean.Fill(destination, Trilean.Unknown);
            return;
        }

        if (node.Kind == CanonicalKind.Dictionary)
        {
            // The codes were held to the dictionary when the node was built; a null row's is not
            // read at all.
            int bad = CodeAnswers.Expand(
                answers, node.Codes.Span[..(destination.Length * sizeof(uint))], Types.PType.U32,
                mask.Bits, mask.BitOffset, mask.AllValid, destination);
            if (bad >= 0)
            {
                throw new VortexFormatException(
                    $"A dictionary's code at row {bad} names none of its {answers.Length} values.");
            }

            return;
        }

        ReadOnlySpan<uint> ends = MemoryMarshal.Cast<byte, uint>(node.RunEnds.Span);
        int start = 0;
        for (int run = 0; run < ends.Length; run++)
        {
            int end = (int)ends[run];
            destination[start..end].Fill(answers[run]);
            start = end;
        }

        if (!mask.AllValid)
        {
            ComparisonKernels.MarkUnknown(mask.Bits, mask.BitOffset, destination);
        }
    }
}
