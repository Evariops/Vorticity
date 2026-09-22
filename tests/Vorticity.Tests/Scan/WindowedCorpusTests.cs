using System;
using System.Collections.Generic;
using System.Globalization;
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
            List<string> plain = await ReadAsync(entry.Path, windowed: false, degree: 1, take: null);
            if (plain.Count < 2)
            {
                continue;
            }

            files++;
            rows += plain.Count;
            Compare(entry.Id, "one lane", plain, await ReadAsync(entry.Path, windowed: true, degree: 1, take: null), failures);
            Compare(entry.Id, "four lanes", plain, await ReadAsync(entry.Path, windowed: true, degree: 4, take: null), failures);

            long[] wanted = Indices(plain.Count);
            List<string> expected = [];
            foreach (long index in wanted)
            {
                expected.Add(plain[(int)index]);
            }

            Compare(entry.Id, "a take", expected, await ReadAsync(entry.Path, windowed: true, degree: 1, take: wanted), failures);
        }

        Console.Out.WriteLine(
            $"WINDOWED SWEEP: {files.ToString(CultureInfo.InvariantCulture)} corpus files, " +
            $"{rows.ToString(CultureInfo.InvariantCulture)} rows read in windows of {WindowRows.ToString(CultureInfo.InvariantCulture)} against a plain scan.");
        Assert.True(files > 550, $"only {files} files were compared");
        Assert.Equal(string.Empty, failures.ToString());
    }

    private static void Compare(string id, string how, List<string> expected, List<string> actual, StringBuilder failures)
    {
        if (expected.Count != actual.Count)
        {
            failures.Append(id).Append(", ").Append(how).Append(": ").Append(actual.Count)
                .Append(" rows, expected ").Append(expected.Count).Append('\n');
            return;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                failures.Append(id).Append(", ").Append(how).Append(": row ").Append(i)
                    .Append(" is ").Append(actual[i]).Append(", expected ").Append(expected[i]).Append('\n');
                return;
            }
        }
    }

    private static async Task<List<string>> ReadAsync(string path, bool windowed, int degree, long[]? take)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, OpenOptionsFor(path), CancellationToken.None);
        ScanBuilder scan = file.ScanBuilder();
        if (windowed)
        {
            scan = scan.WithMaxBatchRows(BatchRows).WithWindowRows(WindowRows).WithDegreeOfParallelism(degree);
        }

        if (take is not null)
        {
            scan = scan.Take(take);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            Values.DescribeRows(batch, values);
        }

        return values;
    }

    /// <summary>The first and last rows, both sides of a FastLanes block boundary, and a prime stride between.</summary>
    private static long[] Indices(int rowCount)
    {
        SortedSet<long> wanted = [0, rowCount - 1];
        foreach (long boundary in (ReadOnlySpan<long>)[1023, 1024, 1025, 2047, 2048])
        {
            if (boundary < rowCount)
            {
                wanted.Add(boundary);
            }
        }

        for (long i = 7; i < rowCount; i += 331)
        {
            wanted.Add(i);
        }

        long[] indices = new long[wanted.Count];
        wanted.CopyTo(indices);
        return indices;
    }

    /// <summary>Open options for a corpus path: the schema out of band when the file has none.</summary>
    private static VortexOpenOptions OpenOptionsFor(string path) =>
        path.Contains("no_dtype_segment", StringComparison.Ordinal)
            ? new VortexOpenOptions { DType = OutOfBandSchema.Value }
            : VortexOpenOptions.Default;

    private static readonly Lazy<DType> OutOfBandSchema = new Lazy<DType>(static () =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                CorpusManifest.Get("types/user_metadata_segments").Path,
                VortexOpenOptions.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return donor.DType;
    });
}
