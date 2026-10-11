// What sealing a local file costs a read, and how the frame size moves it.
//
// A sealed file is read positionally and decrypted, where a plain one is mapped and read in place, so
// the price has two parts: the lost mapping, which the plain file read positionally isolates, and the
// cipher, one pass of AES-256-GCM over every byte read and one call into the platform's library per
// frame. The frame size trades the second part's calls against what a small read decrypts beyond what
// it wants: a point read decrypts one or two whole frames.
//
// Four reads, on sixteen copies of a corpus file one after the other: a scan that decodes every
// column, a band of a few percent on an integer column, an open alone, and one row taken from the
// middle, open included. The files sit in the page cache, so the time is the processor's.
using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Sealing;

namespace Vorticity.Benchmarks;

/// <summary>A sealed file's scan, filter and point read against a plain one's, per frame size: <c>--explore</c>.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class SealedScanBenchmarks
{
    private const string Source = "containers/zoned_many_zones_nulls";

    private const string Field = "monotone";

    private const int Copies = 16;

    private string _directory = string.Empty;
    private string _path = string.Empty;
    private VortexKeyring? _keyring;
    private VortexSession? _session;
    private VortexFile? _file;
    private VortexExpr _band = null!;
    private long _middle;

    /// <summary>
    /// The file read: plain and mapped, plain and read positionally, or sealed with frames of
    /// 2^12 to 2^20 bytes.
    /// </summary>
    [Params("plain mapped", "plain positional", "sealed 4 KiB", "sealed 16 KiB", "sealed 64 KiB", "sealed 256 KiB", "sealed 1 MiB")]
    public string File { get; set; } = "sealed 64 KiB";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Directory.CreateTempSubdirectory("vorticity-sealed-bench-").FullName;
        string plain = Path.Combine(_directory, "plain.vortex");
        string source = Corpus.Path(Source);
        await using (VortexFile input = await VortexFile.OpenAsync(source, CancellationToken.None))
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(plain, input.Schema))
        {
            for (int copy = 0; copy < Copies; copy++)
            {
                await writer.WriteAsync(input.Scan(), CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        // A band of a fiftieth of one copy's values, which every copy holds once.
        Corpus.ColumnShape shape = Corpus.RequireIntegerColumn(source, Field, "none");
        long low = shape.First + (shape.Step * shape.Rows / 2);
        long width = shape.Step * shape.Rows / 50;
        _band = Expr.And(
            Expr.Ge(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low))),
            Expr.Lt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low + width))));
        _middle = shape.Rows * Copies / 2;

        _keyring = VortexKeyring.FromKeys(new VortexKey("bench", new byte[32]));
        _path = plain;
        _session = File switch
        {
            "plain mapped" => VortexSession.Create(_ => { }),
            "plain positional" => VortexSession.Create(o => o.MapFiles = false),
            _ => VortexSession.Create(o => o.Keyring = _keyring),
        };

        if (File.StartsWith("sealed", StringComparison.Ordinal))
        {
            _path = Path.Combine(_directory, "sealed.vortex");
            await SealAsync(plain, _path, FrameLog2(File), _keyring);
        }

        _file = await _session.OpenAsync(_path, cancellationToken: CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_file is not null)
        {
            await _file.DisposeAsync();
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        _keyring?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark(Description = "scan every column")]
    public async Task<long> Scan()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file!.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    [Benchmark(Description = "band of a few percent")]
    public async Task<long> Filter()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file!.ScanBuilder().Where(_band).ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    [Benchmark(Description = "open alone")]
    public async Task<long> Open()
    {
        await using VortexFile file = await _session!.OpenAsync(_path, cancellationToken: CancellationToken.None);
        return file.RowCount;
    }

    [Benchmark(Description = "open, one row from the middle")]
    public async Task<long> PointRead()
    {
        await using VortexFile file = await _session!.OpenAsync(_path, cancellationToken: CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Take([_middle]).ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static int FrameLog2(string file) => file switch
    {
        "sealed 4 KiB" => 12,
        "sealed 16 KiB" => 14,
        "sealed 64 KiB" => 16,
        "sealed 256 KiB" => 18,
        _ => 20,
    };

    /// <summary>Seals the plain file's bytes into a file of frames of 2^<paramref name="frameLog2"/>, under a data key of <paramref name="keyring"/>.</summary>
    private static async Task SealAsync(string plain, string sealedPath, int frameLog2, VortexKeyring keyring)
    {
        using DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(plain);
        await using FileStream target = System.IO.File.Create(sealedPath);
        PipeWriter pipe = PipeWriter.Create(target, new StreamPipeWriterOptions(leaveOpen: true));
        SealingSegmentSink sink = new SealingSegmentSink(pipe, SealParameters.ForFile(frameLog2), _ => new ValueTask<DataKey>(key.Retain()));
        try
        {
            await sink.WriteAsync(bytes, CancellationToken.None);
            await sink.FinishAsync(CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            await pipe.CompleteAsync();
        }
        finally
        {
            sink.Release();
        }
    }
}
