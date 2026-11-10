// Audit VW-10: what a composite key's index costs a write in allocations, the key's columns cut and encoded together for
// every batch. 524,288 rows of a service (20 values) and an instant, written to a file with sorted runs of the key
// (service, instant).
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A file written with an index of a composite key.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditCompositeKeyWriteBenchmarks
{
    private const int Rows = 524_288;

    private Call[] _rows = [];
    private string _path = string.Empty;

    /// <summary>A call: its service and its instant.</summary>
    [VortexRecord]
    public partial record struct Call(string Service, long At);

    /// <summary>Draws the rows.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(89);
        string[] services = [.. Enumerable.Range(0, 20).Select(i => $"svc-{i:D2}")];
        _rows = [.. Enumerable.Range(0, Rows).Select(i => new Call(services[random.Next(20)], 1_762_000_000_000_000L + i))];
        _path = Path.Combine(Path.GetTempPath(), $"composite-key-{Guid.NewGuid():N}.vortex");
        Write();
    }

    /// <summary>Removes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    /// <summary>The rows written; the file's bytes.</summary>
    [Benchmark]
    public long Write() => WriteAsync().GetAwaiter().GetResult();

    private async Task<long> WriteAsync()
    {
        IndexPolicy policy = IndexPolicy.None.ForKey(
            [nameof(Call.Service), nameof(Call.At)], IndexKind.SortedRuns, new Vorticity.RowEncoding.RowKeyEncoder(Vorticity.RowEncoding.RowSortField.Ascending, Vorticity.RowEncoding.RowSortField.Ascending));
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Call>(_path, new VortexWriteOptions { Indexes = policy }))
        {
            await writer.WriteAsync<Call>(_rows, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new FileInfo(_path).Length;
    }
}
