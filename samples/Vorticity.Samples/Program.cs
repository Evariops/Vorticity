using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>
/// Runs the programs the guide quotes. Each one writes what it found to the console, so the
/// numbers a page prints can be checked by running the page's sample again.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<string, Func<Task>> Samples = new(StringComparer.Ordinal)
    {
        ["getting-started"] = GettingStarted.RunAsync,
        ["open-a-file"] = OpenAFile.RunAsync,
        ["read-the-schema"] = ReadTheSchema.RunAsync,
        ["scan-a-table"] = ScanATable.RunAsync,
        ["project-columns"] = ProjectColumns.RunAsync,
        ["filter-rows"] = FilterRows.RunAsync,
        ["read-rows-by-index"] = ReadRowsByIndex.RunAsync,
        ["write-a-file"] = WriteAFile.RunAsync,
        ["options"] = Options.RunAsync,
        ["cancel-work"] = CancelWork.RunAsync,
        ["errors"] = Errors.RunAsync,
        ["indexes"] = Indexes.RunAsync,
        ["keys-in-order"] = KeysInOrder.RunAsync,
        ["statistics-and-pruning"] = StatisticsAndPruning.RunAsync,
        ["encoding-hints"] = EncodingHints.RunAsync,
        ["editions"] = Editions.RunAsync,
        ["append-and-repair"] = AppendAndRepair.RunAsync,
        ["threads"] = Threads.RunAsync,
        ["limits"] = Limits.RunAsync,
        ["datasets"] = Datasets.RunAsync,
        ["dataset-maintenance"] = DatasetMaintenance.RunAsync,
        ["object-store"] = ObjectStore.RunAsync,
        ["row-keys"] = RowKeys.RunAsync,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("usage: vortex-samples [page]...");
            Console.WriteLine("Runs one sample per guide page; with no argument, runs them all.");
            Console.WriteLine("Pages: " + string.Join(", ", Samples.Keys));
            return 0;
        }

        IEnumerable<string> wanted = args.Length == 0 ? Samples.Keys : args;
        try
        {
            foreach (string name in wanted)
            {
                if (!Samples.TryGetValue(name, out Func<Task>? run))
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
