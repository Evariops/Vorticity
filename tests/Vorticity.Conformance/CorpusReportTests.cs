// The whole-corpus pass, in one test, for the number a human actually asks for: how many files
// read back exactly, how many did not, and what differed in each one that did not.
//
// It duplicates the per-file theory on purpose. The theory is the machine-readable form - one test
// case per file, so CI names the file that broke - and this is the human-readable one: a single
// failure message listing every failing file with its component and its first mismatch, which is
// what gets pasted into a report. A run where both agree is a run where the harness itself is not
// the variable.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Conformance.Corpus;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Conformance;

public sealed class CorpusReportTests
{
    [Fact]
    public async Task EveryInScopeFileReadsBackValueForValue()
    {
        List<FileResult> failures = new List<FileResult>();
        List<string> skips = new List<string>();
        int passed = 0;
        long rows = 0;
        long batches = 0;
        long values = 0;

        foreach (ScopeVerdict verdict in CorpusCatalog.InScope())
        {
            FileResult result = await ConformanceRunner.CompareAsync(
                verdict.Entry, TestContext.Current.CancellationToken);

            rows += result.Rows;
            batches += result.Batches;
            values += result.Log.ValuesCompared;

            foreach (string path in result.Log.SkippedNullCountPaths)
            {
                skips.Add($"{result.EntryId}: null_count for the ambiguous path " +
                    (path.Length == 0 ? "<root>" : "'" + path + "'"));
            }

            if (result.Passed)
            {
                passed++;
            }
            else
            {
                failures.Add(result);
            }
        }

        StringBuilder report = new StringBuilder();
        report.Append("\nCONFORMANCE: ").Append(Text(passed)).Append(" of ")
            .Append(Text(passed + failures.Count)).Append(" in-scope corpus files read back value " +
            "for value; ").Append(Text(failures.Count)).Append(" failed. ")
            .Append(Text(rows)).Append(" rows, ").Append(Text(values))
            .Append(" values and validity bits compared, in ").Append(Text(batches)).Append(" batches.\n");

        foreach (string skip in skips)
        {
            report.Append("  SKIPPED ").Append(skip).Append('\n');
        }

        foreach (FileResult failure in failures)
        {
            report.Append("  FAIL ").Append(failure.EntryId).Append(": ")
                .Append(failure.Summarize()).Append('\n');
        }

        Console.Out.Write(report.ToString());

        if (failures.Count > 0)
        {
            StringBuilder detail = new StringBuilder(report.ToString());
            detail.Append('\n');
            int shown = 0;
            foreach (FileResult failure in failures)
            {
                if (shown++ == 20)
                {
                    detail.Append("  ... ").Append(Text(failures.Count - 20)).Append(" more\n");
                    break;
                }

                detail.Append(failure.Describe()).Append('\n');
            }

            Assert.Fail(detail.ToString());
        }
    }

    /// <summary>
    /// The out-of-scope half of the same report: what each of the 203 files did, and which
    /// component id refused it.
    /// </summary>
    [Fact]
    public async Task EveryOutOfScopeFileFailsWithANamedComponentOrReadsCorrectly()
    {
        SortedDictionary<string, int> refusedBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<string> unreachable = new List<string>();
        List<string> problems = new List<string>();
        int threw = 0;
        int openRefused = 0;

        foreach (ScopeVerdict verdict in CorpusCatalog.OutOfScope())
        {
            CorpusEntry entry = verdict.Entry;
            if (!entry.HasDTypeSegment)
            {
                openRefused++;
                continue;
            }

            Phase1Components.EnsureRegistered();
            List<string> reachable = OutOfScopeTests.UnsupportedOnTheDataPath(verdict);
            if (reachable.Count == 0)
            {
                FileResult readable = await ConformanceRunner.CompareAsync(
                    entry, TestContext.Current.CancellationToken);
                unreachable.Add($"{entry.Id} [{string.Join(", ", verdict.AllUnsupported())} in the zone map only]");
                if (!readable.Passed)
                {
                    problems.Add(readable.Describe());
                }

                continue;
            }

            try
            {
                await using VortexFile file = await VortexFile.OpenAsync(
                    entry.FullPath, TestContext.Current.CancellationToken);
                await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                    .WithCancellation(TestContext.Current.CancellationToken))
                {
                    batch.Dispose();
                }

                problems.Add($"{entry.Id}: read without error although {string.Join(", ", reachable)} " +
                    "is on its data path");
            }
            catch (VortexUnsupportedException error)
            {
                threw++;
                string key = $"{error.Kind} '{error.ComponentId}'";
                refusedBy[key] = refusedBy.TryGetValue(key, out int count) ? count + 1 : 1;
            }
            catch (Exception error)
            {
                problems.Add($"{entry.Id}: threw {error.GetType().Name} rather than " +
                    $"VortexUnsupportedException: {error.Message}");
            }
        }

        StringBuilder report = new StringBuilder();
        report.Append("\nOUT OF SCOPE: ").Append(Text(threw))
            .Append(" files refused with a named component, ").Append(Text(unreachable.Count))
            .Append(" read correctly because their unsupported component is unreachable, ")
            .Append(Text(openRefused)).Append(" refused at open.\n");
        foreach (KeyValuePair<string, int> pair in refusedBy)
        {
            report.Append("  refused by ").Append(pair.Key).Append(": ").Append(Text(pair.Value)).Append(" files\n");
        }

        foreach (string file in unreachable)
        {
            report.Append("  READABLE ").Append(file).Append('\n');
        }

        Console.Out.Write(report.ToString());

        if (problems.Count > 0)
        {
            Assert.Fail(report.ToString() + "\n" + string.Join("\n", problems));
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
