using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>The read cases beyond a scan's columns: keys, rows, positions, parallelism, owned batches, the tool path, plans.</summary>
internal static class AccessCases
{
    internal static void Register(Dictionary<string, Func<Task>> samples)
    {
        samples["keys-in-order"] = KeysInOrder.RunAsync;
        samples["read-rows"] = ReadRows.RunAsync;
        samples["read-rows-by-index"] = ReadRowsByIndex.RunAsync;
        samples["threads"] = Threads.RunAsync;
        samples["owned-batches"] = OwnedBatches.RunAsync;
        samples["nested-records"] = NestedRecords.RunAsync;
        samples["untyped-files"] = UntypedFiles.RunAsync;
        samples["statistics-and-pruning"] = StatisticsAndPruning.RunAsync;
        samples["cancel-work"] = CancelWork.RunAsync;
    }
}
