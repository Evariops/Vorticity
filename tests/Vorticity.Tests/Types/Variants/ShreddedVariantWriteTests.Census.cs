// A census of a shredded variant's paths taken from its shredding as it lies counts, path by path and class by class, what a
// census of its rows put back together counts: over random shreddings of random documents, nested objects, lists, values of
// another class than their key's column, rows that are no object, the variant null and a null column among them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantWriteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ACensusOfTheShreddingAsItLiesIsTheCensusOfItsRowsPutBack(int seed)
    {
        Random random = new Random(seed);
        Doc[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Doc(i, Document(random), random.Next(10) == 0 ? null : Document(random)))];
        Dictionary<string, VariantShredding> shredding = new()
        {
            [nameof(Doc.Attributes)] = Shredding(random),
            [nameof(Doc.Extra)] = Shredding(random),
        };

        string path = await WriteAsync(rows, shredding, degree: 1);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            foreach (string column in new[] { nameof(Doc.Attributes), nameof(Doc.Extra) })
            {
                List<(string, long, long)> whole = await CountAsync(file, column, apart: false);
                List<(string, long, long)> apart = await CountAsync(file, column, apart: true);
                Assert.True(whole.Count > 5, $"{column}: {whole.Count} paths");
                Assert.Contains(whole, path => path.Item2 > 0 && path.Item3 > 0);
                Assert.Equal(whole, apart);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The paths of <paramref name="column"/>, read as the shredding holds it or put back, of the rows a third of which are left
    /// and the others kept: each path and class with its rows kept and left, in order.
    /// </summary>
    private static async Task<List<(string Path, long Kept, long Left)>> CountAsync(VortexFile file, string column, bool apart)
    {
        EdgeCounts counts = new EdgeCounts();
        PathCensus kept = new PathCensus();
        PathCensus left = new PathCensus();
        FieldExpr field = new ColumnFieldExpr(column);
        await foreach (RecordBatch batch in file.ScanBuilder().Project(column).WithVariantsApart(apart).ExecuteAsync().WithCancellation(Ct))
        {
            int rows = batch.RowCount;
            ulong[] keep = new ulong[(rows + 63) >> 6];
            ulong[] other = new ulong[keep.Length];
            for (int row = 0; row < rows; row++)
            {
                ulong bit = 1UL << (row & 63);
                if ((batch.StartRow + row) * 2_654_435_761L % 3 == 0)
                {
                    other[row >> 6] |= bit;
                }
                else
                {
                    keep[row >> 6] |= bit;
                }
            }

            int node = FilterEvaluator.Resolve(batch.Arena, batch.RootIndex, field, rows);
            if (apart)
            {
                Assert.True(ApartCensus.IsGroup(batch.Arena, node), column);
                ApartCensus.Add(batch.Arena, node, rows, keep, other, counts);
            }
            else
            {
                PathCensus.Counting counting = PathCensus.Prepare(batch.Arena, node, rows);
                kept.Add(counting, keep);
                left.Add(counting, other);
            }
        }

        counts.Add(kept, left);
        return [.. counts.Entries().Select(entry => (Convert.ToHexString(entry.Key), entry.Kept, entry.Left)).OrderBy(entry => entry.Item1, StringComparer.Ordinal)];
    }
}
