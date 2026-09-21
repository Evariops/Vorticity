// File-level pruning: `VortexFile.MayMatch(expr)` answers from
// the footer's statistics without reading a data segment, and it is a SUPERSET test under the
// pruning invariant: false only when no row can match.
//
// The oracle is the scan itself, unpruned: a file this says false for must scan to nothing, on
// every corpus file that carries statistics, for every top-level field whose maximum is exact and
// of a kind a filter can name. The other direction -- true means "read it and see" -- is held on
// the predicate the maximum itself satisfies.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class FileMayMatchTests
{
    [Fact]
    public async Task AFileThatCannotMatchScansToNothingAndOneThatMayIsNotRefused()
    {
        Decoders.EnsureRegistered();

        int files = 0;
        int fields = 0;
        List<string> refusedWrongly = [];
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            // The file itself says whether it carries statistics: the authority, not a manifest.
            await using VortexFile file = await VortexFile.OpenAsync(
                entry.Path, VortexOpenOptions.Default, CancellationToken.None);
            if (!file.HasFileStatistics || file.DType.Kind != DTypeKind.Struct || file.RowCount == 0)
            {
                continue;
            }

            files++;
            FileStatistics statistics = file.FileStatistics;
            for (int i = 0; i < statistics.FieldCount && i < file.DType.FieldCount; i++)
            {
                DType dtype = file.DType.GetField(i);
                if (dtype.Kind is not (DTypeKind.Primitive or DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary))
                {
                    continue;
                }

                FieldStatistics stats = statistics.GetField(i);
                if (!stats.HasMax || stats.MaxPrecision != StatPrecision.Exact
                    || !FileStatisticsPruner.TryLiteral(stats.Max, out FilterLiteral max))
                {
                    continue;
                }

                // A field NAMED with a dot (`types/struct_field_names` has `a.b`) cannot be
                // addressed by the path syntax, which splits on it: the projection, the zone
                // pruner and this one all read `a.b` as `b` inside `a`, and answer "may match".
                string name = file.DType.GetFieldName(i);
                if (name.Contains('.', StringComparison.Ordinal))
                {
                    continue;
                }

                fields++;
                FieldExpr field = Expr.Field(name);

                // Above an exact maximum nothing can match: the file says so, and the scan agrees.
                VortexExpr above = Expr.Gt(field, Expr.Literal(max));
                Assert.False(
                    file.MayMatch(above),
                    $"{entry.Id}.{file.DType.GetFieldName(i)}: the statistics prove x > max impossible");
                Assert.Equal(0, await Count(file, above));

                // At or below it the file may match, and saying otherwise would be the one error
                // pruning is never allowed to make.
                VortexExpr atMost = Expr.Le(field, Expr.Literal(max));
                if (!file.MayMatch(atMost))
                {
                    refusedWrongly.Add(entry.Id + "." + file.DType.GetFieldName(i));
                }
            }

            // A nested path has no file statistic and is never refused.
            Assert.True(file.MayMatch(Expr.IsNotNull(Expr.Field("no.such.field"))));
        }

        Console.Out.Write(
            "FILE MAYMATCH: " + files.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " struct-root files with statistics, " + fields.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " fields with an exact maximum: every `x > max` refused and scanned to nothing, " +
            refusedWrongly.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " `x <= max` refused wrongly.\n");

        Assert.Empty(refusedWrongly);
        Assert.True(files > 0, "the corpus should carry files with statistics and a struct root");
        Assert.True(fields > 0, "the corpus should carry fields with an exact maximum");
    }

    [Fact]
    public async Task AFileWithoutStatisticsIsNeverRefused()
    {
        Decoders.EnsureRegistered();

        int files = 0;
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            await using VortexFile file = await VortexFile.OpenAsync(
                entry.Path, VortexOpenOptions.Default, CancellationToken.None);
            if (file.HasFileStatistics || file.DType.Kind != DTypeKind.Struct || file.DType.FieldCount == 0)
            {
                continue;
            }

            // The constant is drawn from the column's own kind: a comparison across kinds is
            // refused before the statistics are consulted at all, which would say nothing about
            // what a file without them answers.
            if (!TryExceedingConstant(file.DType.GetField(0), out FilterLiteral above))
            {
                continue;
            }

            files++;
            FieldExpr field = Expr.Field(file.DType.GetFieldName(0));
            Assert.True(file.MayMatch(Expr.Gt(field, Expr.Literal(above))));
        }

        Assert.True(files > 0, "the corpus should carry struct-root files without statistics");
    }

    /// <summary>A constant of <paramref name="dtype"/>'s kind that no value of it exceeds.</summary>
    /// <returns><see langword="false"/> for a kind no comparison reads.</returns>
    private static bool TryExceedingConstant(DType dtype, out FilterLiteral value)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
                value = FilterLiteral.From(true);
                return true;
            case DTypeKind.Primitive:
                value = FilterLiteral.From(long.MaxValue);
                return true;
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                value = FilterLiteral.From(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
                return true;
            default:
                value = FilterLiteral.Null;
                return false;
        }
    }

    /// <summary>Rows an UNPRUNED scan returns under <paramref name="filter"/>: the truth.</summary>
    private static async Task<long> Count(VortexFile file, VortexExpr filter)
    {
        long rows = 0;
        await foreach (var batch in file.ScanBuilder().Where(filter).WithPruning(false).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
