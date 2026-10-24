// Persisted states: each state written as words of 64-bit columns and read back is the same bits,
// through a Vortex file and its encodings, merges as the engine merges in any order, and holds the
// same words on every architecture the CI runs, which fixed vectors pin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class PersistedStateTests
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

    /// <summary>Integer and decimal sums read back are the same totals, signs and carries included.</summary>
    [Fact]
    public void ExactSumsRoundTrip()
    {
        Int128[] sums = [0, -1, Int128.MaxValue, Int128.MinValue, (Int128)long.MaxValue * 3, -((Int128)ulong.MaxValue << 40)];
        long[] low = new long[sums.Length];
        long[] high = new long[sums.Length];
        PersistedStates.Write(sums, low, high);
        Int128[] back = new Int128[sums.Length];
        PersistedStates.Read(low, high, back);
        Assert.Equal(sums, back);

        WideSum decimals = default;
        decimals.Add(Int128.MaxValue);
        decimals.Add(Int128.MaxValue);
        decimals.Add(-(Int128)12345);
        WideSum[] wide = [decimals, default];
        long[][] limbs = [new long[2], new long[2], new long[2], new long[2], new long[2]];
        PersistedStates.Write(wide, limbs[0], limbs[1], limbs[2], limbs[3], limbs[4]);
        WideSum[] wideBack = new WideSum[2];
        PersistedStates.Read(limbs[0], limbs[1], limbs[2], limbs[3], limbs[4], wideBack);
        Assert.Equal(decimals.ToBigInteger(), wideBack[0].ToBigInteger());
        Assert.True(wideBack[1].IsZero);
        Assert.Throws<ArgumentException>(() => PersistedStates.Write(sums, low.AsSpan(1), high));
    }

    [Fact]
    public void EachStateHasAVersionedKind()
    {
        foreach (StateKind state in Enum.GetValues<StateKind>())
        {
            string kind = PersistedStates.KindOf(state);
            Assert.EndsWith(".v1", kind, StringComparison.Ordinal);
            Assert.Equal(state, PersistedStates.Of(kind));
        }

        Assert.Null(PersistedStates.Of("vorticity.state.sum.indexed.v2"));
        Assert.Equal(["m0", "m1", "m2", "meta", "high"], PersistedStates.Columns(StateKind.FloatSum));
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
        long[] m0 = new long[1], m1 = new long[1], m2 = new long[1], meta = new long[1], high = new long[1];
        PersistedStates.Write([sum], m0, m1, m2, meta, high);
        return [m0[0], m1[0], m2[0], meta[0], high[0]];
    }

    /// <summary>Writes the sums' word columns into a Vortex file, through its encodings, and reads them back.</summary>
    private static async Task<WideIndexedSum[]> RoundTripAsync(WideIndexedSum[] sums)
    {
        long[][] columns = [.. Enumerable.Range(0, 5).Select(_ => new long[sums.Length])];
        PersistedStates.Write(sums, columns[0], columns[1], columns[2], columns[3], columns[4]);
        FloatSumWords[] rows = [.. Enumerable.Range(0, sums.Length).Select(i => new FloatSumWords(columns[0][i], columns[1][i], columns[2][i], columns[3][i], columns[4][i]))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-states-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<FloatSumWords>(path))
            {
                await writer.WriteAsync<FloatSumWords>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            List<FloatSumWords> back = [];
            await foreach (FloatSumWords words in file.Scan<FloatSumWords>().ToRecordsAsync(Ct))
            {
                back.Add(words);
            }

            WideIndexedSum[] read = new WideIndexedSum[back.Count];
            PersistedStates.Read(
                [.. back.Select(w => w.M0)], [.. back.Select(w => w.M1)], [.. back.Select(w => w.M2)],
                [.. back.Select(w => w.Meta)], [.. back.Select(w => w.High)], read);
            return read;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct FloatSumWords(long M0, long M1, long M2, long Meta, long High);
}
