// Row encoding throughput, which nothing had measured at all.
//
// It is the cleanest signal on code-generation quality this repository has: the encoder is eleven
// value types specialized through static abstract interface members, so its throughput is almost
// entirely a statement about what the JIT did with that specialization. Everything else here is
// dominated by I/O, layout walking or a compression kernel.
//
// NO CROSS-CHECK AGAINST `vortex-row`, and that is a real limit rather than an omission to fix
// later: tools/vxbench-rs exposes `path in, rows out` entry points only, so there is no way to hand
// the reference a batch and time its encoder. These are absolute figures with no reference beside
// them - which is exactly the kind of figure not to treat as portable. The ratio-bearing
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
[BenchmarkCategory(BenchmarkConfig.Path)]
public class RowEncodingBenchmarks
{
    private VortexFile? _file;
    private IAsyncEnumerator<RecordBatch>? _batches;
    private RecordBatch? _batch;
    private RowSortField[] _fields = [];
    private RowSortField[] _descending = [];
    private int[] _order = [];

    /// <summary>
    /// The file whose first batch is encoded.
    /// </summary>
    /// <remarks>
    /// A struct root is required - row encoding is about ordering tuples. The first two differ in
    /// width and nullability; neither has a decimal column or a nested field, so `EncodeDecimal` and
    /// the per-nested-field `ArrayPool.Rent` made ZERO calls and nothing measured them.
    /// <para>
    /// `chunked_decimal_r1025` is the third case, and ONE file covers both missing sites: four
    /// Decimal(18,4) chunks carried as a struct field, so `EncodeDecimal` runs 368 108 times and the
    /// per-nested-field `ArrayPool.Rent` runs twice, where both were at zero.
    /// </para>
    /// <para>
    /// THREE OTHER CANDIDATES WERE TRIED AND ARE OUT OF REACH, which is worth writing down so the
    /// next person does not try them again. `struct_root_all_dtypes` was the obvious one and the
    /// remark here used to exclude it for a reason that has since expired -- "carries a
    /// `vortex.map`, which this build does not decode" -- but it fails for a real one:
    /// <c>dtype 'decimal256'. Row encoding is not defined for 256-bit decimals</c>.
    /// `fixed_size_list_r1025` has no struct root, so there are no columns to encode.
    /// `struct_nested_deep_nonnull_r8193` carries a `list`, and row encoding refuses it by contract:
    /// the format defines no ordering for variable-size lists.
    /// </para>
    /// </remarks>
    [Params(
        "containers/zoned_many_zones_nulls",
        "containers/uncompressed_canonical",
        "encodings/chunked_decimal_r1025")]
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
        _descending = new RowSortField[_batch.FieldCount];
        Array.Fill(_descending, new RowSortField(descending: true, nullsFirst: true));
        _order = new int[_batch.RowCount];
    }

    /// <summary>
    /// What one invocation moves: the batch's rows, and the key bytes they encode to.
    /// </summary>
    /// <param name="parameters">The case's parameters; <c>Entry</c> says which file.</param>
    /// <remarks>
    /// THIS REPLACES A LINE ON STDERR. The setup printed `rows=… keyBytes=…`
    /// and left the division to the reader, which meant it was not done. The figures are properties
    /// of the corpus entry, so they are measured here the same way -- one encode, once, while the
    /// report is being built -- and land in the table as `ns/row` and `GB/s`.
    /// </remarks>
    public static (long Rows, long Bytes) BenchmarkWork(
        string method, IReadOnlyDictionary<string, object?> parameters)
    {
        // `Entry` is a `[Params]` property, so it arrives in the dictionary: the two cases of this
        // class read different files and therefore move different numbers of rows.
        if (parameters.GetValueOrDefault("Entry") is not string entry)
        {
            return (0, 0);
        }

        VortexFile file = VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        try
        {
            IAsyncEnumerator<RecordBatch> batches = file.Scan().ExecuteAsync().GetAsyncEnumerator();
            try
            {
                if (!batches.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    return (0, 0);
                }

                RecordBatch batch = batches.Current;
                RowSortField[] fields = new RowSortField[batch.FieldCount];
                Array.Fill(fields, RowSortField.Ascending);
                using RowKeys keys = RowEncoder.Encode(batch, fields);
                return (batch.RowCount, keys.TotalBytes);
            }
            finally
            {
                batches.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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

    /// <summary>The same batch with every field descending, which is the only path that inverts.</summary>
    /// <remarks>
    /// A descending variable-length value is copied XORed with 0xFF, and that XOR is the one
    /// operation in row encoding worth a vector. Ascending copies instead, so
    /// the arm above never reaches it and its distance from this one is what inverting costs.
    /// </remarks>
    [Benchmark(Description = "encode a batch to row keys, descending")]
    public int EncodeDescending()
    {
        using RowKeys keys = RowEncoder.Encode(_batch!, _descending);
        return keys.TotalBytes;
    }

    /// <summary>Encode, then order the rows through <see cref="RowKeys.Compare"/>.</summary>
    /// <returns>The first row of the ordering, so the sort is not elided.</returns>
    /// <remarks>
    /// THE ONLY CALLER OF `Compare` IN THE REPOSITORY, outside its own `IComparer`. Encoding to
    /// comparable bytes is worth exactly what comparing them is worth, and that
    /// half had never been timed.
    /// <para>
    /// The encode is INSIDE the arm rather than hoisted into setup, because `RowKeys` owns pooled
    /// arrays it returns on Dispose: holding one across iterations would measure a sort over a
    /// pool that the encode arm keeps churning. The difference between the two arms is therefore
    /// the sort, and the encode is the control.
    /// </para>
    /// </remarks>
    [Benchmark(Description = "encode, then sort rows by key")]
    public int EncodeAndSort()
    {
        using RowKeys keys = RowEncoder.Encode(_batch!, _fields);
        Span<int> order = _order;
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        keys.SortIndices(order);
        return order.Length == 0 ? 0 : order[0];
    }
}
