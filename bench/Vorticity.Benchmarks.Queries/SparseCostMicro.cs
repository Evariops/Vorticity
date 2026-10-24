using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a sparse chunk costs against a dense one (<c>--micro sparsecost</c>), by the share of its rows that are null:
/// a column of integers under 2^20, of doubles and of short texts, nulls at random places, written sparse, dense
/// (bit-packed integers, canonical doubles and texts) and as the writer chooses. For each, the bytes of the column's
/// segments and the time a scan takes to decode it into batches, a row, the best of the rounds, from a file the
/// page cache holds.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SparseCostMicro
{
    private static readonly double[] NullShares = [0.5, 0.75, 0.8, 0.85, 0.9, 0.95, 0.99];

    internal static async Task<int> Run(int rows, int rounds)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-sparsecost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            Console.WriteLine($"{rows} rows a column, nulls at random places; decode time is the best of {rounds} scans into batches");
            Console.WriteLine($"{"column",-8} {"nulls",6} {"written",-8} {"bytes/row",10} {"ns/row",8}");
            foreach (double share in NullShares)
            {
                Random random = new Random(29);
                bool[] present = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    present[i] = random.NextDouble() >= share;
                }

                SparseLong[] longs = new SparseLong[rows];
                SparseReal[] reals = new SparseReal[rows];
                SparseText[] texts = new SparseText[rows];
                for (int i = 0; i < rows; i++)
                {
                    longs[i] = new SparseLong(present[i] ? random.NextInt64(1L << 20) : null);
                    reals[i] = new SparseReal(present[i] ? random.NextDouble() * 1_000 : null);
                    texts[i] = new SparseText(present[i] ? $"value-{random.Next(1 << 20):x5}" : null);
                }

                foreach ((string written, EncodingHint hint) in new[] { ("auto", EncodingHint.Auto), ("sparse", EncodingHint.Sparse), ("dense", EncodingHint.BitPacked) })
                {
                    await ReportAsync(scratch, "long", share, written, longs, hint, rows, rounds);
                }

                foreach ((string written, EncodingHint hint) in new[] { ("auto", EncodingHint.Auto), ("sparse", EncodingHint.Sparse), ("dense", EncodingHint.Canonical) })
                {
                    await ReportAsync(scratch, "double", share, written, reals, hint, rows, rounds);
                }

                foreach ((string written, EncodingHint hint) in new[] { ("auto", EncodingHint.Auto), ("sparse", EncodingHint.Sparse), ("dense", EncodingHint.Canonical) })
                {
                    await ReportAsync(scratch, "text", share, written, texts, hint, rows, rounds);
                }
            }

            return 0;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task ReportAsync<T>(string scratch, string column, double share, string written, T[] rows, EncodingHint hint, int count, int rounds)
        where T : IVortexRecord<T>
    {
        string path = Path.Combine(scratch, $"{column}-{share}-{written}.vortex");
        VortexWriteOptions options = hint == EncodingHint.Auto
            ? new VortexWriteOptions()
            : new VortexWriteOptions { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("Value", hint) };
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(path, options))
        {
            await writer.WriteAsync<T>(rows);
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
            long seen = 0;
            await foreach (RecordBatch batch in file.Scan<T>().ToBatchesAsync())
            {
                seen += batch.RowCount;
                batch.Dispose();
            }

            if (round > 0)
            {
                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            if (seen != count)
            {
                throw new InvalidOperationException($"{seen} rows read of {count}");
            }
        }

        Console.WriteLine($"{column,-8} {share,6:P0} {written,-8} {(double)bytes / count,10:F3} {best * 1e6 / count,8:F2}");
    }
}

/// <summary>A nullable integer, the sparse cost micro's column.</summary>
[VortexRecord]
public partial record struct SparseLong(long? Value);

/// <summary>A nullable double, the sparse cost micro's column.</summary>
[VortexRecord]
public partial record struct SparseReal(double? Value);

/// <summary>A nullable text, the sparse cost micro's column.</summary>
[VortexRecord]
public partial record struct SparseText(string? Value);
