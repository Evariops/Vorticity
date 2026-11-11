// What OnPair's encoding of a chunk of text costs, a dictionary trained on a sample and every row's bytes cut
// into the longest tokens it holds. 512 or 65,536 log lines of 60 to 120 characters, words drawn among 2,000 and numbers.
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A chunk of text encoded by OnPair.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditOnPairEncodeBenchmarks
{
    /// <summary>The chunk's rows: a short column, whose dictionary's fixed costs show, or a long one.</summary>
    [Params(512, 65_536)]
    public int Rows { get; set; } = 65_536;

    private CanonicalArena _arena = null!;
    private int _node;
    private long _plain;

    /// <summary>Draws the chunk.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(79);
        string[] words = [.. Enumerable.Range(0, 2_000).Select(i => new string((char)('a' + (i % 26)), 1) + Convert.ToHexStringLower(BitConverter.GetBytes(i * 2654435761U))[..(3 + (i % 6))])];
        _arena = new CanonicalArena(64);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> view);
        VortexBuffer heap = _arena.Allocate(Rows * 130, 16, out Span<byte> bytes);
        StringBuilder text = new StringBuilder();
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            text.Clear();
            int length = random.Next(60, 120);
            while (text.Length < length)
            {
                text.Append(random.Next(5) == 0 ? random.Next(100_000).ToString() : words[random.Next(words.Length)]).Append(' ');
            }

            byte[] value = Encoding.ASCII.GetBytes(text.ToString());
            Span<byte> v = view.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            value.AsSpan(0, 4).CopyTo(v[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(v[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(v[12..], at);
            value.CopyTo(bytes[at..]);
            at += value.Length;
        }

        _plain = at + (Rows * 4L);
        _node = _arena.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
        if (Encode() == 0)
        {
            throw new InvalidOperationException("OnPair took nothing.");
        }
    }

    /// <summary>The chunk encoded, its rentals handed back; its codes.</summary>
    [Benchmark]
    public long Encode()
    {
        if (OnPairPlan.TryBuild(_arena, _node, _plain) is not { } plan)
        {
            return 0;
        }

        long held = plan.EncodedSize;
        plan.Release();
        return held;
    }
}
