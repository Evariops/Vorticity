using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static partial class CopyAFile
{
    /// <summary>Two columns of the readings: the projection a copy keeps.</summary>
    [VortexRecord]
    public partial record struct DayAndCelsius(int Day, double? Celsius);

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string input = await Demo.ReadingsAsync();
        string output = Demo.Path("copy.vortex");

        await using VortexFile source = await session.OpenAsync(input);
        Console.WriteLine($"source: {source.RowCount} rows, {source.Length} bytes");

        Stopwatch clock = Stopwatch.StartNew();
        WriteReport report;
        await using (VortexFileWriter target = session.CreateWriter(output, source.Schema))
        {
            await foreach (BatchView batch in source.Scan()) await target.WriteAsync(batch, ct);   // the columns decode once and encode once
            report = await target.CompleteAsync(ct);
        }

        Console.WriteLine($"a copy: {report.RowCount} rows, {report.Bytes.Total} bytes, {report.ChunkRows.Length} chunks, in {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await using (VortexFileWriter target = session.CreateWriter(output, source.Schema, new VortexWriteOptions { Compression = CompressionProfile.Smallest }))
        {
            await foreach (BatchView batch in source.Scan()) await target.WriteAsync(batch, ct);
            report = await target.CompleteAsync(ct);
        }

        Console.WriteLine($"re-encoded under Smallest: {report.Bytes.Total} bytes, in {clock.ElapsedMilliseconds} ms; " +
            $"Day {WriteReportText.Encodings(report.Columns[0].Encodings)}, Celsius {WriteReportText.Encodings(report.Columns[1].Encodings)}, " +
            $"City {WriteReportText.Encodings(report.Columns[2].Encodings)}");

        clock.Restart();
        int from = 900;
        await using (VortexFileWriter target = session.CreateWriter(output, source.Schema))
        {
            await foreach (BatchView batch in source.Scan().Where($"Day >= {from}")) await target.WriteAsync(batch, ct);
            report = await target.CompleteAsync(ct);
        }

        Console.WriteLine($"a filtered copy, Day >= {from}: {report.RowCount} rows, {report.Bytes.Total} bytes, in {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await using (VortexFileWriter target = session.CreateWriter<Reading>(output))
        {
            await foreach (Columns<Reading> columns in source.Scan<Reading>().Where(r => r.Day >= from && r.City == "Paris")) await target.WriteAsync(columns, ct);
            report = await target.CompleteAsync(ct);
        }

        Console.WriteLine($"a typed filtered copy, Day >= {from} and City == Paris: {report.RowCount} rows, {report.Bytes.Total} bytes, in {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await using (VortexFileWriter target = session.CreateWriter<DayAndCelsius>(output))
        {
            await foreach (Columns<DayAndCelsius> columns in source.Scan<DayAndCelsius>()) await target.WriteAsync(columns, ct);
            report = await target.CompleteAsync(ct);
        }

        Console.WriteLine($"a projected copy, Day and Celsius: {report.RowCount} rows, {report.Bytes.Total} bytes, in {clock.ElapsedMilliseconds} ms");

        await using VortexFile copy = await session.OpenAsync(output);
        Console.WriteLine($"the projected copy reads back: {copy.Schema}, {copy.RowCount} rows, mean {await copy.Scan<DayAndCelsius>().AvgAsync(r => r.Celsius):F4}");

        try
        {
            await using VortexFileWriter target = session.CreateWriter<DayAndCelsius>(Demo.Path("copy-refused.vortex"));
            await foreach (Columns<Reading> columns in source.Scan<Reading>()) await target.WriteAsync(columns, ct);
            Console.WriteLine("three columns into a writer of two: accepted");
        }
        catch (VortexSchemaException e)
        {
            Console.WriteLine($"three columns into a writer of two: {e.Message}");
        }
    }
}
