// What reading the free space of a spill's directory costs, which the core's spill paid before each page it
// appended (64 KiB to a few MiB): a walk of the system's mounts and a query of the drive.
using System;
using System.IO;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A spill directory's free space read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditSpillSpaceBenchmarks
{
    private readonly string _directory = Path.GetTempPath();

    /// <summary>The free space read once.</summary>
    [Benchmark]
    public long? Free() => GroupCore.Free(_directory);
}
