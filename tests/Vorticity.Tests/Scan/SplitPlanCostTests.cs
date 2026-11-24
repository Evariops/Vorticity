using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>The two numbers a scan asks of its plan without reading: the largest split's bytes, and the blocks its rows touch.</summary>
public sealed partial class SplitPlanCostTests
{
    [VortexRecord]
    public partial record struct Wide(long Id, long Value, string Label);

    /// <summary>
    /// The largest split, found from one split a span, is the largest a walk of every split finds: chunks of uneven sizes
    /// cut into many splits each, under a cap that divides none of them evenly.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(777)]
    [InlineData(5_000)]
    public async Task TheLargestSplitIsFoundFromOneSplitASpan(int cap)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            long found = file.ScanBuilder().WithMaxBatchRows(cap).LargestSplitBytes();
            long walked = LargestByWalk(file, cap, ScanProjection.All.RootMask);
            Assert.True(found > 0);
            Assert.Equal(walked, found);

            long projected = file.ScanBuilder().WithMaxBatchRows(cap).Project("Label").LargestSplitBytes();
            FieldMaskBuilder label = new FieldMaskBuilder();
            ScanProjection.IncludePath(file.DType, "Label", label, "path");
            Assert.Equal(LargestByWalk(file, cap, ScanProjection.Create(label.Build()).RootMask), projected);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The blocks a refused scan counts pruned, taken from its rows alone, are the blocks its plan without a filter counts:
    /// over the whole file, a range inside it, and a take.
    /// </summary>
    [Fact]
    public async Task TheBlocksARefusedScanCountsAreThoseItsPlanCounts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = await WriteAsync(ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Func<ScanBuilder>[] scans =
            [
                () => file.ScanBuilder(),
                () => file.ScanBuilder().Rows(new RowRange(1_000, 17_500)),
                () => file.ScanBuilder().Rows(new RowRange(8_191, 8_193)),
                () => file.ScanBuilder().Take([3, 4, 9_000, 9_001, 20_000, 33_333]),
            ];

            foreach (Func<ScanBuilder> scan in scans)
            {
                ScanExplanation plan = await scan().ExplainAsync(analyze: false, ct);
                Assert.Equal(plan.Blocks, scan().TouchedBlocks());
            }

            // And the refused scan counts them.
            ScanCounters metrics = new ScanCounters();
            FileScanSource source = new FileScanSource(file);
            Assert.Equal(0, (await source.AnswerAsync(
                new ScanSpec { Filter = Expr.Gt(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(1_000_000L))) },
                LogicalShape.Count,
                metrics,
                ct)).Rows);
            Assert.Equal((await file.ScanBuilder().ExplainAsync(analyze: false, ct)).Blocks, metrics.BlocksPruned);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Every split of the plan registered, the largest kept: what the plan's cost was before one split a span.</summary>
    private static long LargestByWalk(VortexFile file, int cap, FieldMask mask)
    {
        LayoutTree tree = file.LayoutTree;
        SplitPlan plan = SplitPlan.Compute(tree, new RowRange(0, file.RowCount), in mask, Math.Min(cap, SplitPlan.NaturalBatchRows(tree)));
        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Require(in root);
        SplitCursor cursor = plan.CreateCursor();
        SegmentRequestSet segments = new SegmentRequestSet();
        long largest = 0;
        try
        {
            while (cursor.TryNext(out RowRange split))
            {
                reader.RegisterSegments(in root, split, in mask, segments);
                long bytes = 0;
                for (int i = 0; i < segments.Count; i++)
                {
                    bytes += segments.GetSpec(i).Length;
                }

                largest = Math.Max(largest, bytes);
                segments.Release();
            }
        }
        finally
        {
            segments.Dispose();
        }

        return largest;
    }

    /// <summary>40 000 rows in writes of uneven sizes, the texts growing with the rows so that the chunks differ in bytes.</summary>
    private static async Task<string> WriteAsync(CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-split-cost-{Guid.NewGuid():N}.vortex");
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(path);
        int row = 0;
        foreach (int size in (int[])[3_000, 11_111, 500, 9_000, 16_389])
        {
            Wide[] rows = new Wide[size];
            for (int i = 0; i < size; i++, row++)
            {
                rows[i] = new Wide(row, row * 7L, new string('x', 1 + (row % 97) + (row / 1_000)));
            }

            await writer.WriteAsync<Wide>(rows, ct);
        }

        await writer.CompleteAsync(ct);
        return path;
    }
}
