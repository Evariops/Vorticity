// The shape of a row against the keys an object shreds: which of them it holds, in their typed class or
// another, and whether anything is left to the residual. Keys held as data only touch the residual's bit,
// so they never multiply shapes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantShapesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantShredding Plan = VariantShredding.Object(
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("status", VariantShredding.Primitive(VariantClass.Integer)));

    /// <summary>
    /// A thousand rows whose user keys are data, each its own key, beside a route and a status: they make
    /// as many shapes as the shredded keys allow, not one a row. A status of text is another shape than
    /// one of an integer, a row that is no object or null another, and the codes follow the shapes' order.
    /// </summary>
    [Fact]
    public async Task KeysHeldAsDataDoNotMultiplyShapes()
    {
        Doc[] rows = [.. Enumerable.Range(0, 1_000).Select(i => new Doc(i, (i % 10) switch
        {
            0 => Variant.From("not an object"),
            1 => Variant.Object(("route", Variant.From("/a")), ("status", Variant.From("timeout"))),
            2 => Variant.Object(("route", Variant.From("/a"))),
            _ => Variant.Object(("route", Variant.From($"/r/{i % 7}")), ("status", Variant.From(200L + (i % 3))), ($"user.{i}.score", Variant.From(i))),
        }))];

        (int[] codes, int distinct) = await CodesAsync(rows);

        // No object; a route alone; a route and a status of text; a route, a status and a key of data.
        Assert.Equal(4, distinct);
        Assert.All(Enumerable.Range(0, rows.Length).Where(i => i % 10 >= 3), i => Assert.Equal(codes[3], codes[i]));

        // A route typed is bits 01, a status typed 01 at bits 2-3 and of text 10, the residual bit 4: a
        // route alone is 1, with a status of text 9, no object 16, with a status and data 21.
        Assert.Equal([0, 1, 2, 3], new[] { codes[2], codes[1], codes[0], codes[3] });
    }

    /// <summary>The variant column of the rows, read back from a file, coded against the plan.</summary>
    private static async Task<(int[] Codes, int Distinct)> CodesAsync(Doc[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-shapes-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path))
            {
                await writer.WriteAsync<Doc>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: Ct);
            List<int> codes = [];
            int distinct = 0;
            VariantShapes shapes = new VariantShapes(Plan);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
            {
                int column = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(file.DType.IndexOfField(nameof(Doc.Attributes)));
                (int[] batchCodes, int batchDistinct) = shapes.Codes(batch.Arena, column, batch.RowCount);
                Assert.Equal(rows.Length, batch.RowCount);
                codes.AddRange(batchCodes);
                distinct = batchDistinct;
            }

            return ([.. codes], distinct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
