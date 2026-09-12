// What a scattered take actually costs, before deciding to specialize it.
//
// [90-registry.md](../../docs/90-registry.md) carries a table of per-encoding `take` strategies -
// take on a dictionary's codes with the values untouched, binary search a run-end's ends, index a
// bit-packed block positionally - and records that none of them exists: inside a split that is
// read, the whole split is decoded and the wanted rows are gathered out of it.
//
// That is a real cost in principle. Whether it is a real cost in practice is a measurement, and it
// is the measurement that decides whether the table is worth building. Three axes, one clock:
//
//   * SCATTERED: one row from each of 64 splits. Worst case for the current shape - 64 splits
//     decoded whole, 64 rows kept.
//   * CLUSTERED: 64 rows from ONE split. One split decoded whole, 64 rows kept.
//   * FULL: the whole file, for scale.
//
// The ratio between scattered and full is the number that matters: if taking 64 of 65 536 rows
// costs what reading all of them costs, the specializations are worth their complexity. If the
// splits an index list never touches already dominate - and the I/O half of F5 IS implemented -
// then the remaining gap is smaller than it looks.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Scattered, clustered and whole-file reads of the same file.</summary>
[Config(typeof(BenchmarkConfig))]
public class TakeBenchmarks
{
    private string _path = string.Empty;
    private long[] _scattered = [];
    private long[] _clustered = [];
    private string _fsstPath = string.Empty;
    private long[] _fsstRows = [];

    [GlobalSetup]
    public void Setup()
    {
        _path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

        // 64 splits of 1024 rows in that file, so one row per split against 64 rows in the first.
        _scattered = new long[64];
        _clustered = new long[64];
        for (int i = 0; i < 64; i++)
        {
            _scattered[i] = (i * 1024L) + 511;
            _clustered[i] = i * 8L;
        }

        // One column, one scheme forced, 4096 rows in one split: a take here is the encoding.
        _fsstPath = Corpus.Path("encodings/fsst");
        _fsstRows = new long[8];
        for (int i = 0; i < _fsstRows.Length; i++)
        {
            _fsstRows[i] = (i * 509L) + 3;
        }
    }

    [Benchmark(Baseline = true, Description = "full scan")]
    public async Task<long> FullScan()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    [Benchmark(Description = "take 64 rows from 64 splits")]
    public async Task<long> Scattered() => await Take(_scattered).ConfigureAwait(false);

    [Benchmark(Description = "take 64 rows from 1 split")]
    public async Task<long> Clustered() => await Take(_clustered).ConfigureAwait(false);

    /// <summary>
    /// The same scattered take with the STRING column projected away.
    /// </summary>
    /// <remarks>
    /// The attribution axis, and it earned its place: `vortex.fsst` and `vortex.onpair` take the
    /// zone-decode fallback, so this measures what the fallback costs rather than leaving it
    /// averaged in with everything else. It came to 830 µs of 1070 - and seeing the residual on its
    /// own is what prompted a second look at WHY those two were in the fallback, which found the
    /// stated reason to be wrong: both carry a `codes_offsets` child bounding each row's codes.
    /// docs/90 records that as a defect to fix. This axis is the before-number for it.
    /// </remarks>
    [Benchmark(Description = "take 64 rows from 64 splits, no string column")]
    public async Task<long> ScatteredWithoutStrings()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone", "banded", "nulls", "nans")
            .Take(_scattered)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>
    /// A scattered take over a file whose ONE column is `vortex.fsst`, against a full scan of it.
    /// </summary>
    /// <remarks>
    /// THE AXES ABOVE CANNOT MEASURE FSST, and that was discovered the expensive way: a selective
    /// decode was written for `vortex.fsst`, and the scattered take above did not move by one byte
    /// of allocation. The `strs` column of `containers/zoned_many_zones_nulls` is `vortex.onpair`,
    /// not FSST - so the 837 us that axis leaves on the table is OnPair alone, and docs and commit
    /// messages saying "FSST and OnPair" were describing a pair where only one is present.
    ///
    /// `encodings/fsst` is 4096 rows of one utf8 column written with that scheme forced, so a take
    /// over it measures the encoding and nothing else. Reported against a full scan of the SAME
    /// file in the same process, because the ratio is what survives comparison with another run:
    /// both halves move together under thermal drift, which an absolute microsecond figure does
    /// not.
    /// </remarks>
    [Benchmark(Description = "fsst: take 8 of 4096 rows")]
    public async Task<long> FsstScattered() => await TakeFrom(_fsstPath, _fsstRows);

    /// <summary>The control for the row above: the same file, every row.</summary>
    [Benchmark(Description = "fsst: full scan of the same file")]
    public async Task<long> FsstFullScan()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_fsstPath, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private async Task<long> TakeFrom(string path, long[] indices)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take(indices).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private async Task<long> Take(long[] indices)
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take(indices).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
