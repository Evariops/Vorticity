// A slab takes one region of the pinned heap and no more, on every platform the tests run on: eight slabs made in a process
// of its own raise the committed heap by eight times 32 MiB, within a mebibyte. Measured in the suite's own process, the
// collector laid two slabs side by side under load and the count was off; a process of its own makes nothing else.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class SlabRegionTests
{
    /// <summary>The variable that tells the process it is the child.</summary>
    private const string Child = "VORTICITY_SLAB_REGION_CHILD";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EightSlabsCommitEightRegions()
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(SlabRegionTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(SlabRegionTests).FullName}.{nameof(EightSlabsMade)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment[Child] = "1";
        start.Environment.Remove(VortexMemory.LimitVariable);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(Ct);
        Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
        await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        string said = await output + await errors;
        Assert.True(child.ExitCode == 0, said);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    [Fact]
    public void EightSlabsMade()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of EightSlabsCommitEightRegions, in a process of its own.");
        const long Region = 32L << 20;
        long before = Committed();
        Slabs.Slab[] slabs = new Slabs.Slab[8];
        for (int i = 0; i < slabs.Length; i++)
        {
            slabs[i] = new Slabs.Slab();
        }

        long grown = Committed() - before;
        GC.KeepAlive(slabs);
        Assert.True(Math.Abs(grown - (slabs.Length * Region)) <= 1L << 20, $"eight slabs committed {grown:N0} bytes, eight regions are {slabs.Length * Region:N0}");
        Console.WriteLine($"ENDED, {grown:N0} bytes");
    }

    /// <summary>What the collector keeps committed after a full collection that compacts.</summary>
    private static long Committed()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo().TotalCommittedBytes;
    }
}
