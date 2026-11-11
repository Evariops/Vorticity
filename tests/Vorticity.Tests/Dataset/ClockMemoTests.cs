// The memo a dataset handle keeps its pages and synopses in: its budget, one value a key, the clock that keeps what
// questions ask while a stream of values read once goes through, and lookups side by side with keeps.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ClockMemoTests
{
    [Fact]
    public void AKeyHoldsTheFirstValueKeptAndTheBudgetIsKept()
    {
        ClockMemo<int, string> memo = new ClockMemo<int, string>(100);
        Assert.True(memo.Keep(1, "one", 40, out string kept));
        Assert.Equal("one", kept);
        Assert.True(memo.Keep(1, "uno", 40, out kept));
        Assert.Equal("one", kept);
        Assert.Equal((40, 1), (memo.Held, memo.Count));

        // A value past the whole budget is never kept, and takes nothing out.
        Assert.False(memo.Keep(2, "huge", 101, out kept));
        Assert.Equal("huge", kept);
        Assert.False(memo.TryGet(2, out _));
        Assert.Equal(1, memo.Count);

        // Past the budget the oldest goes, unless it was asked for since: then the next oldest goes.
        Assert.True(memo.Keep(2, "two", 40, out _));
        Assert.True(memo.TryGet(1, out _));
        Assert.True(memo.Keep(3, "three", 40, out _));
        Assert.True(memo.TryGet(1, out string? first));
        Assert.Equal("one", first);
        Assert.False(memo.TryGet(2, out _));
        Assert.True(memo.TryGet(3, out _));
        Assert.Equal(80, memo.Held);

        // A value is never let go by its own keeping, whatever was asked for.
        Assert.True(memo.Keep(4, "four", 100, out _));
        Assert.True(memo.TryGet(4, out _));
        Assert.Equal((100, 1), (memo.Held, memo.Count));
    }

    [Fact]
    public void WhatQuestionsAskStaysWhileAStreamReadOnceGoesThrough()
    {
        // 20 hot values of 4 asked at every round fill 80 of a budget of 100; 5 cold values a round are kept and never
        // asked again. The hot ones miss at the first round only.
        ClockMemo<int, int> memo = new ClockMemo<int, int>(100);
        int misses = 0;
        int cold = 1_000;
        for (int round = 0; round < 50; round++)
        {
            for (int hot = 0; hot < 20; hot++)
            {
                if (!memo.TryGet(hot, out _))
                {
                    misses++;
                    memo.Keep(hot, hot, 4, out _);
                }
            }

            for (int c = 0; c < 5; c++)
            {
                memo.Keep(++cold, cold, 4, out _);
            }
        }

        Assert.Equal(20, misses);
        Assert.InRange(memo.Held, 80, 100);
    }

    [Fact]
    public async Task LookupsSideBySideWithKeepsFindWhatWasKeptAndNothingElse()
    {
        ClockMemo<int, int> memo = new ClockMemo<int, int>(64 * 8);
        using CancellationTokenSource stop = new CancellationTokenSource();
        Task keeper = Task.Run(() =>
        {
            for (int i = 0; i < 200_000 && !stop.IsCancellationRequested; i++)
            {
                memo.Keep(i % 4_096, (i % 4_096) * 3, 8, out _);
            }
        }, TestContext.Current.CancellationToken);

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                for (int i = 0; i < 200_000; i++)
                {
                    if (memo.TryGet(i % 4_096, out int value))
                    {
                        Assert.Equal((i % 4_096) * 3, value);
                    }
                }
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await keeper;
        Assert.InRange(memo.Held, 0, memo.Budget);
        Assert.Equal(memo.Held, memo.Count * 8L);
    }

    // Keepers side by side over keys they share, a few hot ones among many, with readers marking every key they find: each
    // keep gives back the one value its key holds, each lookup finds it, and the bytes held are the values' and within the
    // budget once all stop. The readers mark slots while an eviction walks past them; it ends all the same.
    [Fact]
    public async Task KeepersSideBySideKeepEachKeysOneValueWithinTheBudget()
    {
        ClockMemo<int, int> memo = new ClockMemo<int, int>(64 * 8);
        Task[] tasks = new Task[8];
        for (int t = 0; t < tasks.Length; t++)
        {
            int lane = t;
            tasks[t] = Task.Run(
                () =>
                {
                    Random random = new Random(lane);
                    for (int i = 0; i < 100_000; i++)
                    {
                        int key = random.Next(4) == 0 ? random.Next(4_096) : random.Next(96);
                        if (lane % 2 == 0)
                        {
                            Assert.True(memo.Keep(key, key * 3, 8, out int kept));
                            Assert.Equal(key * 3, kept);
                        }
                        else if (memo.TryGet(key, out int value))
                        {
                            Assert.Equal(key * 3, value);
                        }
                    }
                },
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);
        Assert.InRange(memo.Held, 0, memo.Budget);
        Assert.Equal(memo.Held, memo.Count * 8L);
    }
}
