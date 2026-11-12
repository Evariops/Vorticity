// What a result's batch of records paid to bind its record: a schema made of the result's type for each
// batch, which the bindings' cache, keyed by the schema, never found; against a binding kept by the type.
using BenchmarkDotNet.Attributes;


namespace Vorticity.Benchmarks.Audit;

/// <summary>A record bound to a result's type.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditRecordBindingBenchmarks
{
    private VortexType _type = null!;

    /// <summary>A record of four fields.</summary>
    [VortexRecord]
    public partial record struct Hit(string Service, long Count, double Bytes, short Status);

    /// <summary>Takes the record's type.</summary>
    [GlobalSetup]
    public void Setup() => _type = Hit.Schema.Root;

    /// <summary>As a batch bound it before: a schema made of the type, the binding made again.</summary>
    [Benchmark(Baseline = true)]
    public object Fresh() => RecordBinding.For<Hit>(VortexSchema.Create(_type.FieldArray), null);

    /// <summary>As a batch binds it now: by the type, kept.</summary>
    [Benchmark]
    public object Kept() => RecordBinding.ForType<Hit>(_type, null);
}
