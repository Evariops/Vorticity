using System;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Publishes a dictionary or run-end column as the encoded node the arena keeps for a consumer
/// that reads the encoded form, from children already decoded: the widening, the bounds and the
/// row validity are established here, once, so every reader of the node can rely on them.
/// </summary>
internal static class EncodedNodes
{
    /// <summary>A dictionary node over a decoded codes child and a decoded values child.</summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype the node produces.</param>
    /// <param name="codesIndex">The codes, a Primitive of integers, one per row.</param>
    /// <param name="valuesIndex">The distinct values, in code order.</param>
    /// <param name="valuesLength">Rows in the values, already checked against the child.</param>
    /// <param name="encodingId">The encoding asking, for the messages about the values.</param>
    /// <param name="codeOwner">What an out-of-range code is attributed to in its message.</param>
    /// <returns>The dictionary node's index.</returns>
    /// <exception cref="VortexFormatException">
    /// A code is outside the dictionary, the values cannot be a dictionary's, or a null lands in a
    /// non-nullable dtype.
    /// </exception>
    internal static int Dictionary(
        ArrayDecodeContext context, DType dtype, int codesIndex, int valuesIndex, int valuesLength,
        string encodingId, string codeOwner)
    {
        CanonicalArena arena = context.Canonical;

        // The same domain the canonical gather accepts, refused with the same message.
        ValueReader values = ValueReader.Of(arena, valuesIndex, encodingId);

        CanonicalNode codesNode = arena.GetNode(codesIndex);
        int length = codesNode.Length;
        PType codesPType = codesNode.PType;
        ReadOnlySpan<byte> codes = codesNode.Values.Span;
        ValidityReader codeBits = ValidityReader.Of(arena, codesNode.Validity);
        ValidityReader valueBits = ValidityReader.Of(arena, values.Validity);
        bool tracked = !codesNode.Validity.IsAllValid || !values.Validity.IsAllValid;

        if (length == 0)
        {
            return arena.AddDictionary(
                dtype, 0, Validity.FromNullability(dtype.Nullability), VortexBuffer.Empty, valuesIndex);
        }

        int bytes = ArrayDecodeContext.CheckedMultiply(length, sizeof(uint), "Dictionary codes");
        if (codeBits.IsAllInvalid)
        {
            // No code to read at all: every row is null and names entry 0.
            VortexBuffer zeros = CompressedValues.Allocate(context, bytes, sizeof(uint), encodingId, out _);
            ValidityWriter none = ValidityWriter.Create(context, length, tracked: true, encodingId);
            return arena.AddDictionary(
                dtype, length, none.Complete(context, dtype, encodingId), zeros, valuesIndex);
        }

        if (!tracked && codesPType is PType.U32 or PType.I32)
        {
            // Already 32 bits wide and nothing to mask: the codes are the child's own bytes, once
            // they are known to be in range. A negative signed code reads as a large one and fails
            // the same check.
            ReadOnlySpan<uint> wide = MemoryMarshal.Cast<byte, uint>(codes)[..length];
            int outside = RowKernels.FirstCodeOutside(wide, (uint)valuesLength);
            if (outside >= 0)
            {
                ThrowCode(codeOwner, codes, codesPType, outside, valuesLength);
            }

            return arena.AddDictionary(
                dtype, length, Validity.FromNullability(dtype.Nullability),
                codesNode.Values.Slice(0, bytes), valuesIndex);
        }

        // Uninitialized: the kernel writes every row's code, a null one as 0.
        VortexBuffer widened = CompressedValues.AllocateUninitialized(
            context, bytes, sizeof(uint), encodingId, out Span<byte> raw);
        ValidityWriter validity = ValidityWriter.Create(context, length, tracked, encodingId);
        int bad = RowKernels.WidenCodes(
            codes, codesPType, valuesLength, MemoryMarshal.Cast<byte, uint>(raw)[..length],
            codeBits.Bits, codeBits.BitOffset, valueBits.Bits, valueBits.BitOffset,
            valueBits.IsAllValid, validity.Bits);
        if (bad >= 0)
        {
            ThrowCode(codeOwner, codes, codesPType, bad, valuesLength);
        }

        return arena.AddDictionary(
            dtype, length, validity.Complete(context, dtype, encodingId), widened, valuesIndex);
    }

    /// <summary>
    /// A run-end node over validated run ends and a decoded values child, rebased to the rows
    /// <c>[offset, offset + length)</c> the array covers.
    /// </summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="dtype">The dtype the node produces.</param>
    /// <param name="length">Rows the array covers.</param>
    /// <param name="ends">The run ends as stored, already checked to strictly increase.</param>
    /// <param name="endsPType">Their physical type, an unsigned integer.</param>
    /// <param name="runCount">Runs stored.</param>
    /// <param name="offset">The array's first row within the runs.</param>
    /// <param name="valuesIndex">One value per stored run.</param>
    /// <param name="encodingId">The encoding asking, for the messages.</param>
    /// <returns>The run-end node's index.</returns>
    /// <remarks>
    /// Only the runs the rows touch are kept -- a run ending at the offset holds none of them, and
    /// the runs past the one reaching <c>offset + length</c> none either -- and the ends are
    /// rebased so the first row is row 0 and clipped so the last end is the row count.
    /// </remarks>
    /// <exception cref="VortexFormatException">The runs do not cover the rows.</exception>
    internal static int RunEnd(
        ArrayDecodeContext context, DType dtype, int length, ReadOnlySpan<byte> ends, PType endsPType,
        int runCount, int offset, int valuesIndex, string encodingId)
    {
        CanonicalArena arena = context.Canonical;
        if (length == 0)
        {
            int none = Layouts.CanonicalSlice.Slice(context, valuesIndex, 0, 0);
            return arena.AddRunEnd(
                dtype, 0, Validity.FromNullability(dtype.Nullability), VortexBuffer.Empty, none);
        }

        ulong start = (ulong)offset;
        ulong stop = start + (ulong)(uint)length;
        int first = 0;
        while (first < runCount && CompressedValues.ReadUnsigned(ends, endsPType, first) <= start)
        {
            first++;
        }

        int last = first;
        while (last < runCount && CompressedValues.ReadUnsigned(ends, endsPType, last) < stop)
        {
            last++;
        }

        if (last == runCount)
        {
            CompressedThrow.Format(
                $"{encodingId} runs do not cover its {length} rows; the last run end must reach " +
                "offset + length.");
        }

        int count = last - first + 1;

        // Uninitialized: the loop writes every kept end.
        VortexBuffer rebased = CompressedValues.AllocateUninitialized(
            context, count * sizeof(uint), sizeof(uint), encodingId, out Span<byte> raw);
        Span<uint> into = MemoryMarshal.Cast<byte, uint>(raw)[..count];
        for (int i = 0; i < into.Length; i++)
        {
            ulong end = Math.Min(CompressedValues.ReadUnsigned(ends, endsPType, first + i), stop);
            into[i] = (uint)(end - start);
        }

        int values = Layouts.CanonicalSlice.Slice(context, valuesIndex, first, count);
        return arena.AddRunEnd(
            dtype, length, RowValidity(context, dtype, values, into, length, encodingId), rebased, values);
    }

    /// <summary>Each run's value validity spread over its rows.</summary>
    private static Validity RowValidity(
        ArrayDecodeContext context, DType dtype, int valuesIndex, ReadOnlySpan<uint> ends, int length,
        string encodingId)
    {
        Validity runs = context.Canonical.GetNode(valuesIndex).Validity;
        if (runs.IsAllValid)
        {
            return Validity.FromNullability(dtype.Nullability);
        }

        ValidityWriter rows = ValidityWriter.Create(context, length, tracked: true, encodingId);
        ValidityReader valid = ValidityReader.Of(context.Canonical, runs);
        int start = 0;
        for (int run = 0; run < ends.Length; run++)
        {
            int end = (int)ends[run];
            if (valid.IsValid(run))
            {
                rows.SetValidRange(start, end - start);
            }

            start = end;
        }

        return rows.Complete(context, dtype, encodingId);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowCode(
        string codeOwner, ReadOnlySpan<byte> codes, PType codesPType, int row, int valuesLength) =>
        CompressedThrow.Format(
            $"{codeOwner} code {CompressedValues.ReadInteger(codes, codesPType, row)} at row {row} is " +
            $"outside [0, {valuesLength}).");
}
