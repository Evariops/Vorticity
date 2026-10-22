// The groups of sketches an hour holds are weighed in bytes: past the bound, its deepest tables of sketches are given up,
// with their reason, however few its groups are.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisTableTests
{
    /// <summary>
    /// The wide rows of two hours, whose tables of sketches are of the region alone: each of an hour's four regions holds
    /// the distinct sketch of its 2 000 hosts, dense at 16 KiB, so that under a bound of 32 KiB the hour's groups outweigh
    /// it and the tables of sketches by region are given up for their bytes; those of no key, one group, stay. Unbound, the
    /// tables by region are kept.
    /// </summary>
    [Fact]
    public async Task SketchesPastTheirBytesAreGivenUp()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-sketch-bytes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            List<WriteReport> reports = [];
            string bound = await WideAsync(scratch, heldSketchBytes: 32 << 10, reports: reports);
            Assert.Contains(
                Assert.Single(reports).Indexes,
                r => r.Kind == IndexKinds.SynopsisTable && r.Reason?.Contains("bytes of sketches in an hour", StringComparison.Ordinal) == true);

            await using (VortexFile file = await VortexFile.OpenAsync(bound, cancellationToken: Ct))
            {
                List<(SynopsisTableInfo Info, List<TableRow> Rows)> tables = await TablesAsync(file);
                Assert.DoesNotContain(tables, t => t.Info is { Kind: SynopsisTableKind.Sketches, Keys.Length: > 0 });
                Assert.Contains(tables, t => t.Info is { Kind: SynopsisTableKind.Sketches, Bucket: SynopsisBucket.Hour, Keys.Length: 0 });
            }

            string unbound = await WideAsync(scratch);
            await using (VortexFile file = await VortexFile.OpenAsync(unbound, cancellationToken: Ct))
            {
                Assert.Contains(await TablesAsync(file), t => t.Info is { Kind: SynopsisTableKind.Sketches, Bucket: SynopsisBucket.Hour, Keys.Length: 1 });
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
