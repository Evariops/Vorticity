// A canonical text column against a literal: `=`, `<` and `StartsWith`, one verdict per row, the
// kernels under every string filter a scan evaluates on decoded values.
//
// Two columns: short labels, each held whole in its view, and URLs, held out of line with only
// their first four bytes in the view. The literals are values of the column, so `=` selects a few
// rows, `<` about half, and the prefix an eighth.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>ComparisonKernels.Compare</c> and <c>StringMatch</c> over 65 536 strings.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class StringCompareBenchmarks
{
    private const int Rows = 1 << 16;

    [Params("labels", "urls")]
    public string Column { get; set; } = "labels";

    private CanonicalArena _arena = new CanonicalArena();
    private int _node;
    private FilterLiteral _equal;
    private FilterLiteral _less;
    private FilterLiteral _prefix;
    private byte[] _states = [];

    /// <summary>What one invocation reads: a view per row, and writes a byte per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 17L);

    [GlobalSetup]
    public void Setup()
    {
        const int ViewSize = 16;
        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        _arena = new CanonicalArena();
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        string[] texts = new string[Rows];
        byte[][] encoded = new byte[Rows][];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            texts[i] = Column == "urls"
                ? string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/{words[random.Next(8)]}/{words[random.Next(8)]}/{random.Next(1_000_000):D7}")
                : string.Create(CultureInfo.InvariantCulture, $"{words[random.Next(8)]}-{random.Next(100)}");
            encoded[i] = System.Text.Encoding.UTF8.GetBytes(texts[i]);
            heapBytes += encoded[i].Length > 12 ? encoded[i].Length : 0;
        }

        VortexBuffer views = _arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        int offset = 0;
        for (int i = 0; i < Rows; i++)
        {
            byte[] bytes = encoded[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapSpan[offset..]);
            offset += bytes.Length;
        }

        _node = _arena.AddVarBinView(types.Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
        _equal = FilterLiteral.From(texts[12_345]);
        _less = FilterLiteral.From(texts[777]);
        _prefix = FilterLiteral.From(Column == "urls" ? "https://example.invalid/gamma/" : "gamma-");
        _states = new byte[Rows];
    }

    [Benchmark(Description = "column = literal")]
    public byte Equal()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Equal, _equal, _states);
        return _states[Rows - 1];
    }

    [Benchmark(Description = "column < literal")]
    public byte Less()
    {
        ComparisonKernels.Compare(_arena, _node, ComparisonOp.Less, _less, _states);
        return _states[Rows - 1];
    }

    [Benchmark(Description = "starts with")]
    public byte Prefix()
    {
        ComparisonKernels.StringMatch(_arena, _node, StringMatchOp.StartsWith, _prefix, 0, _states);
        return _states[Rows - 1];
    }
}
