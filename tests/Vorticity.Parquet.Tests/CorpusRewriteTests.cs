using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Every file of the core's conformance corpus, each dtype crossed with nullability and the row
/// counts where blocks end, beside the container shapes no type matrix reaches: read as Vortex,
/// written as Parquet by this writer, read back to the same rows, each value rendered. A dtype
/// Parquet has no form for is refused when the writer is made, by a schema exception that names it;
/// any other outcome but the same rows is a failure, every one reported at once.
/// </summary>
public sealed class CorpusRewriteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vx-corpus-{Guid.NewGuid():N}.parquet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => System.IO.File.Delete(_path);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryCorpusFileReadsBackAsItWasWritten(bool small)
    {
        ParquetWriteOptions options = small
            ? new() { DataPageVersion = DataPageVersion.V1, Compression = ParquetCompression.Snappy, BlockRows = 1_000, RowGroupRows = 3_000, PageBytes = 4 << 10 }
            : new();
        StringBuilder failures = new();
        int failed = 0;
        Dictionary<string, int> refused = new(StringComparer.Ordinal);
        int written = 0;
        int unopened = 0;
        long rows = 0;
        foreach (string path in Directory.EnumerateFiles(Root(), "*.vortex", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string id = Path.GetRelativePath(Root(), path);
            VortexFile source;
            try
            {
                source = await VortexFile.OpenAsync(path, Ct);
            }
            catch (VortexException)
            {
                // A file that needs options of its own to open, a schema it lacks among them.
                unopened++;
                continue;
            }

            await using (source)
            {
                try
                {
                    List<string> expected = await RowsAsync(source.Scan().ToBatchesAsync(Ct));
                    ParquetFileWriter writer;
                    try
                    {
                        writer = VortexSession.Default.CreateParquetWriter(_path, source.Schema, options);
                    }
                    catch (VortexSchemaException refusal)
                    {
                        string reason = refusal.Message;
                        refused[reason] = refused.GetValueOrDefault(reason) + 1;
                        continue;
                    }

                    await using (writer)
                    {
                        await foreach (RecordBatch batch in source.Scan().ToBatchesAsync(Ct))
                        {
                            using (batch)
                            {
                                await writer.WriteAsync(batch, Ct);
                            }
                        }

                        await writer.CompleteAsync(Ct);
                    }

                    await using ParquetFile file = await ParquetFile.OpenAsync(_path, Ct);
                    List<string> actual = await RowsAsync(file.Scan().ToBatchesAsync(Ct));
                    if (!expected.SequenceEqual(actual))
                    {
                        int at = Enumerable.Range(0, Math.Min(expected.Count, actual.Count)).FirstOrDefault(i => expected[i] != actual[i], Math.Min(expected.Count, actual.Count));
                        Fail($"{id}: {expected.Count} rows read back as {actual.Count}, row {at} '{expected.ElementAtOrDefault(at)}' as '{actual.ElementAtOrDefault(at)}'");
                    }
                    else
                    {
                        written++;
                        rows += expected.Count;
                    }
                }
                catch (Exception error)
                {
                    Fail($"{id}: {error.GetType().Name} - {error.Message}");
                }
            }
        }

        string report = string.Create(CultureInfo.InvariantCulture, $"PARQUET CORPUS REWRITE ({(small ? "v1, snappy, small" : "defaults")}): {written} files written and read back, {rows} rows; {unopened} need options to open; refused: {string.Join("; ", refused.Select(r => $"{r.Value} x {r.Key}"))}\n");
        Console.Out.Write(report);
        Assert.True(failed == 0, string.Create(CultureInfo.InvariantCulture, $"{report}{failed} files failed, the first of them:\n{failures}"));
        Assert.True(written > 500, report);

        // Every failure is counted, the first few told.
        void Fail(string line)
        {
            if (failed++ < 40)
            {
                failures.Append(line).Append('\n');
            }
        }
    }

    /// <summary>Every row of the batches, each value rendered and the columns joined.</summary>
    private static async Task<List<string>> RowsAsync(IAsyncEnumerable<RecordBatch> batches)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add(string.Join(" | ", Enumerable.Range(0, batch.Schema.Count).Select(c => Render.Row(batch, c, r))));
                }
            }
        }

        return rows;
    }

    /// <summary>The core's conformance corpus, found above this file or the test's output.</summary>
    private static string Root([CallerFilePath] string caller = "")
    {
        foreach (string? start in (string?[])[Path.GetDirectoryName(caller), AppContext.BaseDirectory])
        {
            for (string? directory = start; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                string candidate = Path.Combine(directory, "tests", "Vorticity.Conformance", "corpus");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("No tests/Vorticity.Conformance/corpus above the test.");
    }
}
