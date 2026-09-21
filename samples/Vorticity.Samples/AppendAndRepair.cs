using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class AppendAndRepair
{
    private const int BlockRows = 8_192;

    internal static async Task RunAsync()
    {
        // The same rows reached two ways: batches that end on a block boundary, and batches that
        // do not. What differs is where each append resumes.
        await Rounds("batches of 8192 onto 16384", 16_384, BlockRows, 4);
        await Rounds("batches of 5000 onto 20000", 20_000, 5_000, 4);

        string path = Demo.Path("appended.vortex");
        await Demo.WriteCitiesAsync(path, VortexWriteOptions.Default, rows: 16_384);
        DTypeArena types = new DTypeArena();
        DType schema = Demo.CitiesSchema(types);

        Guid? before = await IdentityOf(path);
        await using (VortexFileWriter appender = await VortexFileWriter.AppendAsync(path))
        {
            using RecordBatch batch = Demo.CitiesBatch(
                types, schema, BlockRows, appender.RowCount, everyThousandthIsNull: false);
            await appender.WriteAsync(batch);
            await appender.CompleteAsync();
        }

        Console.WriteLine($"the identity is {(before == await IdentityOf(path) ? "unchanged" : "minted anew")} " +
            "by an append");

        // Abandon leaves the file as it was.
        long unchanged = new FileInfo(path).Length;
        await using (VortexFileWriter abandoning = await VortexFileWriter.AppendAsync(path))
        {
            using RecordBatch batch = Demo.CitiesBatch(
                types, schema, BlockRows, abandoning.RowCount, everyThousandthIsNull: false);
            await abandoning.WriteAsync(batch);
            abandoning.Abandon();
        }

        Console.WriteLine($"after Abandon: {new FileInfo(path).Length} bytes, was {unchanged}");

        try
        {
            await using VortexFile held = await VortexFile.OpenAsync(path);
            await using VortexFileWriter refused = await VortexFileWriter.AppendAsync(path);
        }
        catch (IOException)
        {
            Console.WriteLine("appending to a file that is open for reading: IOException");
        }

        // A tear: a whole file with bytes after it, as a crash mid-append leaves.
        string torn = Demo.Path("torn.vortex");
        byte[] whole = await System.IO.File.ReadAllBytesAsync(path);
        byte[] withTail = new byte[whole.Length + 128];
        whole.CopyTo(withTail, 0);
        await System.IO.File.WriteAllBytesAsync(torn, withTail);

        await using (VortexFile fallen = await VortexFile.OpenAsync(torn))
        {
            Console.WriteLine($"torn, read as the version before it: {fallen.RowCount} rows, " +
                $"valid to {fallen.TornTail?.ValidLength} of {fallen.TornTail?.FileLength}");
        }

        try
        {
            await using VortexFileWriter refused = await VortexFileWriter.AppendAsync(torn);
        }
        catch (VortexFormatException)
        {
            Console.WriteLine("appending to a torn file: VortexFormatException");
        }

        Console.WriteLine($"valid length: {await VortexFileRepair.ValidLengthAsync(torn)}");
        VortexRepairResult result = await VortexFileRepair.RepairAsync(torn);
        Console.WriteLine($"repaired: truncated {result.Truncated}, {result.OriginalLength} -> {result.Length}");

        await using (VortexFileWriter again = await VortexFileWriter.AppendAsync(torn))
        {
            using RecordBatch batch = Demo.CitiesBatch(
                types, schema, BlockRows, again.RowCount, everyThousandthIsNull: false);
            await again.WriteAsync(batch);
            WriteReport report = await again.CompleteAsync();
            Console.WriteLine($"the repaired file appends again: {report.RowCount} rows");
        }
    }

    private static async Task Rounds(string what, int initial, int each, int rounds)
    {
        string path = Demo.Path("rounds.vortex");
        await Demo.WriteCitiesAsync(path, VortexWriteOptions.Default, initial);
        Console.WriteLine($"{what}: {initial} rows, {new FileInfo(path).Length} bytes");

        DTypeArena types = new DTypeArena();
        DType schema = Demo.CitiesSchema(types);
        long rows = initial;
        for (int round = 0; round < rounds; round++)
        {
            await using VortexFileWriter appender = await VortexFileWriter.AppendAsync(path);
            long resumed = appender.RowCount;
            using RecordBatch batch = Demo.CitiesBatch(types, schema, each, rows, everyThousandthIsNull: false);
            await appender.WriteAsync(batch);
            WriteReport report = await appender.CompleteAsync();
            rows = report.RowCount;
            Console.WriteLine($"  resumed at {resumed}: {rows} rows, {new FileInfo(path).Length} bytes");
        }

        string once = Demo.Path("once.vortex");
        await Demo.WriteCitiesAsync(once, VortexWriteOptions.Default, (int)rows);
        Console.WriteLine($"  the same {rows} rows written once: {new FileInfo(once).Length} bytes");
        System.IO.File.Delete(path);
        System.IO.File.Delete(once);
    }

    private static async ValueTask<Guid?> IdentityOf(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path);
        return file.StoredIdentity;
    }
}
