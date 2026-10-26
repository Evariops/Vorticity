using System;
using System.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// Where a slot writes its groups' states as rows of states hold them (<see cref="AggregateSlot.ExportStates"/>), a row a
/// group: the rows a count folded, a measure's words where <see cref="MeasureLayout"/> lays them out, and a sketch's bytes,
/// each what <see cref="AggregateSlot.StepStates"/> folds back to the same states.
/// </summary>
/// <remarks>
/// The words start cleared, as <see cref="StateWords.Clear"/> leaves those of a group of no row: a slot writes the words it
/// keeps, the others' stay. A sum sets the count of values; an extreme raises it to one at least, which the extremes alone
/// read, so that the slots of one measure write in any order.
/// </remarks>
internal readonly ref struct StateExport
{
    /// <summary>Each group's rows, for a count of rows.</summary>
    internal Span<long> Rows { get; init; }

    /// <summary>Each group's words of the measure, <see cref="Stride"/> a group.</summary>
    internal Span<long> Words { get; init; }

    /// <summary>Whether the measure's values are floats, whose words are a float sum's and a double's extremes.</summary>
    internal bool Floats { get; init; }

    /// <summary>Each group's sketch, as a table of sketches holds it.</summary>
    internal Span<byte[]?> Sketches { get; init; }

    /// <summary>The words of a group's measure.</summary>
    internal int Stride => MeasureLayout.Words(Floats);

    /// <summary>The words of group <paramref name="row"/> of those written.</summary>
    internal Span<long> Group(int row) => Words.Slice(row * Stride, Stride);

    /// <summary>The groups <c>[start, start + count)</c> of those written, for a slot whose parts hold them.</summary>
    internal StateExport Slice(int start, int count) => new StateExport
    {
        Rows = Rows.IsEmpty ? default : Rows.Slice(start, count),
        Words = Words.IsEmpty ? default : Words.Slice(start * Stride, count * Stride),
        Floats = Floats,
        Sketches = Sketches.IsEmpty ? default : Sketches.Slice(start, count),
    };
}

/// <summary>How an op writes a group's state into its measure's words, which <see cref="StateInput"/> reads back.</summary>
internal static class StateExports
{
    /// <summary>A sum of integers and its count of values; true, the op's state having that form.</summary>
    internal static bool Sum(Span<long> words, Int128 sum, long count)
    {
        words[MeasureLayout.Values] = count;
        return Total(words, sum);
    }

    /// <summary>A sum of unsigned integers and its count of values, the same 128 bits.</summary>
    internal static bool Sum(Span<long> words, UInt128 sum, long count)
    {
        words[MeasureLayout.Values] = count;
        return Total(words, sum);
    }

    /// <summary>A sum of integers alone, which no mean reads: its words, the count of values left to the extremes.</summary>
    internal static bool Total(Span<long> words, Int128 sum)
    {
        StateWords.Write(sum, words, MeasureLayout.Sum);
        return true;
    }

    /// <summary>A sum of unsigned integers alone, the same 128 bits.</summary>
    internal static bool Total(Span<long> words, UInt128 sum)
    {
        (words[MeasureLayout.Sum], words[MeasureLayout.Sum + 1]) = (unchecked((long)(ulong)sum), unchecked((long)(ulong)(sum >> 64)));
        return true;
    }

    /// <summary>An exact float sum, its words and its count of values.</summary>
    internal static bool Sum(Span<long> words, in WideIndexedSum sum)
    {
        words[MeasureLayout.Values] = sum.Count;
        StateWords.Write(in sum, words, MeasureLayout.Sum);
        return true;
    }

    /// <summary>
    /// A least or greatest value: a double's bits for floats, an integer's own as <see cref="StateInput.TryExtreme"/> reads
    /// them; the count of values one at least, which tells the extreme met.
    /// </summary>
    internal static bool Extreme<TValue>(Span<long> words, bool floats, bool greatest, TValue value)
        where TValue : INumberBase<TValue>
    {
        if (words[MeasureLayout.Values] == 0)
        {
            words[MeasureLayout.Values] = 1;
        }

        words[greatest ? MeasureLayout.Greatest(floats) : MeasureLayout.Least(floats)] = floats
            ? BitConverter.DoubleToInt64Bits(double.CreateTruncating(value))
            : typeof(TValue) == typeof(ulong) || typeof(TValue) == typeof(uint) || typeof(TValue) == typeof(ushort) || typeof(TValue) == typeof(byte)
                ? unchecked((long)ulong.CreateTruncating(value))
                : long.CreateTruncating(value);
        return true;
    }
}
