using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class AppendAndRepair
{
    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        string path = Demo.Path("append.vortex");
        Reading[] rows = Readings(100_000);

        await Create(session, path, rows.AsMemory(0, 16_384));
        Guid identity = await IdentityOf(path);
        Reading[] more = rows[16_384..24_576];

        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            long resumeAt = appender.RowCount;                         // the file's row count, since it ended on a block
            await appender.WriteAsync<Reading>(more.AsSpan(), ct);
            WriteReport report = await appender.CompleteAsync(ct);
            Console.WriteLine($"appended {more.Length} rows to 16384: resumed at {resumeAt}, the file now holds {report.RowCount}");
        }

        Console.WriteLine($"the identity is {(identity == await IdentityOf(path) ? "unchanged" : "drawn anew")} by an append");

        await Create(session, path, rows.AsMemory(0, 20_000));
        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            long resumeAt = appender.RowCount;
            await appender.WriteAsync<Reading>(rows.AsSpan(20_000, 5_000), ct);
            long accepted = appender.RowCount;
            WriteReport report = await appender.CompleteAsync(ct);
            Console.WriteLine($"appended 5000 rows to 20000: resumed at {resumeAt}, RowCount {accepted} after the write, the file now holds {report.RowCount}");
        }

        Console.WriteLine();
        await Rounds(session, path, rows, "appends of 8192 onto 16384", 16_384, 8_192, 4);
        await Rounds(session, path, rows, "appends of 5000 onto 20000", 20_000, 5_000, 4);

        Console.WriteLine();
        await Giving(session, path, rows);

        Console.WriteLine();
        await Torn(session, path, rows, ct);
    }

    /// <summary>Four appends of each size, then the same rows written once.</summary>
    private static async Task Rounds(VortexSession session, string path, Reading[] rows, string what, int initial, int each, int rounds)
    {
        await Create(session, path, rows.AsMemory(0, initial));
        Console.WriteLine($"{what}: {initial} rows, {new FileInfo(path).Length} bytes");
        long total = initial;
        for (int round = 0; round < rounds; round++)
        {
            await using VortexFileWriter appender = await session.OpenWriterAsync(path);
            long resumed = appender.RowCount;
            await appender.WriteAsync<Reading>(rows.AsSpan((int)total, each));
            WriteReport report = await appender.CompleteAsync();
            total = report.RowCount;
            Console.WriteLine($"  resumed at {resumed}: {total} rows, {new FileInfo(path).Length} bytes");
        }

        string once = Demo.Path("append-once.vortex");
        await Create(session, once, rows.AsMemory(0, (int)total));
        Console.WriteLine($"  the same {total} rows written once: {new FileInfo(once).Length} bytes");
    }

    /// <summary>Abandon and disposal without completion, on a created file and on an append.</summary>
    private static async Task Giving(VortexSession session, string path, Reading[] rows)
    {
        await Create(session, path, rows.AsMemory(0, 16_384));
        long length = new FileInfo(path).Length;
        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            await appender.WriteAsync<Reading>(rows.AsSpan(16_384, 50_000));
            await appender.FlushAsync();
            long grown = new FileInfo(path).Length;
            appender.Abandon();
            Console.WriteLine($"an append abandoned after a flush: {grown} bytes on disk before Abandon, {new FileInfo(path).Length} after, {length} before the append");
        }

        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            await appender.WriteAsync<Reading>(rows.AsSpan(16_384, 50_000));
            await appender.FlushAsync();
        }

        Console.WriteLine($"an append disposed without CompleteAsync: {new FileInfo(path).Length} bytes, {length} before");

        string created = Demo.Path("abandoned.vortex");
        await using (VortexFileWriter writer = session.CreateWriter<Reading>(created))
        {
            await writer.WriteAsync<Reading>(rows.AsSpan(0, 50_000));
            await writer.FlushAsync();
            writer.Abandon();
        }

        Console.WriteLine($"a created file abandoned: exists {System.IO.File.Exists(created)}");

        await using (VortexFileWriter writer = session.CreateWriter<Reading>(created))
        {
            await writer.WriteAsync<Reading>(rows.AsSpan(0, 50_000));
        }

        Console.WriteLine($"a created file disposed without CompleteAsync: exists {System.IO.File.Exists(created)}");

        try
        {
            await using VortexFile open = await VortexFile.OpenAsync(path);
            await using VortexFileWriter refused = await session.OpenWriterAsync(path);
            Console.WriteLine("an append while the file is open for reading: accepted");
            refused.Abandon();
        }
        catch (IOException e)
        {
            Console.WriteLine($"an append while the file is open for reading: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>A completed append cut short, read as the version before it, then repaired.</summary>
    private static async Task Torn(VortexSession session, string path, Reading[] rows, CancellationToken ct)
    {
        await Create(session, path, rows.AsMemory(0, 16_384));
        long before = new FileInfo(path).Length;
        await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
        {
            await appender.WriteAsync<Reading>(rows.AsSpan(16_384, 8_192));
            await appender.CompleteAsync();
        }

        long appended = new FileInfo(path).Length;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(appended - 100);                          // the append's last bytes never reached the disk
        }

        Console.WriteLine($"16384 rows in {before} bytes, 24576 after the append in {appended}, cut to {appended - 100}");

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            if (file.TornTail is { } torn)
            {
                Console.WriteLine($"reading the version before the torn append: {file.RowCount} rows, valid to {torn.ValidLength} of {torn.FileLength}");
                Console.WriteLine($"  why: {torn.Reason}");
            }
        }

        try
        {
            await using VortexFile refused = await session.OpenAsync(path, new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse });
        }
        catch (VortexFormatException e)
        {
            Console.WriteLine($"TornTail = Refuse: {e.GetType().Name}");
        }

        try
        {
            await using VortexFileWriter refused = await session.OpenWriterAsync(path);
        }
        catch (VortexFormatException e)
        {
            Console.WriteLine($"an append to the torn file: {e.Message}");
        }

        long valid = await VortexFileRepair.GetValidLengthAsync(path, ct);
        VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path, ct);   // truncates to the last version that parses
        Console.WriteLine($"valid length {valid}; repaired: truncated {repaired.Truncated}, {repaired.OriginalLength} -> {repaired.Length}");

        await using (VortexFileWriter again = await session.OpenWriterAsync(path))
        {
            await again.WriteAsync<Reading>(rows.AsSpan(16_384, 8_192), ct);
            WriteReport report = await again.CompleteAsync(ct);
            Console.WriteLine($"the repaired file appends again: {report.RowCount} rows");
        }

        string cut = Demo.Path("cut.vortex");
        await Create(session, cut, rows.AsMemory(0, 16_384));
        using (FileStream stream = new FileStream(cut, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length / 2);
        }

        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(cut);
        }
        catch (VortexFormatException e)
        {
            Console.WriteLine($"a file cut in half, with no complete version: {e.GetType().Name} on open");
        }
    }

    private static async Task Create(VortexSession session, string path, ReadOnlyMemory<Reading> rows)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
        await writer.WriteAsync<Reading>(rows.Span);
        await writer.CompleteAsync();
    }

    private static async Task<Guid> IdentityOf(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path);
        return file.Identity;
    }

    private static Reading[] Readings(int count)
    {
        Reading[] readings = new Reading[count];
        for (int row = 0; row < count; row++)
        {
            readings[row] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Demo.Cities[row / 7 % Demo.Cities.Length]);
        }

        return readings;
    }
}
