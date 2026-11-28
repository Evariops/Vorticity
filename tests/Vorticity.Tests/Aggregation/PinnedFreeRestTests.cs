// The free space between the arrays of the pinned heap, under a heap the runtime caps: the process's budget counts it with
// the rest of the process, since the cap counts it, as a part of its own; and a reservation short of room asks no full
// collection for it, which no collection gives back. In a process of its own, whose pinned heap the test makes holes in.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class PinnedFreeRestTests
{
    /// <summary>The variable that tells the process it is the child.</summary>
    private const string Child = "VORTICITY_PINNED_FREE_CHILD";

    private const long Mib = 1L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFreeSpaceOfThePinnedHeapIsRestAndAsksNoCollection()
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(PinnedFreeRestTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(PinnedFreeRestTests).FullName}.{nameof(HolesInThePinnedHeap)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x20000000";
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment.Remove(VortexMemory.LimitVariable);
        start.Environment[Child] = "1";
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(Ct);
        Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
        await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        string said = await output + await errors;
        Assert.True(child.ExitCode == 0, said);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    [Fact]
    public void HolesInThePinnedHeap()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of TheFreeSpaceOfThePinnedHeapIsRestAndAsksNoCollection, under a capped heap.");

        // Forty-eight pinned arrays of 2 MiB, all made, then every other one dropped: some 40 MiB of holes between those kept,
        // which the collector never closes, the pinned heap being never compacted. Dropped as they were made, the collections
        // the pinned heap's budget runs meanwhile had the next arrays fill the holes.
        byte[][] made = new byte[48][];
        for (int i = 0; i < made.Length; i++)
        {
            made[i] = GC.AllocateUninitializedArray<byte>(2 << 20, pinned: true);
        }

        List<byte[]> kept = [];
        for (int i = 0; i < made.Length; i += 2)
        {
            kept.Add(made[i]);
        }

        made = [];

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        QueryMemoryBudget process = QueryMemoryBudget.Process;
        Assert.True(process.CanReserve(0));
        (_, _, long reclaimable, long pinnedFree, _, _, _, _, _) = process.RestParts;
        Assert.True(pinnedFree >= 40 * Mib, $"the rest counts {pinnedFree:N0} bytes free in the pinned heap");
        Assert.True(reclaimable < pinnedFree, $"{reclaimable:N0} bytes a collection gives back, {pinnedFree:N0} free in the pinned heap");
        Assert.True(process.RestBytes >= pinnedFree);

        // The room taken but 1 MiB, then 4 MiB asked: short of room, the budget asks the memory kept back, and no collection,
        // where the holes counted as what a collection gives back had it ask one, a sixteenth of the ceiling being less.
        long room = process.CeilingBytes - Mib;
        Assert.True(process.TryReserve(room));
        long asked = QueryMemoryBudget.CollectionsAsked;
        try
        {
            Assert.False(process.TryReserve(4 * Mib));
            Assert.Equal(asked, QueryMemoryBudget.CollectionsAsked);
        }
        finally
        {
            process.Release(room);
        }

        GC.KeepAlive(kept);
        Console.WriteLine($"ENDED, {pinnedFree:N0} bytes free in the pinned heap, {reclaimable:N0} reclaimable");
    }
}
