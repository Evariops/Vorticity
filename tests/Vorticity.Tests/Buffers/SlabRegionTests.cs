// A slab takes a region of the pinned heap alone, under every collector the library runs under: no two slabs made in a row
// lie closer than a region, and slabs made and given back round after round, beside a host's own pinned blocks of a page,
// leave the pinned heap no hole of theirs once collected. Each collector in a process of its own, which its environment
// sets: the workstation one, the server one with its heaps adapted to the load, and the server one on two heaps. A slab
// of 32 MiB less 64 bytes, the size that came first, took a region of 64 MiB that the next slab shared, and the blocks of
// the host laid in the region's end kept it alive once its slabs were gone.
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData("workstation")]
    [InlineData("server")]
    [InlineData("fixed")]
    public async Task SlabsTakeARegionAloneUnderEachCollector(string collector)
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
        start.ArgumentList.Add($"{typeof(SlabRegionTests).FullName}.{nameof(SlabsMadeAndGivenBack)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment[Child] = collector;
        start.Environment.Remove(VortexMemory.LimitVariable);
        start.Environment["DOTNET_gcServer"] = collector == "workstation" ? "0" : "1";
        if (collector == "server")
        {
            start.Environment["DOTNET_GCDynamicAdaptationMode"] = "1";
        }
        else if (collector == "fixed")
        {
            start.Environment["DOTNET_GCDynamicAdaptationMode"] = "0";
            start.Environment["DOTNET_GCHeapCount"] = "2";
        }

        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(Ct);
        Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
        await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        string said = await output + await errors;
        Assert.True(child.ExitCode == 0, said);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    [Fact]
    public void SlabsMadeAndGivenBack()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of SlabsTakeARegionAloneUnderEachCollector, in a process of its own.");
        int region = Regions.LargeBytes;
        int page = Environment.SystemPageSize;

        // Eight slabs in a row: each in a region of its own, so no two closer than a region less where an array starts in it;
        // but for one, which the first region of the pinned heap may hold beside the runtime's own pinned objects, a few
        // kilobytes at its start.
        Slabs.Slab[] slabs = new Slabs.Slab[8];
        for (int i = 0; i < slabs.Length; i++)
        {
            slabs[i] = new Slabs.Slab();
        }

        long[] addresses = [.. slabs.Select(slab => AddressOf(slab.Array)).Order()];
        int close = 0;
        for (int i = 1; i < addresses.Length; i++)
        {
            long apart = addresses[i] - addresses[i - 1];
            close += apart < region - 4096 ? 1 : 0;
        }

        Assert.True(close <= 1, $"{close} slabs closer than a region of {region:N0} to the next: {string.Join(", ", addresses.Select(a => a.ToString("X", System.Globalization.CultureInfo.InvariantCulture)))}");

        GC.KeepAlive(slabs);
        slabs = [];

        // Rounds of slabs made and given back, beside the host's own pinned blocks of a page, one in four kept for good: the
        // pinned heap's free space, once collected, is the host's blocks dropped between those it keeps, never a slab's.
        const int Rounds = 20;
        const int HostBlocks = 16;
        List<byte[]> kept = [];
        for (int round = 0; round < Rounds; round++)
        {
            Slabs.Slab[] made = [new Slabs.Slab(), new Slabs.Slab(), new Slabs.Slab(), new Slabs.Slab()];
            for (int i = 0; i < HostBlocks; i++)
            {
                byte[] block = GC.AllocateUninitializedArray<byte>(page, pinned: true);
                if (i % 4 == 0)
                {
                    kept.Add(block);
                }
            }

            GC.KeepAlive(made);
            if (round % 4 == 3)
            {
                GC.Collect();
            }
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GCGenerationInfo pinned = GC.GetGCMemoryInfo(GCKind.FullBlocking).GenerationInfo[4];
        long dropped = (long)Rounds * (HostBlocks - (HostBlocks / 4)) * (page + 64);
        Assert.True(
            pinned.FragmentationAfterBytes <= dropped + (1 << 20),
            $"the pinned heap holds {pinned.FragmentationAfterBytes:N0} bytes free of {pinned.SizeAfterBytes:N0}, where the host's blocks dropped are {dropped:N0}");
        GC.KeepAlive(kept);
        Console.WriteLine($"ENDED, {pinned.FragmentationAfterBytes:N0} bytes free in a pinned heap of {pinned.SizeAfterBytes:N0}, regions of {region:N0}");
    }

    private static unsafe long AddressOf(byte[] array) => (long)(nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
}
