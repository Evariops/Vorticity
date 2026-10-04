using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// Every corpus file read in windows returns what a plain scan returns, row for row: the range
/// decode of every encoding the corpus carries, at every nesting it carries it, against the whole
/// decode it stands in for.
/// </summary>
/// <remarks>
/// The corpus's chunks are small, so the windows are made small to reach them: batches of 97 rows,
/// odd so that no window starts on a FastLanes block, in windows of ten batches, and a shared
/// child is then lent to ten windows of each chunk. The plain scan is the oracle, as it is for the
/// take sweep; the windowed scans run on one lane and on four, and as a scattered take.
/// </remarks>
public sealed class WindowedCorpusTests
{
    private const int BatchRows = 97;

    private const int WindowRows = 1_000;

    [Fact]
    public async Task EveryCorpusFileReadInWindowsReturnsWhatAPlainScanReturns()
    {
        Decoders.EnsureRegistered();
        int files = 0;
        long rows = 0;
        StringBuilder failures = new StringBuilder();
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            // Compared by the digests of their rows against the plain scan's, and described only
            // where they differ.
            List<UInt128> plain = await CorpusSweep.PlainAsync(entry.Path);
            if (plain.Count < 2)
            {
                continue;
            }

            files++;
            rows += plain.Count;
            await Compare(entry, "one lane", plain, degree: 1, take: null, failures);
            await Compare(entry, "four lanes", plain, degree: 4, take: null, failures);
            await Compare(entry, "a take", plain, degree: 1, take: CorpusSweep.TakeIndices(plain.Count), failures);
        }

        Console.Out.WriteLine(
            $"WINDOWED SWEEP: {files.ToString(CultureInfo.InvariantCulture)} corpus files, " +
            $"{rows.ToString(CultureInfo.InvariantCulture)} rows read in windows of {WindowRows.ToString(CultureInfo.InvariantCulture)} against a plain scan.");
        Assert.True(files > 550, $"only {files} files were compared");
        Assert.Equal(string.Empty, failures.ToString());
    }

    /// <summary>
    /// A windowed read of the file against the plain scan's rows, or those of them a take names: by
    /// digest, then, where a row differs, by the lines both reads describe.
    /// </summary>
    private static async Task Compare(CorpusEntry entry, string how, List<UInt128> plain, int degree, long[]? take, StringBuilder failures)
    {
        List<UInt128> expected = take is null ? plain : [.. take.Select(index => plain[(int)index])];
        List<UInt128> actual = await ReadAsync<UInt128>(entry.Path, degree, take, Values.DigestRows);
        if (expected.Count != actual.Count)
        {
            failures.Append(entry.Id).Append(", ").Append(how).Append(": ").Append(actual.Count)
                .Append(" rows, expected ").Append(expected.Count).Append('\n');
            return;
        }

        int row = CollectionsMarshal.AsSpan(expected).CommonPrefixLength(CollectionsMarshal.AsSpan(actual));
        if (row < expected.Count)
        {
            List<string> lines = await CorpusSweep.DescribeAsync(entry.Path);
            List<string> read = await ReadAsync<string>(entry.Path, degree, take, Values.DescribeRows);
            failures.Append(entry.Id).Append(", ").Append(how).Append(": row ").Append(row)
                .Append(" is ").Append(read[row]).Append(", expected ").Append(lines[take is null ? row : (int)take[row]]).Append('\n');
        }
    }

    /// <summary>The file read in windows, on <paramref name="degree"/> lanes, every row or those of a take.</summary>
    private static async Task<List<T>> ReadAsync<T>(string path, int degree, long[]? take, Action<RecordBatch, List<T>> read)
    {
        List<T> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, await CorpusSweep.OpenOptionsForAsync(path), CancellationToken.None);
        ScanBuilder scan = file.ScanBuilder().WithMaxBatchRows(BatchRows).WithWindowRows(WindowRows).WithDegreeOfParallelism(degree);
        if (take is not null)
        {
            scan = scan.Take(take);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            read(batch, values);
        }

        return values;
    }
}
