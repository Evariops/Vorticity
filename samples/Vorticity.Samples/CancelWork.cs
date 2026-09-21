using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Samples;

internal static class CancelWork
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        using CancellationTokenSource cts = new CancellationTokenSource();
        await using VortexFile file = await VortexFile.OpenAsync(path, VortexOpenOptions.Default, cts.Token);

        int batches = 0;
        try
        {
            // ExecuteAsync takes no token: the loop carries it.
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(cts.Token))
            {
                using (batch)
                {
                    if (++batches == 2)
                    {
                        await cts.CancelAsync();
                    }
                }
            }

            Console.WriteLine($"the scan ended on its own after {batches} batches");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"cancelled after {batches} batches");
        }

        // What each call does when the token is already cancelled.
        using CancellationTokenSource dead = new CancellationTokenSource();
        await dead.CancelAsync();
        VortexExpr recent = Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(900)));

        await Probe("OpenAsync", async () => await VortexFile.OpenAsync(path, VortexOpenOptions.Default, dead.Token));
        await Probe("CountAsync", async () => await file.ScanBuilder().CountAsync(dead.Token));
        await Probe("CountAsync with a filter", async () => await file.ScanBuilder().Where(recent).CountAsync(dead.Token));
        await Probe("AnyAsync", async () => await file.ScanBuilder().AnyAsync(dead.Token));
        await Probe("MinAsync", async () => await file.ScanBuilder().MinAsync("celsius", dead.Token));
        await Probe("ExplainAsync", async () => await file.ScanBuilder().ExplainAsync(dead.Token));
        await Probe("ReadIndexesAsync", async () => await file.ReadIndexesAsync(dead.Token));
        await Probe("the scan itself", async () =>
        {
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(dead.Token))
            {
                batch.Dispose();
            }
        });
    }

    private static async Task Probe(string what, Func<Task> call)
    {
        try
        {
            await call();
            Console.WriteLine($"  {what}: answered anyway");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"  {what}: OperationCanceledException");
        }
    }
}
