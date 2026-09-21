using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class Indexes
{
    private const int Budget = 500;

    internal static async Task RunAsync()
    {
        // What the default policy does on the column an equality is asked of -- a session
        // identifier -- and on a column of five city names.
        await Written("sessions, Auto", VortexWriteOptions.Default, sessions: true);
        await Written("cities, Auto", VortexWriteOptions.Default, sessions: false);

        // Asked for by name. The budget is what decides, so raise it or the filters do not fit.
        await Written("sessions, a Bloom asked for by name",
            new VortexWriteOptions { Indexes = WritePolicy.None.For("session", IndexPolicy.Bloom()) },
            sessions: true);
        await Written("sessions, the same with the budget raised",
            new VortexWriteOptions
            {
                Indexes = WritePolicy.None.For("session", IndexPolicy.Bloom()),
                IndexBudgetPerMille = Budget,
            },
            sessions: true);
        await Written("sessions, at one false positive in a million",
            new VortexWriteOptions
            {
                Indexes = WritePolicy.None.For("session", IndexPolicy.Bloom(falsePositivePpm: 1)),
                IndexBudgetPerMille = Budget,
            },
            sessions: true);
        await Written("cities, a Bloom made required",
            new VortexWriteOptions { Indexes = WritePolicy.None.For("city", IndexPolicy.Bloom().AsRequired()) },
            sessions: false);

        // What a file says about the indexes it carries, and whether they still describe its bytes.
        string path = Demo.Path("indexed.vortex");
        await Demo.WriteSessionsAsync(path, new VortexWriteOptions
        {
            Indexes = WritePolicy.Auto.For("session", IndexPolicy.Bloom()),
            IndexBudgetPerMille = Budget,
        });

        await using VortexFile file = await VortexFile.OpenAsync(path);
        foreach (VortexIndexInfo index in await file.ReadIndexesAsync())
        {
            Console.WriteLine($"  {index.Column} {index.Kind}: {index.Runs} runs over {index.Blocks} blocks " +
                $"of {index.BlockLength} rows, {index.ListedBytes} bytes listed, layout {index.Layout}");
        }

        VortexIndexVerification verification = await file.VerifyIndexesAsync();
        Console.WriteLine($"verification holds: {verification.Holds} " +
            $"(held {verification.Held}, torn {verification.Torn.Count}, bare {verification.Bare})");

        string unindexed = Demo.Path("unindexed.vortex");
        await Demo.WriteSessionsAsync(unindexed, new VortexWriteOptions { Indexes = WritePolicy.None });

        await Cost("a session that exists", Demo.Session(123_456));
        await Cost("a session that does not", "zzzzzzzzzzzz");

        async Task Cost(string what, string session)
        {
            VortexExpr equals = Expr.Eq(Expr.Field("session"), Expr.Literal(FilterLiteral.From(session)));
            (long indexed, long indexedBytes, long rows) =
                await Demo.MeasureAsync(path, f => f.Scan().Where(equals));
            (long plain, long plainBytes, long plainRows) =
                await Demo.MeasureAsync(unindexed, f => f.Scan().Where(equals));
            Console.WriteLine($"{what}: indexed {indexed} rounds and {indexedBytes} bytes for {rows} rows; " +
                $"unindexed {plain} rounds and {plainBytes} bytes for {plainRows}");
        }

        // Indexing a file that was written without any, three ways, from the same starting file.
        await AppendTo("nothing asked for", WritePolicy.None, VortexWriteOptions.Default);
        await AppendTo("a Bloom over the budget", WritePolicy.None.For("session", IndexPolicy.Bloom()),
            VortexWriteOptions.Default);
        await AppendTo("a Bloom within it", WritePolicy.None.For("session", IndexPolicy.Bloom()),
            new VortexWriteOptions { IndexBudgetPerMille = Budget });

        async Task AppendTo(string what, WritePolicy policy, VortexWriteOptions options)
        {
            string bare = Demo.Path("bare.vortex");
            await Demo.WriteSessionsAsync(bare, new VortexWriteOptions { Indexes = WritePolicy.None });
            long before = new FileInfo(bare).Length;
            foreach (IndexWriteReport report in await VortexFileIndexer.AppendIndexesAsync(bare, policy, options))
            {
                Console.WriteLine($"  {report.Path} {report.Kind}: {report.Outcome}, " +
                    $"{report.Bytes} bytes, {report.Runs} runs");
            }

            await using (VortexFile grown = await VortexFile.OpenAsync(bare))
            {
                Console.WriteLine($"{what}: {before} -> {new FileInfo(bare).Length} bytes, " +
                    $"{(await grown.ReadIndexesAsync()).Count} indexes listed");
            }

            System.IO.File.Delete(bare);
        }
    }

    private static async Task Written(string what, VortexWriteOptions options, bool sessions)
    {
        string path = Demo.Path("indexes.vortex");
        WriteReport report = sessions
            ? await Demo.WriteSessionsAsync(path, options)
            : await Demo.WriteCitiesAsync(path, options);
        Console.WriteLine($"{what}: {new FileInfo(path).Length} bytes, {report.Bytes.Indexes} of them indexes");
        foreach (IndexWriteReport index in report.Indexes)
        {
            Console.WriteLine($"  {index.Path} {index.Kind}: {index.Outcome}" +
                (index.Outcome == IndexOutcome.Built
                    ? $", {index.Runs} runs, {index.Bytes} bytes"
                    : $" -- {index.Reason}"));
        }

        await using (VortexFile written = await VortexFile.OpenAsync(path))
        {
            Console.WriteLine($"  the file lists {(await written.ReadIndexesAsync()).Count} indexes");
        }

        System.IO.File.Delete(path);
    }
}
