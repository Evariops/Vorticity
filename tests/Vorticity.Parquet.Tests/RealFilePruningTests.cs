using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Other writers' statistics read as bounds: on every real file, for a value of each comparable
/// column, the rows a filter of it selects counted with the statistics pruning and proving, and
/// counted again reading every row, and counted through a positional read, which reads of each chunk
/// the pages its live batches need alone; on a file of 100 000 rows or fewer, the rows of a filter are
/// compared whole between that read and the mapped one. A bound read wrong, a count proven wrong, or a page
/// placed wrong is a difference.
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
        int rowsCompared = 0;
        await using VortexSession positional = VortexSession.Create(options => options.MapFiles = false);
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(Root!, path);
            List<(string Column, FilterLiteral Value, bool Decimal)> samples;
            try
            {
                samples = await SamplesAsync(path);
            }
            catch (Exception e) when (e is ParquetUnsupportedException or ParquetFormatException)
            {
                continue;
            }

            await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
            await using ParquetFile read = await ParquetFile.OpenAsync(path, ParquetOpenOptions.Default, positional, Ct);
            bool rowsToo = file.RowCount <= 100_000;

            // In batches of the scan's default and of 128 rows, which a file's small pages rule out
            // a batch at a time.
            foreach ((string column, FilterLiteral value, bool decimalColumn) in samples)
            {
                int kind = 0;
                foreach ((int batchRows, VortexExpr filter) in Filters(column, value, decimalColumn))
                {
                    long pruned;
                    long whole;
                    long sparse;
                    try
                    {
                        ScanOptions options = new() { BatchRows = batchRows };
                        pruned = await file.Scan().Where(filter).With(options).CountAsync(Ct);
                        whole = await file.Scan().Where(filter).With(options with { UseStatistics = false }).CountAsync(Ct);
                        sparse = await read.Scan().Where(filter).With(options).CountAsync(Ct);
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or VortexSchemaException)
                    {
                        // A column no filter compares, or a name no path reaches.
                        break;
                    }

                    compared++;
                    if (pruned != whole || sparse != whole)
                    {
                        failures.Add($"{name}: {filter} in batches of {batchRows} counts {pruned} pruned, {sparse} read positionally and {whole} read whole");
                    }

                    // An equality and an order at the default batch, every column's rows.
                    if (rowsToo && batchRows == 0 && kind++ < 2)
                    {
                        rowsCompared++;
                        if (!(await RowsAsync(file, filter)).SequenceEqual(await RowsAsync(read, filter)))
                        {
                            failures.Add($"{name}: {filter} reads other rows positionally than mapped");
                        }
                    }

                    // What the statistics did, so that a run shows they were put to the test.
                    CountPlan plan = (await file.Scan().Where(filter).With(new ScanOptions { BatchRows = batchRows }).ExplainAsync(Ct)).Count;
                    blocksPruned += plan.Pruned;
                    blocksProven += plan.Proven;
                    report?.WriteLine($"{name}: {filter} {pruned} {whole}, {plan.Pruned} pruned, {plan.Proven} proven");
                }
            }
        }

        report?.WriteLine($"{compared} filters compared, {rowsCompared} by their rows, {failures.Count} differ; {blocksPruned} blocks pruned, {blocksProven} proven");
        Assert.Empty(failures);
    }

    /// <summary>
    /// Equal, less, and not less than the value, each in batches of the default and of 128 rows;
    /// equal to the value just past it, and in the two, which the column seldom holds and its bounds
    /// seldom rule out: what a Bloom filter is for.
    /// </summary>
    private static IEnumerable<(int BatchRows, VortexExpr Filter)> Filters(string column, FilterLiteral value, bool decimalColumn)
    {
        FilterLiteral? next = Neighbor(value, decimalColumn);
        foreach (int batchRows in (int[])[0, 128])
        {
            yield return (batchRows, Expr.Eq(Expr.Field(column), Expr.Literal(value)));
            yield return (batchRows, Expr.Lt(Expr.Field(column), Expr.Literal(value)));
            yield return (batchRows, Expr.Ge(Expr.Field(column), Expr.Literal(value)));
            if (next is { } past)
            {
                yield return (batchRows, Expr.Eq(Expr.Field(column), Expr.Literal(past)));
                yield return (batchRows, Expr.In(Expr.Field(column), value, past));
            }
        }
    }

    /// <summary>The value just past <paramref name="value"/>: a float's next, a byte string's with a zero after it.</summary>
    private static FilterLiteral? Neighbor(FilterLiteral value, bool decimalColumn)
    {
        switch (value.Kind)
        {
            case FilterLiteralKind.Signed when value.SignedValue < long.MaxValue:
                return FilterLiteral.From(value.SignedValue + 1);
            case FilterLiteralKind.Unsigned when value.UnsignedValue < ulong.MaxValue:
                return FilterLiteral.From(value.UnsignedValue + 1);
            case FilterLiteralKind.Float:
                double v = value.FloatValue;
                return FilterLiteral.From((float)v == v ? MathF.BitIncrement((float)v) : Math.BitIncrement(v));
            case FilterLiteralKind.Bytes when !decimalColumn:
                byte[] bytes = [.. value.BytesValue, 0];
                return FilterLiteral.From((ReadOnlySpan<byte>)bytes);
            default:
                return null;
        }
    }

    /// <summary>The rows <paramref name="filter"/> keeps, every column rendered.</summary>
    private static async Task<List<string>> RowsAsync(ParquetFile file, VortexExpr filter)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().Where(filter).ToBatchesAsync(Ct))
        {
            using (batch)
            {
                ReadOnlySpan<ulong> kept = batch.SelectionWords;
                for (int r = 0; r < batch.RowCount; r++)
                {
                    if (kept.IsEmpty || ((kept[r >> 6] >> (r & 63)) & 1) != 0)
                    {
                        System.Text.StringBuilder row = new();
                        for (int c = 0; c < batch.Schema.Count; c++)
                        {
                            row.Append(Render.Row(batch, c, r)).Append('|');
                        }

                        rows.Add(row.ToString());
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>A value of each top-level column the first batch holds one of, from its middle row.</summary>
    private static async Task<List<(string Column, FilterLiteral Value, bool Decimal)>> SamplesAsync(string path)
    {
        List<(string, FilterLiteral, bool)> samples = [];
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
                        samples.Add((batch.Schema[c].Name, value, decimalColumn));
                    }
                }
            }

            break;
        }

        return samples;
    }
}
