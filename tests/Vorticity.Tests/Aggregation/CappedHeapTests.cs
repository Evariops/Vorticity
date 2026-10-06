using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// Queries under a heap the runtime caps (PLAN-HIGH-CARDINALITY, H2): the test assembly runs again in a
/// process of its own under <c>DOTNET_GCHeapHardLimit</c>, where eight copies of a group by at once each
/// end exact or fail with a <see cref="VortexMemoryException"/>, never with the runtime's
/// <see cref="OutOfMemoryException"/>.
/// </summary>
[Collection(nameof(CorePressureCollection))]
public sealed partial class CappedHeapTests
{
    /// <summary>The variable that tells the process it is the child, and the degree it runs at.</summary>
    private const string Child = "VORTICITY_CAPPED_HEAP_DEGREE";

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public async Task UnderACappedHeapEightQueriesEndOrFailCleanly(int degree)
    {
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(CappedHeapTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(CappedHeapTests).FullName}.{nameof(EightCopiesUnderTheCap)}");
        start.ArgumentList.Add("-noLogo");
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x20000000";
        start.Environment[Child] = degree.ToString(CultureInfo.InvariantCulture);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(Ct);
        Task<string> errors = child.StandardError.ReadToEndAsync(Ct);
        await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(5), Ct);
        string said = await output + await errors;
        Assert.DoesNotContain(nameof(OutOfMemoryException), said, StringComparison.Ordinal);
        Assert.True(child.ExitCode == 0, said);
        Assert.Contains("ENDED", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EightCopiesUnderTheCap()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Child) is null, "The child of UnderACappedHeapEightQueriesEndOrFailCleanly, under a capped heap.");
        int degree = int.Parse(Environment.GetEnvironmentVariable(Child)!, CultureInfo.InvariantCulture);

        // A million keys over 2.4 million rows: alone at degree 14, the lanes' tables hold some 150 MB;
        // eight at once pass the half gigabyte many times over.
        const int rows = 2_400_000;
        Row[] data = new Row[rows];
        for (int row = 0; row < rows; row++)
        {
            data[row] = new Row((int)(((ulong)row * 0x9E37_79B9_7F4A_7C15UL >> 24) % 1_000_000), row % 100);
        }

        Dictionary<int, long> expected = data.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
        string path = Path.Combine(Path.GetTempPath(), $"capped-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
        {
            await writer.WriteAsync<Row>(data, Ct);
            await writer.CompleteAsync(Ct);
        }

        data = [];
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Task<string>[] copies = new Task<string>[8];
            for (int copy = 0; copy < copies.Length; copy++)
            {
                copies[copy] = Task.Run(
                    async () =>
                    {
                        try
                        {
                            List<KeySum> sums = [];
                            await foreach (KeySum sum in file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Sum(x => x.Value))).As<KeySum>().ToRecordsAsync(Ct))
                            {
                                sums.Add(sum);
                            }

                            Assert.Equal(expected.Count, sums.Count);
                            Assert.All(sums, sum => Assert.Equal(expected[sum.Key], sum.Sum));
                            return "ended";
                        }
                        catch (VortexMemoryException)
                        {
                            return "refused";
                        }
                    },
                    Ct);
            }

            string[] outcomes = await Task.WhenAll(copies);
            Console.WriteLine($"ENDED {outcomes.Count(o => o == "ended")}, refused {outcomes.Count(o => o == "refused")}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct KeySum(int Key, long Sum);
}
