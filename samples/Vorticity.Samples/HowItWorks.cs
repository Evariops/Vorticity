using System;
using System.Linq;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class HowItWorks
{
    internal static async Task RunAsync()
    {
        string path = Demo.Path("how-it-works.vortex");
        WriteReport report;
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            ColumnsBuilder<Reading> builder = writer.Builder<Reading>();
            for (int row = 0; row < Demo.ReadingRows; row++)
            {
                builder.Day.Append(row / 1_000);
                builder.Celsius.Append(row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0));
                builder.City.Append(Demo.Cities[row / 7 % Demo.Cities.Length]);
                if (builder.RowCount == writer.BlockRows)
                {
                    await writer.WriteAsync(builder);
                }
            }

            await writer.WriteAsync(builder);
            report = await writer.CompleteAsync();
        }

        Console.WriteLine($"written: {report.RowCount} rows in blocks of {report.BlockRows}, {report.ChunkRows.Length} chunks of " +
            $"{string.Join(", ", report.ChunkRows.Distinct())} rows; {report.Bytes.Total} bytes: data {report.Bytes.Data}, " +
            $"statistics {report.Bytes.Statistics}, zone maps {report.Bytes.ZoneMaps}, indexes {report.Bytes.Indexes}, footer {report.Bytes.Footer}");
        foreach (ColumnWriteReport column in report.Columns)
        {
            Console.WriteLine($"  {column.Path}: {string.Join(", ", column.Encodings.Distinct())}");
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        VortexLayout root = await file.GetLayoutAsync();
        Console.WriteLine($"opened: {file.Schema}, {file.RowCount} rows, {file.Length} bytes, {file.SegmentMap.Length} segments, edition {VortexEditions.Name(file.Edition)}");
        Console.WriteLine($"layout: {root.Encoding} of {string.Join(", ", root.Children.Select(c => $"{c.Encoding}({c.ZoneCount} zones of {c.ZoneLength})"))}");

        int calls = 0;
        Scan<Reading> scan = file.Scan<Reading>().Where(r =>
        {
            calls++;
            return r.Day >= 900;
        });
        ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync();
        long rows = 0;
        await foreach (Columns<Reading> batch in scan)
        {
            rows += batch.RowCount;
        }

        Console.WriteLine($"plan: {plan.LiveBlocks} of {plan.Blocks} blocks live, {plan.Segments} segments, {plan.BytesToRead} bytes; " +
            $"ran: {rows} rows, {scan.Statistics.BlocksDecoded} blocks decoded, {scan.Statistics.BlocksPruned} pruned; the lambda ran {calls} time");
    }
}
