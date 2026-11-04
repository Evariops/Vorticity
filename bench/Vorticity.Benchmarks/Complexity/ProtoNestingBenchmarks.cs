// What closing nested protobuf messages costs, against their depth and the bytes they hold.
//
// `Nested` writes `Depth` messages one inside the other around a bytes field of `Bytes` bytes, as a
// deep dtype or a commit header's inlined pages are written; `WideStruct` serializes the dtype of a
// struct of 4,096 Int64 fields, the shape metadata usually has. Run in a checkout of the original
// and in the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Nested protobuf messages written, against their depth and their bytes.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ProtoNestingBenchmarks
{
    /// <summary>Messages, one inside the other.</summary>
    [Params(4, 64)]
    public int Depth { get; set; }

    /// <summary>Bytes of the innermost field.</summary>
    [Params(1_024, 1_048_576)]
    public int Bytes { get; set; }

    private byte[] _payload = null!;
    private DType _wide;

    /// <summary>Makes the payload and the wide dtype, and checks the nesting reads back.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[Bytes];
        new Random(43).NextBytes(_payload);
        DTypeArena types = new DTypeArena();
        string[] names = new string[4_096];
        DType[] fields = new DType[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = $"column_{i:D5}";
            fields[i] = types.Primitive(PType.I64, Nullability.NonNullable);
        }

        _wide = types.Struct(names, fields, Nullability.NonNullable);
        if (Nested() <= Bytes || WideStruct() == 0)
        {
            throw new InvalidOperationException("The messages were not written.");
        }
    }

    /// <summary>The nested messages written.</summary>
    [Benchmark]
    public int Nested()
    {
        ProtoWriter writer = new ProtoWriter(Bytes + 1_024);
        try
        {
            Nest(ref writer, Depth);
            return writer.Length;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The dtype of a struct of 4,096 fields serialized.</summary>
    [Benchmark]
    public int WideStruct() => DTypeProtobuf.Serialize(_wide).Length;

    private void Nest(ref ProtoWriter writer, int depth)
    {
        if (depth == 0)
        {
            writer.WriteBytes(1, _payload);
            return;
        }

        ProtoWriter.MessageScope scope = writer.BeginMessage(1);
        Nest(ref writer, depth - 1);
        scope.End();
    }
}
