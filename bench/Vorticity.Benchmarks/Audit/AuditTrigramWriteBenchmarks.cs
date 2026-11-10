// Audit VW-06: what a text column's trigram indexes cost a write, every trigram of every value noted. 131,072 log lines of
// 60 to 120 characters, words drawn among 2,000 and numbers, written to a file with no index, with a trigram Bloom filter,
// or with trigram postings.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A file of log lines written under three index policies.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditTrigramWriteBenchmarks
{
    private const int Rows = 131_072;

    private Line[] _rows = [];
    private string _path = string.Empty;

    /// <summary>The message's index: none, a trigram Bloom filter, or trigram postings.</summary>
    [Params("none", "ngram-bloom", "ngram-postings")]
    public string Indexes { get; set; } = "none";

    /// <summary>A log line: its message and its instant.</summary>
    [VortexRecord]
    public partial record struct Line(string Message, long At);

    /// <summary>Draws the lines.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(59);
        string[] words = [.. Enumerable.Range(0, 2_000).Select(i => new string((char)('a' + (i % 26)), 1) + Convert.ToHexStringLower(BitConverter.GetBytes(i * 2654435761U))[..(3 + (i % 6))])];
        StringBuilder text = new StringBuilder();
        _rows = new Line[Rows];
        for (int i = 0; i < Rows; i++)
        {
            text.Clear();
            int length = random.Next(60, 120);
            while (text.Length < length)
            {
                text.Append(random.Next(5) == 0 ? random.Next(100_000).ToString() : words[random.Next(words.Length)]).Append(' ');
            }

            _rows[i] = new Line(text.ToString(), i);
        }

        _path = Path.Combine(Path.GetTempPath(), $"trigram-write-{Guid.NewGuid():N}.vortex");
        Write();
    }

    /// <summary>Removes the file.</summary>
    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    /// <summary>The rows written; the file's bytes.</summary>
    [Benchmark]
    public long Write() => WriteAsync().GetAwaiter().GetResult();

    private async Task<long> WriteAsync()
    {
        IndexPolicy policy = Indexes switch
        {
            "ngram-bloom" => IndexPolicy.None.NgramBloom(nameof(Line.Message)),
            "ngram-postings" => IndexPolicy.None.ForKey([nameof(Line.Message)], IndexKind.NgramPostings),
            _ => IndexPolicy.None,
        };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Line>(_path, new VortexWriteOptions { Indexes = policy }))
        {
            await writer.WriteAsync<Line>(_rows, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new FileInfo(_path).Length;
    }
}
