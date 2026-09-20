using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class StatisticsAndPruning
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        VortexExpr recent = Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(900)));
        ScanPlan plan = await file.Scan().Where(recent).ExplainAsync();

        Console.WriteLine($"the file: {plan.FileBytes} bytes, {plan.RowCount} rows, " +
            $"{plan.Blocks} blocks of {plan.BlockRows} rows, {plan.Splits} splits");
        Console.WriteLine($"this query: {plan.LiveBlocks} live blocks, {plan.LiveSplits} live splits, " +
            $"{plan.SegmentsToRead} segments and {plan.BytesToRead} bytes to read, " +
            $"file may match: {plan.FileMayMatch}");
        foreach (PruningStep step in plan.Pruning)
        {
            Console.WriteLine($"  {step.Structure}: {step.BlocksPruned} blocks pruned, " +
                $"{step.SegmentsRead} segments and {step.BytesRead} bytes read to decide");
        }

        Console.WriteLine($"count: exact {plan.Count?.ExactCover}, {plan.Count?.ExactCount} rows, " +
            $"{plan.Count?.SplitsPruned} splits pruned, {plan.Count?.SplitsProven} proven, " +
            $"{plan.Count?.SplitsDecoded} decoded");

        // The summaries a pruner sees, and the answer it gives before any block is opened.
        IReadOnlyList<ColumnSummary> summaries = ColumnSummaries.Of(file);
        foreach (ColumnSummary summary in summaries)
        {
            Console.WriteLine($"  {summary.Path}: min known {summary.HasMin}, max known {summary.HasMax}, " +
                $"exact {summary.IsExact}, nulls {(summary.HasNullCount ? summary.NullCount.ToString() : "unknown")}");
        }

        Console.WriteLine($"may match day >= 900:  {ColumnSummaries.MayMatch(recent, summaries, file.RowCount)}");
        Console.WriteLine($"may match day >= 5000: " +
            $"{ColumnSummaries.MayMatch(Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(5000))), summaries, file.RowCount)}");

        SummaryPruner pruner = new SummaryPruner(recent);
        Console.WriteLine($"the pruner reads columns: {pruner.ReadsColumns}, may match: " +
            $"{pruner.MayMatch(summaries, file.RowCount)}");

        // Smaller blocks prune finer. The same predicate, the same rows, three block sizes.
        foreach (int blockRows in new[] { 1_024, 8_192, 65_536 })
        {
            string sized = Demo.Path($"blocks-{blockRows}.vortex");
            await Demo.WriteReadingsAsync(sized, new VortexWriteOptions { RowBlockSize = blockRows });
            await using VortexFile smaller = await VortexFile.OpenAsync(sized);
            ScanPlan sizedPlan = await smaller.Scan().Where(recent).ExplainAsync();
            (long requests, long bytes, long rows) = await Demo.MeasureAsync(sized, f => f.Scan().Where(recent));
            Console.WriteLine($"  blocks of {blockRows}: {sizedPlan.LiveBlocks} of {sizedPlan.Blocks} live, " +
                $"{rows} rows read in {requests} rounds and {bytes} bytes, file {sizedPlan.FileBytes} bytes");
        }

        // A text column prunes only if the writer kept bounds for it, and only if the rows are
        // clustered by that column in the first place.
        foreach (bool clustered in new[] { false, true })
        {
            foreach (int bound in new[] { 0, 16 })
            {
                string text = Demo.Path($"bounds-{clustered}-{bound}.vortex");
                await Demo.WriteCitiesAsync(
                    text, new VortexWriteOptions { StringBoundBytes = bound }, clusteredByCity: clustered);
                await using VortexFile cities = await VortexFile.OpenAsync(text);
                VortexExpr paris = Expr.Eq(Expr.Field("city"), Expr.Literal(FilterLiteral.From("Paris")));
                ScanPlan textPlan = await cities.Scan().Where(paris).WithIndexes(false).ExplainAsync();
                Console.WriteLine($"  clustered {clustered}, StringBoundBytes {bound}: " +
                    $"{textPlan.LiveBlocks} of {textPlan.Blocks} blocks live for city = Paris, " +
                    $"{await cities.Scan().Where(paris).CountAsync()} rows");
            }
        }
    }
}
