// What a write costs on one thread and on eight, with one column and with eight: 1,048,576 log lines of 60 to 120
// characters, words drawn among 2,000 and numbers, written to a file. One column is written on its caller's thread at
// any degree; eight columns are ingested and compressed side by side, a column a thread, the caller awaiting the helpers
// rather than blocking for them. Measured with --warmupCount 15 --iterationCount 15 (2026-11-11, against ff8737dc, three
// rounds): 1 lane 436 / 437 ms (base / head) for one column, 496 / 498 for eight; 8 lanes 434 / 436 for one column,
// 321 / 319 for eight.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A file of log lines written at one thread and at eight, of one column and of eight.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditWriteLanesBenchmarks
{
    private const int Rows = 1 << 20;

    private Lines[] _rows = [];
    private Line[] _single = [];
    private string _path = string.Empty;

    /// <summary>The writer's threads.</summary>
    [Params(1, 8)]
    public int Lanes { get; set; }

    /// <summary>The file's columns.</summary>
    [Params(1, 8)]
    public int Columns { get; set; }

    /// <summary>A log line alone.</summary>
    [VortexRecord]
    public partial record struct Line(string Message);

    /// <summary>Eight log lines a row.</summary>
    [VortexRecord]
    public partial record struct Lines(string A, string B, string C, string D, string E, string F, string G, string H);

    /// <summary>Draws the lines.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(61);
        string[] words = [.. Enumerable.Range(0, 2_000).Select(i => new string((char)('a' + (i % 26)), 1) + Convert.ToHexStringLower(BitConverter.GetBytes(i * 2654435761U))[..(3 + (i % 6))])];
        StringBuilder text = new StringBuilder();
        string Next()
        {
            text.Clear();
            int length = random.Next(60, 120);
            while (text.Length < length)
            {
                text.Append(random.Next(5) == 0 ? random.Next(100_000).ToString(System.Globalization.CultureInfo.InvariantCulture) : words[random.Next(words.Length)]).Append(' ');
            }

            return text.ToString();
        }

        if (Columns == 1)
        {
            _single = new Line[Rows];
            for (int i = 0; i < Rows; i++)
            {
                _single[i] = new Line(Next());
            }
        }
        else
        {
            _rows = new Lines[Rows / 8];
            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i] = new Lines(Next(), Next(), Next(), Next(), Next(), Next(), Next(), Next());
            }
        }

        _path = Path.Combine(Path.GetTempPath(), $"write-lanes-{Guid.NewGuid():N}.vortex");
        Console.WriteLine($"// {Write()} bytes");
    }

    /// <summary>Removes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    /// <summary>The file's bytes.</summary>
    [Benchmark]
    public long Write() => WriteAsync().GetAwaiter().GetResult();

    private async Task<long> WriteAsync()
    {
        VortexWriteOptions options = new VortexWriteOptions { DegreeOfParallelism = Lanes };
        if (Columns == 1)
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(_path, options);
            await writer.WriteAsync<Line>(_single, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Lines>(_path, options);
            await writer.WriteAsync<Lines>(_rows, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new FileInfo(_path).Length;
    }
}
