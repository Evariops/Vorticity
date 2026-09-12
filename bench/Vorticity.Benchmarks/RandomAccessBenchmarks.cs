// What one row costs, and how that cost amortizes.
//
// docs/05-benchmarks.md §3 asks for "random access: the latency of row N alone" and notes that
// `TakeBenchmarks` measures 64 and 1000 scattered rows, never one. The single row is the honest
// statement of the F5 claim - a format that supports random access is one you can ask for a row
// without paying for a scan - and it is also the number that exposes the FIXED cost of a take, which
// an amortized figure hides by construction.
//
// So the axis is a curve rather than a point. Between one row and four thousand, the per-row figure
// falls from "the whole open path divided by one" to something close to the marginal decode cost,
// and where it flattens says how many rows a caller has to want before a take stops being dominated
// by opening the file.
//
// The rows are spread EVENLY OVER THE WHOLE FILE at every count, which matters more than it looks.
// The first version of this used a fixed prime stride, so a take of 8 rows landed entirely in the
// first split while a take of 4096 wrapped the file - and the curve then measured how many rows AND
// how many splits at the same time. Two variables in one axis is not an axis. Spreading by
// `rows / count` holds the split coverage at "all of them" and leaves the count as the only thing
// that moves.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Taking 1 to 4096 scattered rows out of 65 536.</summary>
[Config(typeof(BenchmarkConfig))]
public class RandomAccessBenchmarks
{
    /// <summary>Rows in the file, which turns a count into a stride.</summary>
    private const long Rows = 65_536;

    private string _path = string.Empty;
    private long[] _rows = [];

    /// <summary>How many rows the caller asks for.</summary>
    [Params(1, 8, 64, 512, 4096)]
    public int Count { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
        _rows = new long[Count];
        long stride = Math.Max(Rows / Count, 1);
        for (int i = 0; i < Count; i++)
        {
            _rows[i] = Math.Min(i * stride, Rows - 1);
        }
    }

    [Benchmark(Description = "take n scattered rows")]
    public async Task<long> Take()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take(_rows).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
