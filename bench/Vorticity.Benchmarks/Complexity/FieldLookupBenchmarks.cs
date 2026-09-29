// What resolving a field by name costs a struct of F fields.
//
// `DType.IndexOfField(name)` runs for each field a filter reads, at every batch: it finds the name's
// handle in the arena's intern table, then that handle among the struct's. `Original` is the second
// step as it was, a scalar walk over the handles, after the same first step; `Library` is the
// method itself.
using BenchmarkDotNet.Attributes;

using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One lookup of a field by name, against the struct's width.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class FieldLookupBenchmarks
{
    /// <summary>The struct's field count.</summary>
    [Params(8, 64, 1_000, 10_000)]
    public int Fields { get; set; }

    /// <summary>Where the field looked up sits.</summary>
    [Params(Place.First, Place.Middle, Place.Last)]
    public Place At { get; set; }

    /// <summary>A field's place in the struct.</summary>
    public enum Place
    {
        /// <summary>The first field.</summary>
        First,

        /// <summary>The field halfway.</summary>
        Middle,

        /// <summary>The last field.</summary>
        Last,
    }

    private DTypeArena _types = null!;
    private DType _struct;
    private byte[] _name = [];
    private int[] _handles = [];

    /// <summary>Builds the struct and picks the name.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _types = new DTypeArena();
        DType i64 = _types.Primitive(PType.I64, Nullability.NonNullable);
        string[] names = new string[Fields];
        DType[] fields = new DType[Fields];
        for (int i = 0; i < Fields; i++)
        {
            names[i] = $"column_{i:D5}";
            fields[i] = i64;
        }

        _struct = _types.Struct(names, fields, Nullability.NonNullable);
        _handles = new int[Fields];
        for (int i = 0; i < Fields; i++)
        {
            _types.TryGetName(_struct.GetFieldNameUtf8(i), out _handles[i]);
        }

        int at = At switch
        {
            Place.First => 0,
            Place.Middle => Fields / 2,
            _ => Fields - 1,
        };
        _name = _struct.GetFieldNameUtf8(at).ToArray();
    }

    /// <summary>The name's handle, then a scalar walk over the struct's handles.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        if (!_types.TryGetName(_name, out int handle))
        {
            return -1;
        }

        int[] handles = _handles;
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i] == handle)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The lookup of the library.</summary>
    [Benchmark]
    public int Library() => _struct.IndexOfField(_name);
}
