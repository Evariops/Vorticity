// An equality a string encoding answers from what it stores, and the proof that the column is
// never built to answer it.
//
// The quantity is `FlatLayoutReader.ValuesDecoded`, the same counter the block-pruning tests read:
// it counts what a scan materialized, so a push that works shows as a count near the number of
// rows the predicate keeps rather than near the number of rows the file holds. Correctness is
// asserted against the same scan without a predicate, filtered in memory, so the two paths have to
// agree row for row and not merely in their totals.
//
// One case per encoding that claims to answer, because the claim is per encoding: a channel proven
// on a dictionary says nothing about a compressed-string column, and the two reach the decoder by
// different routes through the writer's cascade.
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
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

[Collection(nameof(AllocationCollection))]
public sealed class FilterPushDownTests
{
    private const string Field = "strs";
    private const int Rows = 24_576;
    private const int Labels = 16;

    /// <summary>The encodings that claim to answer an equality, and a column each.</summary>
    public static TheoryData<string> Encodings => ["dictionary", "fsst"];

    /// <summary>The matching rows come back, and the column is never materialized to find them.</summary>
    /// <param name="encoding">Which fixture to write.</param>
    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task AnEncodingAnswersAnEqualityWithoutBuildingTheColumn(string encoding)
    {
        (EncodingHint hint, Func<int, string> value, string needle) = Fixture(encoding);
        string path = Write(hint, value);
        try
        {
            (List<string> pushed, long decoded) = await ReadAsync(
                path, Expr.Eq(Expr.Field(Field), Expr.Literal(FilterLiteral.From(needle))));
            (List<string> all, _) = await ReadAsync(path, null);

            List<string> expected = all.FindAll(v => v == needle);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, pushed);

            // Without the push the scan materializes every row of the column to compare it; with
            // it, only the rows that survived are built.
            Assert.True(
                decoded < Rows / 2,
                $"a filtered scan over a {encoding} column materialized {decoded} values of " +
                $"{Rows}, so the comparison was answered by decoding the column.");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The hint, the value of a row, and a needle the column holds.</summary>
    private static (EncodingHint Hint, Func<int, string> Value, string Needle) Fixture(
        string encoding) => encoding switch
        {
            // Short and repeated: sixteen entries over the whole column, which is what a dictionary
            // is for, and one row in sixteen matches.
            "dictionary" => (
                EncodingHint.Dictionary,
                row => string.Create(CultureInfo.InvariantCulture, $"label-{row % Labels:D2}"),
                "label-07"),

            // Long, distinct and highly prefixed: no dictionary would help and the symbol table has
            // everything to learn. Exactly one row matches.
            //
            // SCRAMBLED, and that is load-bearing. A column whose values ascend is a sorted column,
            // and a scan answers an equality over one by seeking it -- no index needed, the rows
            // proved before anything is read, and no predicate left to push. Which is the right
            // path when it exists, and not the one this measures.
            "fsst" => (
                EncodingHint.Fsst,
                row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"https://example.invalid/vortex/conformance/{(row * 7_919) % Rows:D9}"),
                "https://example.invalid/vortex/conformance/000012345"),

            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "no such fixture"),
        };

    private static async Task<(List<string> Values, long Decoded)> ReadAsync(
        string path, VortexExpr? filter)
    {
        FlatLayoutReader.ValuesDecoded = 0;
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        ScanBuilder scan = file.ScanBuilder();
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

    /// <summary>A one-column file pinned to <paramref name="hint"/>.</summary>
    private static string Write(EncodingHint hint, Func<int, string> value)
    {
        const int ViewSize = 16;
        const int MaxInline = 12;

        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct([Field], [utf8], Nullability.NonNullable);

        byte[][] encoded = new byte[Rows][];
        int heapBytes = 0;
        for (int row = 0; row < Rows; row++)
        {
            encoded[row] = System.Text.Encoding.UTF8.GetBytes(value(row));
            if (encoded[row].Length > MaxInline)
            {
                heapBytes += encoded[row].Length;
            }
        }

        CanonicalArena arena = new CanonicalArena();
        VortexBuffer views = arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        viewBytes.Clear();
        VortexBuffer heap = VortexBuffer.Empty;
        Span<byte> heapBuffer = default;
        if (heapBytes > 0)
        {
            heap = arena.Allocate(heapBytes, 1, out heapBuffer);
        }

        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            byte[] bytes = encoded[row];
            Span<byte> view = viewBytes.Slice(row * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= MaxInline)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapBuffer[offset..]);
            offset += bytes.Length;
        }

        int column = arena.AddVarBinView(
            utf8, Rows, Validity.NonNullable, views,
            heapBytes == 0 ? [VortexBuffer.Empty] : [heap]);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-pushdown-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, root, hint).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(
        string path, DType schema, CanonicalArena arena, int root, EncodingHint hint)
    {
        // NO INDEX, and that is what this measures. An exact index answers an equality before a
        // scan reads anything, and the scan then takes the rows it proved -- a different path, and
        // the right one when it exists. The push is for the columns no exact index covers, so a
        // fixture that carried one would be testing the index.
        VortexWriteOptions options = new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, EncodingHint> { [Field] = hint },
            WritePolicy = Vorticity.Indexes.WritePolicy.None,
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(batch, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
    }
}
