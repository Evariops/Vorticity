using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Other writers' statistics read as bounds: on every real file, for a value of each comparable
/// column, the rows a filter of it selects counted with the statistics pruning and proving, and
/// counted again reading every row. A bound read wrong, or a count proven wrong, is a difference.
/// <c>VORTICITY_PARQUET_DATA</c> names the files' directory.
/// </summary>
public sealed class RealFilePruningTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrunesRealFilesWithoutLosingARow()
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        using StreamWriter? report = reportPath is null ? null : new StreamWriter(reportPath, append: false);
        List<string> failures = [];
        int compared = 0;
        long blocksPruned = 0;
        long blocksProven = 0;
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(Root!, path);
            List<(string Column, FilterLiteral Value)> samples;
            try
            {
                samples = await SamplesAsync(path);
            }
            catch (Exception e) when (e is ParquetUnsupportedException or ParquetFormatException)
            {
                continue;
            }

            await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
            // In batches of the scan's default and of 128 rows, which a file's small pages rule out
            // a batch at a time.
            foreach ((string column, FilterLiteral value) in samples)
            {
                foreach ((int batchRows, VortexExpr filter) in Filters(column, value))
                {
                    long pruned;
                    long whole;
                    try
                    {
                        ScanOptions options = new() { BatchRows = batchRows };
                        pruned = await file.Scan().Where(filter).With(options).CountAsync(Ct);
                        whole = await file.Scan().Where(filter).With(options with { UseStatistics = false }).CountAsync(Ct);
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or VortexSchemaException)
                    {
                        // A column no filter compares, or a name no path reaches.
                        break;
                    }

                    compared++;
                    if (pruned != whole)
                    {
                        failures.Add($"{name}: {filter} in batches of {batchRows} counts {pruned} pruned and {whole} read whole");
                    }

                    // What the statistics did, so that a run shows they were put to the test.
                    CountPlan plan = (await file.Scan().Where(filter).With(new ScanOptions { BatchRows = batchRows }).ExplainAsync(Ct)).Count;
                    blocksPruned += plan.Pruned;
                    blocksProven += plan.Proven;
                    report?.WriteLine($"{name}: {filter} {pruned} {whole}, {plan.Pruned} pruned, {plan.Proven} proven");
                }
            }
        }

        report?.WriteLine($"{compared} filters compared, {failures.Count} differ; {blocksPruned} blocks pruned, {blocksProven} proven");
        Assert.Empty(failures);
    }

    /// <summary>Equal, less, and not less than the value, each in batches of the default and of 128 rows.</summary>
    private static IEnumerable<(int BatchRows, VortexExpr Filter)> Filters(string column, FilterLiteral value)
    {
        foreach (int batchRows in (int[])[0, 128])
        {
            yield return (batchRows, Expr.Eq(Expr.Field(column), Expr.Literal(value)));
            yield return (batchRows, Expr.Lt(Expr.Field(column), Expr.Literal(value)));
            yield return (batchRows, Expr.Ge(Expr.Field(column), Expr.Literal(value)));
        }
    }

    /// <summary>A value of each top-level column the first batch holds one of, from its middle row.</summary>
    private static async Task<List<(string Column, FilterLiteral Value)>> SamplesAsync(string path)
    {
        List<(string, FilterLiteral)> samples = [];
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                int row = batch.RowCount / 2;
                for (int c = 0; c < batch.Schema.Count; c++)
                {
                    int node = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(c);
                    bool decimalColumn = batch.Schema[c].Type.Kind == VortexTypeKind.Decimal;
                    bool read = decimalColumn
                        ? LiteralReader.TryReadDecimal(batch.Arena, node, row, out FilterLiteral value)
                        : LiteralReader.TryRead(batch.Arena, node, row, out value);
                    if (read && !(value.Kind == FilterLiteralKind.Float && double.IsNaN(value.FloatValue)))
                    {
                        samples.Add((batch.Schema[c].Name, value));
                    }
                }
            }

            break;
        }

        return samples;
    }
}
