using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class WorkFanTests
{
    private const int Writers = 16;

    private const int Pieces = 200;

    private const int Items = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Writers fanning pieces out at once over one budget of three lanes, from threads of the pool, more of them than the
    /// lanes: every item runs once, the writers end, and once they have every lane comes back, those of the helpers the
    /// pool ran late included, which no writer waited for.
    /// </summary>
    [Fact]
    public async Task PiecesFannedOutAtOnceRunEveryItemOnceAndGiveEveryLaneBack()
    {
        LaneBudget budget = new LaneBudget(3);
        int[] runs = new int[Writers * Pieces * Items];
        Task[] writers = [.. Enumerable.Range(0, Writers).Select(writer => Task.Run(
            () =>
            {
                WorkFan fan = WorkFan.Rent(4, budget);
                try
                {
                    for (int piece = 0; piece < Pieces; piece++)
                    {
                        fan.Run(Counting.Instance, Items, runs, ((writer * Pieces) + piece) * Items);
                    }
                }
                finally
                {
                    WorkFan.Return(fan);
                }
            },
            Ct))];
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.All(runs, count => Assert.Equal(1, count));

        // A helper the pool runs late gives its lane back as it leaves, a moment after its writer ended.
        int taken = 0;
        Stopwatch waited = Stopwatch.StartNew();
        while (taken < budget.Lanes && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (budget.TryEnter(out _))
            {
                taken++;
            }
            else
            {
                await Task.Delay(10, Ct);
            }
        }

        Assert.Equal(budget.Lanes, taken);
        Assert.False(budget.TryEnter(out _), "a lane was given back twice");
        for (int i = 0; i < taken; i++)
        {
            budget.Exit(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Counts each item of a piece in the array the piece reads, from the place it starts at.</summary>
    private sealed class Counting : IFanWork
    {
        internal static readonly Counting Instance = new Counting();

        public void Run(WorkFan fan, int item) => Interlocked.Increment(ref ((int[])fan.State!)[fan.Value + item]);
    }
}
