// Audit ED-01: what a writer pays to ask whether its target edition carries an array id, a question its compressor asks
// of each scheme it weighs.
using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An edition asked for an array id.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditEditionContainsBenchmarks
{
    /// <summary>Whether the latest edition carries the fastlanes bit-packing.</summary>
    [Benchmark]
    public bool Contains() => Vorticity.Writing.ColumnCompressor.Allows(VortexEdition.Core20260803, "fastlanes.bitpacked");
}
