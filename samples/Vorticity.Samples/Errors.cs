using System;
using System.IO;
using System.Threading.Tasks;

namespace Vorticity.Samples;

[VortexRecord]
public partial record struct DayAndSite(int Day, Guid Site);

[VortexRecord]
public partial record struct Humidity(int Day, double Percent);

[VortexRecord]
public partial record struct RequiredCelsius(int Day, double Celsius);

internal static class Errors
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await Show("512 zero bytes", async () =>
        {
            string broken = Demo.Path("broken.vortex");
            await System.IO.File.WriteAllBytesAsync(broken, new byte[512]);
            await using VortexFile opened = await VortexFile.OpenAsync(broken);
        });

        await Show("a record member the file has no column for", async () =>
            await file.Scan<Humidity>().CountAsync());

        await Show("a non-nullable member over a nullable column", async () =>
            await file.Scan<RequiredCelsius>().CountAsync());

        await Show("a text literal against an integer column", async () =>
        {
            string threshold = "900";
            await file.Scan("Day").Where($"Day >= {threshold}").CountAsync();
        });

        await Show("a filter that does not parse", async () =>
            await file.Scan().Where(VortexExpr.Parse("Day >=")).CountAsync());

        await Show("a column the file does not have, by name", async () =>
            await file.Scan("nope").CountAsync());

        await Show("a key cursor over a column nothing orders", async () =>
        {
            await using KeyCursor<double?> cursor = await file.Scan<Reading>().Keys(r => r.Celsius).OpenAsync();
        });

        await Show("a uuid column written to an edition older than uuid", async () =>
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<DayAndSite>(
                Demo.Path("old-edition.vortex"), new VortexWriteOptions { TargetEdition = VortexEdition.Core20250500 });
        });

        await Show("a nullable builder over a non-nullable column", async () =>
        {
            await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(Demo.Path("builder.vortex"));
            writer.Builder().Column<int?>("Day").AppendNull();
        });

        await Show("a missing file", async () =>
        {
            await using VortexFile missing = await VortexFile.OpenAsync(Demo.Path("no-such-file.vortex"));
        });

        await Show("a row index past the end", async () =>
        {
            await foreach (Columns<Reading> batch in file.Scan<Reading>().Rows(Demo.ReadingRows))
            {
                _ = batch.RowCount;
            }
        });

        await Show("a row range past the end", async () =>
        {
            long rows = await file.Scan<Reading>().Rows(new RowRange(0, Demo.ReadingRows + 10)).CountAsync();
            Console.WriteLine($"  {rows} rows");
        });

        await Show("a scan used twice", async () =>
        {
            Scan<Reading> scan = file.Scan<Reading>();
            await scan.CountAsync();
            await scan.CountAsync();
        });

        await Show("an owned batch read after it is disposed", async () =>
        {
            await foreach (RecordBatch batch in file.Scan<Reading>().Rows(new RowRange(0, 2)).ToBatchesAsync())
            {
                batch.Dispose();
                _ = batch.View.RowCount;
            }
        });

        await Show("a session disposed while one of its files is open", async () =>
        {
            VortexSession session = VortexSession.Create(o => o.MaxConcurrentReads = 4);
            VortexFile held = await session.OpenAsync(path);
            try
            {
                await session.DisposeAsync();
            }
            finally
            {
                await held.DisposeAsync();
                await session.DisposeAsync();
            }
        });

        VortexFile disposed = await VortexFile.OpenAsync(path);
        await disposed.DisposeAsync();
        Ask("RowCount", () => _ = disposed.RowCount);
        Ask("Schema", () => _ = disposed.Schema);
        Ask("Statistics", () => _ = disposed.Statistics[0]);
        Ask("Identity", () => _ = disposed.Identity);
        Ask("SegmentMap", () => _ = disposed.SegmentMap.Length);
        Ask("Scan<Reading>()", () => _ = disposed.Scan<Reading>());
        await Show("  a scan of the disposed file", async () => await disposed.Scan<Reading>().Where(r => r.Day == 5).CountAsync());
    }

    private static async Task Show(string what, Func<Task> call)
    {
        try
        {
            await call();
            Console.WriteLine($"{what}: no exception");
        }
        catch (VortexUnsupportedException e)
        {
            Console.WriteLine($"{what} -> {e.GetType().Name} ({e.Kind}, {e.ComponentId}): {First(e.Message)}");
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
        catch (Exception e)
        {
            Console.WriteLine($"  disposed.{what}: {e.GetType().Name}");
        }
    }

    private static string First(string message)
    {
        int end = message.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? message : message[..end];
    }
}
