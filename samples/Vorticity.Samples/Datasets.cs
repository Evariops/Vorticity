using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Samples;

/// <summary>A reading as the dataset holds it once its schema has changed: the city renamed, a humidity added.</summary>
[VortexRecord]
public partial record struct Observation(int Day, double? Celsius, string Town, double? Humidity);

internal static class Datasets
{
    internal static async Task RunAsync()
    {
        await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
        {
            ClusteringKey = [Reading.ColumnNames.Day],
        });
        Console.WriteLine($"created: version {dataset.Version}, {dataset.RowCount} rows, clustered by {string.Join(", ", dataset.ClusteringKeyPaths)}");

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Days(0, 50));
            ulong version = await dataset.AppendAsync(draft);
            Console.WriteLine($"appended {draft.Key}: version {version}, {dataset.RowCount} rows");
        }

        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Days(50, 50));
            await dataset.AppendAsync(draft);
        }

        string local = Demo.Path("days-100-119.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(local))
        {
            await writer.WriteAsync<Reading>(Days(100, 20));
            await writer.CompleteAsync();
        }

        await using (FileStream stream = System.IO.File.OpenRead(local))
        {
            await store.PutIfAbsentAsync("imports/days-100-119.vortex", PipeReader.Create(stream), stream.Length, CancellationToken.None);
        }

        ulong imported = await dataset.ImportAsync("imports/days-100-119.vortex");
        Console.WriteLine($"imported: version {imported}, {dataset.RowCount} rows, {dataset.ObjectCount} objects, lag {dataset.Lag}");

        List<DataObject> objects = await dataset.ObjectsAsync().ToListAsync();
        foreach (DataObject entry in objects)
        {
            Console.WriteLine($"  {entry.Key}: level {entry.Level}, rows {entry.FirstRow} to {entry.FirstRow + entry.Rows}, {entry.Bytes} bytes");
        }

        Scan<Reading> recent = dataset.Scan<Reading>().Where(r => r.Day >= 100);
        ScanPlan plan = await dataset.Scan<Reading>().Where(r => r.Day >= 100).ExplainAsync();
        Console.WriteLine($"Day >= 100: {await recent.CountAsync()} rows; plan: {plan.LiveBlocks} of {plan.Blocks} blocks, " +
            string.Join("; ", plan.Pruning.Select(step => $"{step.Structure} pruned {step.BlocksPruned}")));
        Console.WriteLine($"may Day be 500? {dataset.MayMatch<Reading>(r => r.Day == 500)}; may Day be 75? {dataset.MayMatch<Reading>(r => r.Day == 75)}");

        long firstRow = -1;
        await foreach (Columns<Reading> batch in dataset.Scan<Reading>().Where(r => r.Day == 110))
        {
            firstRow = firstRow < 0 ? batch.StartRow : firstRow;
        }

        Console.WriteLine($"the first batch of day 110 starts at dataset row {firstRow}");

        await using (KeyCursor<int> cursor = await dataset.Scan<Reading>().Keys(r => r.Day).OpenAsync())
        {
            bool found = await cursor.SeekAsync(75, SeekOp.AtOrAfter);
            Console.WriteLine($"key cursor: seek 75 found {found}, key {cursor.Key} at row {cursor.Row}; {await cursor.KeyCountAsync()} entries");

            await cursor.SeekLastAsync();
            (int last, long lastRow) = (cursor.Key, cursor.Row);
            await cursor.PrevKeyAsync();
            Console.WriteLine($"walking down: last key {last} at row {lastRow}; the key before it {cursor.Key}, its last entry at row {cursor.Row}");
        }

        await using VortexDataset reader = await VortexDataset.OpenAsync(store);
        Scan<Reading> pinned = dataset.Scan<Reading>();
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Reading>(Days(120, 10));
            await dataset.AppendAsync(draft);
        }

        Console.WriteLine($"after another append: the handle reads version {dataset.Version}, {dataset.RowCount} rows; " +
            $"a scan built before it counts {await pinned.CountAsync()}; another handle reads version {reader.Version} " +
            $"until it refreshes to {await reader.RefreshAsync()}");

        DataObject first = objects[0];
        await using (ObjectDraft rewritten = dataset.StartObject())
        {
            await rewritten.Writer.WriteAsync(dataset.Scan<Reading>()
                .Rows(RowRange.FromLength(first.FirstRow, first.Rows))
                .Where(r => r.Celsius.IsNotNull)
                .ToRecordsAsync());
            ReplaceResult replaced = await dataset.ReplaceAsync([first], [rewritten]);
            Console.WriteLine($"replaced {first.Key} by the rows with a temperature: version {replaced.Version}, {replaced.Outcome}, {dataset.RowCount} rows");
        }

        DataObject importedObject = (await dataset.ObjectsAsync().ToListAsync()).Single(o => o.Key.StartsWith("imports/", StringComparison.Ordinal));
        ReplaceResult removed = await dataset.RemoveAsync([importedObject]);
        Console.WriteLine($"removed the import: version {removed.Version}, {removed.Outcome}, {dataset.RowCount} rows, {dataset.ObjectCount} objects");

        ReplaceResult again = await dataset.RemoveAsync([importedObject]);
        Console.WriteLine($"removing it again: {again.Outcome}, version {again.Version}");

        RowChangeResult deleted = await dataset.DeleteAsync<Reading>(r => r.Day < 10);
        Console.WriteLine($"deleted days 0 to 9: version {deleted.Version}, {deleted.Rows} rows, {deleted.ObjectsIn} object(s) rewritten into {deleted.ObjectsOut}, {deleted.ObjectsMarked} marked, {dataset.RowCount} rows left");

        RowChangeResult updated = await dataset.UpdateAsync<Reading>(
            r => r.City == "Nice" & r.Day >= 60,
            r => r with { Celsius = r.Celsius + 1.0 });
        Console.WriteLine($"warmed Nice from day 60: version {updated.Version}, {updated.Rows} rows changed, {updated.ObjectsIn} object(s) in, {updated.ObjectsOut} out");

        ulong evolved = await dataset.EvolveSchemaAsync(Observation.Schema, new Dictionary<string, string> { ["Town"] = "City" });
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Observation>(Observations(140, 10));
            await dataset.AppendAsync(draft);
        }

        long humid = await dataset.Scan<Observation>().Where(r => r.Humidity > 50.0).CountAsync();
        long unknown = await dataset.Scan<Observation>().Where(r => r.Humidity.IsNull).CountAsync();
        Observation earlier = await dataset.Scan<Observation>().Where(r => r.Day == 10).ToRecordsAsync().FirstAsync();
        Console.WriteLine($"evolved: version {evolved}, columns {string.Join(", ", dataset.Schema.Select(field => field.Name))}");
        Console.WriteLine($"  humidity above 50: {humid} rows; unknown, in every row written before: {unknown} rows");
        Console.WriteLine($"  a row written before: {earlier}");
    }

    private static Observation[] Observations(int firstDay, int days)
    {
        Observation[] rows = new Observation[days * 1_000];
        for (int i = 0; i < rows.Length; i++)
        {
            int row = firstDay * 1_000 + i;
            rows[i] = new Observation(row / 1_000, 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length], row % 100);
        }

        return rows;
    }

    private static Reading[] Days(int firstDay, int days)
    {
        Reading[] rows = new Reading[days * 1_000];
        for (int i = 0; i < rows.Length; i++)
        {
            int row = firstDay * 1_000 + i;
            rows[i] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return rows;
    }
}
