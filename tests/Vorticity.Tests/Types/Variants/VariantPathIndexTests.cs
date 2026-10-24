// Indexes on a path: a Bloom filter named by the variant column, the key and the class of its values,
// built from the values a question of that class reads. A question of the class probes it; a question of
// another class never does, so the integer 502 and the text "502" never find each other.
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

public sealed partial class VariantPathIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantShredding Plan = VariantShredding.Object(("status", VariantShredding.Primitive(VariantClass.Integer)));

    /// <summary>
    /// Two thousand rows in blocks of a hundred, statuses scattered over every block so that no block
    /// statistic refutes one, the integer 502 in two blocks and the text "502" in two others. The file
    /// carries a Bloom filter named by the column, the key and the integer class; an equality of an
    /// integer probes it and reads the two blocks that hold one, an equality of a text never does and
    /// finds the texts, and every count is the rows' own.
    /// </summary>
    [Fact]
    public async Task AnIntegerAndATextNeverFindEachOther()
    {
        Doc[] rows = Rows();
        await using VortexFile file = await VortexFile.OpenAsync(new Vorticity.Tests.File.TestSegmentSource(await WriteAsync(rows)), VortexOpenOptions.Default, Ct);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(Ct))!;
        IndexEntry entry = Assert.Single(directory.Entries, e => e.Kind == IndexKinds.BloomSbbf);
        Assert.Equal([(uint)file.DType.IndexOfField(nameof(Doc.Attributes))], entry.ColumnPath);
        Assert.Equal(["status"], entry.VariantPath);
        Assert.Equal(PathClass.Integer, entry.VariantClass);
        Assert.Contains(await file.GetIndexesAsync(Ct), index => index.Column == "Attributes['status'].AsInt64()");

        Assert.Equal(2, await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() == 502).CountAsync(Ct));
        Assert.Equal(2, await file.Scan<Doc>().Where(r => r.Attributes["status"].AsString() == "502").CountAsync(Ct));
        Assert.Equal(
            rows.Count(r => r.Attributes["status"]?.AsInt64() is 502 or 4_000),
            await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() == 502 || r.Attributes["status"].AsInt64() == 4_000).CountAsync(Ct));

        ScanPlan integers = await file.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() == 502).ExplainAsync(Ct);
        PruningStep bloom = Assert.Single(integers.Pruning, step => step.Structure == "bloom filter");
        Assert.InRange(integers.LiveBlocks, 2, 3);
        Assert.Equal(integers.Blocks - integers.LiveBlocks, bloom.BlocksPruned);

        ScanPlan texts = await file.Scan<Doc>().Where(r => r.Attributes["status"].AsString() == "502").ExplainAsync(Ct);
        Assert.DoesNotContain(texts.Pruning, step => step.Structure == "bloom filter" && step.BlocksPruned > 0);
        Assert.Equal(texts.Blocks, texts.LiveBlocks);
    }

    /// <summary>A shredding with no policy writes no index on its paths.</summary>
    [Fact]
    public async Task NoPolicyIndexesNoPath()
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            new Vorticity.Tests.File.TestSegmentSource(await WriteAsync(Rows(), IndexPolicy.None)), VortexOpenOptions.Default, Ct);
        Assert.DoesNotContain((await file.ReadIndexDirectoryAsync(Ct))?.Entries ?? [], e => e.VariantPath is not null);
    }

    /// <summary>Statuses scattered over 0 to 9 999 in every block, never 502 but where planted: integers in blocks 3 and 7, texts in blocks 5 and 9.</summary>
    private static Doc[] Rows()
    {
        Random random = new Random(6);
        return [.. Enumerable.Range(0, 2_000).Select(i => new Doc(i, Variant.Object(("status", (i % 100, i / 100) switch
        {
            (50, 3 or 7) => Variant.From(502L),
            (50, 5 or 9) => Variant.From("502"),
            _ => Variant.From((long)(random.Next(9_999) is int n && n == 502 ? 503 : n)),
        }))))];
    }

    private static async Task<byte[]> WriteAsync(Doc[] rows, IndexPolicy? policy = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-pathindex-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 100,
                // A file this small holds fewer data bytes than a filter takes.
                Indexes = policy ?? IndexPolicy.None.Bloom(nameof(Doc.Attributes)).WithBudgetPerMille(100_000),
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

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
