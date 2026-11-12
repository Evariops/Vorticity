// What a text column's bounds cost as the writer feeds them, each block's least and greatest value kept, under
// each instruction set (run with DOTNET_EnableAVX512=0 for AVX2). 131,072 values of 16 to 40 characters drawn among 5,000
// that open with one of 50 prefixes, fed by blocks of 8,192.
using System;
using System.Buffers.Binary;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A text column's block bounds.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditStringBoundsBenchmarks
{
    private const int Rows = 131_072;
    private const int Block = 8_192;

    private CanonicalArena _arena = null!;
    private int _node;

    /// <summary>Draws the column.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(73);
        string[] values = new string[5_000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = $"{(char)('a' + (i % 26))}{i % 50:D2}/{(Convert.ToHexStringLower(BitConverter.GetBytes((ulong)i * 2654435761UL)) + Convert.ToHexStringLower(BitConverter.GetBytes((ulong)i * 40503UL)))[..random.Next(12, 33)]}";
        }

        _arena = new CanonicalArena(64);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> view);
        VortexBuffer heap = _arena.Allocate(Rows * 40, 16, out Span<byte> bytes);
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            byte[] value = Encoding.ASCII.GetBytes(values[random.Next(values.Length)]);
            Span<byte> v = view.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            value.AsSpan(0, 4).CopyTo(v[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(v[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(v[12..], at);
            value.CopyTo(bytes[at..]);
            at += value.Length;
        }

        _node = _arena.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }

    /// <summary>Every block's bounds; the blocks that kept some.</summary>
    [Benchmark]
    public int Bounds()
    {
        StringZones zones = new StringZones(64);
        CanonicalNode node = _arena.GetNode(_node);
        for (int start = 0; start < Rows; start += Block)
        {
            zones.Accumulate(_arena, node, start, Block);
            zones.Close();
        }

        int kept = zones.All(Rows / Block)?.Count ?? 0;
        zones.Release();
        return kept;
    }
}
