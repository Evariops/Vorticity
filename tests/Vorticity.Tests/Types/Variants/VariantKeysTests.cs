// The keys of a variant column read together (VariantFieldExpr.ApplyKeys), as a writer indexes its shredded keys: one pass
// over the rows' dictionaries and objects, whatever the number of keys.
//
// THE ORACLE IS EACH PATH READ ALONE (VariantFieldExpr.Apply): every key, in every class, the same column, row for row.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantKeysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantRead[] Reads =
        [VariantRead.AsInt64, VariantRead.AsString, VariantRead.AsDouble, VariantRead.AsBool, VariantRead.ToInt64, VariantRead.Has];

    /// <summary>
    /// 5 000 rows of objects over 40 keys, each row's own dictionary of a few of them in any order, with integers, texts short
    /// and long, doubles, booleans and nested objects, and rows that are no object, null, or absent: every key in every class
    /// read together equals each read alone.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeysReadTogetherEqualEachReadAlone(int seed)
    {
        Random random = new Random(seed);
        string[] names = [.. Enumerable.Range(0, 40).Select(k => k % 7 == 0 ? $"a.rather.long.attribute.name.{k}" : $"k{k}")];
        Row[] rows = [.. Enumerable.Range(0, 5_000).Select(i => new Row(i, Draw(random, names)))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-variant-keys-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
            {
                await writer.WriteAsync<Row>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            VariantFieldExpr[] paths = [.. names.SelectMany(name => Reads.Select(read => new VariantFieldExpr(new ColumnFieldExpr(nameof(Row.Attributes)), [VariantStep.Of(name)], read)))];
            VariantFieldExpr.KeyReads reads = new(paths);
            int[] together = new int[paths.Length];
            long checkedRows = 0;
            await foreach (RecordBatch batch in file.Scan<Row>().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    CanonicalArena arena = batch.Arena;
                    int variant = arena.GetNode(batch.RootIndex).GetFieldIndex(1);
                    Assert.Equal(2, arena.GetNode(variant).FieldCount);
                    VariantFieldExpr.ApplyKeys(arena, variant, reads, together);
                    for (int p = 0; p < paths.Length; p++)
                    {
                        int alone = paths[p].Apply(arena, variant);
                        Same(batch, alone, together[p], paths[p].Text + " " + paths[p].Read);
                    }

                    checkedRows += batch.RowCount;
                }
            }

            Assert.Equal(rows.Length, checkedRows);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A row's attributes: an object of a few keys most of the time, else a scalar, an array, a null or none.</summary>
    private static Variant Draw(Random random, string[] names)
    {
        int shape = random.Next(20);
        if (shape == 0)
        {
            return Variant.Null;
        }

        if (shape == 1)
        {
            return Variant.From((long)random.Next(1000));
        }

        if (shape == 2)
        {
            return Variant.FromJson("""["x", 1]""");
        }

        int count = random.Next(0, 9);
        List<(string, Variant)> fields = [];
        foreach (string name in names.OrderBy(_ => random.Next()).Take(count))
        {
            fields.Add((name, random.Next(7) switch
            {
                0 => Variant.From((long)random.Next(-500, 500)),
                1 => Variant.From($"t{random.Next(30)}"),
                2 => Variant.From($"a text long enough to lie in a data buffer {random.Next(30)}"),
                3 => Variant.From(random.NextDouble() * 1000),
                4 => Variant.From(random.Next(2) == 0),
                5 => Variant.Object(("inner", Variant.From((long)random.Next(10)))),
                _ => Variant.From(random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            }));
        }

        return Variant.Object([.. fields]);
    }

    /// <summary>Whether the columns at <paramref name="left"/> and <paramref name="right"/> hold the same values, row for row.</summary>
    private static void Same(RecordBatch batch, int left, int right, string what)
    {
        VortexColumn a = new VortexColumn(batch, left);
        VortexColumn b = new VortexColumn(batch, right);
        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Length, b.Length);
        for (int row = 0; row < a.Length; row++)
        {
            Assert.True(a.IsValid(row) == b.IsValid(row), $"{what}: row {row} valid {a.IsValid(row)} alone, {b.IsValid(row)} together");
            if (!a.IsValid(row))
            {
                continue;
            }

            switch (a.Kind)
            {
                case CanonicalKind.Bool:
                    Assert.True(a.AsBool()[row] == b.AsBool()[row], $"{what}: row {row}");
                    break;
                case CanonicalKind.VarBinView:
                    Assert.True(a.AsBinary().GetSpan(row).SequenceEqual(b.AsBinary().GetSpan(row)), $"{what}: row {row}");
                    break;
                case CanonicalKind.Primitive when a.DType.PType == PType.F64:
                    Assert.True(a.AsPrimitive<double>()[row].Equals(b.AsPrimitive<double>()[row]), $"{what}: row {row}");
                    break;
                default:
                    Assert.True(a.AsPrimitive<long>()[row] == b.AsPrimitive<long>()[row], $"{what}: row {row}");
                    break;
            }
        }
    }

    [VortexRecord]
    public partial record struct Row(long Id, Variant Attributes);
}
