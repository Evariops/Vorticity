using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class WriterOptions
{
    private const int Rows = 1_000_000;

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("writer-options.vortex");
        Reading[] readings = Readings(Rows, sortedCities: false);

        VortexWriteOptions options = new()
        {
            Compression = CompressionProfile.Auto,                     // prices size and decode speed together
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(Reading.ColumnNames.Celsius, EncodingHint.Zstd),
            StringBoundBytes = 32,
            Metadata = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("producer", "acme/1.4"u8.ToArray()),
            Identity = Guid.Parse("0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f"),   // a reproducible write: the same input gives the same bytes
        };

        WriteReport report;
        await using (VortexFileWriter writer = session.CreateWriter<Reading>(path, options))
        {
            await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
            report = await writer.CompleteAsync(ct);
        }

        foreach (ColumnWriteReport column in report.Columns) Console.WriteLine($"{column.Path}: {WriteReportText.Encodings(column.Encodings)}");

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            ReadOnlyMemory<byte> producer = await file.Metadata.ReadAsync("producer", ct);
            Console.WriteLine($"metadata keys {string.Join(", ", file.Metadata.Keys)}; producer = {Encoding.UTF8.GetString(producer.Span)}");
            Console.WriteLine($"identity {file.Identity}, edition {VortexEditions.Name(file.Edition)}, {file.Length} bytes");
        }

        byte[] first = await System.IO.File.ReadAllBytesAsync(path, ct);
        await Write(session, path, readings, options);
        byte[] second = await System.IO.File.ReadAllBytesAsync(path, ct);
        await Write(session, path, readings, options with { Identity = null });
        byte[] third = await System.IO.File.ReadAllBytesAsync(path, ct);
        Console.WriteLine($"written again with the same identity: {(first.AsSpan().SequenceEqual(second) ? "the same bytes" : "different bytes")}; " +
            $"without one: {(first.AsSpan().SequenceEqual(third) ? "the same bytes" : "different bytes")}");

        Console.WriteLine();
        await Profiles(session, path, readings);

        Console.WriteLine();
        await Hints(session, path, readings);

        Console.WriteLine();
        await StringBounds(session, path);

        Console.WriteLine();
        await Rest(session, path, readings);
    }

    /// <summary>The four compression profiles on the same million rows: bytes, write time, and a full decode.</summary>
    private static async Task Profiles(VortexSession session, string path, Reading[] readings)
    {
        foreach (CompressionProfile profile in (CompressionProfile[])[CompressionProfile.Auto, CompressionProfile.Fastest, CompressionProfile.Smallest, CompressionProfile.None])
        {
            VortexWriteOptions options = new() { Compression = profile };
            long write = long.MaxValue;
            WriteReport? report = null;
            for (int round = 0; round < 3; round++)
            {
                Stopwatch clock = Stopwatch.StartNew();
                report = await Write(session, path, readings, options);
                write = Math.Min(write, clock.ElapsedMilliseconds);
            }

            long read = long.MaxValue;
            await using (VortexFile file = await VortexFile.OpenAsync(path))
            {
                for (int round = 0; round < 3; round++)
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    await foreach (var (day, celsius, city) in file.Scan<Reading>())
                    {
                        day.Canonical();
                        celsius.Canonical();
                        city.Canonical();
                    }

                    read = Math.Min(read, clock.ElapsedMilliseconds);
                }
            }

            Console.WriteLine($"{profile}: {report!.Bytes.Total} bytes, written in {write} ms, decoded in {read} ms; " +
                $"Day {WriteReportText.Encodings(report.Columns[0].Encodings)}; Celsius {WriteReportText.Encodings(report.Columns[1].Encodings)}; " +
                $"City {WriteReportText.Encodings(report.Columns[2].Encodings)}");
        }
    }

    /// <summary>One hint at a time, the other columns left to the chooser.</summary>
    private static async Task Hints(VortexSession session, string path, Reading[] readings)
    {
        (string Column, EncodingHint Hint)[] hints =
        [
            (Reading.ColumnNames.City, EncodingHint.Auto),
            (Reading.ColumnNames.City, EncodingHint.Dictionary),
            (Reading.ColumnNames.City, EncodingHint.Zstd),
            (Reading.ColumnNames.City, EncodingHint.Fsst),
            (Reading.ColumnNames.City, EncodingHint.Canonical),
            (Reading.ColumnNames.Celsius, EncodingHint.Alp),
            (Reading.ColumnNames.Celsius, EncodingHint.Zstd),
            (Reading.ColumnNames.Celsius, EncodingHint.BitPacked),
            (Reading.ColumnNames.Celsius, EncodingHint.Canonical),
        ];

        foreach ((string column, EncodingHint hint) in hints)
        {
            VortexWriteOptions options = new() { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(column, hint) };
            WriteReport report = await Write(session, path, readings, options);
            int index = column == Reading.ColumnNames.City ? 2 : 1;
            Console.WriteLine($"{column} as {hint}: {report.Bytes.Total} bytes, written as {WriteReportText.Encodings(report.Columns[index].Encodings)}");
        }

        VortexWriteOptions smallest = new()
        {
            Compression = CompressionProfile.Smallest,
            Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(Reading.ColumnNames.Celsius, EncodingHint.Canonical),
        };
        WriteReport sizeFirst = await Write(session, path, readings, smallest);
        Console.WriteLine($"Celsius as Canonical under Smallest: {sizeFirst.Bytes.Total} bytes, written as {WriteReportText.Encodings(sizeFirst.Columns[1].Encodings)}");

        try
        {
            await using VortexFileWriter accepted = session.CreateWriter<Reading>(path, new VortexWriteOptions { Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("Town", EncodingHint.Zstd) });
            Console.WriteLine("a hint on a column that does not exist: accepted");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"a hint on a column that does not exist: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>What string bounds cost, and what they prune on a column whose values come in runs.</summary>
    private static async Task StringBounds(VortexSession session, string path)
    {
        Reading[] sorted = Readings(Rows, sortedCities: true);
        foreach (int bound in (int[])[0, 16, 32])
        {
            WriteReport report = await Write(session, path, sorted, new VortexWriteOptions { StringBoundBytes = bound });
            await using VortexFile file = await VortexFile.OpenAsync(path);
            ScanPlan plan = await file.Scan<Reading>().Where(r => r.City == "Nice").ExplainAsync();
            Console.WriteLine($"StringBoundBytes {bound}: {report.Bytes.Total} bytes, zone maps {report.Bytes.ZoneMaps}; City == \"Nice\" reads {plan.LiveBlocks} of {plan.Blocks} blocks");
        }
    }

    /// <summary>Statistics off, an edition, the metadata limits.</summary>
    private static async Task Rest(VortexSession session, string path, Reading[] readings)
    {
        WriteReport with = await Write(session, path, readings, new VortexWriteOptions());
        WriteReport without = await Write(session, path, readings, new VortexWriteOptions { Statistics = false });
        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day > 2_000).ExplainAsync();
            Console.WriteLine($"Statistics = false: {without.Bytes.Total} bytes against {with.Bytes.Total}; the file has statistics for {file.Statistics.Count} columns; " +
                $"Day > 2000 may match: {plan.MayMatch}, reads {plan.LiveBlocks} of {plan.Blocks} blocks");
        }

        WriteReport old = await Write(session, path, readings, new VortexWriteOptions { TargetEdition = VortexEdition.Core20250500 });
        Console.WriteLine($"TargetEdition core2025.05.0: {old.Bytes.Total} bytes, zone maps {old.Bytes.ZoneMaps}");

        try
        {
            ImmutableDictionary<string, ReadOnlyMemory<byte>> metadata = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
            for (int i = 0; i < 15; i++) metadata = metadata.Add($"key{i}", new byte[] { 1 });
            await using VortexFileWriter accepted = session.CreateWriter<Reading>(path, new VortexWriteOptions { Metadata = metadata });
            Console.WriteLine("fifteen metadata entries: accepted");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"fifteen metadata entries: {e.GetType().Name}: {e.Message}");
        }
    }

    private static async Task<WriteReport> Write(VortexSession session, string path, Reading[] readings, VortexWriteOptions options)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path, options);
        await writer.WriteAsync<Reading>(readings.AsSpan());
        return await writer.CompleteAsync();
    }

    private static Reading[] Readings(int count, bool sortedCities)
    {
        Reading[] readings = new Reading[count];
        for (int row = 0; row < count; row++)
        {
            string city = sortedCities ? Demo.Cities[row / (count / Demo.Cities.Length)] : Demo.Cities[row / 7 % Demo.Cities.Length];
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), city);
        }

        return readings;
    }
}
