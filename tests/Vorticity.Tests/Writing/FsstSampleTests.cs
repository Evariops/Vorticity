using System;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The FSST trainer's sample is the same whether a draw walks to its row or finds it in the list of
/// non-empty rows: each draw takes the first non-empty row from a random one, wrapping past the last.
/// </summary>
public sealed class FsstSampleTests
{
    private const int SampleTarget = 1 << 14;

    private const int SampleLine = 512;

    /// <summary>Where a column's values sit.</summary>
    public enum Layout
    {
        /// <summary>A value on every row.</summary>
        Dense,

        /// <summary>Values in the first rows only, so that most draws wrap past the last row.</summary>
        Head,

        /// <summary>Values in the last rows only.</summary>
        Tail,

        /// <summary>A value on every 300th row, farther apart than a walk goes.</summary>
        Sparse,

        /// <summary>One row of 20,000 bytes and nothing else.</summary>
        Single,

        /// <summary>Stretches of 1,000 values and 1,000 empty rows in turn.</summary>
        Stretches,

        /// <summary>Fewer bytes than the sample aims for, every non-empty row taken.</summary>
        Short,
    }

    [Theory]
    [InlineData(Layout.Dense)]
    [InlineData(Layout.Head)]
    [InlineData(Layout.Tail)]
    [InlineData(Layout.Sparse)]
    [InlineData(Layout.Single)]
    [InlineData(Layout.Stretches)]
    [InlineData(Layout.Short)]
    public void EveryDrawTakesTheFirstNonEmptyRowFromItsStart(Layout layout)
    {
        const int Rows = 100_000;
        int[] starts = new int[Rows];
        int[] lengths = new int[Rows];
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            int length = layout switch
            {
                Layout.Dense => 1 + (row % 3),
                Layout.Head => row < 20_000 ? 1 : 0,
                Layout.Tail => row >= Rows - 20_000 ? 1 : 0,
                Layout.Sparse => row % 300 == 7 ? 70 : 0,
                Layout.Single => row == 61_234 ? 20_000 : 0,
                Layout.Stretches => (row / 1_000) % 2 == 0 ? 2 : 0,
                _ => row % 10 == 0 ? 1 : 0,
            };
            starts[row] = at;
            lengths[row] = length;
            at += length;
        }

        FsstSymbols.Line[] expected = new FsstSymbols.Line[SampleTarget + 1];
        int expectedCount = Walked(starts, lengths, expected, out bool expectedSampled);
        FsstSymbols.Line[] drawn = new FsstSymbols.Line[SampleTarget + 1];
        FsstSymbols.MakeSample(starts, lengths, drawn, out int count, out bool sampled);

        Assert.Equal(expectedSampled, sampled);
        Assert.Equal(expectedCount, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((expected[i].Start, expected[i].Length), (drawn[i].Start, drawn[i].Length));
        }
    }

    /// <summary>The sample as a walk from every drawn row to the next non-empty one takes it.</summary>
    private static int Walked(ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, Span<FsstSymbols.Line> into, out bool sampled)
    {
        long total = 0;
        foreach (int length in lengths)
        {
            total += length;
        }

        int drawn = 0;
        sampled = total >= SampleTarget;
        if (!sampled)
        {
            for (int row = 0; row < lengths.Length; row++)
            {
                if (lengths[row] > 0)
                {
                    into[drawn++] = new FsstSymbols.Line(starts[row], lengths[row]);
                }
            }

            return drawn;
        }

        ulong random = Hash(4637947);
        long bytes = 0;
        while (bytes < SampleTarget)
        {
            random = Hash(random);
            int row = (int)(random % (ulong)lengths.Length);
            while (lengths[row] == 0)
            {
                row = row + 1 == lengths.Length ? 0 : row + 1;
            }

            int chunks = 1 + ((lengths[row] - 1) / SampleLine);
            random = Hash(random);
            int chunk = SampleLine * (int)(random % (ulong)chunks);
            int length = Math.Min(SampleLine, lengths[row] - chunk);
            into[drawn++] = new FsstSymbols.Line(starts[row] + chunk, length);
            bytes += length;
        }

        return drawn;
    }

    private static ulong Hash(ulong value) => (value * 2971215073UL) ^ (value >> 15);
}
