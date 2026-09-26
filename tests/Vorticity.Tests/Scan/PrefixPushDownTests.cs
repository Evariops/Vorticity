// A prefix match over a text column with nulls, offered to its encoding: a dictionary answers it,
// matching its distinct values and spreading the answers over its codes; FSST declines, and the
// column is decoded and matched as it always was.
//
// Correctness is asserted against the same scan without a predicate, filtered in memory with an
// ordinal StartsWith, for prefixes that end inside a multi-byte character, are longer than every
// value, are empty, or match nothing: the answer has to agree row for row whichever path gave it,
// and a null row is never selected. The quantity that shows the dictionary answered is the scan's
// `ScanMetrics.ValuesDecoded`, as in FilterPushDownTests.
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
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

[Collection(nameof(AllocationCollection))]
public sealed class PrefixPushDownTests
{
    private const string Field = "strs";
    private const int Rows = 24_576;

    public static TheoryData<string> Encodings => ["dictionary", "fsst"];

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task APrefixIsAnsweredRowForRowAsTheDecodedColumnAnswersIt(string encoding)
    {
        (EncodingHint hint, Func<int, string?> value, string[] prefixes, string? selective) = Fixture(encoding);
        string path = Write(hint, value);
        try
        {
            (List<string?> all, _) = await ReadAsync(path, null);
            foreach (string prefix in prefixes)
            {
                (List<string?> pushed, long decoded) = await ReadAsync(
                    path, Expr.StartsWith(Expr.Field(Field), FilterLiteral.From(prefix)));
                List<string?> expected = all.FindAll(v => v is not null && v.StartsWith(prefix, StringComparison.Ordinal));
                Assert.True(expected.Count == pushed.Count, $"{encoding}, prefix '{prefix}': {pushed.Count} rows, expected {expected.Count}.");
                Assert.Equal(expected, pushed);

                if (prefix == selective)
                {
                    Assert.NotEmpty(expected);
                    Assert.True(
                        decoded < Rows / 2,
                        $"a prefix scan over a {encoding} column materialized {decoded} values of {Rows}, " +
                        "so the prefix was answered by decoding the column.");
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static (EncodingHint Hint, Func<int, string?> Value, string[] Prefixes, string? Selective) Fixture(
        string encoding) => encoding switch
        {
            "dictionary" => (
                EncodingHint.Dictionary,
                row => row % 7 == 3 ? null : string.Create(CultureInfo.InvariantCulture, $"labél-{row % 16:D2}"),
                ["", "l", "labél-0", "labél-07", "labél-07x", "labé", "labél-1", "zzz", "labe"],
                "labél-07"),

            // Scrambled, for FilterPushDownTests' reason: an ascending column is answered by a seek.
            "fsst" => (
                EncodingHint.Fsst,
                row => row % 11 == 5
                    ? null
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"https://example.invalid/vortex/conformancé/{(row * 7_919) % Rows:D9}"),
                [
                    "", "h", "https://", "https://example.invalid/vortex/conformancé/00001", "https://example.invalid/vortex/conformancé/0000123",
                    "https://example.invalid/vortex/conformancé/000012345",
                    "https://example.invalid/vortex/conformancé/000012345/longer",
                    "https://example.invalid/vortex/conformancÃ", "https://example.invalid/vortex/conformance", "ftp://",
                ],
                null),

            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "no such fixture"),
        };

    private static async Task<(List<string?> Values, long Decoded)> ReadAsync(string path, VortexExpr? filter)
    {
        ScanMetrics metrics = new ScanMetrics();
        List<string?> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        ScanBuilder scan = file.ScanBuilder().WithMetrics(metrics);
        if (filter is not null)
        {
            scan = scan.Where(filter);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            BinaryColumn column = batch.Column(0).AsBinary();
            for (int row = 0; row < column.Length; row++)
            {
                values.Add(column.GetString(row));
            }
        }

        return (values, metrics.ValuesDecoded);
    }

    /// <summary>A one-column nullable file pinned to <paramref name="hint"/>.</summary>
    private static string Write(EncodingHint hint, Func<int, string?> value)
    {
        const int ViewSize = 16;
        const int MaxInline = 12;

        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.Nullable);
        DType schema = types.Struct([Field], [utf8], Nullability.NonNullable);

        byte[]?[] encoded = new byte[Rows][];
        int heapBytes = 0;
        for (int row = 0; row < Rows; row++)
        {
            string? text = value(row);
            encoded[row] = text is null ? null : System.Text.Encoding.UTF8.GetBytes(text);
            if (encoded[row] is { Length: > MaxInline } bytes)
            {
                heapBytes += bytes.Length;
            }
        }

        CanonicalArena arena = new CanonicalArena();
        VortexBuffer views = arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> valid);
        VortexBuffer heap = VortexBuffer.Empty;
        Span<byte> heapBuffer = default;
        if (heapBytes > 0)
        {
            heap = arena.Allocate(heapBytes, 1, out heapBuffer);
        }

        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (encoded[row] is not { } bytes)
            {
                continue;
            }

            valid[row >> 3] |= (byte)(1 << (row & 7));
            Span<byte> view = viewBytes.Slice(row * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= MaxInline)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapBuffer[offset..]);
            offset += bytes.Length;
        }

        Validity validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        int column = arena.AddVarBinView(utf8, Rows, validity, views, heapBytes == 0 ? [VortexBuffer.Empty] : [heap]);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-prefix-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, root, hint).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(string path, DType schema, CanonicalArena arena, int root, EncodingHint hint)
    {
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
