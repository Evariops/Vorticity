// What writing a table costs as Parquet, under each codec, against writing it as Vortex.
//
// A million rows of the four shapes a table mostly holds -- a key that climbs, a measure with a
// seventh of its rows null, a label of a thousand values, a flag -- filled into the writer's builder
// from arrays made once, and written to a pipe that counts the bytes and drops them, so that the
// disk is not what is measured. Each format's rows cost the same to append; what differs is what
// the writer does with them: Parquet stages PLAIN pages and compresses them, Vortex chooses an
// encoding per chunk. The bytes each format wrote are printed at setup, beside the times.
using System;
using System.Buffers;
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

    /// <summary>The format and its codec: <c>vortex</c>, or <c>parquet-</c> and a codec.</summary>
    [Params("vortex", "parquet-none", "parquet-snappy", "parquet-zstd")]
    public string Format { get; set; } = "vortex";

    [GlobalSetup]
    public void Setup()
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

        long bytes = WriteAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"// {Format}: {bytes:N0} bytes");
    }

    [Benchmark(Description = "write 1M rows")]
    public ValueTask<long> Write() => WriteAsync();

    private async ValueTask<long> WriteAsync()
    {
        DiscardingPipe pipe = new();
        if (Format == "vortex")
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter(pipe, Schema);
            Fill(writer.Builder());
            await writer.WriteAsync(writer.Builder(), CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }
        else
        {
            ParquetCompression compression = Format switch
            {
                "parquet-none" => ParquetCompression.Uncompressed,
                "parquet-snappy" => ParquetCompression.Snappy,
                _ => ParquetCompression.Zstd,
            };
            await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(pipe, Schema, new ParquetWriteOptions { Compression = compression });
            Fill(writer.Builder());
            await writer.WriteAsync(writer.Builder(), CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return pipe.Written;
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
