// Per-encoding decode, the axis docs/05-benchmarks.md §3 calls the one that matters most during
// development: it localizes a regression to one kernel instead of to "the scan got slower".
//
// Each benchmark reads a corpus file that was written to exercise ONE encoding in isolation -- the
// generator forces each scheme individually for exactly this reason -- so the number is dominated by
// that kernel rather than by whatever the sampler happened to pick.
//
// The file is read into memory once in GlobalSetup and decoded from a memory source per iteration,
// so the measurement is decode rather than page-cache behaviour. That is the opposite of what the
// scan benchmarks want and the reason these are a separate class.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>One encoding at a time, decoded from memory.</summary>
[Config(typeof(BenchmarkConfig))]
public class DecodeBenchmarks
{
    private byte[] _bytes = [];

    /// <summary>
    /// The corpus entry to decode. Each is a file the generator forced onto one scheme.
    /// </summary>
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
        "encodings/datetimeparts",
        "encodings/decimal_byte_parts")]
    public string Encoding { get; set; } = "encodings/fastlanes_bitpacked";

    [GlobalSetup]
    public void Setup()
    {
        string path = Corpus.Path(Encoding);

        // A missing entry is a hard failure rather than a skipped row: a benchmark that silently
        // measures nothing is worse than one that does not run.
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException($"no corpus entry '{Encoding}'", path);
        }

        _bytes = System.IO.File.ReadAllBytes(path);
    }

    [Benchmark]
    public async Task<long> Decode()
    {
        await using MemorySegmentSource source = new MemorySegmentSource(_bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
