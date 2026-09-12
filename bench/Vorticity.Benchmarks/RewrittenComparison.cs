// Reading a file THIS LIBRARY WROTE, which nothing else in the suite does.
//
// Every other axis here reads the conformance corpus, and the conformance corpus was written by the
// reference. So the whole suite measures how fast we read the REFERENCE'S encoding choices, and the
// cost of our own has never appeared in any number. That gap hid a real one: with zstd disabled
// entirely, `distributions/high_cardinality_i64_r8193` scans 1.27x slower from our writer's output
// than from the reference's - a pre-existing difference in what the compressor picks, invisible to
// every benchmark and to the written-size ratchet alike.
//
// FOUR ARMS, because two of them separate the question that matters. "Our file is slower" can mean
// the encoding is genuinely more expensive to decode, or that OUR DECODER is weak on a scheme our
// writer happens to like. Having Rust read both files answers it: if Rust reads our file at the same
// speed it reads the reference's, the encoding is fine and the decoder is ours to fix; if Rust slows
// down too, the writer is choosing a scheme that costs everyone.
//
// The written file is produced once in GlobalSetup, not per iteration - writing is not what this
// measures.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>Scanning the reference's bytes against scanning our own, with both readers.</summary>
[Config(typeof(BenchmarkConfig))]
public class RewrittenComparison
{
    private string _reference = string.Empty;
    private string _ours = string.Empty;

    /// <summary>
    /// The files whose rewrite is worth watching.
    /// </summary>
    /// <remarks>
    /// The five-column file because it is the one every other axis uses, and the high-cardinality
    /// integer file because it is where the gap was first seen.
    /// </remarks>
    [Params("containers/zoned_many_zones_nulls", "distributions/high_cardinality_i64_r8193")]
    public string Entry { get; set; } = "containers/zoned_many_zones_nulls";

    [GlobalSetup]
    public void Setup()
    {
        if (!RustReader.Available)
        {
            throw new InvalidOperationException(
                $"The native comparison harness is missing (looked for {RustReader.ExpectedPath}). " +
                "Build it with: cd tools/vxbench-rs && cargo build --release.");
        }

        _reference = Corpus.Path(Entry);
        _ours = Path.Combine(Path.GetTempPath(), $"vorticity-rewritten-{Guid.NewGuid():N}.vortex");
        Rewrite(_reference, _ours).GetAwaiter().GetResult();

        // Both files must hold the same rows, or the pair of ratios below compares two different
        // computations - the same precondition --ffi-check exists for, asserted here because this
        // axis introduces a file the corpus manifest knows nothing about.
        long reference = RustReader.Require(RustReader.ScanAll(_reference), "reference scan");
        long ours = RustReader.Require(RustReader.ScanAll(_ours), "rewritten scan");
        if (reference != ours)
        {
            throw new InvalidOperationException(
                $"The rewrite of {Entry} holds {ours} rows against the original's {reference}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (System.IO.File.Exists(_ours))
        {
            System.IO.File.Delete(_ours);
        }
    }

    [Benchmark(Baseline = true, Description = "ours reads the reference's file")]
    public Task<long> OursOnReference() => Scan(_reference);

    [Benchmark(Description = "ours reads our file")]
    public Task<long> OursOnOurs() => Scan(_ours);

    [Benchmark(Description = "Rust reads the reference's file")]
    public long RustOnReference() => RustReader.Require(RustReader.ScanAll(_reference), "scan");

    [Benchmark(Description = "Rust reads our file")]
    public long RustOnOurs() => RustReader.Require(RustReader.ScanAll(_ours), "scan");

    private static async Task<long> Scan(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task Rewrite(string source, string destination)
    {
        await using VortexFile file = await VortexFile.OpenAsync(source, CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(destination, file.Schema);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }
}
