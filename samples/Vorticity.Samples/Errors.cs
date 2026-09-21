using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;

namespace Vorticity.Samples;

internal static class Errors
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await Show("512 zero bytes", async () =>
        {
            string broken = Demo.Path("broken.vortex");
            await System.IO.File.WriteAllBytesAsync(broken, new byte[512]);
            await using VortexFile file = await VortexFile.OpenAsync(broken);
        });

        await Show("a missing file", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(Demo.Path("no-such-file.vortex"));
        });

        await Show("a column the file does not have", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await foreach (RecordBatch batch in file.Scan().Project(["nope"]).ExecuteAsync())
            {
                batch.Dispose();
            }
        });

        await Show("a filter on a column the file does not have", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await file.Scan()
                .Where(Expr.Gt(Expr.Field("nope"), Expr.Literal(FilterLiteral.From(1))))
                .CountAsync();
        });

        await Show("a negative row index", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await foreach (RecordBatch batch in file.Scan().Take([-1L]).ExecuteAsync())
            {
                batch.Dispose();
            }
        });

        await Show("a range past the end", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            long rows = 0;
            await foreach (RecordBatch batch in file.Scan()
                .Rows(new RowRange(0, Demo.ReadingRows + 10)).ExecuteAsync())
            {
                using (batch)
                {
                    rows += batch.RowCount;
                }
            }

            Console.WriteLine($"  clamped to {rows} rows");
        });

        await Show("a batch read after it is disposed", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await foreach (RecordBatch batch in file.Scan().Rows(new RowRange(0, 2)).ExecuteAsync())
            {
                batch.Dispose();
                Console.WriteLine($"  {batch.RowCount} rows");
                break;
            }
        });

        // A whole file with rubbish appended after it: the version before the tear is still there.
        string torn = Demo.Path("torn.vortex");
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
        byte[] withTail = new byte[bytes.Length + 128];
        bytes.CopyTo(withTail, 0);
        await System.IO.File.WriteAllBytesAsync(torn, withTail);

        await Show("a torn tail, read as the version before it", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(torn);
            Console.WriteLine($"  {file.RowCount} rows, valid up to {file.TornTail?.ValidLength} " +
                $"of {file.TornTail?.FileLength}: {file.TornTail?.Reason}");
        });

        await Show("the same file, refusing the tear", async () =>
        {
            await using VortexFile file = await VortexFile.OpenAsync(
                torn, new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse });
        });

        await Show("repairing it", async () =>
        {
            VortexRepairResult result = await VortexFileRepair.RepairAsync(torn);
            Console.WriteLine($"  truncated: {result.Truncated}, {result.OriginalLength} -> {result.Length}");
        });

        // A disposed file refuses the questions whose answer would read a released buffer, and
        // answers the ones the open already settled. Ask it none of them either way.
        VortexFile disposed = await VortexFile.OpenAsync(path);
        await disposed.DisposeAsync();
        Ask("RowCount", () => _ = disposed.RowCount);
        Ask("Schema", () => _ = disposed.Schema);
        Ask("Scan()", () => _ = disposed.Scan());
        Ask("Identity", () => _ = disposed.Identity);
        Ask("SegmentSpecs", () => _ = disposed.SegmentSpecs.Length);
        Ask("GetArrayEncodingId(0)", () => _ = disposed.GetArrayEncodingId(0));
    }

    private static async Task Show(string what, Func<Task> call)
    {
        try
        {
            await call();
            Console.WriteLine($"{what}: no exception");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{what} -> {e.GetType().Name}: {First(e.Message)}");
        }
    }

    private static void Ask(string what, Action call)
    {
        try
        {
            call();
            Console.WriteLine($"  disposed.{what}: answered");
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine($"  disposed.{what}: ObjectDisposedException");
        }
    }

    private static string First(string message)
    {
        int end = message.IndexOf('\n');
        return end < 0 ? message : message[..end];
    }
}
