using System;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class ReadRowsByIndex
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        Scan<Reading> take = file.Scan<Reading>().Rows(4, 900_000);
        await foreach (Columns<Reading> cols in take)
        {
            Console.WriteLine($"a block of {cols.RowCount} rows from row {cols.StartRow}, {cols.Selection.Count} selected");
            foreach (int i in cols.Selection)
            {
                Console.WriteLine($"  row {cols.StartRow + i}: day {cols.Day[i]}, {cols.City.GetString(i)}");
            }
        }

        ScanMetrics took = take.Metrics;
        Scan<Reading> all = file.Scan<Reading>();
        await foreach (Columns<Reading> _ in all)
        {
        }

        ScanMetrics read = all.Metrics;
        Console.WriteLine($"Rows(4, 900_000): {took.Requests} requests, {took.BytesRequested} bytes, {took.BlocksDecoded} blocks decoded");
        Console.WriteLine($"the whole file:   {read.Requests} requests, {read.BytesRequested} bytes, {read.BlocksDecoded} blocks decoded");

        ScanPlan plan = await file.Scan<Reading>().Rows(4, 900_000).ExplainAsync();
        Console.WriteLine($"the plan of the take: {plan.Rows} rows, {plan.LiveBlocks} of {plan.Blocks} blocks live, {plan.Segments} segments, {plan.BytesToRead} bytes to read");
        ScanPlan range = await file.Scan<Reading>().Rows(RowRange.FromLength(1_000, 10)).ExplainAsync();
        Console.WriteLine($"the plan of a range: {range.Rows} rows, {range.LiveBlocks} of {range.Blocks} blocks live, {range.Segments} segments, {range.BytesToRead} bytes to read");

        await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(RowRange.FromLength(1_000, 10)))
        {
            Console.WriteLine($"RowRange.FromLength(1_000, 10): {cols.RowCount} rows from row {cols.StartRow}, selection all: {cols.Selection.IsAll}");
        }

        int delivered = 0;
        await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(900_000, 4, 4))
        {
            delivered += cols.Selection.Count;
        }

        Console.WriteLine($"Rows(900_000, 4, 4) delivers {delivered} rows");

        // Row r of the demo file holds 10 + (r % 400) / 10 degrees, so the temperatures say which rows survived.
        await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(4, 10, 20, 900_000).Where(r => r.Celsius > 10.0))
        {
            Console.WriteLine($"a take with a filter: {cols.RowCount} rows, StartRow {cols.StartRow}, selection all: {cols.Selection.IsAll}");
            for (int i = 0; i < cols.RowCount; i++)
            {
                Console.WriteLine($"  batch row {i}: {cols.Celsius[i]} degrees, so file row {(cols.Celsius[i] - 10.0) * 10:F0}; StartRow + {i} = {cols.StartRow + i}");
            }
        }

        ScanOptions positions = new ScanOptions { Compact = false };
        await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(4, 10, 20, 900_000).Where(r => r.Celsius > 10.0).With(positions))
        {
            Console.Write($"the same under Compact = false: {cols.RowCount} rows from row {cols.StartRow}, selected rows");
            foreach (int i in cols.Selection)
            {
                Console.Write($" {cols.StartRow + i}");
            }

            Console.WriteLine();
        }

        try
        {
            await foreach (Columns<Reading> _ in file.Scan<Reading>().Rows(2_000_000))
            {
            }
        }
        catch (ArgumentOutOfRangeException e)
        {
            Console.WriteLine($"a row past the end: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }

        await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(RowRange.FromLength(999_990, 100)))
        {
            Console.WriteLine($"a range past the end: {cols.RowCount} rows from row {cols.StartRow}");
        }
    }
}
