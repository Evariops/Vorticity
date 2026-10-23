using System;

namespace Vorticity.Aggregating;

/// <summary>
/// How a measure's states lie, one 64-bit word each, in a table of states, in a batch of them and in a run's record: the
/// count of its values, the words of its exact sum, its least value and its greatest. A sum of integers is an
/// <see cref="Int128"/>, its low word then its high one; a sum of floats a <see cref="WideIndexedSum"/>, its three words,
/// their meta word and its high one. A float's extremes are a double's bits, NaN while it has met no value but NaN.
/// </summary>
/// <remarks>
/// The table's row type, the names a batch of states carries, a run's records, the slices' accumulators and the
/// correction of a table's rows all find the states through it, so that they agree on where each word lies.
/// </remarks>
internal static class MeasureLayout
{
    private static readonly string[] IntegerSumWords = ["low", "high"];
    private static readonly string[] FloatSumWords = ["m0", "m1", "m2", "meta", "high"];

    /// <summary>The word of the count of values, from the measure's first word.</summary>
    internal const int Values = 0;

    /// <summary>The first word of the sum, from the measure's first word.</summary>
    internal const int Sum = 1;

    /// <summary>The words of a sum of integers.</summary>
    internal const int IntegerSumLength = 2;

    /// <summary>The words of a sum of floats.</summary>
    internal const int FloatSumLength = 5;

    /// <summary>The word of the least integer, from the measure's first word.</summary>
    internal const int IntegerLeast = Sum + IntegerSumLength;

    /// <summary>The word of the greatest integer, from the measure's first word.</summary>
    internal const int IntegerGreatest = IntegerLeast + 1;

    /// <summary>The words of a measure of integers.</summary>
    internal const int IntegerWords = IntegerGreatest + 1;

    /// <summary>The word of the least float, from the measure's first word.</summary>
    internal const int FloatLeast = Sum + FloatSumLength;

    /// <summary>The word of the greatest float, from the measure's first word.</summary>
    internal const int FloatGreatest = FloatLeast + 1;

    /// <summary>The words of a measure of floats.</summary>
    internal const int FloatWords = FloatGreatest + 1;

    /// <summary>The names of the words of the sum, in their order: <c>low</c> and <c>high</c>, or <c>m0</c> to <c>m2</c>, <c>meta</c> and <c>high</c>.</summary>
    internal static ReadOnlySpan<string> SumWords(bool floats) => floats ? FloatSumWords : IntegerSumWords;

    /// <summary>The words of the sum.</summary>
    internal static int SumLength(bool floats) => floats ? FloatSumLength : IntegerSumLength;

    /// <summary>The word of the least value, from the measure's first word.</summary>
    internal static int Least(bool floats) => floats ? FloatLeast : IntegerLeast;

    /// <summary>The word of the greatest value, from the measure's first word.</summary>
    internal static int Greatest(bool floats) => floats ? FloatGreatest : IntegerGreatest;

    /// <summary>The words of a measure's states.</summary>
    internal static int Words(bool floats) => floats ? FloatWords : IntegerWords;

    /// <summary>
    /// The names of a measure's columns of states, in the order of its words, for the column or path named
    /// <paramref name="column"/> (<see cref="StateRequest.NameOf"/>).
    /// </summary>
    internal static string[] Names(string column, bool floats)
    {
        ReadOnlySpan<string> sum = SumWords(floats);
        string[] names = new string[Words(floats)];
        names[Values] = StateRequest.ValuesOf(column);
        for (int i = 0; i < sum.Length; i++)
        {
            names[Sum + i] = StateRequest.SumWord(column, sum[i]);
        }

        names[Least(floats)] = StateRequest.LeastOf(column);
        names[Greatest(floats)] = StateRequest.GreatestOf(column);
        return names;
    }
}
