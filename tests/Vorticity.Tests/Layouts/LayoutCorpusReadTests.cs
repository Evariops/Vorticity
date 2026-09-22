// Every fully decodable corpus file, read whole and read as two halves, with the halves required
// to compose back to the whole. This is the broad sweep the sampled LayoutExecutionTests trades
// away for depth: ~500 real files, every layout shape a 0.86.1 writer produces, every dtype the
// decoders implement.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;

using Xunit;

namespace Vorticity.Tests.Layouts;

public sealed class LayoutCorpusReadTests
{
    [Fact]
    public async Task EveryDecodableCorpusFileReadsAndSplitsConsistently()
    {
        int read = 0;
        long rows = 0;
        List<string> failures = new List<string>();

        foreach (LayoutCorpusEntry entry in LayoutCorpus.Entries)
        {
            if (!LayoutExecutor.IsFullyDecodable(entry))
            {
                continue;
            }

            await using VortexFile file = await VortexFile.OpenAsync(LayoutCorpus.FullPath(entry), TestContext.Current.CancellationToken);
            LayoutTree tree = LayoutTree.Parse(file);
            using ScanContext context = new ScanContext(file);

            long rowCount = file.RowCount;
            byte[] whole = await DigestAsync(file, tree, context, new RowRange(0, rowCount), entry.Id);

            foreach (long split in SplitPoints(rowCount))
            {
                byte[] head = await DigestAsync(file, tree, context, new RowRange(0, split), entry.Id);
                byte[] tail = await DigestAsync(file, tree, context, new RowRange(split, rowCount), entry.Id);

                byte[] glued = new byte[head.Length + tail.Length];
                head.CopyTo(glued, 0);
                tail.CopyTo(glued, head.Length);

                if (!whole.AsSpan().SequenceEqual(glued))
                {
                    failures.Add($"{entry.Id} (split at {split.ToString(CultureInfo.InvariantCulture)})");
                }
            }

            read++;
            rows += rowCount;
        }

        Assert.Empty(failures);
        Assert.True(read > 400, $"Only {read.ToString(CultureInfo.InvariantCulture)} corpus files were decodable.");
        Assert.True(rows > 100_000, $"Only {rows.ToString(CultureInfo.InvariantCulture)} rows were read.");
    }

    /// <summary>The corpus's own boundary row counts, clamped into the file.</summary>
    private static IEnumerable<long> SplitPoints(long rowCount)
    {
        HashSet<long> points = new HashSet<long>();
        foreach (long candidate in new long[] { 1, 1023, 1024, 1025, 8191, 8192, 8193, rowCount / 2, rowCount - 1 })
        {
            if (candidate > 0 && candidate < rowCount)
            {
                points.Add(candidate);
            }
        }

        return points;
    }

    private static async ValueTask<byte[]> DigestAsync(
        VortexFile file, LayoutTree tree, ScanContext context, RowRange rows, string id)
    {
        try
        {
            int root = await LayoutExecutor.ReadAsync(file, tree, context, rows, FieldMask.All);
            CanonicalNode node = context.Canonical.GetNode(root);
            Assert.True(
                node.Length == rows.Length,
                $"{id}: asked for {rows.Length} rows and got {node.Length}.");
            return CanonicalDigest.Of(context, root);
        }
        finally
        {
            context.ResetBatch();
        }
    }
}
