// What a table costs as Parquet, under each codec, against the same table as Vortex: written, then
// read back by the same engine.
//
// A million rows of the four shapes a table mostly holds -- a key that climbs, a measure with a
// seventh of its rows null, a label of a thousand values, a flag -- filled into a writer's builder
// from arrays made once. The write goes to a pipe that counts the bytes and drops them, so that the
// disk is not what is measured; the scans read a file written once at setup, opened by each call,
// through the tool scan both formats share. The bytes each format wrote are printed at setup.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>A million rows written as Parquet under each codec, and as Vortex.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class ParquetWriteBenchmarks
{
    private readonly ParquetTable _table = new();

    /// <summary>The format and its codec: <c>vortex</c>, or <c>parquet-</c> and a codec.</summary>
    [Params("vortex", "parquet-none", "parquet-snappy", "parquet-zstd", "parquet-bloom", "parquet-crc")]
    public string Format { get; set; } = "vortex";

    [GlobalSetup]
    public void Setup()
    {
        long bytes = Write().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Format}: {bytes:N0} bytes");
    }

    [Benchmark(Description = "write 1M rows")]
    public async ValueTask<long> Write()
    {
        DiscardingPipe pipe = new();
        await _table.WriteAsync(Format, pipe).ConfigureAwait(false);
        return pipe.Written;
    }

    /// <summary>A pipe that counts what it is given and keeps none of it.</summary>
    private sealed class DiscardingPipe : PipeWriter
    {
        private byte[] _buffer = new byte[1 << 16];

        internal long Written { get; private set; }

        public override void Advance(int bytes) => Written += bytes;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length)
            {
                _buffer = new byte[sizeHint];
            }

            return _buffer;
        }

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));

        public override void CancelPendingFlush()
        {
        }

        public override void Complete(Exception? exception = null)
        {
        }
    }
}

/// <summary>The same million rows read back from Parquet under each codec, and from Vortex.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class ParquetScanBenchmarks
{
    private string _path = string.Empty;

    /// <summary>The format and its codec: <c>vortex</c>, or <c>parquet-</c> and a codec.</summary>
    [Params("vortex", "parquet-none", "parquet-snappy", "parquet-zstd", "parquet-crc")]
    public string Format { get; set; } = "vortex";

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-bench-{Format}-{Environment.ProcessId}.{(Format == "vortex" ? "vortex" : "parquet")}");
        using (FileStream stream = new(_path, FileMode.Create))
        {
            PipeWriter pipe = PipeWriter.Create(stream);
            new ParquetTable().WriteAsync(Format, pipe).AsTask().GetAwaiter().GetResult();
        }

        Console.WriteLine($"// {Format}: {new FileInfo(_path).Length:N0} bytes");
        if (Format != "vortex")
        {
            // What each structure the filter's pruning reads costs it, and spares it.
            ParquetFile parquet = ParquetFile.OpenAsync(_path, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            foreach ((string name, Scan scan) in (ReadOnlySpan<(string, Scan)>)[("value > 900", parquet.Scan("id").Where($"value > {900.0}")), ("label = label-0500", parquet.Scan("id").Where($"label = {"label-0500"}"))])
            {
                ScanPlan plan = scan.ExplainAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                foreach (PruningStep step in plan.Pruning)
                {
                    Console.WriteLine($"// {Format}, {name}: {step.Structure}: {step.BlocksPruned} blocks pruned, {step.SegmentsRead} reads of {step.BytesRead:N0} bytes");
                }
            }

            parquet.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    /// <summary>The Parquet file, its pages held to their checksums under <c>parquet-crc</c>.</summary>
    private ValueTask<ParquetFile> OpenAsync() =>
        VortexSession.Default.OpenParquetAsync(_path, new ParquetOpenOptions { VerifyChecksums = Format == "parquet-crc" }, CancellationToken.None);

    [Benchmark(Description = "scan every column")]
    public async Task<long> Scan()
    {
        long rows = 0;
        if (Format == "vortex")
        {
            await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            await foreach (BatchView batch in file.Scan())
            {
                rows += batch.RowCount;
            }
        }
        else
        {
            await using ParquetFile file = await OpenAsync().ConfigureAwait(false);
            await foreach (BatchView batch in file.Scan())
            {
                rows += batch.RowCount;
            }
        }

        return rows;
    }

    [Benchmark(Description = "filter value > 900")]
    public async Task<long> Filter()
    {
        if (Format == "vortex")
        {
            await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            return await file.Scan("id").Where($"value > {900.0}").CountAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using ParquetFile parquet = await OpenAsync().ConfigureAwait(false);
        return await parquet.Scan("id").Where($"value > {900.0}").CountAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A label every row group holds: in Parquet, a column of dictionary codes whose dictionary the
    /// pruning reads and rules nothing out by, what pruning by dictionaries costs where it fails.
    /// </summary>
    [Benchmark(Description = "filter label = label-0500")]
    public async Task<long> FilterLabel()
    {
        if (Format == "vortex")
        {
            await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            return await file.Scan("id").Where($"label = {"label-0500"}").CountAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using ParquetFile parquet = await OpenAsync().ConfigureAwait(false);
        return await parquet.Scan("id").Where($"label = {"label-0500"}").CountAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>The rows both classes write: generated once, the same for every format.</summary>
internal sealed class ParquetTable
{
    private const int Rows = 1 << 20;

    private static readonly VortexSchema Schema =
    [
        ("id", VortexType.Int64),
        ("value", VortexType.Float64.Nullable),
        ("label", VortexType.Utf8),
        ("flag", VortexType.Bool),
    ];

    private readonly long[] _ids = new long[Rows];
    private readonly double[] _values = new double[Rows];
    private readonly ulong[] _valid = new ulong[Rows / 64];
    private readonly string[] _labels = new string[Rows];
    private readonly bool[] _flags = new bool[Rows];

    internal ParquetTable()
    {
        Random random = new(42);
        string[] distinct = new string[1_000];
        for (int i = 0; i < distinct.Length; i++)
        {
            distinct[i] = $"label-{i:D4}";
        }

        for (int i = 0; i < Rows; i++)
        {
            _ids[i] = 1_000_000 + i * 3L;
            _values[i] = Math.Round(random.NextDouble() * 1_000, 2);
            if (i % 7 != 0)
            {
                _valid[i >> 6] |= 1UL << (i & 63);
            }

            _labels[i] = distinct[random.Next(distinct.Length)];
            _flags[i] = random.Next(4) == 0;
        }
    }

    /// <summary>Writes the rows to <paramref name="pipe"/> as <paramref name="format"/>, completing the pipe.</summary>
    internal async ValueTask WriteAsync(string format, PipeWriter pipe)
    {
        if (format == "vortex")
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter(pipe, Schema);
            Fill(writer.Builder());
            await writer.WriteAsync(writer.Builder(), CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        ParquetCompression compression = format switch
        {
            "parquet-none" or "parquet-bloom" or "parquet-crc" => ParquetCompression.Uncompressed,
            "parquet-snappy" => ParquetCompression.Snappy,
            _ => ParquetCompression.Zstd,
        };
        await using ParquetFileWriter parquet = VortexSession.Default.CreateParquetWriter(pipe, Schema, new ParquetWriteOptions
        {
            Compression = compression,
            BloomFilters = format == "parquet-bloom" ? new Dictionary<string, double> { ["id"] = 0.01, ["label"] = 0.01 } : null,
            WriteChecksums = format == "parquet-crc",
        });
        Fill(parquet.Builder());
        await parquet.WriteAsync(parquet.Builder(), CancellationToken.None).ConfigureAwait(false);
        await parquet.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void Fill(ColumnsBuilder builder)
    {
        builder.Column<long>(0).Append(_ids);
        builder.Column<double?>(1).Append(_values, _valid);
        ColumnBuilder<string> labels = builder.Column<string>(2);
        foreach (string label in _labels)
        {
            labels.Append(label);
        }

        ColumnBuilder<bool> flags = builder.Column<bool>(3);
        foreach (bool flag in _flags)
        {
            flags.Append(flag);
        }
    }
}
