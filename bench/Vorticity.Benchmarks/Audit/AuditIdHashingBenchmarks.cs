// Audit VW-06: what a column of identifiers costs a write in hashes, the same value hashed by each index of the column.
// 524,288 rows of a trace identifier (32 hexadecimal characters) and an instant, written to a file with no index, with a
// Bloom filter of the identifier, or with a Bloom filter and hashed postings of it.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A file of identifiers written under three index policies.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditIdHashingBenchmarks
{
    private const int Rows = 524_288;

    private Span[] _rows = [];
    private string _path = string.Empty;

    /// <summary>The identifier's indexes: none, a Bloom filter, or a Bloom filter and hashed postings.</summary>
    [Params("none", "bloom", "bloom+hashed")]
    public string Indexes { get; set; } = "none";

    /// <summary>A span: its trace and its start.</summary>
    [VortexRecord]
    public partial record struct Span(string Trace, long At);

    /// <summary>Draws the rows.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(53);
        byte[] bytes = new byte[16];
        _rows = [.. Enumerable.Range(0, Rows).Select(i =>
        {
            random.NextBytes(bytes);
            return new Span(Convert.ToHexStringLower(bytes), i);
        })];
        _path = Path.Combine(Path.GetTempPath(), $"id-hashing-{Guid.NewGuid():N}.vortex");
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
        IndexPolicy policy = Indexes switch
        {
            "bloom" => IndexPolicy.None.Bloom(nameof(Span.Trace)),
            "bloom+hashed" => IndexPolicy.None.Bloom(nameof(Span.Trace)).HashedPostings(nameof(Span.Trace)),
            _ => IndexPolicy.None,
        };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Span>(_path, new VortexWriteOptions { Indexes = policy }))
        {
            await writer.WriteAsync<Span>(_rows, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new FileInfo(_path).Length;
    }
}
