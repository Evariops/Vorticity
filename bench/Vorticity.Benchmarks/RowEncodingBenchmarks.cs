// Row encoding throughput, which nothing had measured at all.
//
// docs/05-benchmarks.md §3 lists it and calls it the cleanest signal on code-generation quality this
// repository has: the encoder is eleven value types specialized through static abstract interface
// members, so its throughput is almost entirely a statement about what the JIT did with that
// specialization. Everything else here is dominated by I/O, layout walking or a compression kernel.
//
// NO CROSS-CHECK AGAINST `vortex-row`, and that is a real limit rather than an omission to fix
// later: tools/vxbench-rs exposes `path in, rows out` entry points only, so there is no way to hand
// the reference a batch and time its encoder. These are absolute figures with no reference beside
// them - which is exactly the kind docs/05 §1 says not to treat as portable. The ratio-bearing
// version needs an FFI surface that does not exist.
//
// What they ARE good for is a before-and-after on this machine, which is what a kernel change needs,
// and a ns/row figure that is not diluted by a 35 us open cost the way the per-encoding decode table
// is.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.RowEncoding;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Encoding a batch's rows into comparable byte keys.</summary>
[Config(typeof(BenchmarkConfig))]
public class RowEncodingBenchmarks
{
    private VortexFile? _file;
    private IAsyncEnumerator<RecordBatch>? _batches;
    private RecordBatch? _batch;
    private RowSortField[] _fields = [];

    /// <summary>
    /// The file whose first batch is encoded.
    /// </summary>
    /// <remarks>
    /// A struct root is required - row encoding is about ordering tuples. `struct_root_all_dtypes`
    /// would have been the interesting one and cannot be used: it carries a `vortex.map`, which this
    /// build does not decode, so the batch never arrives. These two differ in width and nullability
    /// instead.
    /// </remarks>
    [Params("containers/zoned_many_zones_nulls", "containers/uncompressed_canonical")]
    public string Entry { get; set; } = "containers/zoned_many_zones_nulls";

    /// <summary>Rows in the batch under test, for the ns/row figure.</summary>
    public int Rows => _batch?.RowCount ?? 0;

    [GlobalSetup]
    public void Setup()
    {
        // The batch is held across iterations, which means the enumerator must be held too: a
        // RecordBatch is valid only until the next MoveNextAsync resets the arenas behind it.
        _file = VortexFile.OpenAsync(Corpus.Path(Entry), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        _batches = _file.Scan().ExecuteAsync().GetAsyncEnumerator();
        if (!_batches.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            throw new InvalidOperationException($"{Entry} produced no batches.");
        }

        _batch = _batches.Current;
        _fields = new RowSortField[_batch.FieldCount];
        Array.Fill(_fields, RowSortField.Ascending);

        // Printed rather than returned, because the interesting figures are per ROW and per BYTE and
        // BenchmarkDotNet reports neither: the mean below divides by these two.
        using RowKeys probe = RowEncoder.Encode(_batch, _fields);
        Console.Error.WriteLine(
            $"ROWENC {Entry}: rows={_batch.RowCount} fields={_batch.FieldCount} keyBytes={probe.TotalBytes}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _batches?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _file?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Description = "encode a batch to row keys")]
    public int Encode()
    {
        using RowKeys keys = RowEncoder.Encode(_batch!, _fields);
        return keys.TotalBytes;
    }
}
