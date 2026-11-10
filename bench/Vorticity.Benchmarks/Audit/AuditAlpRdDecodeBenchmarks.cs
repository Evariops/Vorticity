// Audit DE-23: what an ALP-RD column costs read, its values' left parts looked up in its dictionary of eight a vector at a
// time: on AVX-512 before, a row at a time on AVX2. Run as it is and under DOTNET_EnableAVX512=0.
//
// 1,048,576 doubles (or floats) of full precision, written as ALP-RD to a file in memory and read back, every batch.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An ALP-RD column read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditAlpRdDecodeBenchmarks
{
    private const int Rows = 1 << 20;

    private VortexFile _file = null!;

    /// <summary>The values' width.</summary>
    [Params(64, 32)]
    public int Bits { get; set; }

    /// <summary>A double.</summary>
    [VortexRecord]
    public partial record struct Wide(double Value);

    /// <summary>A float.</summary>
    [VortexRecord]
    public partial record struct Narrow(float Value);

    /// <summary>Writes the file and opens it in memory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-alprd-{Guid.NewGuid():N}.vortex");
        try
        {
            Random random = new Random(199);
            double[] values = new double[Rows];
            for (int i = 0; i < Rows; i++)
            {
                values[i] = 1_000 + (random.NextDouble() * 10);
            }

            WriteAsync(path, values).GetAwaiter().GetResult();
            _file = VortexFile.OpenAsync(new MemorySegmentSource(System.IO.File.ReadAllBytes(path)), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        Console.WriteLine($"// encodings: {string.Join(", ", _file.ResolvedArrayEncodings.ToArray())}; rows {Read()}");
    }

    /// <summary>Closes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => _file.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Every batch read, by row.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public long Read()
    {
        long rows = 0;
        foreach (RecordBatch batch in _file.ScanBuilder().WithDegreeOfParallelism(1).ExecuteAsync().ToBlockingEnumerable())
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        return rows;
    }

    private async Task WriteAsync(string path, double[] values)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, EncodingHint> { ["Value"] = EncodingHint.AlpRd },
        };
        if (Bits == 64)
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path, options);
            await writer.WriteAsync<Wide>(values.Select(v => new Wide(v)).ToArray(), CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Narrow>(path, options);
            await writer.WriteAsync<Narrow>(values.Select(v => new Narrow((float)v)).ToArray(), CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
