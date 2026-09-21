using System;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Samples;

internal static class DatasetMaintenance
{
    internal static async Task RunAsync()
    {
        await using CountingObjectStore store = new CountingObjectStore(new MemoryObjectStore(), ownsInner: true);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
        {
            ClusteringKey = [Reading.ColumnNames.Day],
        });

        const int Appends = 10;
        for (int i = 0; i < Appends; i++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Reading>(Rows(i * 5_000, 5_000));
            await dataset.AppendAsync(draft);
        }

        Console.WriteLine($"after {Appends} appends: version {dataset.Version}, {dataset.RowCount} rows, {dataset.ObjectCount} objects, lag {dataset.Lag}");

        store.Reset();
        CompactionPlan plan = await dataset.PlanCompactionAsync();
        Console.WriteLine($"plan: work {plan.HasWork}, style {plan.Style}, clustered {plan.IsClustered}, lag {plan.Lag}, " +
            $"objects by level [{string.Join(", ", plan.ObjectsByLevel)}], bytes by level [{string.Join(", ", plan.BytesByLevel)}]");
        if (plan.Job is { } job)
        {
            Console.WriteLine($"  job: level {job.FromLevel} to {job.ToLevel}, {job.Trigger}, {job.Objects.Length} objects, {job.Rows} rows, " +
                $"{job.Bytes} bytes, target {job.TargetBytes}");
        }

        Console.WriteLine($"  cost: {Cost(store)}");

        store.Reset();
        CompactionResult? compacted = await dataset.CompactAsync();
        if (compacted is not null)
        {
            Console.WriteLine($"compacted: version {compacted.Version}, {compacted.Outcome}, level {compacted.FromLevel} to {compacted.ToLevel}, " +
                $"{compacted.ObjectsIn} objects in and {compacted.ObjectsOut} out, {compacted.BytesIn} bytes in and {compacted.BytesOut} out, {compacted.Rows} rows");
        }

        Console.WriteLine($"  cost: {Cost(store)}");
        Console.WriteLine($"now: {dataset.ObjectCount} objects, lag {dataset.Lag}; again: {(await dataset.CompactAsync() is null ? "nothing to do" : "more")}");

        store.Reset();
        DatasetVerification verified = await dataset.VerifyAsync();
        Console.WriteLine($"verify: holds {verified.Holds}, {verified.Objects} objects, {verified.Commits} commits, {verified.Pages} pages, " +
            $"{verified.Fragments} fragments, {verified.Unhashed} unhashed, {verified.Problems.Length} problems; cost: {Cost(store)}");

        ulong checkedAt = dataset.Version;
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Rows(50_000, 5_000));
            await dataset.AppendAsync(draft);
        }

        store.Reset();
        DatasetVerification since = await dataset.VerifyAsync(since: checkedAt);
        Console.WriteLine($"verify since {checkedAt}: holds {since.Holds}, {since.Objects} objects, {since.Pages} pages; cost: {Cost(store)}");

        store.Reset();
        VacuumResult today = await dataset.VacuumAsync(new VacuumOptions { DryRun = true });
        Console.WriteLine($"vacuum today, dry run: {today.Deleted.Length} would go, {today.Young.Length} too young, " +
            $"{today.Retained.Length} versions retained, window {today.Window}; cost: {Cost(store)}");

        VacuumOptions later = new VacuumOptions { TimeProvider = new Later(TimeSpan.FromDays(8)), DryRun = true };
        store.Reset();
        VacuumResult dry = await dataset.VacuumAsync(later);
        Console.WriteLine($"vacuum in eight days, dry run: {dry.Deleted.Length} would go, {dry.Retained.Length} retained, " +
            $"{dry.PagesRead} pages read; cost: {Cost(store)}");

        store.Reset();
        VacuumResult done = await dataset.VacuumAsync(later with { DryRun = false });
        Console.WriteLine($"vacuum in eight days: {done.Deleted.Length} deleted, latest version {done.Latest}; cost: {Cost(store)}");

        Console.WriteLine($"and the data: {await dataset.Scan<Reading>().CountAsync()} rows, {dataset.ObjectCount} objects");
    }

    private static string Cost(CountingObjectStore store) =>
        $"{store.Requests} requests ({store.CountOf(ObjectOperation.GetRange)} get, {store.CountOf(ObjectOperation.Head)} head, " +
        $"{store.CountOf(ObjectOperation.PutIfAbsent)} put, {store.CountOf(ObjectOperation.Delete)} delete, {store.CountOf(ObjectOperation.List)} list), " +
        $"{store.DependentSteps} dependent steps, {store.BytesRead} bytes read, {store.BytesWritten} written";

    private static Reading[] Rows(int first, int count)
    {
        Reading[] rows = new Reading[count];
        for (int i = 0; i < count; i++)
        {
            int row = first + i;
            rows[i] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return rows;
    }

    /// <summary>A clock a fixed distance ahead, to cross a retention window.</summary>
    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }
}
