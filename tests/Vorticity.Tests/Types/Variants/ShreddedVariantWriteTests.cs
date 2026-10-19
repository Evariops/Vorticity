// Shredded variants as the writer writes them: documents of every shape under a shredding drawn at
// random read back equal to what was written, integers equal in their class; and the format's rules on
// what the writer produces, a shredded key never in the residual, a key of another class kept in its
// field's values, a missing key missing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class ShreddedVariantWriteTests
{
    private static readonly string[] Routes = ["/a", "/api/v1/orders", "/api/v1/orders/{id}/lines", "/health"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Documents of every shape: objects of known keys of the right or another class, missing or null,
    /// a nested object, lists of text or mixed, long text, keys nobody shreds, and rows that are not
    /// objects or are null. Each seed draws a shredding for both columns, writes chunks of 512 rows
    /// on one lane or four, and every row reads back equal under the format's classes.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(3, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 1)]
    [InlineData(6, 4)]
    public async Task ARandomShreddingReadsBackEveryDocument(int seed, int degree)
    {
        Random random = new Random(seed);
        Doc[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Doc(i, Document(random), random.Next(10) == 0 ? null : Document(random)))];
        Dictionary<string, VariantShredding> shredding = new()
        {
            [nameof(Doc.Attributes)] = Shredding(random),
            [nameof(Doc.Extra)] = Shredding(random),
        };

        List<Doc> read = await RoundTripAsync(rows, shredding, degree);
        Assert.Equal(rows.Length, read.Count);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].Id, read[i].Id);
            Assert.True(rows[i].Attributes.Equals(read[i].Attributes), $"row {i}: {rows[i].Attributes.ToJson()} read as {read[i].Attributes.ToJson()}");
            Assert.Equal(rows[i].Extra is null, read[i].Extra is null);
            if (rows[i].Extra is { } extra)
            {
                Assert.True(extra.Equals(read[i].Extra!.Value), $"row {i}: {extra.ToJson()} read as {read[i].Extra!.Value.ToJson()}");
            }
        }
    }

    /// <summary>
    /// Integers of the four widths under one shredded key go to one 64-bit column, and come back at
    /// that width, equal in value; a key without a shredding keeps its width in the residual.
    /// </summary>
    [Fact]
    public async Task IntegersOfEveryWidthComeBackAs64Bits()
    {
        long[] values = [7, 300, 70_000, 5_000_000_000, -128, -40_000];
        Doc[] rows = [.. values.Select((value, i) => new Doc(i, Variant.Object(("n", value), ("kept", value)), null))];
        Assert.Equal([VariantKind.Int8, VariantKind.Int16, VariantKind.Int32, VariantKind.Int64, VariantKind.Int8, VariantKind.Int32], rows.Select(r => r.Attributes["n"]!.Value.Kind));

        List<Doc> read = await RoundTripAsync(rows, new() { [nameof(Doc.Attributes)] = VariantShredding.Object(("n", VariantShredding.Primitive(VariantClass.Integer))) }, 1);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].Attributes, read[i].Attributes);
            Assert.Equal(VariantKind.Int64, read[i].Attributes["n"]!.Value.Kind);
            Assert.Equal(values[i], read[i].Attributes["n"]!.Value.AsInt64());
            Assert.Equal(rows[i].Attributes["kept"]!.Value.Kind, read[i].Attributes["kept"]!.Value.Kind);
        }
    }

    /// <summary>
    /// What the shredder makes of a chunk: the residual holds no shredded key; a key whose values all
    /// fit is its typed column alone; a key that also held another class, a null or a missing row is
    /// told apart, the others in a variant of its own values beside its typed column; a list whose
    /// elements do not all fit is kept whole.
    /// </summary>
    [Fact]
    public async Task TheShredderKeepsTheFormatsRules()
    {
        Doc[] rows =
        [
            new(0, Variant.FromJson("""{"route":"/a","status":200,"tags":["x","y"],"other":1}"""), null),
            new(1, Variant.FromJson("""{"route":"/b","status":"502","tags":["x",2]}"""), null),
            new(2, Variant.FromJson("""{"route":"/c","status":null}"""), null),
            new(3, Variant.FromJson("""{"route":"/d"}"""), null),
            new(4, Variant.FromJson("""[1,2,3]"""), null),
        ];

        VariantShredding plan = VariantShredding.Object(
            ("route", VariantShredding.Primitive(VariantClass.String)),
            ("status", VariantShredding.Primitive(VariantClass.Integer)),
            ("tags", VariantShredding.List(VariantClass.String)));
        string path = await WriteAsync(rows, null, 1);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await foreach (RecordBatch batch in file.Scan<Doc>().ToBatchesAsync(Ct))
            {
                CanonicalArena arena = batch.Arena;
                int column = arena.GetNode(batch.RootIndex).GetFieldIndex(file.DType.IndexOfField(nameof(Doc.Attributes)));
                DTypeArena types = new DTypeArena();
                int shredded = VariantShredder.Shred(arena, types, column, plan);
                CanonicalNode node = arena.GetNode(shredded);
                Assert.Equal(3, node.FieldCount);

                // The residual: the key nobody shreds, the whole array of the row that is not an object.
                CanonicalNode metadata = arena.GetNode(node.GetFieldIndex(0));
                CanonicalNode residual = arena.GetNode(node.GetFieldIndex(1));
                for (int row = 0; row < rows.Length; row++)
                {
                    if (!ArenaWords.IsValid(arena, node.GetFieldIndex(1), row))
                    {
                        Assert.Contains(row, new[] { 1, 2, 3 });
                        continue;
                    }

                    Variant left = Variant.FromBytes(LiteralReader.ViewAt(metadata, row), LiteralReader.ViewAt(residual, row));
                    Assert.Equal(row == 0 ? Variant.FromJson("""{"other":1}""") : rows[row].Attributes, left);
                    Assert.DoesNotContain("route", left.Kind == VariantKind.Object ? left.GetKeys() : []);
                }

                // The tree: route fits, a typed column; status and tags kept values of their own.
                CanonicalNode tree = arena.GetNode(node.GetFieldIndex(2));
                Assert.Equal(DTypeKind.Struct, tree.DType.Kind);
                Assert.Equal(DTypeKind.Utf8, tree.DType.GetField(tree.DType.IndexOfField("route")).Kind);
                Assert.Equal(DTypeKind.Variant, tree.DType.GetField(tree.DType.IndexOfField("status")).Kind);
                Assert.Equal(DTypeKind.Variant, tree.DType.GetField(tree.DType.IndexOfField("tags")).Kind);

                // The struct is valid where the row is an object; status is present in rows 0 to 2,
                // as 200, as a text and as a null, and missing from row 3.
                int structure = node.GetFieldIndex(2);
                Assert.Equal([true, true, true, true, false], Enumerable.Range(0, 5).Select(row => ArenaWords.IsValid(arena, structure, row)));
                int status = tree.GetFieldIndex(tree.DType.IndexOfField("status"));
                Assert.Equal([true, true, true, false, false], Enumerable.Range(0, 5).Select(row => ArenaWords.IsValid(arena, status, row)));
                batch.Dispose();
            }

            List<Doc> read = await RoundTripAsync(rows, new() { [nameof(Doc.Attributes)] = plan }, 1);
            Assert.Equal(rows.Select(r => r.Attributes), read.Select(r => r.Attributes));
            Assert.False(read[3].Attributes.Has("status"));
            Assert.True(read[2].Attributes["status"]!.Value.IsNull);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// A file whose rows hold a shredded key in their residual too is refused as it is read: the row
    /// would hold two values of one key. Forged by writing the shredder's node with its residual
    /// swapped for the rows' whole values.
    /// </summary>
    [Fact]
    public async Task AKeyBothShreddedAndInTheResidualIsRefused()
    {
        Doc[] rows = [new(0, Variant.FromJson("""{"route":"/a","n":1}"""), null), new(1, Variant.FromJson("""{"route":"/b"}"""), null)];
        string plain = await WriteAsync(rows, null, 1);
        string forged = Path.Combine(Path.GetTempPath(), $"vorticity-forged-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile file = await VortexFile.OpenAsync(plain, Ct))
            {
                await foreach (RecordBatch batch in file.Scan<Doc>().ToBatchesAsync(Ct))
                {
                    CanonicalArena arena = batch.Arena;
                    CanonicalNode root = arena.GetNode(batch.RootIndex);
                    int attributes = file.DType.IndexOfField(nameof(Doc.Attributes));
                    int column = root.GetFieldIndex(attributes);
                    CanonicalNode variant = arena.GetNode(column);
                    int shredded = VariantShredder.Shred(arena, new DTypeArena(), column, VariantShredding.Object(("route", VariantShredding.Primitive(VariantClass.String))));
                    CanonicalNode node = arena.GetNode(shredded);
                    int both = arena.AddStruct(variant.DType, variant.Length, variant.Validity, [node.GetFieldIndex(0), variant.GetFieldIndex(1), node.GetFieldIndex(2)]);
                    int[] fields = new int[root.FieldCount];
                    for (int i = 0; i < fields.Length; i++)
                    {
                        fields[i] = i == attributes ? both : root.GetFieldIndex(i);
                    }

                    using RecordBatch rebuilt = new RecordBatch(arena, arena.AddStruct(root.DType, root.Length, root.Validity, fields), 0);
                    await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(forged);
                    await writer.WriteBatchAsync(rebuilt, Ct);
                    await writer.CompleteAsync(Ct);
                    batch.Dispose();
                }
            }

            await using VortexFile read = await VortexFile.OpenAsync(forged, Ct);
            VortexFormatException refused = await Assert.ThrowsAsync<VortexFormatException>(async () => await read.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct));
            Assert.Contains("both shredded and in its value", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(plain);
            System.IO.File.Delete(forged);
        }
    }

    /// <summary>A shredding names top-level variant columns only, before anything is written.</summary>
    [Fact]
    public async Task AShreddingOfAnotherColumnIsRefused()
    {
        VariantShredding plan = VariantShredding.Object(("n", VariantShredding.Primitive(VariantClass.Integer)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await WriteAsync([new Doc(0, Variant.Null, null)], new() { [nameof(Doc.Id)] = plan }, 1));
        await Assert.ThrowsAsync<ArgumentException>(async () => await WriteAsync([new Doc(0, Variant.Null, null)], new() { ["Missing"] = plan }, 1));
        Assert.Throws<ArgumentException>(() => VariantShredding.Object(("n", plan), ("n", plan)));
    }

    private static async Task<List<Doc>> RoundTripAsync(Doc[] rows, Dictionary<string, VariantShredding> shredding, int degree)
    {
        string path = await WriteAsync(rows, shredding, degree);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Contains(Vorticity.Arrays.ArrayEncodingId.Variant, file.ResolvedArrayEncodings.ToArray());
            return await file.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<string> WriteAsync(Doc[] rows, Dictionary<string, VariantShredding>? shredding, int degree)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-shredded-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { BlockRows = 512, ShreddedVariants = shredding, DegreeOfParallelism = degree };
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options);
        await writer.WriteAsync<Doc>(rows, Ct);
        await writer.CompleteAsync(Ct);
        return path;
    }

    /// <summary>A document of any shape, most often an object of the keys a shredding may name.</summary>
    private static Variant Document(Random random)
    {
        switch (random.Next(20))
        {
            case 0:
                return Variant.Null;
            case 1:
                return Variant.From(random.NextInt64(-1_000, 1_000));
            case 2:
                return Variant.Array("a", 1L);
            case 3:
                return Variant.From("not an object at all, and long");
        }

        List<(string, Variant)> fields = [];
        void Maybe(string key, Func<Variant> value)
        {
            if (random.Next(10) < 7)
            {
                fields.Add((key, value()));
            }
        }

        Maybe("status", () => random.Next(8) switch
        {
            0 => Variant.From("502"),
            1 => Variant.Null,
            _ => Variant.From(random.Next(4) switch { 0 => random.Next(100), 1 => random.Next(30_000), 2 => random.Next(), _ => random.NextInt64() }),
        });
        Maybe("duration", () => random.Next(6) == 0 ? Variant.From(random.Next(100)) : Variant.From(random.NextDouble() * 1_000));
        Maybe("route", () => Routes[random.Next(Routes.Length)]);
        Maybe("ok", () => random.Next(9) == 0 ? Variant.From("yes") : Variant.From(random.Next(2) == 0));
        Maybe("user", () => random.Next(5) == 0
            ? Variant.From("anonymous")
            : Variant.Object(("id", random.NextInt64(1, 1_000_000)), ("name", $"user-{random.Next(50)}"), (random.Next(2) == 0 ? "role" : "team", "ops")));
        Maybe("tags", () => random.Next(4) == 0
            ? Variant.Array("x", random.Next(10))
            : Variant.Array([.. Enumerable.Range(0, random.Next(4)).Select(i => Variant.From($"tag-{random.Next(20)}"))]));
        Maybe("big", () => new string('x', random.Next(40)));
        Maybe("http.route", () => Routes[random.Next(Routes.Length)]);
        Maybe($"k{random.Next(30)}", () => random.NextDouble());
        return Variant.Object(fields.ToArray());
    }

    /// <summary>A shredding drawn at random: most often some keys of an object, each of a class that may not be its values'.</summary>
    private static VariantShredding Shredding(Random random)
    {
        if (random.Next(10) == 0)
        {
            return random.Next(2) == 0 ? VariantShredding.Primitive(VariantClass.Integer) : VariantShredding.List(VariantClass.String);
        }

        VariantClass AnyClass() => (VariantClass)random.Next(4);
        List<(string, VariantShredding)> keys = [];
        void Maybe(string key, Func<VariantShredding> field)
        {
            if (random.Next(10) < 6)
            {
                keys.Add((key, field()));
            }
        }

        Maybe("status", () => VariantShredding.Primitive(random.Next(3) == 0 ? AnyClass() : VariantClass.Integer));
        Maybe("duration", () => VariantShredding.Primitive(VariantClass.Double));
        Maybe("route", () => VariantShredding.Primitive(VariantClass.String));
        Maybe("ok", () => VariantShredding.Primitive(VariantClass.Boolean));
        Maybe("user", () => random.Next(3) == 0
            ? VariantShredding.Primitive(VariantClass.String)
            : VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)), ("name", VariantShredding.Primitive(VariantClass.String))));
        Maybe("tags", () => VariantShredding.List(VariantClass.String));
        Maybe("big", () => VariantShredding.Primitive(VariantClass.String));
        Maybe("http.route", () => VariantShredding.Primitive(VariantClass.String));
        Maybe("missing", () => VariantShredding.Primitive(AnyClass()));
        if (keys.Count == 0)
        {
            keys.Add(("route", VariantShredding.Primitive(VariantClass.String)));
        }

        return VariantShredding.Object(keys.ToArray());
    }

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes, Variant? Extra);
}
