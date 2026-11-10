// Audit PQ-04: what a Parquet writer costs for columns of 8- and 16-bit integers, which it widens to INT32 a row at a time.
//
// 1,048,576 rows of four such columns (signed and unsigned bytes and shorts), written to a sink that keeps nothing, with no compression, one
// in ten null or none.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Narrow integers written as Parquet.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditParquetWidenBenchmarks
{
    private const int Rows = 1 << 20;

    private Narrow[] _rows = [];
    private NullableNarrow[] _nullable = [];
    private string _path = string.Empty;

    /// <summary>Whether one row in ten is null.</summary>
    [Params(false, true)]
    public bool Nulls { get; set; }

    /// <summary>A row of narrow integers.</summary>
    [VortexRecord]
    public partial record struct Narrow(sbyte A, short B, byte C, ushort D);

    /// <summary>A row of narrow integers, null now and then.</summary>
    [VortexRecord]
    public partial record struct NullableNarrow(sbyte? A, short? B, byte? C, ushort? D);

    /// <summary>Draws the rows.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-audit-widen-{Guid.NewGuid():N}.parquet");
        Random random = new Random(61);
        _rows = new Narrow[Rows];
        _nullable = new NullableNarrow[Rows];
        for (int i = 0; i < Rows; i++)
        {
            _rows[i] = new Narrow((sbyte)random.Next(-128, 128), (short)random.Next(-32768, 32768), (byte)random.Next(256), (ushort)random.Next(65536));
            bool none = random.Next(10) == 0;
            _nullable[i] = new NullableNarrow(none ? null : _rows[i].A, none ? null : _rows[i].B, none ? null : _rows[i].C, none ? null : _rows[i].D);
        }
    }

    /// <summary>Deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    /// <summary>The rows written.</summary>
    [Benchmark]
    public async Task<long> Write()
    {
        ParquetWriteOptions options = new ParquetWriteOptions { Compression = ParquetCompression.Uncompressed };
        if (Nulls)
        {
            await using ParquetFileWriter nullable = VortexSession.Default.CreateParquetWriter(System.IO.Pipelines.PipeWriter.Create(Stream.Null), NullableNarrow.Schema, options);
            nullable.Builder<NullableNarrow>();
            await nullable.WriteAsync<NullableNarrow>(_nullable, CancellationToken.None).ConfigureAwait(false);
            await nullable.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(System.IO.Pipelines.PipeWriter.Create(Stream.Null), Narrow.Schema, options);
            writer.Builder<Narrow>();
            await writer.WriteAsync<Narrow>(_rows, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return Rows;
    }
}
