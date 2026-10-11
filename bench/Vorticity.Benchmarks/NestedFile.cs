// The nested file of the Parquet benchmark page (docs/guide/benchmarks-parquet-x64.md).
//
// Two million events of 22 fields, six of them lists, written by this package's writer under
// SNAPPY, its other options the defaults: the file whose fields cannot take the field pipeline,
// which only flat fields take, and whose `Note`, short or long text that seldom repeats, the
// writer stores as DELTA_BYTE_ARRAY once its dictionary gives up. Its values are drawn from one
// seed, so that the file is the same on every machine.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Parquet;

namespace Vorticity.Benchmarks;

/// <summary>Writes the page's nested file.</summary>
internal static partial class NestedFile
{
    /// <summary>An event: scalars, text of few and of many values, and lists of numbers and of text.</summary>
    [VortexRecord]
    internal partial record struct Event(
        long Id, int Day, double Amount, string Country, string Page, long User, int[] Tags, string[] Labels,
        double Score, int Status, long Session, int[] Codes, string Agent, double Latency, int Region,
        long[] Ids, string Referrer, int Kind, double[] Weights, string Note, int[] Flags, long Stamp);

    internal static async Task<int> RunAsync(string[] args)
    {
        int at = Array.IndexOf(args, "--nested-file") + 1;
        if (at <= 0 || at >= args.Length)
        {
            Console.Error.WriteLine("usage: --nested-file <path.parquet> [--rows N]");
            return 2;
        }

        int rowsAt = Array.IndexOf(args, "--rows");
        int rows = rowsAt >= 0 && rowsAt + 1 < args.Length && int.TryParse(args[rowsAt + 1], out int given) && given > 0 ? given : 2_000_000;
        Random random = new(7);
        string[] countries = ["fr", "de", "us", "gb", "es", "it", "jp", "br"];
        Event[] events = new Event[rows];
        for (int i = 0; i < rows; i++)
        {
            events[i] = new Event(
                i, i / 20_000, Math.Round(random.NextDouble() * 1000, 2), countries[random.Next(countries.Length)],
                $"https://example.com/{random.Next(5_000)}/page", random.Next(200_000),
                [.. Enumerable.Range(0, random.Next(5)).Select(_ => random.Next(1_000))],
                [.. Enumerable.Range(0, random.Next(3)).Select(_ => $"label-{random.Next(50)}")],
                random.NextDouble(), random.Next(6), random.Next(1_000_000),
                [.. Enumerable.Range(0, random.Next(4)).Select(_ => random.Next(64))],
                $"agent/{random.Next(300)}.{random.Next(10)}", random.NextDouble() * 200, random.Next(400),
                [.. Enumerable.Range(0, random.Next(3)).Select(_ => (long)random.Next())],
                random.Next(4) == 0 ? "" : $"https://ref.example.org/{random.Next(20_000)}", random.Next(12),
                [.. Enumerable.Range(0, random.Next(3)).Select(_ => random.NextDouble())],
                random.Next(3) == 0 ? "short" : $"a note of some length {random.Next()}",
                [.. Enumerable.Range(0, random.Next(6)).Select(_ => random.Next(2))],
                1_700_000_000_000L + (i * 37L));
        }

        ParquetWriteOptions options = new() { Compression = ParquetCompression.Snappy };
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter<Event>(args[at], options))
        {
            await writer.WriteAsync<Event>(events, CancellationToken.None).ConfigureAwait(false);
            await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Console.Out.WriteLine($"wrote {rows:N0} events to {args[at]}");
        return 0;
    }
}
