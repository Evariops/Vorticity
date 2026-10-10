using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class CancelWork
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        using (CancellationTokenSource cts = new CancellationTokenSource())
        {
            int batches = 0;
            try
            {
                await foreach (Columns<Reading> cols in file.Scan<Reading>().WithCancellation(cts.Token))
                {
                    if (cols.RowCount > 0 && ++batches == 2)
                    {
                        await cts.CancelAsync();
                    }
                }

                Console.WriteLine($"the scan ended on its own after {batches} batches");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"borrowed columns: cancelled after {batches} batches");
            }
        }

        using (CancellationTokenSource cts = new CancellationTokenSource())
        {
            long rows = 0;
            try
            {
                await foreach (Reading r in file.Scan<Reading>().ToRecordsAsync(cts.Token))
                {
                    if (++rows == 10_000)
                    {
                        await cts.CancelAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"rows: cancelled after {rows} rows");
            }
        }

        using (CancellationTokenSource cts = new CancellationTokenSource())
        {
            int owned = 0;
            try
            {
                await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(cts.Token))
                {
                    using (batch)
                    {
                        if (++owned == 3)
                        {
                            await cts.CancelAsync();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"owned batches: cancelled after {owned}, each disposed by its using");
            }
        }

        using CancellationTokenSource dead = new CancellationTokenSource();
        await dead.CancelAsync();
        CancellationToken ct = dead.Token;
        Console.WriteLine("with a token cancelled before the call:");
        await ProbeAsync("OpenAsync", async () => await (await VortexSession.Default.OpenAsync(path, null, ct)).DisposeAsync());
        await ProbeAsync("CountAsync", async () => await file.Scan<Reading>().CountAsync(ct));
        await ProbeAsync("CountAsync, filtered", async () => await file.Scan<Reading>().Where(r => r.Celsius > 45.0).CountAsync(ct));
        await ProbeAsync("AnyAsync", async () => await file.Scan<Reading>().AnyAsync(ct));
        await ProbeAsync("MaxAsync", async () => await file.Scan<Reading>().MaxAsync(r => r.Celsius, ct));
        await ProbeAsync("SumAsync", async () => await file.Scan<Reading>().SumAsync(r => r.Celsius, ct));
        await ProbeAsync("SumAsync, filtered", async () => await file.Scan<Reading>().Where(r => r.Day >= 900).SumAsync(r => r.Celsius, ct));
        await ProbeAsync("ExplainAsync", async () => await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync(ct));
        await ProbeAsync("GetIndexesAsync", async () => await file.GetIndexesAsync(ct));
        await ProbeAsync("borrowed columns", async () =>
        {
            await using Scan<Reading>.AsyncEnumerator e = file.Scan<Reading>().GetAsyncEnumerator(ct);
            await e.MoveNextAsync();
        });
        await ProbeAsync("ToRecordsAsync", async () =>
        {
            await foreach (Reading _ in file.Scan<Reading>().ToRecordsAsync(ct))
            {
            }
        });
        await ProbeAsync("Keys().OpenAsync", async () => await (await file.Scan<Reading>().Keys(r => r.Day).OpenAsync(ct)).DisposeAsync());

        await using (KeyCursor<int> cursor = await file.Scan<Reading>().Keys(r => r.Day).OpenAsync())
        {
            await cursor.SeekAsync(500, SeekOp.Exact);
            await ProbeAsync("KeyCursor.SeekAsync", async () => await cursor.SeekAsync(900, SeekOp.Exact, ct));
            Console.WriteLine($"  the cursor after it: valid {cursor.IsValid}");
            await ProbeAsync("  its Key", () => Task.FromResult(cursor.Key));
            Console.WriteLine($"  a new seek with no token: {await cursor.SeekAsync(900, SeekOp.Exact)}, key {cursor.Key}");
        }

        await WritesAsync(ct);
    }

    private static async Task WritesAsync(CancellationToken cancelled)
    {
        Reading[] rows = new Reading[50_000];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Reading(i / 1_000, 20.0, "Paris");
        }

        string created = Demo.Path("cancelled.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(created))
        {
            await writer.WriteAsync<Reading>(rows);
            await ProbeAsync("WriteAsync", async () => await writer.WriteAsync<Reading>(rows, cancelled));
            Console.WriteLine($"  the writer after it: {writer.RowCount} rows accepted");
            await ProbeAsync("FlushAsync", async () => await writer.FlushAsync(cancelled));
            await ProbeAsync("CompleteAsync", async () => await writer.CompleteAsync(cancelled));
            await ProbeAsync("CompleteAsync again, no token", async () => await writer.CompleteAsync());
        }

        Console.WriteLine($"  a created file, disposed after a cancelled CompleteAsync: exists {System.IO.File.Exists(created)}");

        string appended = Demo.Path("appended.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(appended))
        {
            await writer.WriteAsync<Reading>(rows);
            await writer.CompleteAsync();
        }

        long length = new FileInfo(appended).Length;
        await using (VortexFileWriter appender = await VortexSession.Default.OpenWriterAsync(appended))
        {
            await appender.WriteAsync<Reading>(rows);
            await appender.FlushAsync();
            await ProbeAsync("CompleteAsync of an append", async () => await appender.CompleteAsync(cancelled));
        }

        await using VortexFile file = await VortexFile.OpenAsync(appended);
        Console.WriteLine($"  the appended file afterwards: {new FileInfo(appended).Length} bytes, as before: {new FileInfo(appended).Length == length}, {file.RowCount} rows");
    }

    private static async Task ProbeAsync(string what, Func<Task> call)
    {
        try
        {
            await call();
            Console.WriteLine($"  {what}: answered");
        }
        catch (OperationCanceledException e)
        {
            Console.WriteLine($"  {what}: {e.GetType().Name}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {what}: {e.GetType().Name}: {e.Message}");
        }
    }
}
