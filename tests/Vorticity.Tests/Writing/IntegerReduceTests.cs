// Integers reduced into one state, as a granule's record takes them, against the fold of every row into its entry's state
// with every row in the same entry: the count, the exact sum past a word and the extremes, signed or as their bits, with
// and without nulls, over values drawn at random, at the ends of the type and all null.
using System;
using System.Linq;
using Vorticity.Aggregating;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class IntegerReduceTests
{
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    public void AReductionIsTheFoldOfEveryRowIntoOneEntry(bool signed, bool nulls, int draw)
    {
        Random random = new Random((signed ? 1 : 0) + (nulls ? 2 : 0) + (4 * draw));
        for (int round = 0; round < 20; round++)
        {
            int count = random.Next(0, 3_000);
            long[] values = new long[count];
            byte[] valid = new byte[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = draw switch
                {
                    0 => random.NextInt64(long.MinValue, long.MaxValue),
                    1 => random.Next(4) switch { 0 => long.MinValue, 1 => long.MaxValue, 2 => -1, _ => 0 },
                    _ => random.NextInt64(),
                };
                valid[i] = draw == 2 ? (byte)0 : (byte)(random.Next(10) == 0 ? 0 : 1);
            }

            // The state a granule opens with, then a batch folded in already.
            long[] expected = Opened(signed);
            long[] actual = Opened(signed);
            long[] first = [.. Enumerable.Range(0, 7).Select(_ => random.NextInt64(-1_000, 1_000))];
            ColumnCountsBuilder.Reduce(ref expected[0], first, default, signed);
            ColumnCountsBuilder.Reduce(ref actual[0], first, default, signed);

            ReadOnlySpan<byte> flags = nulls ? valid : default;
            if (signed)
            {
                ColumnCountsBuilder.FoldSigned(ref expected[0], new int[count], values, flags);
            }
            else
            {
                ColumnCountsBuilder.FoldUnsigned(ref expected[0], new int[count], values, flags);
            }

            ColumnCountsBuilder.Reduce(ref actual[0], values, flags, signed);
            Assert.Equal(expected, actual);
        }
    }

    private static long[] Opened(bool signed)
    {
        MeasureKind kind = signed ? MeasureKind.Signed : MeasureKind.Unsigned;
        long[] state = new long[MeasureLayout.Words(floats: false)];
        ColumnCountsBuilder.OpenState(state, kind);
        return state;
    }
}
