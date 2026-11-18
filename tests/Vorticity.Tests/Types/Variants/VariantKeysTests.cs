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
    [
        VariantRead.AsInt64, VariantRead.AsString, VariantRead.AsDouble, VariantRead.AsBool, VariantRead.AsTimestamp,
        VariantRead.ToInt64, VariantRead.ToDouble, VariantRead.Has,
    ];

    /// <summary>Keys no row holds: each one's column is every row null, in every class.</summary>
    private static readonly string[] Absent = ["never", "a.rather.long.attribute.name.no.row.holds"];

    /// <summary>
    /// Rows whose keys hold every scalar a read takes from its header or passes over, an integer of each width, a double, a
    /// float, a timestamp, a null, a date and a decimal among them, and keys no row holds: every key in every class read
    /// together equals each read alone.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task KeysOfEveryScalarAndKeysNoRowHoldsReadTogetherEqualEachReadAlone(int seed)
    {
        Random random = new Random(seed);
        string[] names = [.. Enumerable.Range(0, 12).Select(k => k % 5 == 0 ? $"a.rather.long.attribute.name.{k}" : $"k{k}")];
        Row[] rows = [.. Enumerable.Range(0, 3_000).Select(i => new Row(i, DrawScalars(random, names)))];
        await TogetherEqualsAloneAsync(rows, [.. names, .. Absent], written: true);
    }

    /// <summary>
    /// Row <paramref name="row"/> of the column <paramref name="path"/> was read into holds what the row written holds at the
    /// path's key, read through the value itself (<see cref="Variant"/>): the path read alone goes through the code that
    /// writes a column's slots too, and says nothing of it.
    /// </summary>
    private static void SameAsWritten(VortexColumn column, int row, VariantFieldExpr path, string key, Variant written)
    {
        Variant? field = written[key];
        string what = $"{key} {path.Read}: row {row}, written {written.ToJson()}";
        switch (path.Read)
        {
            case VariantRead.Has:
                Assert.True(column.AsBool()[row] == field.HasValue, what);
                break;
            case VariantRead.AsBool:
                Assert.True(column.IsValid(row) == (field?.AsBool()).HasValue, what);
                Assert.True(!column.IsValid(row) || column.AsBool()[row] == field!.Value.AsBool(), what);
                break;
            case VariantRead.AsString:
                Assert.True(column.IsValid(row) == (field?.AsString() is not null), what);
                Assert.True(!column.IsValid(row) || column.AsBinary().GetSpan(row).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(field!.Value.AsString()!)), what);
                break;
            case VariantRead.AsDouble or VariantRead.ToDouble:
            {
                double? expected = path.Read == VariantRead.AsDouble ? field?.AsDouble() : field?.ToDouble();
                Assert.True(column.IsValid(row) == expected.HasValue, what);
                Assert.True(!column.IsValid(row) || column.AsPrimitive<double>()[row].Equals(expected!.Value), what);
                break;
            }

            default:
            {
                long? expected = path.Read switch
                {
                    VariantRead.AsInt64 => field?.AsInt64(),
                    VariantRead.ToInt64 => field?.ToInt64(),
                    _ => field?.AsTimestamp() is { } instant ? (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond : null,
                };
                Assert.True(column.IsValid(row) == expected.HasValue, what);
                Assert.True(!column.IsValid(row) || column.AsPrimitive<long>()[row] == expected!.Value, what);
                break;
            }
        }
    }

    /// <summary>An object of a few keys, each a scalar of any type the encoding writes a field as.</summary>
    private static Variant DrawScalars(Random random, string[] names)
    {
        List<(string, Variant)> fields = [];
        foreach (string name in names.OrderBy(_ => random.Next()).Take(random.Next(0, 7)))
        {
            fields.Add((name, random.Next(14) switch
            {
                0 => Variant.From((long)random.Next(-100, 100)),
                1 => Variant.From((long)random.Next(-30_000, 30_000)),
                2 => Variant.From((long)random.Next(int.MinValue, int.MaxValue)),
                3 => Variant.From(random.NextInt64()),
                4 => Variant.From(random.NextDouble() * 1000),
                5 => Variant.From((float)(random.NextDouble() * 1000)),
                6 => Variant.From(random.Next(2) == 0),
                7 => Variant.Null,
                8 => Variant.From(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(100_000))),
                9 => Variant.From(new DateOnly(2026, 11, 1).AddDays(random.Next(30))),
                10 => Variant.From((decimal)random.Next(100_000) / 100),
                11 => Variant.From($"t{random.Next(30)}"),
                12 => Variant.From($"a text long enough to lie in a data buffer, past a short string's sixty-three bytes: {random.Next(30)}"),
                _ => Variant.From(random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            }));
        }

        return Variant.Object([.. fields]);
    }

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
        await TogetherEqualsAloneAsync(rows, names);
    }

    /// <summary>
    /// Rows that repeat a value one after the other, as a metric's series holds its attributes, which a read of the keys
    /// together takes from the first row of their run: in runs of one to forty, nulls and absent rows among them, every key in
    /// every class read together equals each read alone.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task KeysOfRowsThatRepeatReadTogetherEqualEachReadAlone(int seed)
    {
        Random random = new Random(seed);
        string[] names = [.. Enumerable.Range(0, 40).Select(k => k % 7 == 0 ? $"a.rather.long.attribute.name.{k}" : $"k{k}")];
        List<Row> rows = [];
        while (rows.Count < 5_000)
        {
            Variant value = Draw(random, names);
            for (int run = 1 + random.Next(random.Next(4) == 0 ? 1 : 40); run > 0; run--)
            {
                rows.Add(new Row(rows.Count, value));
            }
        }

        await TogetherEqualsAloneAsync([.. rows], names);
    }

    private static async Task TogetherEqualsAloneAsync(Row[] rows, string[] names, bool written = false)
    {
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
                        int alone = Storage(arena, paths[p].Apply(arena, variant));
                        int read = Storage(arena, together[p]);
                        Same(batch, alone, read, paths[p].Text + " " + paths[p].Read);
                        Cleared(batch, read, paths[p].Text + " " + paths[p].Read);
                        if (written)
                        {
                            VortexColumn ids = new VortexColumn(batch, arena.GetNode(batch.RootIndex).GetFieldIndex(0));
                            VortexColumn column = new VortexColumn(batch, read);
                            for (int row = 0; row < batch.RowCount; row++)
                            {
                                SameAsWritten(column, row, paths[p], names[p / Reads.Length], rows[ids.AsPrimitive<long>()[row]].Attributes);
                            }
                        }
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

    /// <summary>The column at <paramref name="index"/>, or the numbers a column of timestamps stores them as.</summary>
    private static int Storage(CanonicalArena arena, int index)
    {
        CanonicalNode node = arena.GetNode(index);
        return node.Kind == CanonicalKind.Extension ? node.StorageIndex : index;
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

    /// <summary>
    /// The slots of the rows the column at <paramref name="index"/> holds no value at are zero, as a column is before its rows
    /// are written: a number's eight bytes, a view's sixteen.
    /// </summary>
    private static void Cleared(RecordBatch batch, int index, string what)
    {
        CanonicalNode node = batch.Arena.GetNode(index);
        int width = node.Kind switch
        {
            CanonicalKind.VarBinView => 16,
            CanonicalKind.Primitive => 8,
            _ => 0,
        };

        ReadOnlySpan<byte> slots = width == 0 ? default : (width == 16 ? node.Views : node.Values).Span;
        VortexColumn column = new VortexColumn(batch, index);
        for (int row = 0; width > 0 && row < column.Length; row++)
        {
            if (!column.IsValid(row))
            {
                Assert.True(!slots.Slice(row * width, width).ContainsAnyExcept((byte)0), $"{what}: row {row} absent, its slot not cleared");
            }
        }
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
