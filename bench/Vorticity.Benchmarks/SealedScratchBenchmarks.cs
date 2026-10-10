// What sealing the scratch costs a spill, against the disk the spill writes to anyway.
//
// A query that spills writes its runs once and reads them back once, in long appends and reads of
// some tens of kilobytes. Sealed, each 64 KiB frame is encrypted when it fills and decrypted when a
// read reaches it. The run is 64 MiB written in pieces of 1 MiB and read back in pieces of 64 KiB, the
// file written to the temporary directory and left in the page cache.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>A spill's scratch, written and read back, sealed or plain: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class SealedScratchBenchmarks
{
    private const int Total = 64 << 20;

    private const int Piece = 1 << 20;

    private const int ReadPiece = 64 << 10;

    private readonly byte[] _piece = new byte[Piece];
    private readonly byte[] _read = new byte[ReadPiece];
    private RunScratch? _written;

    /// <summary>Whether the scratch file is sealed.</summary>
    [Params(false, true)]
    public bool Sealed { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        new Random(20261011).NextBytes(_piece);
        _written = await WriteAsync(Sealed);
    }

    [GlobalCleanup]
    public void Cleanup() => _written?.Dispose();

    [Benchmark(Description = "write 64 MiB")]
    public async Task<long> Write()
    {
        using RunScratch scratch = await WriteAsync(Sealed);
        return scratch.Length;
    }

    [Benchmark(Description = "read 64 MiB back")]
    public async Task<long> Read()
    {
        long sum = 0;
        for (long at = 0; at < Total; at += ReadPiece)
        {
            await _written!.ReadAsync(at, _read, CancellationToken.None);
            sum += _read[0];
        }

        return sum;
    }

    private async Task<RunScratch> WriteAsync(bool sealFile)
    {
        RunScratch scratch = new RunScratch(memoryBudget: 0, directory: null, sealFile);
        for (int written = 0; written < Total; written += Piece)
        {
            await scratch.AppendAsync(_piece, CancellationToken.None);
        }

        return scratch;
    }
}
