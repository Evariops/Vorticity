// What building a projection's field mask costs, against the fields and the order they come in.
//
// `Fields` fields of one level are included one at a time, in the schema's order, the reverse of
// it, or shuffled, and the mask is built. Run in a checkout of the original and in the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Layouts;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A field mask built from single fields, against their count and their order.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FieldMaskBuildBenchmarks
{
    /// <summary>Fields included.</summary>
    [Params(100, 1_000, 10_000)]
    public int Fields { get; set; }

    /// <summary>The order they are included in.</summary>
    [Params(Arrival.InOrder, Arrival.Reversed, Arrival.Shuffled)]
    public Arrival Order { get; set; }

    /// <summary>How the fields arrive.</summary>
    public enum Arrival
    {
        /// <summary>By increasing index.</summary>
        InOrder,

        /// <summary>By decreasing index.</summary>
        Reversed,

        /// <summary>In a fixed random order.</summary>
        Shuffled,
    }

    private int[] _fields = [];

    /// <summary>Lays the fields out in the order asked for, and checks the mask holds them all.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _fields = new int[Fields];
        for (int i = 0; i < Fields; i++)
        {
            _fields[i] = Order == Arrival.Reversed ? Fields - 1 - i : i;
        }

        if (Order == Arrival.Shuffled)
        {
            new Random(30).Shuffle(_fields);
        }

        if (Build() != Fields)
        {
            throw new InvalidOperationException("The mask lost a field.");
        }
    }

    /// <summary>Every field included, and the mask built.</summary>
    [Benchmark]
    public int Build()
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        foreach (int field in _fields)
        {
            builder.IncludeField(field);
        }

        return builder.Build().NamedFieldCount;
    }
}
