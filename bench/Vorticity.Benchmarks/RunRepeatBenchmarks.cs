// A run-end column's runs repeated into its rows: `ValueWriter.RepeatRuns`, the loop under every
// `vortex.runend` of typed values, one fill per run.
//
// Runs of a few rows are what a run-end column of a slowly varying value is made of, and where a
// fill's call outweighs the rows it writes; long runs are there to show a fill's cost is not
// paid twice. Each invocation writes a fresh output in the scan's arena, which it then clears.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary><c>ValueWriter.RepeatRuns</c> over 65 536 rows of u32 values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Kernel)]
public class RunRepeatBenchmarks
{
    private const int Rows = 1 << 16;

    private const string Id = "vortex.runend";

    /// <summary>How many rows a run holds: 1 to 6, 8 to 24, or 100 to 300.</summary>
    [Params("short", "medium", "long")]
    public string Runs { get; set; } = "short";

    private CanonicalArena _source = new CanonicalArena();
    private ScanContext? _scan;
    private int _values;
    private byte[] _ends = [];
    private int _runCount;

    /// <summary>What one invocation writes: four bytes per row.</summary>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters) => (Rows, Rows * 4L);

    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(20260927);
        (int low, int high) = Runs switch { "short" => (1, 7), "medium" => (8, 25), _ => (100, 301) };
        List<uint> ends = [];
        uint end = 0;
        while (end < Rows)
        {
            end = (uint)Math.Min(Rows, end + random.Next(low, high));
            ends.Add(end);
        }

        _runCount = ends.Count;
        _ends = MemoryMarshal.AsBytes(ends.ToArray().AsSpan()).ToArray();
        DTypeArena types = new DTypeArena();
        _source = new CanonicalArena();
        VortexBuffer values = _source.Allocate(_runCount * 4, 4, out Span<byte> bytes);
        random.NextBytes(bytes);
        _values = _source.AddPrimitive(types.Primitive(PType.U32, Nullability.NonNullable), _runCount, Validity.NonNullable, PType.U32, values);
        _scan = new ScanContext(["vortex.primitive"]);
    }

    [GlobalCleanup]
    public void Cleanup() => _scan?.Dispose();

    [Benchmark(Description = "repeat runs")]
    public int Repeat()
    {
        ScanContext scan = _scan!;
        ValueReader values = ValueReader.Of(_source, _values, Id);
        ValueWriter writer = ValueWriter.CreateUninitialized(scan.Decode, in values, Rows, 0, Id);
        ValidityReader valid = ValidityReader.Of(_source, Validity.NonNullable);
        ValidityWriter validity = ValidityWriter.Create(scan.Decode, Rows, false, Id);
        int position = writer.RepeatRuns(in values, _ends, PType.U32, 0, _runCount, 0, Rows, in valid, in validity, false);
        scan.Canonical.Reset();
        return position;
    }
}
