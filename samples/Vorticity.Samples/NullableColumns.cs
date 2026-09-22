using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class NullableColumns
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await BestOfThreeAsync("by the words", () => ByTheWordsAsync(file.Scan<Reading>()));
        await BestOfThreeAsync("by the words, nulls filtered out", () => ByTheWordsAsync(file.Scan<Reading>().Where(r => r.Celsius != null)));
        await BestOfThreeAsync("per element", () => PerElementAsync(file.Scan<Reading>()));
        await BestOfThreeAsync("SumAsync", async () => $"sum {await file.Scan<Reading>().SumAsync(r => r.Celsius):F1}");

        long nulls = 0;
        await foreach (var (_, celsius, _) in file.Scan<Reading>())
        {
            nulls += celsius.NullCount;
        }

        long counted = await file.Scan<Reading>().Where(r => r.Celsius == null).CountAsync();
        Console.WriteLine($"NullCount summed over the batches: {nulls}; rows where r.Celsius == null: {counted}");
    }

    private static async Task<string> ByTheWordsAsync(Scan<Reading> scan)
    {
        double total = 0;
        long allValid = 0;
        long fullWords = 0;
        long otherWords = 0;
        await foreach (var (_, celsius, _) in scan)
        {
            ReadOnlySpan<double> values = celsius.Values;
            ReadOnlySpan<ulong> valid = celsius.ValidityWords;
            if (valid.IsEmpty)
            {
                allValid++;
                total += Sum(values);
                continue;
            }

            for (int w = 0; w < valid.Length; w++)
            {
                ulong word = valid[w];
                int start = w << 6;
                int width = Math.Min(64, values.Length - start);
                if (word == ulong.MaxValue)
                {
                    fullWords++;
                    total += Sum(values.Slice(start, width));
                    continue;
                }

                otherWords++;
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    total += values[start + bit];
                    word &= word - 1;
                }
            }
        }

        return $"sum {total:F1}; {allValid} batches all valid, {fullWords} words all valid, {otherWords} words with a null";
    }

    private static async Task<string> PerElementAsync(Scan<Reading> scan)
    {
        double total = 0;
        await foreach (var (_, celsius, _) in scan)
        {
            for (int i = 0; i < celsius.Length; i++)
            {
                if (celsius[i] is double value)
                {
                    total += value;
                }
            }
        }

        return $"sum {total:F1}";
    }

    private static double Sum(ReadOnlySpan<double> values)
    {
        Vector<double> acc = Vector<double>.Zero;
        int i = 0;
        for (; i <= values.Length - Vector<double>.Count; i += Vector<double>.Count)
        {
            acc += new Vector<double>(values.Slice(i));
        }

        double sum = Vector.Sum(acc);
        for (; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    private static async Task BestOfThreeAsync(string name, Func<Task<string>> run)
    {
        string outcome = string.Empty;
        TimeSpan best = TimeSpan.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            outcome = await run();
            best = clock.Elapsed < best ? clock.Elapsed : best;
        }

        Console.WriteLine($"{name,-33} {best.TotalMilliseconds,6:F1} ms  {outcome}");
    }
}
