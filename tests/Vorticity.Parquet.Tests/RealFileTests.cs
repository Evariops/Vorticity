using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Files from the real world, written by other writers, read whole: every column of every row
/// decoded, and the rows counted against the footer. The files are not in the repository; the
/// directory <c>VORTICITY_PARQUET_DATA</c> names holds them, and the tests are skipped without it.
/// </summary>
/// <remarks>
/// A file is untrusted input: one this reader cannot read must say so with
/// <see cref="ParquetUnsupportedException"/>, one that is malformed with
/// <see cref="ParquetFormatException"/>, and anything else escaping is a failure.
/// <c>VORTICITY_PARQUET_REPORT</c> names a file the outcome of each one is written to.
/// </remarks>
public sealed class RealFileTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    [Fact]
    public async Task ReadsEveryRealFileWholeOrSaysWhyNot()
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        using StreamWriter? report = reportPath is null ? null : new StreamWriter(reportPath, append: false);
        List<string> failures = [];
        int read = 0;
        int unsupported = 0;
        int malformed = 0;
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(Root!, path);
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                await using ParquetFile file = await ParquetFile.OpenAsync(path, ct);
                double opened = clock.Elapsed.TotalMilliseconds;
                long rows = 0;
                await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(ct))
                {
                    using (batch)
                    {
                        rows += batch.RowCount;
                    }
                }

                if (rows != file.RowCount)
                {
                    failures.Add($"{name}: {rows} rows read where the footer counts {file.RowCount}");
                }

                read++;
                report?.WriteLine($"read        {name}: {file.RowCount:N0} rows, {file.RowGroupCount} row groups, {file.Schema.Count} columns, opened in {opened:N1} ms, read in {clock.Elapsed.TotalMilliseconds:N0} ms");
            }
            catch (ParquetUnsupportedException e)
            {
                unsupported++;
                report?.WriteLine($"unsupported {name}: {e.Kind} {e.ComponentId}: {e.Message}");
            }
            catch (ParquetFormatException e)
            {
                malformed++;
                report?.WriteLine($"malformed   {name}: {e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
                report?.WriteLine($"FAILED      {name}: {e}");
            }
        }

        report?.WriteLine($"{read} read, {unsupported} unsupported, {malformed} malformed, {failures.Count} failed");
        Assert.Empty(failures);
    }
}
