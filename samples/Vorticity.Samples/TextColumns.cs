using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class TextColumns
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await BestOfThreeAsync("UTF-8 spans", () => SpansAsync(file));
        await BestOfThreeAsync("GetString", () => StringsAsync(file));
        await BestOfThreeAsync("the scan alone", async () =>
        {
            long rows = 0;
            await foreach (var (_, _, city) in file.Scan<Reading>())
            {
                rows += city.Length;
            }

            return $"{rows} rows";
        });
        await BestOfThreeAsync("CountDistinctAsync", async () => $"{await file.Scan<Reading>().CountDistinctAsync(r => r.City)} cities");

        string visits = await Demo.VisitsAsync();
        await using VortexFile visitFile = await VortexFile.OpenAsync(visits);
        long referrers = await visitFile.Scan<Visit>().CountDistinctAsync(v => v.Referrer);
        long none = await visitFile.Scan<Visit>().Where(v => v.Referrer == null).CountAsync();
        long secure = await visitFile.Scan<Visit>().Where(v => v.Referrer.StartsWith("https://")).CountAsync();
        Console.WriteLine($"referrers: {referrers} distinct, {none} null, {secure} starting with https://");

        int longest = 0;
        string? first = null;
        int nulls = 0;
        await foreach (Columns<Visit> v in visitFile.Scan<Visit>().Rows(RowRange.FromLength(0, 8)))
        {
            Column<string?> referrer = v.Referrer;
            for (int i = 0; i < referrer.Length; i++)
            {
                longest = Math.Max(longest, referrer.GetLength(i));
                first ??= referrer.GetString(i);
                nulls += referrer.IsValid(i) ? 0 : 1;
            }
        }

        Console.WriteLine($"the first eight visits: {nulls} null referrers, the longest {longest} bytes, the first \"{first}\"");
    }

    private static async Task<string> SpansAsync(VortexFile file)
    {
        long paris = 0;
        long startingWithL = 0;
        await foreach (var (_, _, city) in file.Scan<Reading>())
        {
            for (int i = 0; i < city.Length; i++)
            {
                ReadOnlySpan<byte> name = city[i];
                if (name.SequenceEqual("Paris"u8))
                {
                    paris++;
                }

                if (name.StartsWith("L"u8))
                {
                    startingWithL++;
                }
            }
        }

        return $"{paris} rows in Paris, {startingWithL} starting with L";
    }

    private static async Task<string> StringsAsync(VortexFile file)
    {
        long paris = 0;
        long startingWithL = 0;
        await foreach (var (_, _, city) in file.Scan<Reading>())
        {
            for (int i = 0; i < city.Length; i++)
            {
                string? name = city.GetString(i);
                if (name == "Paris")
                {
                    paris++;
                }

                if (name?.StartsWith('L') == true)
                {
                    startingWithL++;
                }
            }
        }

        return $"{paris} rows in Paris, {startingWithL} starting with L";
    }

    private static async Task BestOfThreeAsync(string name, Func<Task<string>> run)
    {
        string outcome = string.Empty;
        TimeSpan best = TimeSpan.MaxValue;
        long allocated = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();
            outcome = await run();
            best = clock.Elapsed < best ? clock.Elapsed : best;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        Console.WriteLine($"{name,-19} {best.TotalMilliseconds,6:F1} ms, {allocated,10} bytes allocated: {outcome}");
    }
}
