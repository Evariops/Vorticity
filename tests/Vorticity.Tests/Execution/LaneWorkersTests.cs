using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Execution;
using Xunit;

namespace Vorticity.Tests.Execution;

/// <summary>
/// Workers run side by side throw the failure that caused the others, not the cancellation it made them meet, whichever
/// worker it came from: the caller's, or any of the others.
/// </summary>
public sealed class LaneWorkersTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheFaultThatCancelledTheOthersIsTheOneThrown(int faulty)
    {
        LaneBudget lanes = new(8);
        using CancellationTokenSource failed = new();
        IOException thrown = await Assert.ThrowsAsync<IOException>(() => LaneWorkers.RunAsync(lanes, 4, async worker =>
        {
            if (worker == faulty)
            {
                await Task.Yield();
                throw new IOException($"worker {worker}");
            }

            await Task.Delay(Timeout.Infinite, failed.Token);
        }, failed));
        Assert.Equal($"worker {faulty}", thrown.Message);
    }

    [Fact]
    public async Task OnlyCancellationsThrowACancellation()
    {
        LaneBudget lanes = new(8);
        using CancellationTokenSource failed = new();
        await failed.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LaneWorkers.RunAsync(lanes, 3, worker => Task.Delay(Timeout.Infinite, failed.Token), failed));
    }
}
