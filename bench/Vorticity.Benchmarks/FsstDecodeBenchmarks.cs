using System;
using System.Collections.Generic;
using System.Globalization;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>
/// The <c>vortex.fsst</c> decode kernel alone: the library's padded tables and walk over a node's
/// code stream, against a frozen copy of the kernel as it was, in one process and on one clock.
/// </summary>
/// <remarks>
/// <para>
/// The text is compressed by the writer's own trainer. <c>urls</c> is the per-encoding corpus file's
/// shape, 52-byte URLs that differ in their last nine digits, about six bytes a code; <c>uuids</c>
/// and <c>logs</c> are the two text shapes of the encoding trade-offs, two bytes a code and four
/// with an escape every dozen rows.
/// </para>
/// <para>
/// 1 024 rows decode into the first-level cache, and show the kernel's own cost; 131 072 rows are a
/// scan window, whose heap goes out to the second level. The arms are checked against the text
/// the codes were made from before anything is timed.
/// </para>
/// </remarks>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
[MinColumn]
[MedianColumn]
[CyclesColumn]
public class FsstDecodeBenchmarks
{
    private const string Id = "vortex.fsst";

    private byte[] _symbols = [];
    private byte[] _lengths = [];
    private byte[] _codes = [];
    private byte[] _text = [];
    private byte[] _output = [];

    /// <summary>The text: <c>urls</c>, <c>uuids</c> or <c>logs</c>.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "urls";

    /// <summary>Rows of one node.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int Rows { get; set; } = 1024;

    /// <summary>
    /// Every shape in every profile: wide symbols, narrow ones, and escapes, which a change tuned
    /// for one moves differently.
    /// </summary>
    public static IEnumerable<string> Shapes => ["urls", "uuids", "logs"];

    /// <summary>A node whose heap stays in the first-level cache, and a scan window.</summary>
    public static IEnumerable<int> RowCounts => [1024, 131_072];

    /// <summary>What one invocation decodes: every row, and the bytes of its text.</summary>
    /// <param name="method">Unused; the copy floor moves the same bytes.</param>
    /// <param name="parameters">The case's shape and rows.</param>
    public static (long Rows, long Bytes) BenchmarkWork(string method, IReadOnlyDictionary<string, object?> parameters)
    {
        string shape = (string)parameters[nameof(Shape)]!;
        int rows = (int)parameters[nameof(Rows)]!;
        return (rows, Text(shape, rows).Length);
    }

    [GlobalSetup]
    public void Setup()
    {
        _text = Text(Shape, Rows, out int[] starts, out int[] lengths);
        FsstSymbols symbols = FsstSymbols.Train(_text, starts, lengths)
            ?? throw new InvalidOperationException($"{Shape}: nothing to train on");

        _symbols = new byte[symbols.Count * FsstSymbolTable.SymbolSize];
        _lengths = new byte[symbols.Count];
        for (int code = 0; code < symbols.Count; code++)
        {
            BitConverter.TryWriteBytes(_symbols.AsSpan(code * FsstSymbolTable.SymbolSize), symbols.SymbolBits(code));
            _lengths[code] = symbols.SymbolLength(code);
        }

        byte[] codes = new byte[_text.Length * 2];
        int written = 0;
        for (int row = 0; row < Rows; row++)
        {
            written += symbols.Compress(_text.AsSpan(starts[row], lengths[row]), codes.AsSpan(written));
        }

        _codes = codes.AsSpan(0, written).ToArray();
        _output = new byte[_text.Length];
        Check();
    }

    private void Check()
    {
        Original();
        Require("original");
        _output.AsSpan().Fill(0xA5);
        Current();
        Require("library");
    }

    private void Require(string arm)
    {
        if (!_output.AsSpan().SequenceEqual(_text))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{Shape}: the {arm} decode differs from the text at byte {_output.AsSpan().CommonPrefixLength(_text)}."));
        }
    }

    [Benchmark(Baseline = true)]
    public int Original()
    {
        int written = FsstDecodeOriginal.Decode(_symbols, _lengths, _codes, _output, out bool ascii);
        return ascii ? written : -written;
    }

    [Benchmark]
    public int Current()
    {
        Span<byte> symbolScratch = stackalloc byte[FsstSymbolTable.SymbolScratchBytes];
        Span<byte> widthScratch = stackalloc byte[FsstSymbolTable.WidthScratchBytes];
        FsstDecodeTable table = FsstSymbolTable.Create(_symbols, _lengths, Id).Prepare(symbolScratch, widthScratch);
        uint escapeBits = 0;
        int written = table.Decode(_codes, _output, Id, ref escapeBits);
        return table.SymbolsAreAscii && (escapeBits & 0x80) == 0 ? written : -written;
    }

    /// <summary>The decoded bytes copied from where they already are: how fast the heap can be written at all.</summary>
    [Benchmark]
    public int CopyFloor()
    {
        _text.AsSpan().CopyTo(_output);
        return _output[0];
    }

    private static byte[] Text(string shape, int rows) => Text(shape, rows, out _, out _);

    private static byte[] Text(string shape, int rows, out int[] starts, out int[] lengths)
    {
        byte[] heap = new byte[rows * 160];
        starts = new int[rows];
        lengths = new int[rows];
        int at = 0;
        for (int row = 0; row < rows; row++)
        {
            Span<byte> destination = heap.AsSpan(at);
            int length = shape switch
            {
                "urls" => Url(row, destination),
                "uuids" => EncodingTradeoffs.Uuid(row, destination),
                "logs" => EncodingTradeoffs.LogLine(row, destination),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "urls, uuids or logs"),
            };
            starts[row] = at;
            lengths[row] = length;
            at += length;
        }

        return heap.AsSpan(0, at).ToArray();
    }

    private static int Url(int row, Span<byte> destination)
    {
        "https://example.invalid/vortex/conformance/"u8.CopyTo(destination);
        row.TryFormat(destination[43..], out int digits, "D9", CultureInfo.InvariantCulture);
        return 43 + digits;
    }
}
