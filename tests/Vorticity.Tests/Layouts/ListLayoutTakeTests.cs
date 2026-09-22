using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Layouts;

/// <summary>
/// A take from a list layout read in batches that do not start at its first row. The wanted rows
/// arrive in the layout's row space, so every batch after the first reads them at their distance
/// from its own first row.
/// </summary>
public sealed class ListLayoutTakeTests
{
    private const int BatchRows = 97;

    [Fact]
    public async Task ATakeAcrossBatchesOfAListLayoutReturnsWhatAPlainScanReturns()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        int files = 0;
        foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
        {
            if (Array.IndexOf(entry.LayoutIds, "vortex.list") < 0 || !entry.HasDTypeSegment ||
                entry.RowCount <= 2 * BatchRows)
            {
                continue;
            }

            string path = LayoutCorpus.FullPath(entry);
            List<string> plain = await ReadAsync(path, take: null);
            List<long> wanted = [];
            List<string> expected = [];
            for (int row = 3; row < plain.Count; row += 7)
            {
                wanted.Add(row);
                expected.Add(plain[row]);
            }

            Assert.Equal(expected, await ReadAsync(path, wanted.ToArray()));
            files++;
        }

        Assert.True(files > 0, "no corpus file has a list layout longer than two batches");
    }

    private static async Task<List<string>> ReadAsync(string path, long[]? take)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, VortexOpenOptions.Default, CancellationToken.None);
        ScanBuilder scan = file.ScanBuilder().WithMaxBatchRows(BatchRows);
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
}
