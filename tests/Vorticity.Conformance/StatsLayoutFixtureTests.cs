// vortex.stats, the only layout this library reads that no corpus file exercises.
//
// It is the legacy zone map of editions core2025.05.0 through core2025.10.0, read upstream by the
// same ZonedReader as vortex.zoned and structurally identical to it - data at child 0. Vortex 0.86.1
// has no writer path that emits one, so no corpus file contains one. A reader can
// support a component and never once be exercised, and nothing in the suite could tell the
// difference between that and a reader that does not support it at all.
//
// THE FIXTURE IS THE SOURCE FILE WITH TWELVE BYTES CHANGED. `vortex.zoned` and `vortex.stats` are
// both twelve bytes, so the equal-length patch that forged/ already uses applies unchanged: the id
// is a length-prefixed flatbuffer string, an in-place overwrite keeps every offset valid, and Vortex
// checksums nothing.
//
// WHY THAT IS A BETTER ORACLE THAN A HAND-BUILT FIXTURE. A forged file usually comes with expected
// values in a manifest, which means the fixture and the expectation are written by the same hand and
// agree by construction. Here the expectation is not written down at all: the patched file must
// return exactly what `containers/zoned_many_zones_nulls` returns, because the two files differ in
// one identifier and in nothing else. 65 536 rows over five columns, compared value by value,
// against an oracle that is the same bytes read by a different reader.
//
// PARTIAL BY CONSTRUCTION, AND THE LIMIT IS WORTH STATING. Upstream's two vtables both take
// exactly two children, (data, zones), and both put data at child 0, but the legacy one validates
// child 1 against a stats-table dtype where the modern one expects aggregate specs. Only the id was
// patched, so this fixture's child 1 is still a zoned zone map. That exercises everything
// StatsLayoutReader actually does, which is resolve child 0 and read it, and it does NOT exercise
// legacy metadata parsing. A fixture claiming otherwise would be asserting a schema it does not
// contain.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.Conformance.Corpus;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Conformance;

public sealed class StatsLayoutFixtureTests
{
    private const string Fixture = "legacy/stats_layout";

    private static string FixturePath =>
        System.IO.Path.Combine(CorpusCatalog.ForgedRoot, ForgedCatalog.Find(Fixture).Path);

    private static string SourcePath =>
        System.IO.Path.Combine(CorpusCatalog.CorpusRoot, ForgedCatalog.Find(Fixture).Source!);

    /// <summary>The patched file declares the legacy id where the source declares the modern one.</summary>
    [Fact]
    public async Task TheFixtureDeclaresTheLegacyLayoutId()
    {
        await using VortexFile forged = await VortexFile.OpenAsync(FixturePath, CancellationToken.None);
        await using VortexFile source = await VortexFile.OpenAsync(SourcePath, CancellationToken.None);

        List<string> forgedIds = LayoutIds(forged);
        List<string> sourceIds = LayoutIds(source);

        Assert.Contains("vortex.stats", forgedIds);
        Assert.DoesNotContain("vortex.zoned", forgedIds);
        Assert.Contains("vortex.zoned", sourceIds);
        Assert.DoesNotContain("vortex.stats", sourceIds);

        // Everything else about the two files is the same, which is what makes the value comparison
        // below an oracle rather than a second opinion.
        Assert.Equal(sourceIds.Count, forgedIds.Count);
        Assert.Equal(source.Schema.ToString(), forged.Schema.ToString());
    }

    /// <summary>A legacy zone map resolves to the stats reader rather than to an unknown id.</summary>
    [Fact]
    public async Task TheLegacyIdResolvesToTheStatsReader()
    {
        await using VortexFile forged = await VortexFile.OpenAsync(FixturePath, CancellationToken.None);

        bool found = false;
        for (int i = 0; i < forged.LayoutEncodingCount; i++)
        {
            if (string.Equals(forged.GetLayoutEncodingId(i), "vortex.stats", StringComparison.Ordinal))
            {
                found = true;
                Assert.Equal(LayoutEncodingId.Stats, forged.GetLayoutEncoding(i));
            }
        }

        Assert.True(found, "the fixture declares no vortex.stats layout");
        Assert.Equal(LayoutEncodingId.Stats, StatsLayoutReader.Instance.EncodingId);
    }

    /// <summary>A full scan through the legacy layout returns exactly what the modern one returns.</summary>
    /// <remarks>
    /// The assertion is on the rendered values rather than on a row count, because a structural
    /// layout that resolved the wrong child would still produce the right NUMBER of rows - reading
    /// the zones child instead of the data child is precisely the mistake that costs nothing
    /// structurally and everything semantically.
    /// </remarks>
    [Fact]
    public async Task ScanningThroughTheLegacyLayoutReturnsTheSourcesValues()
    {
        List<string> forged = await Render(FixturePath);
        List<string> source = await Render(SourcePath);

        Assert.NotEmpty(source);
        Assert.Equal(source.Count, forged.Count);
        Assert.Equal(source, forged);
    }

    /// <summary>
    /// A filtered scan returns the same rows through the legacy layout, which cannot prune.
    /// </summary>
    /// <remarks>
    /// The distinguishing behaviour, and the one a structural-only reader could get wrong in a way
    /// the unfiltered scan cannot catch. `vortex.stats` reports pruning unavailable, so the filter
    /// must be evaluated against every split rather than skipped by zone; the ROWS it returns must
    /// still equal what the prunable layout returns. A reader that pruned on a zone map it has not
    /// actually parsed would silently drop rows here.
    /// </remarks>
    [Fact]
    public async Task AFilteredScanReturnsTheSameRowsThroughTheLegacyLayout()
    {
        VortexExpr filter = Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_020_000L)));

        List<string> forged = await RenderFiltered(FixturePath, filter);
        List<string> source = await RenderFiltered(SourcePath, filter);

        Assert.NotEmpty(source);
        Assert.Equal(source, forged);
    }

    private static async Task<List<string>> RenderFiltered(string path, VortexExpr filter)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Where(filter)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            StructColumn root = batch.Root.AsStruct();
            for (int row = 0; row < batch.RowCount; row++)
            {
                rows.Add(Cell(root.GetField(0), row));
            }
        }

        return rows;
    }

    private static List<string> LayoutIds(VortexFile file)
    {
        List<string> ids = [];
        for (int i = 0; i < file.LayoutEncodingCount; i++)
        {
            ids.Add(file.GetLayoutEncodingId(i));
        }

        return ids;
    }

    private static async Task<List<string>> Render(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        List<string> rows = [];
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            StructColumn root = batch.Root.AsStruct();
            for (int row = 0; row < batch.RowCount; row++)
            {
                StringBuilder line = new StringBuilder();
                for (int field = 0; field < root.FieldCount; field++)
                {
                    line.Append(Cell(root.GetField(field), row)).Append('│');
                }

                rows.Add(line.ToString());
            }
        }

        return rows;
    }

    private static string Cell(VortexColumn column, int row)
    {
        if (!column.IsValid(row))
        {
            return "null";
        }

        if (column.Kind == CanonicalKind.VarBinView)
        {
            return column.AsBinary().GetString(row) ?? "null";
        }

        if (column.Kind == CanonicalKind.Bool)
        {
            return column.AsBool()[row] ? "true" : "false";
        }

        // Dispatched on the physical type rather than widened to i64: AsPrimitive<T> requires the
        // exactly matching .NET type, and the point here is to render whatever the
        // file holds, not to assume a width.
        return column.DType.PType switch
        {
            PType.I8 => column.AsPrimitive<sbyte>()[row].ToString(CultureInfo.InvariantCulture),
            PType.I16 => column.AsPrimitive<short>()[row].ToString(CultureInfo.InvariantCulture),
            PType.I32 => column.AsPrimitive<int>()[row].ToString(CultureInfo.InvariantCulture),
            PType.I64 => column.AsPrimitive<long>()[row].ToString(CultureInfo.InvariantCulture),
            PType.U8 => column.AsPrimitive<byte>()[row].ToString(CultureInfo.InvariantCulture),
            PType.U16 => column.AsPrimitive<ushort>()[row].ToString(CultureInfo.InvariantCulture),
            PType.U32 => column.AsPrimitive<uint>()[row].ToString(CultureInfo.InvariantCulture),
            PType.U64 => column.AsPrimitive<ulong>()[row].ToString(CultureInfo.InvariantCulture),
            PType.F32 => column.AsPrimitive<float>()[row].ToString("R", CultureInfo.InvariantCulture),
            PType.F64 => column.AsPrimitive<double>()[row].ToString("R", CultureInfo.InvariantCulture),
            _ => column.Kind.ToString(),
        };
    }
}
