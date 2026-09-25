using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks;

/// <summary>
/// The validation of a <c>vortex.varbinview</c> node's views alone: the library's
/// <c>VarBinViewDecoder.ValidateViews</c> against a frozen copy of the view-at-a-time loop, in one
/// process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// <c>corpus</c> is the per-encoding corpus file's shape, values of 0 to 19 bytes, two in three
/// inline; <c>inline</c> has every value inline and <c>long</c> every value in the data buffer, 24 to
/// 60 bytes. All of it is ASCII text in a Utf8 column without nulls.
/// </para>
/// <para>
/// Before anything is timed, both arms accept the views, and the library still refuses a view whose
/// prefix differs from its value, a value and an inline value that are not UTF-8, and a view past
/// its buffer.
/// </para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class ViewValidationBenchmarks
{
    private const int ViewSize = 16;

    private byte[] _views = [];
    private byte[][] _arrays = [];
    private VortexBuffer[] _buffers = [];

    /// <summary>The values: <c>corpus</c>, <c>inline</c> or <c>long</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "corpus";

    /// <summary>Views validated in one call.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>Every shape in every profile.</summary>
    public static IEnumerable<string> Shapes => ["corpus", "inline", "long"];

    /// <summary>Views in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation reads: every view, and the bytes of the values out of line.</summary>
    /// <param name="method">Unused; every arm reads the same.</param>
    /// <param name="parameters">The case's shape and rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        string shape = (string)parameters[nameof(Shape)]!;
        int rows = (int)parameters[nameof(Rows)]!;
        long bytes = (long)rows * ViewSize;
        for (int row = 0; row < rows; row++)
        {
            int size = Size(shape, row);
            bytes += size > 12 ? size : 0;
        }

        return (rows, bytes);
    }

    [GlobalSetup]
    public void Setup()
    {
        (_views, byte[] data) = Build(Shape, Rows);
        _arrays = [data];
        _buffers = [VortexBuffer.FromPinned(data, 0)];
        Check();
    }

    private void Check()
    {
        Original();
        Current();
        Refuses("a wrong prefix", (views, data) => Corrupt(views, data, referencing: true, (v, d, offset) => v[4] ^= 1));
        Refuses("a value that is not UTF-8", (views, data) => Corrupt(views, data, referencing: true, (v, d, offset) => d[offset + 5] = 0xFF));
        Refuses("an inline value that is not UTF-8", (views, data) => Corrupt(views, data, referencing: false, (v, d, offset) => v[5] = 0xFF));
        Refuses("a view past its buffer", (views, data) => Corrupt(views, data, referencing: true, (v, d, offset) => BinaryPrimitives.WriteUInt32LittleEndian(v[12..], (uint)d.Length)));

        // Padding is not the value: a short inline value followed by bytes that are not text is
        // still a valid view.
        byte[] padded = (byte[])_views.Clone();
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = padded.AsSpan(row * ViewSize, ViewSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(view) < 12)
            {
                view[15] = 0xFF;
                VarBinViewDecoder.ValidateViews(padded, _buffers, ValidityMask.NonNullable, Rows, requireUtf8: true);
                break;
            }
        }
    }

    private void Refuses(string what, Func<byte[], byte[], bool> corrupt)
    {
        byte[] views = (byte[])_views.Clone();
        byte[] data = GC.AllocateArray<byte>(_arrays[0].Length, pinned: true);
        _arrays[0].CopyTo(data, 0);
        if (!corrupt(views, data))
        {
            return;
        }

        try
        {
            VarBinViewDecoder.ValidateViews(views, [VortexBuffer.FromPinned(data, 0)], ValidityMask.NonNullable, Rows, requireUtf8: true);
        }
        catch (VortexFormatException)
        {
            return;
        }

        throw new InvalidOperationException($"{Shape}: the library accepted {what}.");
    }

    /// <summary>Corrupts the last view of the wanted kind; false when the shape has none.</summary>
    private static bool Corrupt(byte[] views, byte[] data, bool referencing, CorruptView corrupt)
    {
        for (int row = views.Length / ViewSize - 1; row >= 0; row--)
        {
            Span<byte> view = views.AsSpan(row * ViewSize, ViewSize);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
            if ((size > 12) == referencing && size >= 6)
            {
                corrupt(view, data, (int)BinaryPrimitives.ReadUInt32LittleEndian(view[12..]));
                return true;
            }
        }

        return false;
    }

    private delegate void CorruptView(Span<byte> view, byte[] data, int offset);

    [Benchmark(Baseline = true)]
    public int Original()
    {
        ViewValidationOriginal.Validate(_views, _arrays, Rows, requireUtf8: true);
        return Rows;
    }

    [Benchmark]
    public int Current()
    {
        VarBinViewDecoder.ValidateViews(_views, _buffers, ValidityMask.NonNullable, Rows, requireUtf8: true);
        return Rows;
    }

    private static int Size(string shape, int row) => shape switch
    {
        "corpus" => row % 20,
        "inline" => row % 13,
        "long" => 24 + (row % 37),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "corpus, inline or long"),
    };

    private static (byte[] Views, byte[] Data) Build(string shape, int rows)
    {
        byte[] views = new byte[rows * ViewSize];
        List<byte> data = [];
        Span<byte> scratch = stackalloc byte[64];
        for (int row = 0; row < rows; row++)
        {
            int size = Size(shape, row);
            Span<byte> value = scratch[..size];
            for (int k = 0; k < size; k++)
            {
                value[k] = (byte)('a' + ((row + k) % 26));
            }

            Span<byte> view = views.AsSpan(row * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)size);
            if (size <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..], (uint)data.Count);
            foreach (byte b in value)
            {
                data.Add(b);
            }
        }

        byte[] pinned = GC.AllocateArray<byte>(Math.Max(data.Count, 1), pinned: true);
        data.CopyTo(pinned);
        return (views, pinned);
    }
}
