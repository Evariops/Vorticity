using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Types;

namespace Vorticity.Samples;

internal static class DatasetMaintenance
{
    internal static async Task RunAsync()
    {
        await using IObjectStore store = new FileObjectStore(Demo.Path("maintained"));
        DTypeArena types = new DTypeArena();
        DType schema = Demo.CitiesSchema(types);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, new DatasetOptions
        {
            ClusteringKey = ["city"],
        });

        // Many small commits are what makes maintenance necessary. Level zero holds eight objects
        // before compaction has anything to do.
        const int Appends = 10;
        for (int i = 0; i < Appends; i++)
        {
            await dataset.AppendAsync(One(types, schema, 5_000, i * 5_000));
        }

        Console.WriteLine($"after {Appends} appends: version {dataset.Version}, {dataset.RowCount} rows, " +
            $"{dataset.ObjectCount} objects, depth {dataset.Depth}, lag {dataset.Lag}");
        Report(dataset.Levels);

        CompactionPlan plan = await dataset.PlanCompactionAsync();
        Console.WriteLine($"the plan: work to do {plan.HasWork}, style {plan.Style}, " +
            $"clustered {plan.IsClustered}, lag {plan.Lag}, fragmented {plan.FragmentedObjects}");
        Console.WriteLine($"  objects by level [{string.Join(", ", plan.ObjectsByLevel)}], " +
            $"bytes by level [{string.Join(", ", plan.BytesByLevel)}]");
        if (plan.Job is { } job)
        {
            Console.WriteLine($"  job: level {job.FromLevel} to {job.ToLevel}, trigger {job.Trigger}, " +
                $"{job.Inputs.Count} inputs, {job.Rows} rows, {job.Bytes} bytes, target {job.TargetBytes}");
        }

        if (await dataset.CompactAsync() is { } compacted)
        {
            Console.WriteLine($"compacted: version {compacted.Version}, outcome {compacted.Outcome}, " +
                $"level {compacted.FromLevel} to {compacted.ToLevel}, {compacted.ObjectsIn} objects in and " +
                $"{compacted.ObjectsOut} out, {compacted.BytesIn} bytes in and {compacted.BytesOut} out, " +
                $"{compacted.Rows} rows");
        }
        else
        {
            Console.WriteLine("compacted: nothing to do");
        }
        Console.WriteLine($"now: {dataset.ObjectCount} objects, depth {dataset.Depth}, lag {dataset.Lag}");
        Report(dataset.Levels);

        DatasetVerification verified = await dataset.VerifyAsync();
        Console.WriteLine($"verify: holds {verified.Holds}, {verified.Objects} objects, " +
            $"{verified.Commits} commits, {verified.Pages} pages, {verified.Fragments} fragments, " +
            $"{verified.Unhashed} unhashed, {verified.Problems.Count} problems");

        // Vacuum keeps every version younger than the retention window, so nothing goes today.
        VacuumResult now = await dataset.VacuumAsync(new VacuumOptions { DryRun = true });
        Console.WriteLine($"vacuum today: {now.Deleted.Count} would go, {now.Young.Count} too young, " +
            $"{now.Retained.Count} versions retained, window {now.Window}");

        // A week later, the old commits are past it.
        VacuumOptions later = new VacuumOptions { Clock = new Later(TimeSpan.FromDays(8)), DryRun = true };
        VacuumResult dry = await dataset.VacuumAsync(later);
        Console.WriteLine($"vacuum in eight days, dry run: {dry.Deleted.Count} would go, " +
            $"{dry.Young.Count} too young, {dry.Retained.Count} retained, {dry.PagesRead} pages read");

        VacuumResult done = await dataset.VacuumAsync(later with { DryRun = false });
        Console.WriteLine($"vacuum in eight days: {done.Deleted.Count} deleted, latest version {done.Latest}");

        Console.WriteLine($"and the data is unchanged: {await dataset.Scan().CountAsync()} rows");

        static void Report(DatasetLevels levels)
        {
            Console.WriteLine($"  {levels.Count} levels, {levels.Entries} entries, {levels.Rows} rows");
            foreach ((int level, DatasetTree tree) in levels.Occupied())
            {
                Console.WriteLine($"    level {level}: {tree.Entries} entries, {tree.Rows} rows, depth {tree.Depth}");
            }
        }
    }

    private static async IAsyncEnumerable<RecordBatch> One(DTypeArena types, DType schema, int rows, long startRow)
    {
        yield return Demo.CitiesBatch(types, schema, rows, startRow, everyThousandthIsNull: false);
        await Task.CompletedTask;
    }

    /// <summary>A clock a fixed distance ahead, so that a retention window can be crossed.</summary>
    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }
}
