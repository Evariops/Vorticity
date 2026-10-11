// What nested columns cost as Parquet, against the same table as Vortex: written, then read back.
//
// A million rows: a key that climbs, a list of zero to seven integers, and a list of up to four labels
// of a hundred values, a fifth of those lists null. Parquet shreds the lists into levels on the write
// and assembles them back from the levels on the read; Vortex keeps their offsets.
using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>A million rows of lists written as Parquet and as Vortex, and read back.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class ParquetNestedBenchmarks
{
    private readonly NestedTable _table = new();
    private string _path = string.Empty;

    /// <summary>The format and its codec: <c>vortex</c>, or <c>parquet-</c> and a codec.</summary>
    [Params("vortex", "parquet-none", "parquet-zstd")]
    public string Format { get; set; } = "vortex";

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vorticity-nested-{Format}-{Environment.ProcessId}.{(Format == "vortex" ? "vortex" : "parquet")}");
        using (FileStream stream = new(_path, FileMode.Create))
        {
            _table.WriteAsync(Format, PipeWriter.Create(stream)).AsTask().GetAwaiter().GetResult();
        }

        Console.WriteLine($"// {Format}: {new FileInfo(_path).Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "write 1M rows of lists")]
    public async ValueTask<long> Write()
    {
        CountingPipe pipe = new();
        await _table.WriteAsync(Format, pipe).ConfigureAwait(false);
        return pipe.Written;
    }

    [Benchmark(Description = "scan 1M rows of lists")]
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
            await using ParquetFile file = await ParquetFile.OpenAsync(_path, CancellationToken.None).ConfigureAwait(false);
            await foreach (BatchView batch in file.Scan())
            {
                rows += batch.RowCount;
            }
        }

        return rows;
    }

    /// <summary>A pipe that counts what it is given and keeps none of it.</summary>
    private sealed class CountingPipe : PipeWriter
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

/// <summary>The rows of lists both formats write: generated once.</summary>
internal sealed class NestedTable
{
    private const int Rows = 1 << 20;

    private static readonly VortexSchema Schema =
    [
        ("id", VortexType.Int64),
        ("values", VortexType.List(VortexType.Int64)),
        ("labels", VortexType.List(VortexType.Utf8).Nullable),
    ];

    private readonly long[] _ids = new long[Rows];
    private readonly long[] _elements;
    private readonly int[] _starts = new int[Rows + 1];
    private readonly string[] _labels;
    private readonly int[] _labelStarts = new int[Rows + 1];
    private readonly bool[] _nullLabels = new bool[Rows];

    internal NestedTable()
    {
        Random random = new(42);
        string[] distinct = new string[100];
        for (int i = 0; i < distinct.Length; i++)
        {
            distinct[i] = $"label-{i:D3}";
        }

        int[] sizes = new int[Rows];
        int[] labelSizes = new int[Rows];
        int total = 0;
        int labelTotal = 0;
        for (int i = 0; i < Rows; i++)
        {
            _ids[i] = i;
            sizes[i] = random.Next(8);
            total += sizes[i];
            _nullLabels[i] = random.Next(5) == 0;
            labelSizes[i] = _nullLabels[i] ? 0 : random.Next(5);
            labelTotal += labelSizes[i];
        }

        _elements = new long[total];
        _labels = new string[labelTotal];
        for (int i = 0, at = 0, labelAt = 0; i < Rows; i++)
        {
            _starts[i] = at;
            for (int k = 0; k < sizes[i]; k++)
            {
                _elements[at++] = random.Next(1_000_000);
            }

            _labelStarts[i] = labelAt;
            for (int k = 0; k < labelSizes[i]; k++)
            {
                _labels[labelAt++] = distinct[random.Next(distinct.Length)];
            }

            _starts[i + 1] = at;
            _labelStarts[i + 1] = labelAt;
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

        ParquetCompression compression = format == "parquet-none" ? ParquetCompression.Uncompressed : ParquetCompression.Zstd;
        await using ParquetFileWriter parquet = VortexSession.Default.CreateParquetWriter(pipe, Schema, new ParquetWriteOptions { Compression = compression });
        Fill(parquet.Builder());
        await parquet.WriteAsync(parquet.Builder(), CancellationToken.None).ConfigureAwait(false);
        await parquet.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void Fill(ColumnsBuilder builder)
    {
        builder.Column<long>(0).Append(_ids);
        ColumnBuilder<ReadOnlyMemory<long>> values = builder.Column<ReadOnlyMemory<long>>(1);
        ColumnBuilder<ReadOnlyMemory<string>> labels = builder.Column<ReadOnlyMemory<string>>(2);
        for (int i = 0; i < Rows; i++)
        {
            values.Append(_elements.AsSpan(_starts[i], _starts[i + 1] - _starts[i]));
            if (_nullLabels[i])
            {
                labels.AppendNull();
            }
            else
            {
                labels.Append(_labels.AsSpan(_labelStarts[i], _labelStarts[i + 1] - _labelStarts[i]));
            }
        }
    }
}
