// A dataset under a stream of small commits.
//
// WHAT IT MEASURES. A dataset is loaded, then takes thousands of small operations -- appends,
// updates and deletes of a handful of rows, each its own commit -- with the caller's compaction and
// vacuum between them, as a service would run them. Every window of operations prints what each
// kind cost: time, requests, bytes written, and the compaction it made due. Every checkpoint prints
// what reads cost on the version reached, and what the process holds. The answer wanted is a shape:
// whether a cost stays flat as operations accumulate, grows with them, or grows with the dataset.
//
// WHY A CONSOLE AND NOT A BENCHMARK CLASS. The state is the point: the ten-thousandth operation is
// measured on the version the ones before it left, which a harness repeating one operation on a
// fixed state cannot show.
//
//     dotnet run -c Release --project bench/Vorticity.Benchmarks.Churn -- --rows 1000000 --ops 2000
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Vorticity.Bench.Churn;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ChurnOptions options;
        try
        {
            options = ChurnOptions.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(ChurnOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.Out.WriteLine(ChurnOptions.Usage);
            return 0;
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"churn: {options}"));
        await Churn.RunAsync(options).ConfigureAwait(false);
        return 0;
    }
}
