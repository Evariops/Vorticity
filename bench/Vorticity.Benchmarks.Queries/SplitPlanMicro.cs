using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Layouts;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The price of a scan's plan of splits over a file (<c>--micro splitplan --file F</c>): the plan a filtered scan of
/// the file's every row draws before its first read, of every column and of one, a call at a time and its bytes.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SplitPlanMicro
{
    internal static async Task<int> Run(string path, int rounds)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path);
        LayoutTree tree = file.LayoutTree;
        RowRange rows = new RowRange(0, tree.Root.RowCount);
        long natural = SplitPlan.NaturalBatchRows(tree);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{path}: {rows.Length:N0} rows, {tree.Root.DType.FieldCount} columns"));
        foreach ((string name, FieldMask mask) in new[] { ("every column", FieldMask.All), ("one column", FieldMask.Single(0)) })
        {
            int boundaries = SplitPlan.Compute(tree, rows, in mask, natural).BoundaryCount;
            int calls = Math.Max(1, rounds) * 20;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long begin = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++)
            {
                boundaries = SplitPlan.Compute(tree, rows, in mask, natural).BoundaryCount;
            }

            double micros = (Stopwatch.GetTimestamp() - begin) * 1e6 / Stopwatch.Frequency / calls;
            double bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)calls;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  {name,-14} {boundaries,9:N0} boundaries  {micros,10:F1} us a plan  {bytes,12:N0} B a plan"));
        }

        return 0;
    }
}
