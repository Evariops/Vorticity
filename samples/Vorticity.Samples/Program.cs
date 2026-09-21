using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>
/// Runs the programs the guide quotes, one per case. Each writes what it found to the console, so
/// the figures a page prints can be checked by running the page's sample again.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Dictionary<string, Func<Task>> samples = new(StringComparer.Ordinal);
        ReadCases.Register(samples);
        AccessCases.Register(samples);
        WriteCases.Register(samples);
        MoreCases.Register(samples);

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("usage: vortex-samples [case]...");
            Console.WriteLine("Runs one sample per guide page; with no argument, runs them all.");
            Console.WriteLine("Cases: " + string.Join(", ", samples.Keys));
            return 0;
        }

        IEnumerable<string> wanted = args.Length == 0 ? samples.Keys : args;
        try
        {
            foreach (string name in wanted)
            {
                if (!samples.TryGetValue(name, out Func<Task>? run))
                {
                    Console.Error.WriteLine($"no sample named '{name}'");
                    return 2;
                }

                Console.WriteLine($"== {name} ==");
                await run();
            }
        }
        finally
        {
            Demo.Clean();
        }

        return 0;
    }
}
