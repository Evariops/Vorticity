// What the run search costs over a text column with nulls, the shape of a variant's shredded leaf: a block's worth of rows
// in runs of one value, nulls between, the values short enough to sit in their views or longer, as the compressor weighs a
// chunk of it for run-end. Over metric records the search took 9 % of a consolidation's processor, its comparisons each
// slicing both rows' bytes out of their buffers (2026-11-18).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The compressor's run search over one nullable text column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class RunScanBenchmarks
{
    /// <summary>The values' lengths: within a view (up to twelve bytes), past it, or both.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "mixed";

    /// <summary>The shapes the current profile measures.</summary>
    public static IEnumerable<string> Shapes => BenchmarkConfig.Full ? ["short", "long", "mixed"] : ["mixed"];

    /// <summary>
    /// Whether every row holds a value, as a merge's gathered chunk of a series' attributes does, its views copied unpadded and
    /// each long value's bytes its own: the search then reads views, not the validity.
    /// </summary>
    [Params(false, true)]
    public bool AllValid { get; set; }

    /// <summary>Whether a row that repeats the value before views the same bytes, as a shredded leaf's rows do, or a copy of its own.</summary>
    [Params(false, true)]
    public bool Shared { get; set; }

    private const int Rows = 8_192;

    private CanonicalArena _arena = null!;
    private int _column;
    private int[] _starts = [];
    private int[] _ends = [];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(7);
        bool[] present = new bool[Rows];
        string[] texts = new string[Rows];
        for (int row = 0; row < Rows;)
        {
            // Runs of one value a few dozen rows long, a tenth of the stretches null, as a series' attribute is.
            int stretch = random.Next(8, 64);
            bool valid = random.Next(10) != 0;
            int value = random.Next(40);
            string text = Shape switch
            {
                "short" => "v" + value,
                "long" => "service.instance.id/" + value,
                _ => value % 2 == 0 ? "v" + value : "service.instance.id/" + value,
            };

            for (int i = 0; i < stretch && row < Rows; i++, row++)
            {
                present[row] = valid || AllValid;
                texts[row] = text;
            }
        }

        _arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        _column = Texts(_arena, types, texts, AllValid ? Validity.NonNullable : Bitmap(_arena, types, present), Shared);
        _starts = new int[Rows + 1];
        _ends = new int[Rows + 1];
    }

    /// <summary>The runs, as the compressor's search finds them for run-end, sixty-four rows of validity at a time.</summary>
    [Benchmark]
    public int Runs() => ColumnCompressor.RunsOf(_arena, _column, compared: false, _starts, _ends);

    private static int Texts(CanonicalArena arena, DTypeArena types, string[] texts, Validity validity, bool shared)
    {
        // Each row's bytes its own, the heap holding every repeat's copy, and two equal long values lying apart in it unless
        // the repeat views the bytes of the row before.
        int heapBytes = 0;
        foreach (string text in texts)
        {
            heapBytes += Encoding.UTF8.GetByteCount(text);
        }

        VortexBuffer views = arena.Allocate(texts.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = arena.Allocate(Math.Max(heapBytes, 1), 8, out Span<byte> heapSpan);
        viewBytes.Clear();
        int at = 0;
        for (int row = 0; row < texts.Length; row++)
        {
            byte[] value = Encoding.UTF8.GetBytes(texts[row]);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            if (shared && row > 0 && texts[row] == texts[row - 1])
            {
                viewBytes.Slice((row - 1) * 16, 16).CopyTo(view);
                value.CopyTo(heapSpan[at..]);
                at += value.Length;
                continue;
            }

            MemoryMarshal.Write(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..]);
                MemoryMarshal.Write(view[8..], 0);
                MemoryMarshal.Write(view[12..], at);
                value.CopyTo(heapSpan[at..]);
                at += value.Length;
            }
        }

        return arena.AddVarBinView(types.Utf8(validity.IsAllValid ? Nullability.NonNullable : Nullability.Nullable), texts.Length, validity, views, [heap]);
    }

    private static Validity Bitmap(CanonicalArena arena, DTypeArena types, bool[] present)
    {
        VortexBuffer buffer = arena.Allocate((present.Length + 7) / 8, 1, out Span<byte> bits);
        bits.Fill(0xFF);
        for (int i = 0; i < present.Length; i++)
        {
            if (!present[i])
            {
                bits[i >> 3] &= (byte)~(1 << (i & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), present.Length, Validity.NonNullable, buffer, 0));
    }
}
