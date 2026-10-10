using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Samples;

internal static class Limits
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"depth: array {VortexLimits.MaxArrayDepth}, dtype {VortexLimits.MaxDTypeDepth}, layout {VortexLimits.MaxLayoutDepth}, " +
            $"flatbuffer {VortexLimits.MaxFlatBufferDepth}; flatbuffer tables {VortexLimits.MaxFlatBufferTables}");
        Console.WriteLine($"metadata segments {VortexLimits.MaxMetadataSegments}, key length {VortexLimits.MaxMetadataKeyLength}, " +
            $"compression specs {VortexLimits.MaxCompressionSpecs}, alignment {VortexLimits.MaxAlignment} (exponent {VortexLimits.MaxAlignmentExponent}), " +
            $"postscript {VortexLimits.MaxPostscriptSize}");
        Console.WriteLine($"decompression ceiling by default: {VortexLimits.DefaultMaxDecompressedBytes} bytes per decode");

        string path = await Demo.ReadingsAsync();
        foreach (long ceiling in new long[] { 4_096, 1024 * 1024, VortexLimits.DefaultMaxDecompressedBytes })
        {
            await using VortexFile file = await VortexSession.Default.OpenAsync(path, new VortexOpenOptions
            {
                MaxDecompressedBytes = ceiling,
                VerifyStatistics = true,
            });

            try
            {
                long rows = 0;
                await foreach (Columns<Reading> batch in file.Scan<Reading>())
                {
                    rows += batch.RowCount;
                }

                Console.WriteLine($"ceiling {ceiling}: {rows} rows");
            }
            catch (VortexFormatException e)
            {
                Console.WriteLine($"ceiling {ceiling}: {e.GetType().Name}: {e.Message}");
            }
        }

        await UnknownComponentAsync(path);
        await HostileTailsAsync();
    }

    private static async Task UnknownComponentAsync(string path)
    {
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
        byte[] id = Encoding.UTF8.GetBytes("vortex.alp");
        int at = bytes.AsSpan().LastIndexOf(id);
        bytes[at + id.Length - 1] = (byte)'z';
        string patched = Demo.Path("unknown-encoding.vortex");
        await System.IO.File.WriteAllBytesAsync(patched, bytes);

        await using VortexFile file = await VortexSession.Default.OpenAsync(patched);
        string unsupported = string.Join(", ", file.ArrayEncodings.Where(c => !c.Supported).Select(c => c.Id));
        long rows = 0;
        await foreach (BatchView batch in file.Scan("Day", "City"))
        {
            rows += batch.RowCount;
        }

        try
        {
            await foreach (Columns<Reading> batch in file.Scan<Reading>())
            {
                _ = batch.RowCount;
            }
        }
        catch (VortexUnsupportedException e)
        {
            Console.WriteLine($"opened, not supported: {unsupported}; Day and City read {rows} rows; Scan<Reading> throws {e.Kind} {e.ComponentId}");
        }
    }

    private static async Task HostileTailsAsync()
    {
        string path = Demo.Path("small.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            Reading[] rows = new Reading[20_000];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new Reading(i / 100, i % 50 == 0 ? null : i % 400 / 10.0, Demo.Cities[i % Demo.Cities.Length]);
            }

            await writer.WriteAsync<Reading>(rows);
            await writer.CompleteAsync();
        }

        byte[] original = await System.IO.File.ReadAllBytesAsync(path);
        Random random = new Random(42);
        SortedDictionary<string, int> outcomes = new(StringComparer.Ordinal);
        const int Trials = 1_000;
        for (int trial = 0; trial < Trials; trial++)
        {
            byte[] bytes = (byte[])original.Clone();
            int tail = Math.Min(bytes.Length, 4_096);
            for (int flip = 0; flip < 4; flip++)
            {
                bytes[bytes.Length - 1 - random.Next(tail)] ^= (byte)(1 << random.Next(8));
            }

            string outcome;
            try
            {
                await using VortexFile file = await VortexSession.Default.OpenAsync(new MemorySegmentSource(bytes));
                long rows = 0;
                await foreach (BatchView batch in file.Scan())
                {
                    rows += batch.RowCount;
                }

                outcome = "read whole";
            }
            catch (Exception e)
            {
                outcome = e.GetType().Name;
            }

            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        Console.WriteLine($"{Trials} copies of a {original.Length}-byte file, four bits flipped in the last 4 KiB of each:");
        foreach ((string outcome, int count) in outcomes)
        {
            Console.WriteLine($"  {outcome}: {count}");
        }
    }
}
