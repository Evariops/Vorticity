using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Vorticity.Advice;
using Xunit;

namespace Vorticity.Tests.Advice;

/// <summary>
/// The choice among measured candidates, held to numbers written here: what a goal counts, the
/// tolerance toward the writer's own choice, the chunk target's margin, the crossing, the options.
/// No test here reads a clock.
/// </summary>
public sealed class AdviceChoiceTests
{
    private const long Rows = 10_000_000;

    private static readonly EncodingGoal OneBytePerNanosecond = new EncodingGoal { StorageBytesPerSecond = 1_000_000_000 };

    [Fact]
    public void ReadTimeCountsTheBytesAtTheStorageThroughputThenTheDecode()
    {
        MeasuredCandidate candidate = Candidate(EncodingHint.Auto, bytes: 4, scan: 3);

        Assert.Equal(7, AdviceChoice.Cost(candidate, OneBytePerNanosecond, Rows), 9);
        Assert.Equal(3 + 0.4, AdviceChoice.Cost(candidate, OneBytePerNanosecond with { StorageBytesPerSecond = 10_000_000_000 }, Rows), 9);
    }

    [Fact]
    public void ALookupAddsItsDecodeAndTheChunkItReadsPerRowOfTheScan()
    {
        MeasuredCandidate candidate = Candidate(EncodingHint.Auto, bytes: 4, scan: 3, lookup: 2, chunkBytes: 1_000_000);
        EncodingGoal goal = OneBytePerNanosecond with { LookupsPerScan = 1_000 };

        // Each lookup: 2 µs of decode and a megabyte at a byte a nanosecond; a thousand of them
        // over the ten million rows a scan reads.
        double lookups = 1_000 * (2_000 + 1_000_000) / (double)Rows;
        Assert.Equal(7 + lookups, AdviceChoice.Cost(candidate, goal, Rows), 9);
    }

    [Fact]
    public void SizeCountsTheBytesAlone()
    {
        MeasuredCandidate candidate = Candidate(EncodingHint.Zstd, bytes: 2.5, scan: 60, lookup: 300, chunkBytes: 1 << 20);

        Assert.Equal(2.5, AdviceChoice.Cost(candidate, EncodingGoal.Smallest with { LookupsPerScan = 1_000 }, Rows));
    }

    [Fact]
    public void TheWritersOwnChoiceStandsWithinTheMargin()
    {
        List<MeasuredCandidate> close = [Candidate(EncodingHint.Auto, bytes: 1.04, scan: 1), Candidate(EncodingHint.Zstd, bytes: 1.00, scan: 9)];
        List<MeasuredCandidate> far = [Candidate(EncodingHint.Auto, bytes: 1.06, scan: 1), Candidate(EncodingHint.Zstd, bytes: 1.00, scan: 9)];

        Assert.Equal(0, AdviceChoice.Recommend(close, 0, EncodingGoal.Smallest, Rows));
        Assert.Equal(1, AdviceChoice.Recommend(far, 0, EncodingGoal.Smallest, Rows));
    }

    [Fact]
    public void AnEqualCostGoesToTheFasterScan()
    {
        List<MeasuredCandidate> candidates =
        [
            Candidate(EncodingHint.Auto, bytes: 3, scan: 1),
            Candidate(EncodingHint.Zstd, bytes: 2, scan: 5),
            Candidate(EncodingHint.Dictionary, bytes: 2, scan: 2),
        ];

        Assert.Equal(2, AdviceChoice.Recommend(candidates, 0, EncodingGoal.Smallest, Rows));
    }

    [Fact]
    public void OnlyTheCandidatesWrittenAtTheTargetAreWeighed()
    {
        List<MeasuredCandidate> candidates =
        [
            Candidate(EncodingHint.Auto, bytes: 3, scan: 1),
            Candidate(EncodingHint.Auto, bytes: 2, scan: 1, target: 16 << 20),
            Candidate(EncodingHint.Zstd, bytes: 1, scan: 9),
        ];

        Assert.Equal(1, AdviceChoice.Recommend(candidates, 16 << 20, EncodingGoal.Smallest, Rows));
        Assert.Equal(-1, AdviceChoice.Recommend(candidates, 4 << 20, EncodingGoal.Smallest, Rows));
    }

    [Fact]
    public void ALargerChunkIsTakenOnlyWhenItSavesTheColumnTheMargin()
    {
        // 9.6 against 10 is 4 %; 9.4 is 6 %. Each column weighs its own targets: the writer gives a
        // column a target alone, so another column's is no concern of this one's.
        List<MeasuredCandidate> little = [Candidate(EncodingHint.Auto, bytes: 10, scan: 1), Candidate(EncodingHint.Auto, bytes: 9.6, scan: 1, target: 16 << 20)];
        List<MeasuredCandidate> enough = [Candidate(EncodingHint.Auto, bytes: 10, scan: 1), Candidate(EncodingHint.Auto, bytes: 9.4, scan: 1, target: 16 << 20)];

        Assert.Equal(0, AdviceChoice.Recommend(little, EncodingGoal.Smallest, Rows));
        Assert.Equal(1, AdviceChoice.Recommend(enough, EncodingGoal.Smallest, Rows));
    }

    [Fact]
    public void TheChunkTargetIsTheLeastCostlyOfThoseTried()
    {
        List<MeasuredCandidate> column =
        [
            Candidate(EncodingHint.Auto, bytes: 10, scan: 1),
            Candidate(EncodingHint.Auto, bytes: 8, scan: 1, target: 4 << 20),
            Candidate(EncodingHint.Dictionary, bytes: 7, scan: 1, target: 16 << 20),
        ];

        Assert.Equal(2, AdviceChoice.Recommend(column, EncodingGoal.Smallest, Rows));
    }

    [Fact]
    public void TwoCandidatesCrossWhereTheyReadAColumnInTheSameTime()
    {
        // The guide's unique UUIDs: zstd at 20.61 B and 33.5 ns a value, FSST at 24.91 B and 12.2 ns.
        MeasuredCandidate zstd = Candidate(EncodingHint.Auto, bytes: 20.61, scan: 33.5);
        MeasuredCandidate fsst = Candidate(EncodingHint.Fsst, bytes: 24.91, scan: 12.2);

        long? crossing = AdviceChoice.Crossing(zstd, fsst);

        Assert.NotNull(crossing);
        Assert.InRange(crossing.Value, 201_000_000, 203_000_000);
        // The crossing is whole bytes a second, so the two costs there agree to its rounding.
        EncodingGoal there = new EncodingGoal { StorageBytesPerSecond = crossing.Value };
        double cost = AdviceChoice.Cost(fsst, there, Rows);
        Assert.Equal(cost, AdviceChoice.Cost(zstd, there, Rows), cost * 1e-6);
    }

    [Fact]
    public void ACandidateBetterOnBothCountsCrossesNowhere()
    {
        MeasuredCandidate better = Candidate(EncodingHint.Dictionary, bytes: 1, scan: 1);
        MeasuredCandidate worse = Candidate(EncodingHint.Canonical, bytes: 8, scan: 2);

        Assert.Null(AdviceChoice.Crossing(worse, better));
        Assert.Null(AdviceChoice.Crossing(better, worse));
    }

    [Fact]
    public void TheRankingPutsTheRecommendedFirstThenTheLeastCostly()
    {
        List<MeasuredCandidate> candidates =
        [
            Candidate(EncodingHint.Canonical, bytes: 8, scan: 1),
            Candidate(EncodingHint.Auto, bytes: 1.03, scan: 2),
            Candidate(EncodingHint.Zstd, bytes: 1.00, scan: 9),
            Candidate(EncodingHint.Dictionary, bytes: 2, scan: 1),
        ];
        int recommended = AdviceChoice.Recommend(candidates, 0, EncodingGoal.Smallest, Rows);

        ImmutableArray<EncodingCandidate> ranked = AdviceChoice.Rank(candidates, recommended, EncodingGoal.Smallest, Rows);

        Assert.Equal(1, recommended);
        Assert.Equal([EncodingHint.Auto, EncodingHint.Zstd, EncodingHint.Dictionary, EncodingHint.Canonical], [.. ranked.Select(c => c.Hint)]);
        Assert.Null(ranked[0].CrossesAtBytesPerSecond);
        Assert.Equal(1.00, ranked[1].Cost);
    }

    [Fact]
    public void TheOptionsCarryTheHintsTheColumnsChunkTargetsAndTheProfile()
    {
        VortexWriteOptions baseline = new VortexWriteOptions
        {
            BlockRows = 4_096,
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("kept", EncodingHint.Zstd),
            ChunkTargetBytes = 2 << 20,
            ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add("kept", 4 << 20),
        };

        VortexWriteOptions scans = Advice(EncodingGoal.Default, 16 << 20).ToWriteOptions(baseline);
        VortexWriteOptions size = Advice(EncodingGoal.Smallest, 0).ToWriteOptions(baseline);

        Assert.Equal(CompressionProfile.Auto, scans.Compression);
        Assert.Equal(16 << 20, scans.ColumnChunkTargetBytes["text"]);
        Assert.False(scans.ColumnChunkTargetBytes.ContainsKey("number"));
        Assert.Equal(4 << 20, scans.ColumnChunkTargetBytes["kept"]);
        Assert.Equal(2 << 20, scans.ChunkTargetBytes);
        Assert.Equal(4_096, scans.BlockRows);
        Assert.Equal(EncodingHint.Fsst, scans.Hints["text"]);
        Assert.Equal(EncodingHint.Zstd, scans.Hints["kept"]);
        Assert.False(scans.Hints.ContainsKey("number"));
        Assert.Equal(CompressionProfile.Smallest, size.Compression);
        Assert.Equal(baseline.ColumnChunkTargetBytes, size.ColumnChunkTargetBytes);
    }

    [Fact]
    public void WindowsSpreadWholeBlocksOverTheData()
    {
        Assert.Equal([new RowRange(0, 100)], EncodingAdvisor.WindowsOf(100, 1_048_576));
        Assert.Equal([new RowRange(0, 5_000)], EncodingAdvisor.WindowsOf(10_000_000, 5_000));
        Assert.Empty(EncodingAdvisor.WindowsOf(0, 1_048_576));

        RowRange[] windows = EncodingAdvisor.WindowsOf(10_000_000, 1_048_576);
        Assert.Equal(8, windows.Length);
        Assert.Equal(0, windows[0].Start);
        Assert.True(windows[^1].End > 10_000_000 - 8_192);
        for (int i = 0; i < windows.Length; i++)
        {
            Assert.Equal(131_072, windows[i].Length);
            Assert.Equal(0, windows[i].Start % 8_192);
            Assert.True(i == 0 || windows[i].Start >= windows[i - 1].End);
        }

        RowRange[] few = EncodingAdvisor.WindowsOf(10_000_000, 20_000);
        Assert.Equal(2, few.Length);
        Assert.All(few, w => Assert.Equal(8_192, w.Length));
    }

    [Fact]
    public void LookupsReadTheMiddleRowOfChunksSpreadOverTheFile()
    {
        Assert.Equal([50L, 150, 250], EncodingAdvisor.LookupRows([100, 100, 100]));

        int[] sixtyFour = new int[64];
        Array.Fill(sixtyFour, 10);
        long[] rows = EncodingAdvisor.LookupRows([.. sixtyFour]);
        Assert.Equal(32, rows.Length);
        Assert.Equal(5, rows[0]);
        Assert.Equal(25, rows[1]);
        Assert.Empty(EncodingAdvisor.LookupRows([]));
    }

    [Fact]
    public void AGoalThatDescribesNoReadIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (EncodingGoal.Default with { StorageBytesPerSecond = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (EncodingGoal.Default with { LookupsPerScan = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (EncodingGoal.Default with { SampleRows = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (EncodingGoal.Default with { Objective = (EncodingObjective)7 }).Validate());
        EncodingGoal.Default.Validate();
    }

    private static MeasuredCandidate Candidate(
        EncodingHint hint, double bytes, double scan, int target = 0, double lookup = 0, double chunkBytes = 0) =>
        new MeasuredCandidate(hint, target, ["Canonical x1"], bytes, scan, lookup, chunkBytes);

    /// <summary>Text advised as FSST at <paramref name="textTarget"/>, a number at the writer's own choice and size.</summary>
    private static EncodingAdvice Advice(EncodingGoal goal, int textTarget)
    {
        ColumnProfile profile = new ColumnProfile(100, 0, 10, 10, 1, false, 8);
        EncodingCandidate fsst = new EncodingCandidate(EncodingHint.Fsst, textTarget, ["Fsst x1"], 9, 2, 1, 11, null);
        EncodingCandidate auto = new EncodingCandidate(EncodingHint.Auto, 0, ["BitPacked x1"], 1, 1, 1, 2, null);
        return new EncodingAdvice(
            goal,
            100,
            100,
            [
                new ColumnEncodingAdvice("text", profile, [fsst], fsst, "text"),
                new ColumnEncodingAdvice("number", profile, [auto], auto, "number"),
            ]);
    }
}
