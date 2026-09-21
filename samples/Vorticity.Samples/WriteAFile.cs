using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class WriteAFile
{
    private const int Rows = 1_000_000;

    internal static async Task RunAsync()
    {
        string path = Demo.Path("write-a-file.vortex");
        byte[][] cities = Array.ConvertAll(Demo.Cities, Encoding.UTF8.GetBytes);
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        Stopwatch clock = Stopwatch.StartNew();
        WriteReport report;

        await using (VortexFileWriter writer = session.CreateWriter<Reading>(path))
        {
            ColumnsBuilder<Reading> b = writer.Builder<Reading>();
            double[] temperatures = new double[writer.BlockRows];
            ulong[] validity = new ulong[writer.BlockRows / 64];

            for (int start = 0; start < Rows; start += writer.BlockRows)
            {
                int count = Math.Min(writer.BlockRows, Rows - start);

                Span<int> day = b.Day.GetSpan(count);
                for (int i = 0; i < day.Length; i++) day[i] = (start + i) / 1_000;
                b.Day.Advance(day.Length);

                Temperatures(start, temperatures.AsSpan(0, count), validity);
                b.Celsius.Append(temperatures.AsSpan(0, count), validity);

                for (int i = 0; i < count; i++) b.City.Append(cities[(start + i) / 7 % cities.Length]);

                await writer.WriteAsync(b, ct);
                if (writer.UnflushedBytes > 8 << 20) await writer.FlushAsync(ct);
            }

            report = await writer.CompleteAsync(ct);
        }

        clock.Stop();
        Console.WriteLine($"{report.RowCount} rows in blocks of {report.BlockRows}, {report.ChunkRows.Length} chunks, " +
            $"{report.Bytes.Total} bytes, in {clock.ElapsedMilliseconds} ms");
        Console.WriteLine($"  {WriteReportText.Bytes(report.Bytes)}");
        Console.WriteLine($"  chunk rows: {WriteReportText.Chunks(report.ChunkRows)}");
        foreach (ColumnWriteReport column in report.Columns)
        {
            Console.WriteLine($"  {column.Path}: {WriteReportText.Encodings(column.Encodings)}");
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);
        Console.WriteLine($"read back: {file.RowCount} rows, {new FileInfo(path).Length} bytes on disk, mean {mean:F4} °C");
    }

    /// <summary>A block's temperatures and their validity: one row in fifty has none.</summary>
    private static void Temperatures(int start, Span<double> values, Span<ulong> validity)
    {
        validity.Fill(ulong.MaxValue);
        for (int i = 0; i < values.Length; i++)
        {
            int row = start + i;
            if (row % 50 == 0)
            {
                values[i] = 0;
                validity[i >> 6] &= ~(1UL << i);
            }
            else
            {
                values[i] = 10.0 + (row % 400 / 10.0);
            }
        }
    }
}
