// A measure's states as the words a table of states and a run's records hold them in: written and read back through
// StateWords, they are the same bits, through a Vortex file and its encodings too; float sums merge as the engine merges, in
// any order; and their words are the same on every architecture the CI runs, which fixed vectors pin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class StateWordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The words of float sums over fixed values, pinned as arm64 computed them, so that the x64 runs
    /// of the CI hold them: a sum of tenths; one across magnitudes from 1e-300 to 1e300, whose top bin
    /// leaves the small terms below the three it keeps and whose large terms cancel; and one past an
    /// epoch, whose bins have settled into high parts.
    /// </summary>
    [Fact]
    public void FloatSumsHoldTheirWordsOnEveryArchitecture()
    {
        long[][] expected =
        [
            [721, 8191475712, 782, 11258999068427240, 0],
            [0, 0, 0, 21392098230009863, 0],
            [-18014353204183040, 0, 0, 10977524091727930, 18014398509481986],
        ];
        Assert.Equal(expected, Vectors().Select(Words).ToArray());
        Assert.Equal(50_050, Vectors().First().Value, 6);
    }

    /// <summary>
    /// Float sums of groups written to a Vortex file as their five word columns and read back are the
    /// same words; merged in any order and any cut, they are the bits of the sum of every value.
    /// </summary>
    [Fact]
    public async Task FloatSumsRoundTripAndMergeInAnyOrder()
    {
        Random random = new Random(7);
        double[] values = [.. Enumerable.Range(0, 20_000).Select(_ => (random.NextDouble() - 0.3) * Math.Pow(10, random.Next(-8, 9)))];
        WideIndexedSum whole = default;
        whole.AddSpan(values);

        // A hundred groups of a hundred and fifty or so values each.
        WideIndexedSum[] groups = new WideIndexedSum[100];
        for (int i = 0; i < values.Length; i++)
        {
            groups[(i * 7919) % groups.Length].Add(values[i]);
        }

        WideIndexedSum[] read = await RoundTripAsync(groups);
        Assert.Equal(groups.Select(Words), read.Select(Words));

        for (int trial = 0; trial < 5; trial++)
        {
            WideIndexedSum merged = default;
            foreach (WideIndexedSum group in read.OrderBy(_ => random.Next()))
            {
                merged.Merge(in group);
            }

            Assert.Equal(Words(whole), Words(merged));
            Assert.Equal(BitConverter.DoubleToInt64Bits(whole.Value), BitConverter.DoubleToInt64Bits(merged.Value));
        }
    }

    /// <summary>Integer sums written as their two words and read back are the same totals, signs and carries included.</summary>
    [Fact]
    public void IntegerSumsRoundTrip()
    {
        Int128[] sums = [0, -1, Int128.MaxValue, Int128.MinValue, (Int128)long.MaxValue * 3, -((Int128)ulong.MaxValue << 40)];
        long[] words = new long[2 * sums.Length];
        for (int i = 0; i < sums.Length; i++)
        {
            StateWords.Write(sums[i], words, 2 * i);
        }

        Assert.Equal(sums, Enumerable.Range(0, sums.Length).Select(i => StateWords.IntegerOf(words, 2 * i)));
    }

    /// <summary>
    /// A measure's states take five words for a sum of integers and eight for one of floats, named in their order; a group
    /// of a count and those two measures takes fourteen, merged word for word as their rows together.
    /// </summary>
    [Fact]
    public void AMeasuresWordsLieWhereItsLayoutSays()
    {
        Assert.Equal(
            ["d.values", "d.sum.m0", "d.sum.m1", "d.sum.m2", "d.sum.meta", "d.sum.high", "d.min", "d.max"],
            MeasureLayout.Names("d", floats: true));
        Assert.Equal(["n.values", "n.sum.low", "n.sum.high", "n.min", "n.max"], MeasureLayout.Names("n", floats: false));
        MeasureKind[] kinds = [MeasureKind.Signed, MeasureKind.Float];
        Assert.Equal(14, StateWords.Count(kinds));

        // Two groups of states, two rows of -5 and 7 and of 2.5 and 4, and one of 9 and -1.25; and a group of no row,
        // whose float extremes are NaN, which a merge leaves out.
        long[] into = new long[14];
        long[] from = new long[14];
        long[] none = new long[14];
        StateWords.Clear(into, kinds);
        StateWords.Clear(from, kinds);
        StateWords.Clear(none, kinds);
        Fill(into, rows: 2, (Sum: 2, Least: -5, Greatest: 7), [2.5, 4]);
        Fill(from, rows: 1, (Sum: 9, Least: 9, Greatest: 9), [-1.25]);
        StateWords.Merge(into, from, kinds);
        StateWords.Merge(into, none, kinds);

        Assert.Equal(3, into[0]);
        Assert.Equal(3, into[1 + MeasureLayout.Values]);
        Assert.Equal(11, StateWords.IntegerOf(into, 1 + MeasureLayout.Sum));
        Assert.Equal(-5, into[1 + MeasureLayout.IntegerLeast]);
        Assert.Equal(9, into[1 + MeasureLayout.IntegerGreatest]);
        int floats = 1 + MeasureLayout.IntegerWords;
        Assert.Equal(3, into[floats + MeasureLayout.Values]);
        Assert.Equal(5.25, StateWords.SumOf(into, floats + MeasureLayout.Sum).Value);
        Assert.Equal(-1.25, BitConverter.Int64BitsToDouble(into[floats + MeasureLayout.FloatLeast]));
        Assert.Equal(4, BitConverter.Int64BitsToDouble(into[floats + MeasureLayout.FloatGreatest]));
    }

    /// <summary>Lays out <paramref name="rows"/> rows of one integer measure, its states given, and one float measure's values.</summary>
    private static void Fill(long[] words, int rows, (long Sum, long Least, long Greatest) integers, double[] floats)
    {
        words[0] = rows;
        words[1 + MeasureLayout.Values] = rows;
        StateWords.Write(integers.Sum, words, 1 + MeasureLayout.Sum);
        words[1 + MeasureLayout.IntegerLeast] = integers.Least;
        words[1 + MeasureLayout.IntegerGreatest] = integers.Greatest;

        int at = 1 + MeasureLayout.IntegerWords;
        WideIndexedSum sum = default;
        foreach (double value in floats)
        {
            sum.Add(value);
        }

        words[at + MeasureLayout.Values] = floats.Length;
        StateWords.Write(in sum, words, at + MeasureLayout.Sum);
        words[at + MeasureLayout.FloatLeast] = BitConverter.DoubleToInt64Bits(floats.Min());
        words[at + MeasureLayout.FloatGreatest] = BitConverter.DoubleToInt64Bits(floats.Max());
    }

    private static IEnumerable<WideIndexedSum> Vectors()
    {
        WideIndexedSum tenths = default;
        for (int i = 1; i <= 1_000; i++)
        {
            tenths.Add(i * 0.1);
        }

        yield return tenths;

        WideIndexedSum magnitudes = default;
        foreach (double value in (double[])[1e-300, 1e300, 3.5, -1e300, 2.25e-10, -7.0, 1e15])
        {
            magnitudes.Add(value);
        }

        yield return magnitudes;

        WideIndexedSum settled = default;
        settled.AddWeighted(1.75, WideIndexedSum.Epoch + 12_345);
        settled.Add(-0.5);
        yield return settled;
    }

    private static long[] Words(WideIndexedSum sum)
    {
        long[] words = new long[MeasureLayout.SumLength(floats: true)];
        StateWords.Write(in sum, words, 0);
        return words;
    }

    /// <summary>Writes the sums' word columns into a Vortex file, through its encodings, and reads them back.</summary>
    private static async Task<WideIndexedSum[]> RoundTripAsync(WideIndexedSum[] sums)
    {
        FloatSumWords[] rows = [.. sums.Select(Words).Select(w => new FloatSumWords(w[0], w[1], w[2], w[3], w[4]))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-states-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<FloatSumWords>(path))
            {
                await writer.WriteAsync<FloatSumWords>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<WideIndexedSum> back = [];
            await foreach (FloatSumWords words in file.Scan<FloatSumWords>().ToRecordsAsync(Ct))
            {
                back.Add(StateWords.SumOf([words.M0, words.M1, words.M2, words.Meta, words.High], 0));
            }

            return [.. back];
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct FloatSumWords(long M0, long M1, long M2, long Meta, long High);
}
