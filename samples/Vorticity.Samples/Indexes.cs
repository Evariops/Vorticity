using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.RowEncoding;

namespace Vorticity.Samples;

internal static partial class Indexes
{
    private const int Rows = 400_000;

    /// <summary>A request to a site: a session identifier, a path, a status and a score, none of them in order.</summary>
    [VortexRecord]
    public partial record struct Hit(string Session, string Path, int Status, int Score);

    internal static async Task RunAsync()
    {
        VortexSession session = VortexSession.Default;
        CancellationToken ct = CancellationToken.None;
        Hit[] hits = Hits(Rows);
        string plain = Demo.Path("indexes-none.vortex");
        string auto = Demo.Path("indexes-auto.vortex");
        string path = Demo.Path("indexes.vortex");

        WriteReport none = await Write(session, plain, hits, new VortexWriteOptions());
        Console.WriteLine($"IndexPolicy.None: {none.Bytes.Total} bytes");
        WriteReport automatic = await Write(session, auto, hits, new VortexWriteOptions { Indexes = IndexPolicy.Auto });
        Print("IndexPolicy.Auto", automatic);

        string known = hits[123_456].Session;
        await using (VortexFile file = await VortexFile.OpenAsync(auto))
        {
            await Pruned(file, "Session == a value that exists", r => r.Session == known);
            await Pruned(file, "Status == 301", r => r.Status == 301);
        }

        Console.WriteLine();
        VortexWriteOptions options = new()
        {
            Indexes = IndexPolicy.None
                .Bloom(Hit.ColumnNames.Session)
                .NgramBloom(Hit.ColumnNames.Path)
                .WithBudgetPerMille(300),
        };

        WriteReport report = await Write(session, path, hits, options);
        Print("Bloom on Session, NgramBloom on Path, budget 300 per mille", report);

        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            foreach (VortexIndexInfo index in await file.GetIndexesAsync(ct))
            {
                Console.WriteLine($"  listed: {index.Column} {index.Kind}, {index.Runs} runs over {index.Blocks} blocks of {index.BlockLength} rows, " +
                    $"{index.Entries} entries, {index.ListedBytes} bytes listed, layout {index.Layout}");
            }

            VortexIndexVerification verification = await file.VerifyIndexesAsync(ct);
            Console.WriteLine($"  verification holds: {verification.Holds} (held {verification.Held}, torn {verification.Torn.Count}, bare {verification.Bare})");

            await Pruned(file, "Session == a value that exists", r => r.Session == known);
            await Pruned(file, "Session == a value that does not", r => r.Session == "8badf00dzzzz");
            await Pruned(file, "Path.Contains(\"checkout\")", r => r.Path.Contains("checkout"));
        }

        Console.WriteLine();
        report = await Write(session, path, hits, new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.Postings(Hit.ColumnNames.Status).SortedRuns(Hit.ColumnNames.Score).WithBudgetPerMille(3_000),
        });
        Print("Postings on Status, SortedRuns on Score, budget 3000 per mille", report);
        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            int score = hits[123_456].Score;
            await Pruned(file, "Status == 301", r => r.Status == 301);
            await Pruned(file, "Score == a value that exists", r => r.Score == score);
            await Pruned(file, "Score between 1000 and 1100", r => r.Score.Between(1_000, 1_100));
            await using KeyCursor<int> cursor = await file.Scan<Hit>().Keys(r => r.Score).OpenAsync(ct);
            await cursor.SeekAsync(1_000, SeekOp.AtOrAfter, ct);
            Console.WriteLine($"  a key cursor on Score: the first key at or after 1000 is {cursor.Key}, at row {cursor.Row}; {await cursor.RankAsync(1_000, ct)} rows hold a smaller one");
        }

        await using (VortexFile file = await VortexFile.OpenAsync(plain))
        {
            try
            {
                await using KeyCursor<int> cursor = await file.Scan<Hit>().Keys(r => r.Score).OpenAsync(ct);
            }
            catch (VortexUnsupportedException e)
            {
                Console.WriteLine($"  the same cursor without the index: {e.Kind} {e.ComponentId}: {e.Message}");
            }
        }

        // The index added to the file written without one, in place: the data stays, the tail is
        // written again with the index regions, and the file takes a new identity.
        string later = Demo.Path("indexes-later.vortex");
        System.IO.File.Copy(plain, later, overwrite: true);
        long before = new FileInfo(later).Length;
        IReadOnlyList<IndexWriteReport> added = await VortexFileIndexer.AppendIndexesAsync(
            later, IndexPolicy.None.SortedRuns(Hit.ColumnNames.Score).WithBudgetPerMille(3_000), ct);
        await using (VortexFile file = await VortexFile.OpenAsync(later))
        {
            await using KeyCursor<int> cursor = await file.Scan<Hit>().Keys(r => r.Score).OpenAsync(ct);
            await cursor.SeekAsync(1_000, SeekOp.AtOrAfter, ct);
            Console.WriteLine($"  added afterwards: {added[0].Kind} {added[0].Outcome}, {added[0].Bytes} bytes; the file grew from {before} to {file.Length} bytes; " +
                $"the cursor finds {cursor.Key} at row {cursor.Row}");
        }

        Console.WriteLine();
        report = await Write(session, path, hits, new VortexWriteOptions
        {
            Indexes = IndexPolicy.None
                .ForKey([Hit.ColumnNames.Status, Hit.ColumnNames.Score], IndexKind.SortedRuns, new RowKeyEncoder())
                .WithBudgetPerMille(3_000),
        });
        Print("a composite key (Status, Score) through a RowKeyEncoder", report);
        await using (VortexFile file = await VortexFile.OpenAsync(path))
        {
            await Pruned(file, "Status == 301 && Score >= 1000", r => r.Status == 301 & r.Score >= 1_000);
        }

        Console.WriteLine();
        WriteReport tight = await Write(session, path, hits, new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.Bloom(Hit.ColumnNames.Session).NgramBloom(Hit.ColumnNames.Path),
        });
        Print("Bloom and NgramBloom, default budget of 100 per mille", tight);

        WriteReport noEncoder = await Write(session, path, hits, new VortexWriteOptions
        {
            Indexes = IndexPolicy.None.ForKey([Hit.ColumnNames.Status, Hit.ColumnNames.Session], IndexKind.SortedRuns).WithBudgetPerMille(1_000),
        });
        Print("a composite key without an encoder", noEncoder);

        try
        {
            await Write(session, path, hits, new VortexWriteOptions
            {
                Indexes = IndexPolicy.None.Bloom(Hit.ColumnNames.Session, falsePositiveRate: 0.000_001, required: true).WithBudgetPerMille(50),
            });
            Console.WriteLine("a required index over its budget: written");
        }
        catch (VortexException e)
        {
            Console.WriteLine($"a required index over its budget: {e.GetType().Name}: {e.Message}");
            Console.WriteLine($"  the path still holds the file written before it: {System.IO.File.Exists(path)}");
        }

        try
        {
            await using VortexFileWriter refused = session.CreateWriter<Hit>(path, new VortexWriteOptions { Indexes = IndexPolicy.None.Bloom("Referrer") });
            Console.WriteLine("an index on a column that does not exist: accepted");
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"an index on a column that does not exist: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Print(string what, WriteReport report)
    {
        Console.WriteLine($"{what}: {report.Bytes.Total} bytes, {report.Bytes.Indexes} of them indexes");
        foreach (IndexWriteReport index in report.Indexes)
        {
            Console.WriteLine($"  {index.Column} {index.Kind}: {index.Outcome}, {index.Bytes} bytes{(index.Reason is null ? string.Empty : " -- " + index.Reason)}");
        }
    }

    private static async Task Pruned(VortexFile file, string what, Func<Probe<Hit>, Predicate> filter)
    {
        ScanPlan with = await file.Scan<Hit>().Where(filter).ExplainAsync();
        ScanPlan without = await file.Scan<Hit>().Where(filter).With(new ScanOptions { UseIndexes = false }).ExplainAsync();
        Scan<Hit> scan = file.Scan<Hit>().Where(filter);
        long rows = await scan.CountAsync();
        string steps = string.Join(", ", Array.ConvertAll([.. with.Pruning], s => $"{s.Structure} pruned {s.BlocksPruned} reading {s.BytesRead} bytes"));
        Console.WriteLine($"  {what}: {rows} rows; {with.LiveBlocks} of {with.Blocks} blocks and {with.BytesToRead} bytes with the indexes " +
            $"({steps}), {without.LiveBlocks} blocks and {without.BytesToRead} bytes without");
    }

    private static async Task<WriteReport> Write(VortexSession session, string path, Hit[] hits, VortexWriteOptions options)
    {
        await using VortexFileWriter writer = session.CreateWriter<Hit>(path, options);
        await writer.WriteAsync<Hit>(hits.AsSpan());
        return await writer.CompleteAsync();
    }

    private static Hit[] Hits(int count)
    {
        int[] statuses = [200, 200, 200, 200, 200, 200, 404, 404, 500, 200];
        Hit[] hits = new Hit[count];
        for (int row = 0; row < count; row++)
        {
            ulong scrambled = (ulong)row * 0x9E3779B97F4A7C15UL;
            string sessionId = scrambled.ToString("x16")[..12];
            string pagePath = row % 40_000 == 17 ? $"/cart/checkout/{row}" : $"/products/{scrambled % 5_000}";
            int status = row % 25_000 == 3 ? 301 : statuses[row % statuses.Length];
            hits[row] = new Hit(sessionId, pagePath, status, (int)(scrambled >> 44));
        }

        return hits;
    }
}
