// Audit RE-02: what the seek key of a composite index costs made from a record, as a caller makes one a lookup.
//
// A key of a service name, a status and a timestamp.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.RowEncoding;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A row key from a record.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditEncodeKeyBenchmarks
{
    private readonly Key _key = new Key("checkout", 503, new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc));

    /// <summary>A composite key.</summary>
    [VortexRecord]
    public partial record struct Key(string Service, int Status, DateTime At);

    /// <summary>The key's bytes.</summary>
    [Benchmark]
    public int Encode() => RowEncoder.EncodeKey(in _key).Length;
}
