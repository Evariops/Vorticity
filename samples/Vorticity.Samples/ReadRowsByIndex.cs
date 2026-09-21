using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Samples;

internal static class ReadRowsByIndex
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using VortexFile file = await VortexFile.OpenAsync(path);
        await foreach (RecordBatch batch in file.Scan().Take([4L, 900_000L]).ExecuteAsync())
        {
            // One batch per row taken, and StartRow is the block it came out of, not the row.
            using (batch)
            {
                Console.WriteLine($"{batch.RowCount} row from block start {batch.StartRow}: " +
                    $"day {batch.Column("day"u8).AsPrimitive<int>()[0]}");
            }
        }

        await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(1_000, 1_010)).ExecuteAsync())
        {
            using (batch)
            {
                Console.WriteLine($"rows {batch.StartRow} to {batch.StartRow + batch.RowCount - 1}");
            }
        }

        (long takeRequests, long takeBytes, long taken) =
            await Demo.MeasureAsync(path, f => f.Scan().Take([4L, 900_000L]));
        (long wholeRequests, long wholeBytes, long all) = await Demo.MeasureAsync(path, f => f.Scan());
        Console.WriteLine($"{taken} rows taken: {takeRequests} rounds, {takeBytes} bytes");
        Console.WriteLine($"{all} rows scanned: {wholeRequests} rounds, {wholeBytes} bytes");

        // Three indexes asked for, out of order and with a repeat: two rows, in the file's order.
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take([900_000L, 4L, 4L]).ExecuteAsync())
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        Console.WriteLine($"Take([900000, 4, 4]) delivered {rows} rows");

        await foreach (RecordBatch batch in file.Scan().Project(["celsius"]).Take([4L, 900_000L]).ExecuteAsync())
        {
            using (batch)
            {
                Console.WriteLine($"celsius {batch.Column("celsius"u8).AsPrimitive<double>()[0]}");
            }
        }

        try
        {
            await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(0, 1_000)).Take([4L]).ExecuteAsync())
            {
                batch.Dispose();
            }
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"a range and a list together: {e.Message}");
        }
    }
}
