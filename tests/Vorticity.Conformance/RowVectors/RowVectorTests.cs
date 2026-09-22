// Byte-exactness against Rust, for the row encoding.
//
// Being merely order-compatible is not enough - two implementations
// could each be internally consistent and still disagree, which would silently break any
// cross-language comparison. The vectors in row-vectors/vectors.jsonl were produced by
// vortex-row itself (tools/row-vectors); this test rebuilds the same columns and compares the
// encoded bytes one case at a time.
//
// The property tests in Vorticity.Tests prove the encoding SORTS right. Only this one proves it
// is the same encoding the reference produces.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Conformance.Sidecar;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Conformance.RowVectors;

public sealed class RowVectorTests
{
    /// <summary>
    /// The Vortex release the checked-in vectors were produced by. The format is experimental
    /// upstream, so a version bump is a REGENERATION - this constant makes that a loud failure
    /// rather than a silent re-baseline.
    /// </summary>
    private const string ExpectedVortexVersion = "0.86.1";

    [Fact]
    public void EveryVectorMatchesTheReferenceByteForByte()
    {
        (string version, Dictionary<string, Vector> vectors) = Load();
        Assert.Equal(ExpectedVortexVersion, version);
        Assert.Equal(
            ExpectedVortexVersion,
            RowEncoder.VortexVersion);

        int cases = 0;
        int rows = 0;
        long bytes = 0;
        List<string> failures = [];

        foreach (string name in RowVectorCases.Names)
        {
            Assert.True(vectors.ContainsKey(name), $"the vector file has no case '{name}'");
            Vector expected = vectors[name];

            using RowVectorCases builder = new RowVectorCases();
            int[] columns = builder.Build(name);
            Assert.Equal(expected.Fields.Length, columns.Length);

            using RowKeys keys = RowEncoder.Encode(builder.Batch(columns), expected.Fields);
            if (keys.RowCount != expected.Rows.Length)
            {
                failures.Add($"{name}: {keys.RowCount} rows, reference had {expected.Rows.Length}");
                continue;
            }

            for (int i = 0; i < keys.RowCount; i++)
            {
                string actual = Hex(keys.Row(i));
                if (!string.Equals(actual, expected.Rows[i], StringComparison.Ordinal))
                {
                    failures.Add($"{name} row {i}:\n  reference {expected.Rows[i]}\n  ours      {actual}");
                }

                rows++;
                bytes += keys.Sizes[i];
            }

            cases++;
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // Every case the file carries is a case we checked: a name added to the generator and not
        // to RowVectorCases.Names would otherwise be silently ignored.
        Assert.Equal(vectors.Count, cases);

        Console.Out.WriteLine(
            $"ROW VECTORS: {cases} cases, {rows} rows, {bytes} bytes matched vortex-row " +
            $"{ExpectedVortexVersion} byte for byte.");
    }

    /// <summary>
    /// The coverage assertion. "48 cases matched" means nothing if the cases are all i32: this
    /// requires the file to actually reach the encoders whose bytes are hardest to get right.
    /// </summary>
    [Fact]
    public void TheVectorsCoverEveryEncoder()
    {
        (_, Dictionary<string, Vector> vectors) = Load();

        foreach (string required in
            (string[])["i8_", "u64_", "f16_", "f32_", "f64_", "bool_", "null_dtype",
                       "utf8_", "binary_", "decimal_p38", "struct_", "fsl_", "multi_column"])
        {
            bool found = false;
            foreach (string name in vectors.Keys)
            {
                if (name.StartsWith(required, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found, $"no vector case covers '{required}'");
        }

        // Both directions and both null placements must appear, since they are independent and
        // the encoder gets exactly one chance to keep them so.
        bool descending = false;
        bool nullsLast = false;
        foreach (Vector vector in vectors.Values)
        {
            foreach (RowSortField field in vector.Fields)
            {
                descending |= field.Descending;
                nullsLast |= !field.NullsFirst;
            }
        }

        Assert.True(descending, "no vector case is descending");
        Assert.True(nullsLast, "no vector case puts nulls last");
    }

    private static (string Version, Dictionary<string, Vector> Vectors) Load()
    {
        string path = Path.Combine(Root(), "row-vectors", "vectors.jsonl");
        Assert.True(
            System.IO.File.Exists(path),
            $"no row vectors at {path}; regenerate with tools/row-vectors");

        string? version = null;
        Dictionary<string, Vector> vectors = [];
        foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonValue value = JsonParser.Parse(line);
            if (value.Find("vortex_version") is JsonValue declared)
            {
                version = declared.Text;
                continue;
            }

            string name = value.RequireString("case");
            JsonValue[] fieldItems = value.Require("fields").Items;
            RowSortField[] fields = new RowSortField[fieldItems.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = new RowSortField(
                    fieldItems[i].RequireBoolean("descending"),
                    fieldItems[i].RequireBoolean("nulls_first"));
            }

            JsonValue[] rowItems = value.Require("rows").Items;
            string[] rows = new string[rowItems.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = rowItems[i].Text;
            }

            vectors[name] = new Vector(fields, rows);
        }

        Assert.NotNull(version);
        return (version, vectors);
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        StringBuilder text = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string Root([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

    private readonly record struct Vector(RowSortField[] Fields, string[] Rows);
}
