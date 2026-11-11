// What a writer pays to borrow a zstd compressor from the process's pool and give it back, under its lock.
using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A compressor borrowed and given back.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditZstdEncodersBenchmarks
{
    /// <summary>One loan and its return, the pool holding a few compressors of two levels.</summary>
    [Benchmark]
    public int Loan()
    {
        var encoder = ZstdEncoders.Rent();
        int level = encoder.Level;
        ZstdEncoders.Return(encoder);
        return level;
    }
}
