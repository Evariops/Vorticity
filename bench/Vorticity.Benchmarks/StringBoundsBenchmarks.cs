// The least and greatest value of a text column's block, kept for its zone map: every text column
// of every chunk passes each row by the bounds, and nearly every row changes neither.
//
// 65 536 values of 4 to 40 bytes from a wide alphabet, so their first bytes are diverse; the
// bounds settle in the first rows and every row after lies between them. And the two shapes of the
// corpus's text: `value-{i}`, every row opening with the same four bytes, and `s{i}`, short rows
// whose first four bytes mostly lie between the bounds'. Each invocation starts a fresh block.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary><c>StringBounds.Accumulate</c> over 65 536 values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class StringBoundsBenchmarks
{
    private const int Rows = 1 << 16;

    /// <summary>The values: diverse, `value-{i}` or `s{i}`.</summary>
    [Params("diverse", "value-i", "s-i")]
    public string Shape { get; set; } = "diverse";

    private readonly CanonicalArena _arena = new CanonicalArena();
    private int _node;

    /// <summary>What one invocation reads: a view per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 16L);

    [GlobalSetup]
    public void Setup()
    {
        if (Shape != "diverse")
        {
            SetupFormatted(Shape == "value-i" ? "value-" : "s");
            return;
        }

        Random random = new Random(20260927);
        int[] sizes = new int[Rows];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            sizes[i] = random.Next(4, 41);
            heapBytes += sizes[i] > 12 ? sizes[i] : 0;
        }

        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        for (int i = 0; i < heapSpan.Length; i++)
        {
            heapSpan[i] = (byte)random.Next(33, 127);
        }

        int at = 0;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)sizes[i]);
            if (sizes[i] <= 12)
            {
                for (int b = 0; b < sizes[i]; b++)
                {
                    view[4 + b] = (byte)random.Next(33, 127);
                }

                continue;
            }

            heapSpan.Slice(at, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
            at += sizes[i];
        }

        _node = _arena.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }

    /// <summary>`{prefix}{i}` for each row, as the corpus writes its text columns.</summary>
    private void SetupFormatted(string prefix)
    {
        byte[][] values = new byte[Rows][];
        int heapBytes = 0;
        for (int i = 0; i < Rows; i++)
        {
            values[i] = System.Text.Encoding.ASCII.GetBytes(prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            heapBytes += values[i].Length > 12 ? values[i].Length : 0;
        }

        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = _arena.Allocate(Math.Max(heapBytes, 1), 1, out Span<byte> heapSpan);
        int at = 0;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)values[i].Length);
            if (values[i].Length <= 12)
            {
                values[i].CopyTo(view[4..]);
                continue;
            }

            values[i].CopyTo(heapSpan[at..]);
            values[i].AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)at);
            at += values[i].Length;
        }

        _node = _arena.AddVarBinView(new DTypeArena().Utf8(Nullability.NonNullable), Rows, Validity.NonNullable, views, [heap]);
    }

    [Benchmark(Description = "string bounds")]
    public int Accumulate()
    {
        StringBounds bounds = new StringBounds(64, utf8: true);
        bounds.Accumulate(_arena, _arena.GetNode(_node), 0, Rows);
        return Rows;
    }
}
