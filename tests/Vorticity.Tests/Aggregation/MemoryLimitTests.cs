// The memory the host holds the process to (VortexMemory): a size as a host writes it; a process held to 1 GiB by its
// environment, whose collector's heap is held to three quarters of it and whose library works within what is left, and
// whose compaction of a backlog runs to its end without its resident memory passing the limit; and limits set in turn,
// one the heap already passes refused, one past the collector's own giving it back. A limit holds the whole process,
// so each runs in a process of its own: the test assembly again, its one method.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class MemoryLimitTests
{
    /// <summary>The variable that tells the process it is the child.</summary>
    private const string Child = "VORTICITY_MEMORY_LIMIT_CHILD";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("4294967296", 4L << 30)]
    [InlineData("4g", 4L << 30)]
    [InlineData("4G", 4L << 30)]
    [InlineData("4Gi", 4L << 30)]
    [InlineData("4GiB", 4L << 30)]
    [InlineData(" 4096m ", 4L << 30)]
    [InlineData("4 g", 4L << 30)]
    [InlineData("512M", 512L << 20)]
    [InlineData("1t", 1L << 40)]
    [InlineData("64k", 64L << 10)]
    [InlineData("100b", 100)]
    public void ASizeIsItsBytes(string text, long bytes) => Assert.Equal(bytes, VortexMemory.ParseBytes(text));

    [Theory]
    [InlineData("")]
    [InlineData("g")]
    [InlineData("4x")]
    [InlineData("4gb2")]
    [InlineData("4bb")]
    [InlineData("-1g")]
    [InlineData("0")]
    [InlineData("4.5g")]
    [InlineData("9999999t")]
    public void WhatIsNoSizeIsNone(string text) => Assert.Null(VortexMemory.ParseBytes(text));

    /// <summary>
    /// A process held to 1 GiB by <see cref="VortexMemory.LimitVariable"/>: the collector's heap to 768 MiB, the library to
    /// an eighth less, a backlog of 120 small objects with variant columns consolidated to its end, and the resident memory
    /// never past the limit.
    /// </summary>
    [Fact]
    public async Task AProcessHeldToALimitCompactsItsBacklogWithinIt()
    {
        string said = await RunChildAsync(nameof(ABacklogCompactedUnderTheLimit), "1g");
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    /// <summary>Limits set in turn, in a process that set none: held, refused when the heap holds more, given back past the collector's own.</summary>
    [Fact]
    public async Task ALimitSetHoldsTheHeapAndALargerOneGivesItBack()
    {
        string said = await RunChildAsync(nameof(LimitsSetInTurn), limit: null);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABacklogCompactedUnderTheLimit()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of AProcessHeldToALimitCompactsItsBacklogWithinIt, held to 1 GiB.");
        Assert.Equal(1L << 30, VortexMemory.LimitBytes);
        Assert.Equal(768L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Assert.Equal((768L << 20) - (96L << 20), VortexDiagnostics.GetMemoryReport().LimitBytes);

        long[] peak = [Environment.WorkingSet];
        using Timer sampler = new Timer(_ => Raise(peak, Environment.WorkingSet), null, 0, 10);

        const int Appends = 120;
        const int Rows = 5_000;
        Random random = new Random(17);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, new DatasetOptions
        {
            Session = session,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Doc.Day)),
                SortKey = [LayoutValue.Column(nameof(Doc.Service))],
                CalmAfter = null,
            },
        }, Ct);
        for (int a = 0; a < Appends; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * Rows, Rows).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        Assert.NotEmpty(await dataset.ConsolidateAsync(Ct));
        Assert.Equal(0, dataset.Levels[0].Entries);
        Assert.Equal(Appends * Rows, await dataset.Scan<Doc>().ToRecordsAsync(Ct).CountAsync(Ct));
        Raise(peak, Environment.WorkingSet);
        long most = Volatile.Read(ref peak[0]);
        Assert.True(most <= 1L << 30, $"the process held {most >> 20} MiB under a limit of 1 024 MiB");
        Console.WriteLine($"ENDED, {most >> 20} MiB at most");
    }

    [Fact]
    public void LimitsSetInTurn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of ALimitSetHoldsTheHeapAndALargerOneGivesItBack, which sets its limits itself.");
        long own = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Assert.SkipWhen(own < 4L << 30, $"The collector's own limit, {own >> 20} MiB, is under the limits the test sets.");
        Assert.Null(VortexMemory.LimitBytes);

        VortexMemory.SetLimit(2L << 30);
        Assert.Equal(2L << 30, VortexMemory.LimitBytes);
        Assert.Equal(1536L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Assert.Equal((1536L << 20) - (192L << 20), VortexDiagnostics.GetMemoryReport().LimitBytes);

        // A heap that holds more than a limit's three quarters: the limit refused, the last one holds.
        byte[] held = GC.AllocateArray<byte>(400 << 20);
        Assert.Throws<InvalidOperationException>(() => VortexMemory.SetLimit(256L << 20));
        GC.KeepAlive(held);
        Assert.Equal(2L << 30, VortexMemory.LimitBytes);
        Assert.Equal(1536L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

        // A limit whose heap passes the collector's own: the collector's own again.
        VortexMemory.SetLimit(long.MaxValue / 4);
        Assert.Equal(own, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Console.WriteLine("ENDED");
    }

    private static async Task<string> RunChildAsync(string method, string? limit)
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(MemoryLimitTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(MemoryLimitTests).FullName}.{method}");
        start.ArgumentList.Add("-noLogo");
        start.Environment[Child] = "1";
        start.Environment.Remove(VortexMemory.LimitVariable);
        if (limit is not null)
        {
            start.Environment[VortexMemory.LimitVariable] = limit;
        }

        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(Ct);
        Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
        await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(5), Ct);
        string said = await output + await errors;
        Assert.True(child.ExitCode == 0, said);
        return said;
    }

    /// <summary>The most bytes seen, raised to <paramref name="bytes"/> when it passes it.</summary>
    private static void Raise(long[] peak, long bytes)
    {
        long seen = Volatile.Read(ref peak[0]);
        while (bytes > seen)
        {
            long now = Interlocked.CompareExchange(ref peak[0], bytes, seen);
            if (now == seen)
            {
                return;
            }

            seen = now;
        }
    }

    /// <summary>Attributes of a service's records: a route, a status, a duration, a user, and now and then a key of its own.</summary>
    private static Variant Attributes(Random random)
    {
        (string, Variant)[] fields =
        [
            ("route", Variant.From($"/api/r{random.Next(20)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(500)}")),
            ($"k{random.Next(40)}", Variant.From(random.Next(1_000))),
        ];
        return Variant.Object(fields);
    }

    [VortexRecord]
    public partial record struct Doc(int Day, string Service, long Sequence, Variant Attributes);
}
