using System;
using System.Buffers.Binary;
using System.Numerics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// One aggregate's column in a batch of states, as a dataset's synopsis keeps them: per row of the batch,
/// how many values of the column it folds, their exact sum in the words <see cref="MeasureLayout"/> lays
/// out, two for an integer and five for a float, and their least and greatest widened to 64 bits, a
/// float's as its double's bits with NaN left out, an unsigned integer's as its bits.
/// </summary>
/// <remarks>
/// A state folds into a group as its values would have, one at a time, and to the same bits: an exact
/// sum merges exactly, an extreme compares. A row of states whose count of values is 0 holds no sum and
/// no extreme, and folds nothing but its rows.
/// </remarks>
internal readonly ref struct StateInput
{
    /// <summary>Per row, the rows of the data it stands for, nulls included: what a count of rows folds.</summary>
    internal ReadOnlySpan<long> Rows { get; init; }

    /// <summary>Per row, the values of the column it folds, its nulls left out.</summary>
    internal ReadOnlySpan<long> Values { get; init; }

    /// <summary>The sum's words: the low and the high word of an integer's, or a float's three bins, its meta word and its high word.</summary>
    internal ReadOnlySpan<long> Sum0 { get; init; }

    internal ReadOnlySpan<long> Sum1 { get; init; }

    internal ReadOnlySpan<long> Sum2 { get; init; }

    internal ReadOnlySpan<long> Sum3 { get; init; }

    internal ReadOnlySpan<long> Sum4 { get; init; }

    /// <summary>Per row, the least value, widened to 64 bits.</summary>
    internal ReadOnlySpan<long> Least { get; init; }

    /// <summary>Per row, the greatest value, widened to 64 bits.</summary>
    internal ReadOnlySpan<long> Greatest { get; init; }

    /// <summary>Whether the column's values are floats, whose sum takes five words and whose extremes are a double's bits.</summary>
    internal bool Floats { get; init; }

    /// <summary>
    /// Per row, the 16-byte view of the sketch of the column's values (<see cref="QuantileSketch.Encode"/>) a table of
    /// sketches holds, in a binary column; empty for a batch of no sketches.
    /// </summary>
    internal ReadOnlySpan<byte> SketchViews { get; init; }

    /// <summary>The data buffers the views of <see cref="SketchViews"/> reference.</summary>
    internal ReadOnlySpan<VortexBuffer> SketchData { get; init; }

    /// <summary>Row <paramref name="row"/>'s sketch, inline in its view or in a data buffer.</summary>
    /// <exception cref="VortexFormatException">The view reaches past its buffer.</exception>
    internal ReadOnlySpan<byte> Sketch(int row)
    {
        ReadOnlySpan<byte> view = SketchViews.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= CanonicalSupport.MaxInlineViewLength)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        if ((uint)buffer >= (uint)SketchData.Length || (ulong)(uint)offset + (ulong)(uint)size > (ulong)SketchData[buffer].Length)
        {
            throw new VortexFormatException($"The sketch of row {row} reaches past its data buffer.");
        }

        return SketchData[buffer].Span.Slice(offset, size);
    }

    /// <summary>Row <paramref name="row"/>'s exact sum of integers, signed.</summary>
    internal Int128 IntegerSum(int row) => new Int128(unchecked((ulong)Sum1[row]), unchecked((ulong)Sum0[row]));

    /// <summary>Row <paramref name="row"/>'s exact sum of integers, unsigned: the same 128 bits.</summary>
    internal UInt128 UnsignedSum(int row) => new UInt128(unchecked((ulong)Sum1[row]), unchecked((ulong)Sum0[row]));

    /// <summary>Row <paramref name="row"/>'s exact float sum.</summary>
    internal WideIndexedSum FloatSum(int row)
    {
        WideIndexedSum sum = default;
        sum.Sum.M0 = Sum0[row];
        sum.Sum.M1 = Sum1[row];
        sum.Sum.M2 = Sum2[row];
        sum.Sum.Meta = Sum3[row];
        sum.High = Sum4[row];
        return sum;
    }

    /// <summary>
    /// Row <paramref name="row"/>'s least or greatest as a value of <typeparamref name="TValue"/>, which the
    /// column's own values were before they were widened; false where the row holds none, its values
    /// all null or NaN.
    /// </summary>
    internal bool TryExtreme<TValue>(int row, bool greatest, out TValue value)
        where TValue : unmanaged, INumberBase<TValue>
    {
        long bits = greatest ? Greatest[row] : Least[row];
        if (Values[row] == 0)
        {
            value = default;
            return false;
        }

        if (Floats)
        {
            double number = BitConverter.Int64BitsToDouble(bits);
            value = TValue.CreateTruncating(number);
            return !double.IsNaN(number);
        }

        value = typeof(TValue) == typeof(ulong) || typeof(TValue) == typeof(uint) || typeof(TValue) == typeof(ushort) || typeof(TValue) == typeof(byte)
            ? TValue.CreateTruncating(unchecked((ulong)bits))
            : TValue.CreateTruncating(bits);
        return true;
    }
}
