// The ratio against Rust: 1.0 acceptance criterion 4, and the only one that was unmeasurable.
//
// docs/05-benchmarks.md §2 is specific about the method, and the specificity is the point: ONE
// process, ONE clock, the SAME bytes, the SAME page-cache state. Two CI runs of two binaries can
// differ by more than the thing being measured, which is how a 1.4x ratio becomes unreadable.
//
// Each class below is one axis with two benchmarks: ours as the baseline, Rust as the comparand,
// so BenchmarkDotNet's Ratio column IS the criterion. They are separate classes rather than one
// class of eight benchmarks because a single baseline would make the ratio column compare a
// projected scan against a full scan, which means nothing.
//
// THE HARNESS IS REQUIRED, NOT OPTIONAL, FOR THESE. GlobalSetup throws when the cdylib is missing,
// naming the command that builds it. The alternative - skipping quietly - produces a run that
// reports no ratio and looks like a run that reported a good one.
//
// FAIRNESS, ITEM BY ITEM:
//   * Both sides open the file from scratch on every iteration. The native side builds a fresh
//     session per call precisely so Rust does not get a warm segment cache we are not offered.
//   * Both sides are single-threaded: ours has no worker pool, and the shim uses Vortex's
//     single-thread runtime rather than its default (docs/05 §5).
//   * The FFI call itself is measured by FfiFloor below, so it can be subtracted. On a whole-file
//     scan it is noise; on the open-only axis it is not necessarily.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Shared setup: the dataset, and the refusal to report a ratio against nothing.</summary>
public abstract class ComparisonBase
{
    /// <summary>The file both implementations read.</summary>
    protected string Path { get; private set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        if (!RustReader.Available)
        {
            throw new InvalidOperationException(
                $"The native comparison harness is missing (looked for {RustReader.ExpectedPath}). " +
                "Build it with: cd tools/vxbench-rs && cargo build --release. " +
                "Without it there is no ratio to report, and reporting one anyway is the failure " +
                "mode docs/05-benchmarks.md §2 exists to prevent.");
        }

        Path = Corpus.Dataset("VORTICITY_BENCH_DATA", "containers/zoned_many_zones_nulls");

        // Both readers must agree on what they read, or the ratio is between two different
        // computations. Checked once here rather than asserted per iteration, which would be
        // measured.
        long ours = Managed().GetAwaiter().GetResult();
        long theirs = RustReader.Require(RustReader.ScanAll(Path), "scan");
        if (ours != theirs)
        {
            throw new InvalidOperationException(
                $"The two readers disagree on {Path}: {ours} rows versus {theirs}.");
        }
    }

    /// <summary>A full scan through our reader, used by <see cref="Setup"/> to cross-check.</summary>
    /// <returns>The row count.</returns>
    protected async Task<long> Managed()
    {
        await using VortexFile file = await VortexFile.OpenAsync(Path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}

/// <summary>
/// Per-encoding decode, against Rust. docs/05-benchmarks.md §3 calls this the axis that matters
/// most during development, and §1 sets it a tighter target than the scan: **within 1.5x**, because
/// a kernel is the one place where a missing vectorized path has nowhere to hide.
/// </summary>
/// <remarks>
/// Each file was written by the generator with ONE scheme forced, so the number is dominated by
/// that kernel rather than by whatever a sampler picked. Both sides read the same file from the
/// same path, so the layout walk and the file I/O are common-mode and cancel in the ratio.
///
/// The encodings listed are the ones both implementations read. pco, fastlanes.delta,
/// zstd_buffers, variant and map are in the corpus and out of our scope, so a ratio for them would
/// be a ratio against an exception.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
public class DecodeComparison
{
    private string _path = string.Empty;

    /// <summary>The corpus entry to decode.</summary>
    [Params(
        "encodings/fastlanes_bitpacked",
        "encodings/fastlanes_for",
        "encodings/fastlanes_rle",
        "encodings/runend",
        "encodings/dict",
        "encodings/sparse",
        "encodings/zigzag",
        "encodings/alp",
        "encodings/alprd",
        "encodings/fsst",
        "encodings/onpair",
        "encodings/zstd",
        "encodings/varbinview",
        "encodings/bool",
        "encodings/datetimeparts",
        "encodings/decimal_byte_parts")]
    public string Encoding { get; set; } = "encodings/fastlanes_bitpacked";

    [GlobalSetup]
    public void Setup()
    {
        if (!RustReader.Available)
        {
            throw new InvalidOperationException(
                $"The native comparison harness is missing (looked for {RustReader.ExpectedPath}). " +
                "Build it with: cd tools/vxbench-rs && cargo build --release.");
        }

        _path = Corpus.Path(Encoding);
        long ours = Managed().GetAwaiter().GetResult();
        long theirs = RustReader.Require(RustReader.ScanAll(_path), "scan");
        if (ours != theirs)
        {
            throw new InvalidOperationException(
                $"The two readers disagree on {_path}: {ours} rows versus {theirs}.");
        }
    }

    [Benchmark(Baseline = true, Description = "Vorticity")]
    public Task<long> Managed_Decode() => Managed();

    [Benchmark(Description = "Vortex Rust")]
    public long Rust_Decode() => RustReader.Require(RustReader.ScanAll(_path), "scan");

    private async Task<long> Managed()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            for (int field = 0; field < batch.FieldCount; field++)
            {
                VortexColumn column = batch.Column(field);
                for (int row = 0; row < batch.RowCount; row++)
                {
                    // Touch every row: a scan that only counts them can skip work a decode cannot.
                    _ = column.IsValid(row);
                }
            }

            rows += batch.RowCount;
        }

        return rows;
    }
}

/// <summary>Full scan, every column decoded: the headline ratio.</summary>
[Config(typeof(BenchmarkConfig))]
public class FullScanComparison : ComparisonBase
{
    [Benchmark(Baseline = true, Description = "Vorticity")]
    public Task<long> Managed_FullScan() => Managed();

    [Benchmark(Description = "Vortex Rust")]
    public long Rust_FullScan() => RustReader.Require(RustReader.ScanAll(Path), "scan");
}

/// <summary>One column out of five: I/O pruning rather than decode.</summary>
[Config(typeof(BenchmarkConfig))]
public class ProjectionComparison : ComparisonBase
{
    /// <summary>The field both sides project; it exists in the default dataset.</summary>
    private const string Field = "monotone";

    [Benchmark(Baseline = true, Description = "Vorticity")]
    public async Task<long> Managed_Projected()
    {
        await using VortexFile file = await VortexFile.OpenAsync(Path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(Field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    [Benchmark(Description = "Vortex Rust")]
    public long Rust_Projected() =>
        RustReader.Require(RustReader.ScanProjected(Path, Field), "projected scan");
}

/// <summary>
/// Open to first batch: the 1-2 round trip promise, and the axis where the FFI call is not
/// automatically noise.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class OpenComparison : ComparisonBase
{
    [Benchmark(Baseline = true, Description = "Vorticity")]
    public async Task<long> Managed_FirstBatch()
    {
        await using VortexFile file = await VortexFile.OpenAsync(Path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return batch.RowCount;
        }

        return 0;
    }

    [Benchmark(Description = "Vortex Rust")]
    public long Rust_FirstBatch() =>
        RustReader.Require(RustReader.OpenFirstBatch(Path), "first batch");

    /// <summary>Open and read the footer's row count, touching no data segment.</summary>
    [Benchmark(Description = "Vorticity, footer only")]
    public async Task<long> Managed_OpenOnly()
    {
        await using VortexFile file = await VortexFile.OpenAsync(Path, CancellationToken.None);
        return file.RowCount;
    }

    [Benchmark(Description = "Vortex Rust, footer only")]
    public long Rust_OpenOnly() => RustReader.Require(RustReader.OpenOnly(Path), "open");

    /// <summary>
    /// The empty native call. Not an axis: the floor to subtract from the others, per docs/05 §2.
    /// </summary>
    [Benchmark(Description = "FFI floor")]
    public long FfiFloor() => RustReader.NoOp();
}
