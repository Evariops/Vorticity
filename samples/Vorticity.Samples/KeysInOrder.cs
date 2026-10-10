using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class KeysInOrder
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await using (KeyCursor<int> cursor = await file.Scan<Reading>().Keys(r => r.Day).OpenAsync())
        {
            await cursor.SeekFirstAsync();
            Console.WriteLine($"first key {cursor.Key} at row {cursor.Row}");
            await cursor.NextKeyAsync();
            Console.WriteLine($"next distinct key {cursor.Key} at row {cursor.Row}");

            if (await cursor.SeekAsync(900, SeekMode.AtOrAfter))
            {
                Console.WriteLine($"seek to 900 landed on {cursor.Key} at row {cursor.Row}");
            }

            Console.WriteLine($"rank of 900: {await cursor.RankAsync(900)}");
            Console.WriteLine($"{await cursor.CountAtKeyAsync()} entries share key {cursor.Key}");
            await cursor.SeekRankAsync(123_456);
            Console.WriteLine($"entry of rank 123456: key {cursor.Key} at row {cursor.Row}");
            await cursor.SeekLastAsync();
            Console.WriteLine($"last key {cursor.Key} at row {cursor.Row}");

            bool found = await cursor.SeekAsync(1_234, SeekMode.Exact);
            Console.WriteLine($"an exact seek to a key that is not there: {found}, cursor valid: {cursor.IsValid}");

            long inRange = await cursor.RankAsync(102) - await cursor.RankAsync(100);
            Console.WriteLine($"rows with 100 <= day < 102, from two ranks: {inRange}");

            await cursor.SeekAsync(700, SeekMode.AtOrBefore);
            long row = cursor.Row;
            Console.WriteLine($"seek at or before 700: key {cursor.Key} at row {row}, the last of its run");
            await foreach (Columns<Reading> cols in file.Scan<Reading>().Rows(row))
            {
                foreach (int i in cols.Selection)
                {
                    Console.WriteLine($"  read back by Rows({row}): day {cols.Day[i]}, {cols.Celsius[i]} degrees, {cols.City.GetString(i)}");
                }
            }

            Stopwatch watch = Stopwatch.StartNew();
            for (int key = 0; key < 1_000; key++)
            {
                await cursor.SeekAsync(key, SeekMode.AtOrAfter);
            }

            Console.WriteLine($"1000 seeks in {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        KeyPlan plan = await file.Scan<Reading>().Keys(r => r.Day).ExplainAsync();
        Console.WriteLine($"day: source {plan.Source}, {plan.Runs} run, {plan.EntryCount} entries, rows reachable: {plan.HasRows}");
        foreach (KeySourceRejection rejected in plan.Rejected)
        {
            Console.WriteLine($"  turned down {rejected.Source}: {rejected.Reason}");
        }

        KeyPlan unsorted = await file.Scan<Reading>().Keys(r => r.Celsius).ExplainAsync();
        Console.WriteLine($"celsius: source {unsorted.Source}");
        try
        {
            await using KeyCursor<double?> none = await file.Scan<Reading>().Keys(r => r.Celsius).OpenAsync();
        }
        catch (VortexUnsupportedException e)
        {
            Console.WriteLine($"  OpenAsync: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            file.Scan<Reading>().Where(r => r.Day >= 900).Keys(r => r.Day);
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"Keys after Where: {e.Message}");
        }

        await using (KeyCursor<int> distinct = await file.Scan<Reading>().Keys(r => r.Day).Distinct().OpenAsync())
        {
            int stops = 0;
            for (bool more = await distinct.SeekFirstAsync(); more; more = await distinct.NextAsync())
            {
                stops++;
            }

            Console.WriteLine($"a distinct cursor stops {stops} times, rows reachable: {distinct.HasRows}");
        }

        await OrderedScanAsync(file);
        await SortedRunsAsync();
    }

    private static async Task OrderedScanAsync(VortexFile file)
    {
        Scan<Reading> ordered = file.Scan<Reading>().OrderByDescending(r => r.Day);
        ScanPlan plan = await file.Scan<Reading>().OrderByDescending(r => r.Day).ExplainAsync();
        Console.WriteLine($"ordered scan: source {plan.Order?.Source}, {plan.Order?.Runs} run, {plan.Order?.Entries} entries, descending {plan.Order?.Descending}");

        int batches = 0;
        await foreach (var (day, _, _) in ordered)
        {
            if (batches++ == 0)
            {
                Console.WriteLine($"  first batch: {day.Length} rows, day {day[0]} down to {day[day.Length - 1]}");
            }
        }

        Console.WriteLine($"  {batches} batches");
    }

    private static async Task SortedRunsAsync()
    {
        string path = Demo.Path("cities-indexed.vortex");
        IndexPolicy byCity = IndexPolicy.None.SortedRuns(Reading.ColumnNames.City);
        await WriteCitiesAsync(path, byCity);
        await WriteCitiesAsync(path, byCity.WithBudgetPerMille(20_000));

        await using VortexFile file = await VortexFile.OpenAsync(path);
        KeyPlan plan = await file.Scan<Reading>().Keys(r => r.City).ExplainAsync();
        Console.WriteLine($"city, with a sorted-runs index: source {plan.Source}, {plan.Runs} runs, {plan.EntryCount} entries");

        await using KeyCursor<string> cursor = await file.Scan<Reading>().Keys(r => r.City).OpenAsync();
        for (bool more = await cursor.SeekFirstAsync(); more; more = await cursor.NextKeyAsync())
        {
            string city = cursor.Key;
            long first = await cursor.RankAsync(city);
            Console.WriteLine($"  {city}: first entry of rank {first}, row {cursor.Row}");
        }
    }

    private static async Task WriteCitiesAsync(string path, IndexPolicy indexes)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, new VortexWriteOptions { Indexes = indexes });
        Reading[] rows = new Reading[100_000];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Reading(i / 1_000, 10.0 + (i % 400 / 10.0), Demo.Cities[i / 7 % Demo.Cities.Length]);
        }

        await writer.WriteAsync<Reading>(rows);
        WriteReport report = await writer.CompleteAsync();
        foreach (IndexWriteReport index in report.Indexes)
        {
            Console.WriteLine($"sorted-runs index on {index.Column}: {index.Outcome}, {index.Bytes} bytes against {report.Bytes.Data} bytes of data. {index.Reason}");
        }
    }
}
