// The choice of the paths an object shreds: those whose occurrences save more residual bytes than they
// add to each chunk, in the class most of their values hold, the nested keys of an object, a list of
// one class; and when more pay than a chunk's tree may hold, those that save the most. A ratchet on a
// column of ten thousand keys that are data: only the keys every row holds are shredded, the file is
// smaller than unshredded, and its largest encoding tree stays small.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Serialization.Schemas;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddingChoiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ThePathsThatPayAreChosenInTheirClass()
    {
        Random random = new Random(1);
        PathCensus census = new PathCensus();
        for (int i = 0; i < 20_000; i++)
        {
            List<(string, Variant)> fields =
            [
                ("status", Variant.From(200 + random.Next(5))),
                ("route", Variant.From($"/r/{random.Next(50)}")),
                ("duration", random.Next(10) == 0 ? Variant.From("n/a") : Variant.From(random.NextDouble())),
                ("user", Variant.Object(("id", random.Next(1_000)), ("name", $"user-{random.Next(80)}"))),
                ("tags", Variant.Array("a", "b")),
                ("mixed", random.Next(10) < 7 ? Variant.From(random.Next(9)) : Variant.From("text")),
                ($"k{random.Next(10_000):D4}", Variant.From(1L)),
            ];
            if (random.Next(2) == 0)
            {
                fields.Add(("ok", Variant.From(true)));
            }

            Variant row = Variant.Object(fields.ToArray());
            if (random.Next(100) == 0)
            {
                census.AddNull();
            }
            else
            {
                census.Add(row.Metadata.Span, row.Value.Span);
            }
        }

        VariantShredding plan = ShreddingChoice.Choose(census)!;
        Assert.Equal(VariantShreddingKind.Object, plan.Kind);
        Dictionary<string, VariantShredding> keys = plan.Keys.ToDictionary(key => key.Name, key => key.Field);
        // A boolean held by half the rows writes a slot for every null row, and costs more than it saves.
        Assert.Equal(["duration", "mixed", "route", "status", "tags", "user"], keys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((VariantShreddingKind.Primitive, VariantClass.Integer), (keys["status"].Kind, keys["status"].Class));
        Assert.Equal((VariantShreddingKind.Primitive, VariantClass.String), (keys["route"].Kind, keys["route"].Class));
        Assert.Equal((VariantShreddingKind.Primitive, VariantClass.Double), (keys["duration"].Kind, keys["duration"].Class));
        Assert.Equal((VariantShreddingKind.Primitive, VariantClass.Integer), (keys["mixed"].Kind, keys["mixed"].Class));
        Assert.Equal((VariantShreddingKind.List, VariantClass.String), (keys["tags"].Kind, keys["tags"].Class));
        Assert.Equal(VariantShreddingKind.Object, keys["user"].Kind);
        Assert.Equal(["id", "name"], keys["user"].Keys.Select(key => key.Name));

        // A path with too few occurrences to pay for a chunk is left in the residual.
        PathCensus rare = new PathCensus();
        for (int i = 0; i < 100; i++)
        {
            Variant row = Variant.Object(("status", 200L));
            rare.Add(row.Metadata.Span, row.Value.Span);
        }

        Assert.Null(ShreddingChoice.Choose(rare));
    }

    /// <summary>When more paths pay than the tree may hold, the ones that save the most, the densest here, are kept.</summary>
    [Fact]
    public void TheBudgetKeepsThePathsThatSaveTheMost()
    {
        PathCensus census = new PathCensus();
        string[] names = ["e", "d", "c", "b", "a"];
        for (int i = 0; i < 10_000; i++)
        {
            // a in every row, b in 95 rows of a hundred, then 90, 85 and 80.
            List<(string, Variant)> fields = [];
            for (int k = 0; k < names.Length; k++)
            {
                if (i % 100 < 80 + (5 * k))
                {
                    fields.Add((names[k], Variant.From(i)));
                }
            }

            Variant row = Variant.Object(fields.ToArray());
            census.Add(row.Metadata.Span, row.Value.Span);
        }

        Assert.Equal(5, ShreddingChoice.Choose(census)!.Keys.Count);
        VariantShredding kept = ShreddingChoice.Choose(census, tableBudget: 3 * ShreddingChoice.PathTables)!;
        Assert.Equal(["a", "b", "c"], kept.Keys.Select(key => key.Name));
    }

    /// <summary>
    /// Ten thousand keys that are data, each held by a few rows, beside four keys every row holds: the
    /// choice shreds the four, the file is smaller than whole, and its bytes and its largest encoding
    /// tree stay under their ceilings, a ratchet. Measured on 2026-10-19: 839 364 bytes shredded
    /// against 999 780 whole, a tree of 20 tables.
    /// </summary>
    [Fact]
    public async Task TenThousandKeysThatAreDataStayInTheResidual()
    {
        Random random = new Random(2);
        Row[] rows = new Row[30_000];
        PathCensus census = new PathCensus();
        for (int i = 0; i < rows.Length; i++)
        {
            int first = random.Next(10_000);
            int second = (first + 1 + random.Next(4_999)) % 10_000;
            int third = (first + 5_000 + random.Next(4_999)) % 10_000;
            Variant attributes = Variant.Object(
                ("status", Variant.From(200 + random.Next(5))),
                ("route", Variant.From($"/r/{random.Next(50)}")),
                ("service", Variant.From($"service-{random.Next(30)}")),
                ("duration", Variant.From(random.NextDouble())),
                ($"k{first:D4}", Variant.From(random.Next(100))),
                ($"k{second:D4}", Variant.From("x")),
                ($"k{third:D4}", Variant.From(random.NextDouble())));
            rows[i] = new Row(i, attributes);
            census.Add(attributes.Metadata.Span, attributes.Value.Span);
        }

        VariantShredding plan = ShreddingChoice.Choose(census)!;
        Assert.Equal(["duration", "route", "service", "status"], plan.Keys.Select(key => key.Name));

        (long whole, _) = await WriteAsync(rows, null);
        (long shredded, int tables) = await WriteAsync(rows, plan);
        string measured = $"a tree of {tables} tables, {shredded} bytes shredded against {whole} whole";
        Assert.True(shredded < whole, measured);
        Assert.True(shredded <= 845_000, measured);
        Assert.True(tables <= 22, measured);
    }

    /// <summary>The rows in one chunk, whole or shredded: the file's bytes, and the tables of its largest encoding tree.</summary>
    private static async Task<(long Bytes, int Tables)> WriteAsync(Row[] rows, VariantShredding? plan)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-choice-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = rows.Length,
                ChunkTargetBytes = 1 << 30,
                ShreddedVariants = plan is null ? null : new Dictionary<string, VariantShredding> { [nameof(Row.Attributes)] = plan },
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, options))
            {
                await writer.WriteAsync<Row>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, Ct);
            int tables = 0;
            await using (VortexFile file = await VortexFile.OpenAsync(path, Ct))
            {
                List<Row> read = await file.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
                Assert.Equal(rows.Select(r => r.Attributes), read.Select(r => r.Attributes));
                foreach (VortexSegment segment in file.SegmentMap)
                {
                    tables = Math.Max(tables, Tables(bytes.AsSpan((int)segment.Offset, (int)segment.Length)));
                }
            }

            return (bytes.Length, tables);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The tables of a segment's encoding tree, or 0 when it holds none.</summary>
    private static int Tables(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 8)
        {
            return 0;
        }

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(segment[^4..]);
        if (length <= 0 || length > segment.Length - 4)
        {
            return 0;
        }

        try
        {
            int budget = VortexLimits.MaxFlatBufferTables;
            return Count(ArrayView.Root(segment[(segment.Length - 4 - length)..^4], ref budget).Root_);
        }
        catch (VortexFormatException)
        {
            return 0;
        }
    }

    private static int Count(ArrayNodeView node)
    {
        int tables = node.HasStats ? 2 : 1;
        for (int i = 0; i < node.ChildCount; i++)
        {
            tables += Count(node.GetChild(i));
        }

        return tables;
    }

    [VortexRecord]
    public partial record struct Row(long Id, Variant Attributes);
}
