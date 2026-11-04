using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What counting the paths of log records costs a row (<c>--micro censusrate</c>): <c>--rows</c> records of
/// <see cref="LogGeneratorV2"/>, in time order as an append takes them, written once and read back as batches, then counted
/// as an append's writer counts them, its groups a granule's rows; the best of <c>--rounds</c>, after a second of warming.
/// Then what an append of 65 536 of them pays once its object completes: its directory made and written.
/// </summary>
internal static class CensusRateMicro
{
    internal static async Task<int> Run(long rows, int rounds)
    {
        LogGeneratorV2 generator = new LogGeneratorV2(3_000);
        DateTimeOffset start = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        LogRow[] records = new LogRow[rows];
        for (long row = 0; row < rows; row++)
        {
            records[row] = generator.Next(start.AddTicks(row * TimeSpan.TicksPerSecond / 1_000));
        }

        string path = Path.Combine(Path.GetTempPath(), $"vortex-censusrate-{Guid.NewGuid():N}.vortex");
        List<RecordBatch> batches = [];
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<LogRow>(path))
            {
                await writer.WriteAsync<LogRow>(records);
                await writer.CompleteAsync();
            }

            await using VortexFile file = await VortexFile.OpenAsync(path);
            await foreach (RecordBatch batch in file.Scan<LogRow>().ToBatchesAsync())
            {
                batches.Add(batch);
            }

            int field = file.DType.IndexOfField(nameof(LogRow.Attributes));
            int groupRows = VortexWriteOptions.Default.BlockRows;

            // The rows whose metadata is not the row's before: a run of rows of one metadata reads its names once.
            long switches = 0;
            byte[] last = [];
            HashSet<string> distinct = [];
            foreach (RecordBatch batch in batches)
            {
                PathCensus.Counting ready = PathCensus.Prepare(batch, field);
                Arrays.CanonicalNode metadata = batch.Arena.GetNode(ready.Metadata);
                for (int row = 0; row < ready.Rows; row++)
                {
                    ReadOnlySpan<byte> bytes = Compute.LiteralReader.ViewAt(metadata, row);
                    if (!bytes.SequenceEqual(last))
                    {
                        switches++;
                        last = bytes.ToArray();
                        distinct.Add(Convert.ToBase64String(last));
                    }
                }
            }

            double Count()
            {
                long begin = Stopwatch.GetTimestamp();
                PathCensus census = new PathCensus(VortexWriteOptions.Default.StringBoundBytes, groupRows);
                foreach (RecordBatch batch in batches)
                {
                    census.Add(batch, field);
                }

                double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
                census.Release();
                return seconds;
            }

            long warm = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(warm).TotalSeconds < 1)
            {
                Count();
            }

            double best = double.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                double seconds = Count();
                best = Math.Min(best, seconds);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"round {round}: {seconds * 1e9 / rows:F1} ns a row"));
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{rows:N0} log records, {switches:N0} rows of a metadata other than the row's before, {distinct.Count:N0} metadata: best of {rounds}, {best * 1e9 / rows:F1} ns a row"));

            // What an append of 65 536 rows then pays once, as its object completes: the directory made of its census, and written.
            PathCensus append = new PathCensus(VortexWriteOptions.Default.StringBoundBytes, groupRows);
            int counted = 0;
            foreach (RecordBatch batch in batches)
            {
                if (counted >= 65_536)
                {
                    break;
                }

                append.Add(batch, field);
                counted += batch.RowCount;
            }

            double made = double.MaxValue;
            int written = 0;
            for (int round = 0; round < Math.Max(rounds, 20); round++)
            {
                long begin = Stopwatch.GetTimestamp();
                written = Vorticity.Indexes.VariantDirectory.From(append, null, groupRows).ToBytes().Length;
                made = Math.Min(made, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            }

            append.Release();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"the directory of {counted:N0} rows: made and written in {made:F2} ms, {written:N0} bytes"));
        }
        finally
        {
            foreach (RecordBatch batch in batches)
            {
                batch.Dispose();
            }

            System.IO.File.Delete(path);
        }

        return 0;
    }
}
