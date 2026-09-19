// A filter a dictionary answers from its values, and the proof that the column is never built.
//
// The quantity is `FlatLayoutReader.ValuesDecoded`, the same counter the block-pruning tests read:
// it counts what a scan materialized, so a push that works shows as a count near the number of
// rows the predicate keeps rather than near the number of rows the file holds. Correctness is
// asserted against the same scan without a predicate, filtered in memory, so the two paths have to
// agree row for row and not merely in their totals.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class FilterPushDownTests
{
    private const string Field = "strs";
    private const int Rows = 24_576;
    private const int Labels = 16;
    private const string Needle = "label-07";

    /// <summary>One row in sixteen matches, and the column is never materialized to find them.</summary>
    [Fact]
    public async Task ADictionaryAnswersAnEqualityWithoutBuildingTheColumn()
    {
        string path = Write();
        try
        {
            (List<string> pushed, long decoded) = await ReadAsync(
                path, Expr.Eq(Expr.Field(Field), Expr.Literal(FilterLiteral.From(Needle))));
            (List<string> all, _) = await ReadAsync(path, null);

            List<string> expected = all.FindAll(value => value == Needle);
            Assert.Equal(Rows / Labels, expected.Count);
            Assert.Equal(expected, pushed);

            // The predicate keeps one row in sixteen. Without the push the scan materializes every
            // row of the column to compare it; with it, only the rows that survived are built.
            Assert.True(
                decoded < Rows / 2,
                $"a filtered scan materialized {decoded} values of {Rows}, so the comparison was " +
                "answered by decoding the column rather than by the dictionary.");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<(List<string> Values, long Decoded)> ReadAsync(
        string path, VortexExpr? filter)
    {
        FlatLayoutReader.ValuesDecoded = 0;
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        ScanBuilder scan = file.Scan();
        if (filter is not null)
        {
            scan = scan.Where(filter);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            BinaryColumn column = batch.Column(0).AsBinary();
            for (int row = 0; row < column.Length; row++)
            {
                values.Add(column.GetString(row)!);
            }
        }

        return (values, FlatLayoutReader.ValuesDecoded);
    }

    /// <summary>A one-column file of sixteen repeated labels, pinned to the dictionary encoding.</summary>
    private static string Write()
    {
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct([Field], [utf8], Nullability.NonNullable);

        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(Rows * 16, 1, out Span<byte> views);
        views.Clear();
        for (int row = 0; row < Rows; row++)
        {
            byte[] value = System.Text.Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"label-{row % Labels:D2}"));

            // Eight bytes fit inside the view, so the column has no heap at all and the fixture
            // stays a single buffer.
            Span<byte> view = views.Slice(row * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
            value.CopyTo(view[4..]);
        }

        int column = arena.AddVarBinView(
            utf8, Rows, Validity.NonNullable, buffer, [VortexBuffer.Empty]);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-pushdown-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, root).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(string path, DType schema, CanonicalArena arena, int root)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, VortexEncodingHint>
            {
                [Field] = VortexEncodingHint.Dictionary,
            },
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(batch, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
    }
}
