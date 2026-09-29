// What the small messages a writer mostly writes cost: `Node` clears the writer and writes a few
// fields and one nested message, as the blob writer's workspace does for each node's metadata;
// `Fields` writes one message of 4,096 varint fields, where the capacity check is most of the work.
// Each against `ProtoWriterBefore`, the writer as it kept its waiting lengths in an array of its own.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Small protobuf messages written, against the original writer.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ProtoMessageBenchmarks
{
    private const int Nodes = 1_000;
    private const int FieldCount = 4_096;

    private readonly byte[] _bytes = new byte[16];

    // Held by the object and reached by reference, kept from one call to the next, as the
    // workspace holds its metadata writer.
    private ProtoWriter _writer;
    private ProtoWriterBefore _writerBefore;

    /// <summary>Hands the writers' arrays back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        _writerBefore.Dispose();
    }

    /// <summary>The nodes' metadata written, each after a clear.</summary>
    [Benchmark]
    public int Node()
    {
        ref ProtoWriter writer = ref _writer;
        int total = 0;
        for (int i = 0; i < Nodes; i++)
        {
            writer.Clear();
            writer.WriteUInt32(1, (uint)i);
            ProtoWriter.MessageScope scope = writer.BeginMessage(2);
            writer.WriteUInt64(1, (ulong)i * 3);
            writer.WriteBool(2, true);
            scope.End();
            writer.WriteBytes(3, _bytes);
            total += writer.Length;
        }

        return total;
    }

    /// <summary>The nodes' metadata written by the original writer.</summary>
    [Benchmark(Baseline = true)]
    public int NodeBefore()
    {
        ref ProtoWriterBefore writer = ref _writerBefore;
        int total = 0;
        for (int i = 0; i < Nodes; i++)
        {
            writer.Clear();
            writer.WriteUInt32(1, (uint)i);
            ProtoWriterBefore.MessageScope scope = writer.BeginMessage(2);
            writer.WriteUInt64(1, (ulong)i * 3);
            writer.WriteBool(2, true);
            scope.End();
            writer.WriteBytes(3, _bytes);
            total += writer.Length;
        }

        return total;
    }

    /// <summary>One message of many varint fields, by the original writer.</summary>
    [Benchmark]
    public int FieldsBefore()
    {
        ProtoWriterBefore writer = default;
        try
        {
            for (int i = 0; i < FieldCount; i++)
            {
                writer.WriteUInt64(1, (ulong)i * 7);
            }

            return writer.Length;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>One message of many varint fields.</summary>
    [Benchmark]
    public int Fields()
    {
        ProtoWriter writer = default;
        try
        {
            for (int i = 0; i < FieldCount; i++)
            {
                writer.WriteUInt64(1, (ulong)i * 7);
            }

            return writer.Length;
        }
        finally
        {
            writer.Dispose();
        }
    }
}
