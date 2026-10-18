// Variant columns the reference shredded, read back. Each case's rows were written twice by Vortex Rust
// (tools/conformance-gen/examples/gen_shredded_variants.rs): shredded by the Arrow variant crate the
// reference uses, `shred_variant`, and as they were. The shredded file must read as the same variants,
// row for row, as its plain twin.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class ShreddedVortexVariantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each case in each shredded form: the reference's tree, <c>shredded</c>, and the Parquet layout it reads but never writes on its own, <c>parquet</c>.</summary>
    public static TheoryData<string, string> Cases
    {
        get
        {
            TheoryData<string, string> cases = [];
            foreach (string name in new[] { "object", "nested", "list", "primitive", "dotted", "chunks" })
            {
                cases.Add(name, "shredded");
                cases.Add(name, "parquet");
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AShreddedColumnReadsAsItsRowsUnshredded(string name, string form)
    {
        string directory = DataDirectory();
        List<string> plain = await RowsAsync(Path.Combine(directory, $"{name}.plain.vortex"));
        List<string> shredded = await RowsAsync(Path.Combine(directory, $"{name}.{form}.vortex"));
        Assert.NotEmpty(plain);
        Assert.Equal(plain, shredded);
    }

    /// <summary>
    /// The rows of the shredded object, value by value: a key of another type than its shredded one
    /// read from the field's own values, a shredded key a row lacks left out rather than made null,
    /// a key whose value is null kept, the residual's keys merged in, and rows that are not objects
    /// read whole from the variant's values.
    /// </summary>
    [Fact]
    public async Task AShreddedObjectReadsAsTheStandardRebuildsIt()
    {
        List<string> rows = await RowsAsync(Path.Combine(DataDirectory(), "object.shredded.vortex"));
        Assert.Equal(
            [
                """{"route":"/api/users","status":200,"user":"u1"}""",
                """{"route":"/api/orders","status":502}""",
                """{"extra":[1,2,3],"route":"/health","status":"late"}""",
                """{"route":"/api/users"}""",
                "null",
                "42",
                """{"route":null,"status":404,"user":{"id":7,"name":"x"}}""",
            ],
            rows);
    }

    /// <summary>
    /// A take of rows across chunks shredded three ways reads each row as a scan of the whole does:
    /// the selection goes down the core storage and the shredded tree alike.
    /// </summary>
    [Theory]
    [InlineData("chunks.plain.vortex")]
    [InlineData("chunks.shredded.vortex")]
    [InlineData("chunks.parquet.vortex")]
    public async Task ATakeAcrossChunksShreddedDifferentlyReadsTheRowsAScanReads(string file)
    {
        string path = Path.Combine(DataDirectory(), file);
        List<string> all = await RowsAsync(path);
        long[] wanted = [0, 1, 8191, 8192, 8193, 12000, 16384, 16385, 24575];
        List<string> taken = await RowsAsync(path, wanted);
        Assert.Equal(wanted.Select(row => all[(int)row]), taken);
    }

    /// <summary>Where the files the reference wrote lie, beside this test's sources.</summary>
    private static string DataDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "Data", "shredded-variants"));

    /// <summary>Each row of the file's variant column <c>v</c>, or of the rows <paramref name="take"/> names, as the JSON of its value.</summary>
    private static async Task<List<string>> RowsAsync(string path, long[]? take = null)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
        // The take is the one that pushes the wanted rows down into the decoders.
        IAsyncEnumerable<RecordBatch> batches = take is null
            ? file.Scan().ToBatchesAsync(Ct)
            : file.ScanBuilder().Take(take).ExecuteAsync();
        await foreach (RecordBatch batch in batches.WithCancellation(Ct))
        {
            using (batch)
            {
                VortexColumn variant = batch.Column(0);
                Assert.Equal(DTypeKind.Variant, variant.DType.Kind);
                StructColumn parts = variant.AsStruct();
                BinaryColumn metadata = parts.GetField(0).AsBinary();
                BinaryColumn value = parts.GetField(1).AsBinary();

                // A take delivers its rows in their blocks, with a selection.
                foreach (int row in batch.View.Selection)
                {
                    rows.Add(variant.IsValid(row) ? Json(metadata.GetSpan(row), value.GetSpan(row)) : "<null row>");
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// The value as JSON: an object's fields in the order of their names, numbers in their value whatever
    /// their width, which the format's equivalence classes hold equal and a rebuild need not keep.
    /// </summary>
    private static string Json(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        int basic = value[0] & 0x03;
        if (basic is 2 or 3)
        {
            VariantNested nested = VariantNested.Read(value);
            if (basic == 2)
            {
                VariantDictionary names = VariantDictionary.Read(metadata);
                List<(string Name, string Json)> fields = [];
                for (int i = 0; i < nested.Count; i++)
                {
                    fields.Add((Encoding.UTF8.GetString(names[nested.IdAt(i)]), Json(metadata, nested.ValueAt(i))));
                }

                return "{" + string.Join(",", fields.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"\"{f.Name}\":{f.Json}")) + "}";
            }

            List<string> elements = [];
            for (int i = 0; i < nested.Count; i++)
            {
                elements.Add(Json(metadata, nested.ValueAt(i)));
            }

            return "[" + string.Join(",", elements) + "]";
        }

        VariantValue scalar = ParquetVariant.Read(metadata, value);
        return scalar.Kind switch
        {
            VariantKind.Null => "null",
            VariantKind.Bool => scalar.Integer != 0 ? "true" : "false",
            VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => scalar.Integer.ToString(CultureInfo.InvariantCulture),
            VariantKind.Float or VariantKind.Double => scalar.Number.ToString("R", CultureInfo.InvariantCulture),
            VariantKind.String => $"\"{Encoding.UTF8.GetString(scalar.Bytes)}\"",
            _ => Convert.ToHexString(scalar.Bytes),
        };
    }
}
