using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class Datasets
{
    internal static async Task RunAsync()
    {
        // The store is the seam; this one is a directory. See object-store.md for the others.
        await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));

        DTypeArena types = new DTypeArena();
        DType schema = Demo.CitiesSchema(types);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, new DatasetOptions
        {
            ClusteringKey = ["city"],
            SummaryColumns = ["city"],
        });

        Console.WriteLine($"created at version {dataset.Version}, {dataset.RowCount} rows, " +
            $"clustered by {string.Join(", ", dataset.ClusteringKeyPaths)}");

        // Appending a stream of batches: one commit, one new version.
        ulong version = await dataset.AppendAsync(Batches(types, schema, 3, 20_000));
        Console.WriteLine($"appended: version {version}, {dataset.RowCount} rows, " +
            $"{dataset.ObjectCount} objects, depth {dataset.Depth}, lag {dataset.Lag}");

        // Importing adopts an object that is already in the store, by its key, without rewriting
        // its bytes. Putting it there is the caller's job.
        string file = Demo.Path("to-import.vortex");
        await Demo.WriteCitiesAsync(file, VortexWriteOptions.Default, rows: 30_000);
        await store.PutIfAbsentAsync(
            "imported.vortex", await System.IO.File.ReadAllBytesAsync(file), CancellationToken.None);
        ulong imported = await dataset.ImportAsync("imported.vortex");
        Console.WriteLine($"imported: version {imported}, {dataset.RowCount} rows, " +
            $"{dataset.ObjectCount} objects");

        await foreach (ObjectEntry entry in dataset.ObjectsAsync())
        {
            Console.WriteLine($"  object {entry.Key}: {entry.Rows} rows, {entry.Bytes} bytes");
        }

        // Reading it: the same scan vocabulary as a file, over every object at once.
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        long rows = 0;
        await foreach (RecordBatch batch in dataset.Scan().WithMetrics(metrics).ExecuteAsync())
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        Console.WriteLine($"scanned {rows} rows: {metrics.ObjectsConsidered} objects considered, " +
            $"{metrics.ObjectsOpened} opened, {metrics.ObjectsSkipped} skipped");

        VortexExpr paris = Expr.Eq(Expr.Field("city"), Expr.Literal(FilterLiteral.From("Paris")));
        DatasetScanMetrics filtered = new DatasetScanMetrics();
        long matching = await dataset.Scan().Where(paris).WithMetrics(filtered).CountAsync();
        Console.WriteLine($"city = Paris: {matching} rows, {filtered.ObjectsSkipped} objects skipped, " +
            $"{filtered.SubtreesSkipped} subtrees skipped");

        DatasetPlan plan = await dataset.Scan().Where(paris).ExplainAsync();
        Console.WriteLine($"the plan: version {plan.Version}, {plan.Objects} objects, " +
            $"{plan.ObjectsSkipped} skipped, {plan.SubtreesSkipped} subtrees skipped, " +
            $"{plan.Rows} rows, order {plan.Order}, clustered {plan.IsClustered}");

        Console.WriteLine($"rank of Paris among the keys: {await dataset.RankAsync(FilterLiteral.From("Paris"))}");
        Console.WriteLine($"rows 100 to 110: {await dataset.Rows(100, 110).CountAsync()}");
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, int count, int rows)
    {
        for (int i = 0; i < count; i++)
        {
            yield return Demo.CitiesBatch(types, schema, rows, i * rows, everyThousandthIsNull: false);
            await Task.Yield();
        }
    }
}
