// What one row costs, and how that cost amortizes.
//
// Random access is about the latency of row N alone, and the take axis
// of `--ratio-check` measures 64 scattered rows, never one. The single row is the honest
// statement of the random-access claim - a format that supports random access is one you can ask
// for a row without paying for a scan - and it is also the number that exposes the FIXED cost of a
// take, which an amortized figure hides by construction.
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

/// <summary>Taking 1 to 4096 scattered rows out of 65 536. A curve: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RandomAccessBenchmarks
{
    /// <summary>The column whose presence says the file is the shape this class expects.</summary>
    private const string Field = "monotone";

    private string _path = string.Empty;
    private long[] _rows = [];

    /// <summary>
    /// How many rows the caller asks for. Three points rather than five: the curve has ANSWERED -
    /// the cost is per split, and one row costs the open - so what is left is its two ends and its
    /// middle, kept so the shape can be re-read, not so it can guard. The
    /// guard is the take axis of `--ratio-check`.
    /// </summary>
    [Params(1, 64, 4096)]
    public int Count { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        // THE ROW COUNT COMES FROM THE FOOTER. It was the constant 65 536 in a
        // class that honours `VORTICITY_BENCH_DATA`, so a smaller file was asked for rows past
        // its end and a larger one had most of itself never touched.
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");
        long rows = Corpus.RequireIntegerColumn(_path, Field, "VORTICITY_BENCH_DATA").Rows;
        _rows = new long[Count];
        long stride = Math.Max(rows / Count, 1);
        for (int i = 0; i < Count; i++)
        {
            _rows[i] = Math.Min(i * stride, rows - 1);
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
