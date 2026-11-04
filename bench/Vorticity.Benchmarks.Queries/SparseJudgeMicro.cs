using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What judging a pushed predicate costs a sparse chunk against a dense one (<c>--micro sparsejudge</c>), by the share of
/// its rows that are present: a column of integers under 2^20, nulls at random places, written sparse and bit-packed; an
/// equality inside the values' range, which no zone map refutes and which keeps a row or two, so that what is timed is the
/// judgment and the read of the column. The sparse chunk judges its fill once and its present values alone (spec N, 2.2), so that
/// its time a row follows the share present; the dense one judges every row. The best of the rounds, from a file the page
/// cache holds.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SparseJudgeMicro
{
    private static readonly double[] PresentShares = [0.5, 0.2, 0.05, 0.01, 0.001];

    internal static async Task<int> Run(int rows, int rounds)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-sparsejudge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            Console.WriteLine($"{rows} rows a column, values present at random places; the time an equality that keeps a few takes, a row, the best of {rounds}");
            Console.WriteLine($"{"present",8} {"written",-8} {"bytes/row",10} {"ns/row",8}");
            foreach (double share in PresentShares)
            {
                Random random = new Random(31);
                SparseLong[] values = new SparseLong[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = new SparseLong(random.NextDouble() < share ? random.NextInt64(1L << 20) : null);
                }

                foreach ((string written, EncodingHint hint) in new[] { ("sparse", EncodingHint.Sparse), ("dense", EncodingHint.BitPacked) })
                {
                    await ReportAsync(scratch, share, written, values, hint, rows, rounds);
                }
            }

            return 0;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task ReportAsync(string scratch, double share, string written, SparseLong[] rows, EncodingHint hint, int count, int rounds)
    {
        string path = Path.Combine(scratch, $"{share}-{written}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<SparseLong>(
            path, new VortexWriteOptions { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(nameof(SparseLong.Value), hint) }))
        {
            await writer.WriteAsync<SparseLong>(rows);
            await writer.CompleteAsync();
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        long bytes = 0;
        foreach (Serialization.Schemas.SegmentSpec spec in file.SegmentSpecs)
        {
            bytes += spec.Length;
        }

        double best = double.MaxValue;
        for (int round = 0; round <= rounds; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            int kept = (await file.Scan<SparseLong>().Where(r => r.Value == 777_777).Select(r => r.Value).ToListAsync()).Count;
            if (round > 0)
            {
                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            if (kept > 16)
            {
                throw new InvalidOperationException($"{kept} rows kept of a few");
            }
        }

        Console.WriteLine($"{share,8:P1} {written,-8} {(double)bytes / count,10:F3} {best * 1e6 / count,8:F3}");
    }
}
