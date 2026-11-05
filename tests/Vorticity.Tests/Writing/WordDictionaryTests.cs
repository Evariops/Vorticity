// The dictionary walk over a column of four- or eight-byte values compares the values themselves, kept beside its
// chains, where the comparer's walk read the column again at each code's first row. Both must hand out the same codes,
// in the order the values first appear, the nulls under one code, and abandon at the same distinct value, or a file's
// bytes would move.
using System;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class WordDictionaryTests
{
    public static TheoryData<int, int, bool, int> Shapes()
    {
        TheoryData<int, int, bool, int> shapes = new TheoryData<int, int, bool, int>();
        foreach (int width in (int[])[4, 8])
        {
            foreach (int distinct in (int[])[1, 7, 300, 5_000, 40_000])
            {
                foreach (bool nulls in (bool[])[false, true])
                {
                    // The budget as a share of the plain column, in percent: the dictionary kept, abandoned late, early.
                    foreach (int share in (int[])[100, 40, 3])
                    {
                        shapes.Add(width, distinct, nulls, share);
                    }
                }
            }
        }

        return shapes;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheWordWalkHandsOutTheComparersCodes(int width, int distinct, bool nulls, int share)
    {
        const int Rows = 40_000;
        using ColumnFixture fixture = new ColumnFixture();
        Random random = new Random((width * 1_000_003) + (distinct * 31) + share);
        long[] entries = new long[distinct];
        for (int entry = 0; entry < distinct; entry++)
        {
            entries[entry] = width == 4 ? random.Next(int.MinValue, int.MaxValue) : random.NextInt64(long.MinValue, long.MaxValue);
        }

        bool[] valid = new bool[Rows];
        for (int row = 0; row < Rows; row++)
        {
            valid[row] = !nulls || random.Next(10) != 0;
        }

        Validity validity = nulls ? fixture.BitmapValidity(valid) : Validity.NonNullable;
        Nullability nullability = nulls ? Nullability.Nullable : Nullability.NonNullable;
        int node;
        if (width == 4)
        {
            int[] values = new int[Rows];
            for (int row = 0; row < Rows; row++)
            {
                values[row] = (int)entries[random.Next(distinct)];
            }

            node = fixture.Int32Node(values, validity, nullability);
        }
        else
        {
            long[] values = new long[Rows];
            for (int row = 0; row < Rows; row++)
            {
                values[row] = entries[random.Next(distinct)];
            }

            node = fixture.Int64Node(values, validity, nullability);
        }

        long budget = (long)Rows * width * share / 100;
        ColumnPlan words = ColumnCompressor.WalkDictionary(fixture.Arena, node, budget, words: true);
        ColumnPlan comparer = ColumnCompressor.WalkDictionary(fixture.Arena, node, budget, words: false);
        try
        {
            Assert.Equal(comparer.Scheme, words.Scheme);
            if (comparer.Scheme == ColumnScheme.Dict)
            {
                Assert.Equal(comparer.Entries, words.Entries);
                Assert.Equal(comparer.Rows, words.Rows);
                Assert.True(comparer.EntryRows.SequenceEqual(words.EntryRows), "the entries' first rows differ");
                Assert.True(
                    comparer.Codes.AsSpan(0, comparer.Rows).SequenceEqual(words.Codes.AsSpan(0, words.Rows)),
                    "the rows' codes differ");
            }
        }
        finally
        {
            words.ReleaseCodes();
            comparer.ReleaseCodes();
        }
    }
}
