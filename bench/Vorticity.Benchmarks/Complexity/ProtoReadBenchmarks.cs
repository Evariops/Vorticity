// What reading varint fields costs: `Fields` reads back the message `ProtoMessageBenchmarks.Fields`
// writes, 4,096 fields whose values take one to three bytes, so that the reader's one-byte path
// and its longer one both run. Run in a checkout of the original and in the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A message of many varint fields, read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ProtoReadBenchmarks
{
    private const int FieldCount = 4_096;

    private byte[] _message = [];

    /// <summary>Writes the message once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        ProtoWriter writer = default;
        try
        {
            for (int i = 0; i < FieldCount; i++)
            {
                writer.WriteUInt64(1, (ulong)i * 7);
            }

            _message = writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Every field's tag and value read.</summary>
    [Benchmark]
    public ulong Fields()
    {
        ProtoReader reader = new(_message);
        ulong sum = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType _))
        {
            sum += reader.ReadVarint() + (ulong)field;
        }

        return sum;
    }
}
