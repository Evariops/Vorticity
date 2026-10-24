// Block statistics of shredded keys: what a zone map gives a declared column, a shredded path gets in the
// index directory, so that a range on the path skips the blocks the same range skips on the column; and
// the statistics of a key whose values scatter over every block are not kept.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantBlockStatsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantShredding Plan = VariantShredding.Object(
        ("duration", VariantShredding.Primitive(VariantClass.Integer)),
        ("flag", VariantShredding.Primitive(VariantClass.Boolean)),
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("status", VariantShredding.Primitive(VariantClass.Integer)));

    /// <summary>
    /// Two thousand rows in blocks of a hundred, the status rising with the rows, held twice: as a
    /// declared column and at a shredded key. Every range keeps the same blocks live on both, and
    /// counts the same rows.
    /// </summary>
    [Fact]
    public async Task ARangeOnAPathSkipsWhatADeclaredColumnSkips()
    {
        await using VortexFile file = await OpenAsync(await WriteAsync(Rows()));
        foreach ((long low, long high) in new[] { (1_500L, long.MaxValue), (777L, 777L), (long.MinValue, 99L), (400L, 650L), (5_000L, long.MaxValue) })
        {
            ScanPlan declared = await file.Scan<Doc>().Where(r => r.Status >= low && r.Status <= high).ExplainAsync(Ct);
            ScanPlan path = await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() >= low && r.Attributes["status"].AsInt64() <= high).ExplainAsync(Ct);
            Assert.Equal((declared.Blocks, declared.LiveBlocks), (path.Blocks, path.LiveBlocks));
            Assert.Equal(
                await file.Scan<Doc>().Where(r => r.Status >= low && r.Status <= high).CountAsync(Ct),
                await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() >= low && r.Attributes["status"].AsInt64() <= high).CountAsync(Ct));
        }

        ScanPlan narrow = await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() == 777).ExplainAsync(Ct);
        Assert.Equal(1, narrow.LiveBlocks);
        Assert.Equal(19, Assert.Single(narrow.Pruning, step => step.Structure == "path statistics").BlocksPruned);
    }

    /// <summary>
    /// A text and a flag that change from block to block refute blocks too: the route of five blocks,
    /// the flag set in every other block; a duration that spans its whole range in every block keeps
    /// no statistics, since they could refute nothing.
    /// </summary>
    [Fact]
    public async Task StatisticsAreKeptWhereTheyPrune()
    {
        await using VortexFile file = await OpenAsync(await WriteAsync(Rows()));
        int field = file.DType.IndexOfField(nameof(Doc.Attributes));
        (VariantBlockStats? stats, _, _) = await VariantBlockStats.ReadAsync(
            file, field, [("status", PathClass.Integer), ("route", PathClass.String), ("flag", PathClass.Boolean), ("duration", PathClass.Integer)], Ct);
        Assert.NotNull(stats);
        Assert.Equal(-1, stats.IndexOf("duration", PathClass.Integer));
        Assert.Equal(20, stats.Blocks[stats.IndexOf("status", PathClass.Integer)].Length);

        Assert.Equal(5, (await file.Scan<Doc>().Where(r => r.Attributes["route"].AsString() == "/r/1").ExplainAsync(Ct)).LiveBlocks);
        Assert.Equal(500, await file.Scan<Doc>().Where(r => r.Attributes["route"].AsString() == "/r/1").CountAsync(Ct));
        Assert.Equal(10, (await file.Scan<Doc>().Where(r => r.Attributes["flag"].AsBool() == true).ExplainAsync(Ct)).LiveBlocks);
        Assert.Equal(1_000, await file.Scan<Doc>().Where(r => r.Attributes["flag"].AsBool() == true).CountAsync(Ct));
        Assert.Equal(20, (await file.Scan<Doc>().Where(r => r.Attributes["duration"].AsInt64() > 500).ExplainAsync(Ct)).LiveBlocks);
    }

    /// <summary>
    /// The column sets a parallel write keeps in a pool outlive it: a write on four threads that counts
    /// a variant's keys, then one of a wider schema that counts none, takes them back without reading
    /// the first one's slots, and writes the bytes it writes on one thread.
    /// </summary>
    [Fact]
    public async Task AWriteAfterOneThatCountedKeysTakesItsColumnsBackClean()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-blockstats-pool-{Guid.NewGuid():N}.vortex");
        try
        {
            // Chunks of a block or so, so that both writes compress their whole chunks across threads.
            VortexWriteOptions shredded = new VortexWriteOptions
            {
                BlockRows = 100,
                ChunkTargetBytes = 1_024,
                DegreeOfParallelism = 4,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = Plan },
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, shredded))
            {
                await writer.WriteAsync<Doc>(Rows(), Ct);
                await writer.CompleteAsync(Ct);
            }

            Wide[] rows = [.. Enumerable.Range(0, 2_000).Select(i => new Wide(i, i * 2, i * 3, i * 4, i * 5, i * 6))];
            byte[]? one = null;
            foreach (int degree in new[] { 4, 1 })
            {
                await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path, new VortexWriteOptions { BlockRows = 100, ChunkTargetBytes = 1_024, DegreeOfParallelism = degree }))
                {
                    await writer.WriteAsync<Wide>(rows, Ct);
                    await writer.CompleteAsync(Ct);
                }

                byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, Ct);
                one ??= bytes;
                Assert.Equal(one.Length, bytes.Length);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows: a status rising with them, a route of five hundred, a flag set every other block, a duration all over.</summary>
    private static Doc[] Rows()
    {
        Random random = new Random(4);
        return [.. Enumerable.Range(0, 2_000).Select(i => new Doc(i, i, Variant.Object(
            ("duration", Variant.From(i % 2 == 0 ? random.Next(1_000) : random.Next(2) * 999)),
            ("flag", Variant.From(i / 100 % 2 == 0)),
            ("route", Variant.From($"/r/{i / 500}")),
            ("status", Variant.From((long)i)))))];
    }

    private static async Task<byte[]> WriteAsync(Doc[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-blockstats-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 100,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = Plan },
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options))
            {
                await writer.WriteAsync<Doc>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            return await System.IO.File.ReadAllBytesAsync(path, Ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static ValueTask<VortexFile> OpenAsync(byte[] bytes) =>
        VortexFile.OpenAsync(new Vorticity.Tests.File.TestSegmentSource(bytes), VortexOpenOptions.Default, Ct);

    [VortexRecord]
    public partial record struct Doc(long Id, long Status, Variant Attributes);

    [VortexRecord]
    public partial record struct Wide(long A, long B, long C, long D, long E, long F);
}
