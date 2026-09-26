// A filtered scan of one text column, the `--ratio-check` string-predicate group measured on its own:
// the same two files, sixty-five thousand URLs under FSST and sixteen labels under a dictionary,
// scanned for an equality and a prefix.
//
// It exists to judge a change to the path a string predicate takes -- offered to the encoding, or
// the column decoded and then matched -- before and after, on one machine, without the reference
// reader. The ratio against the reference stays `--ratio-check`'s job.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Equality and prefix scans over an FSST and a dictionary text column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Path)]
public class StringPredicateScanBenchmarks
{
    private const int Rows = 65_536;
    private const string Field = "strs";

    [Params("fsst", "dict")]
    public string Column { get; set; } = "fsst";

    [Params("equality", "prefix", "narrow prefix")]
    public string Predicate { get; set; } = "prefix";

    private string _path = "";
    private VortexExpr _filter = Expr.Literal(FilterLiteral.From(true));

    [GlobalSetup]
    public void Setup()
    {
        string directory = Environment.GetEnvironmentVariable("VORTEX_BENCH_DIR") ?? System.IO.Path.GetTempPath();
        _path = System.IO.Path.Combine(directory, $"vorticity-stringscan-{Column}-{Guid.NewGuid():N}.vortex");
        bool fsst = Column == "fsst";
        WriteAsync(
            _path,
            fsst ? EncodingHint.Fsst : EncodingHint.Dictionary,
            fsst
                ? row => string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/conformance/{(row * 7_919) % Rows:D9}")
                : row => string.Create(CultureInfo.InvariantCulture, $"label-{row % 16:D2}"))
            .GetAwaiter().GetResult();

        string literal = (fsst, Predicate) switch
        {
            (true, "equality") => "https://example.invalid/vortex/conformance/000040000",
            (true, "prefix") => "https://example.invalid/vortex/conformance/00000",
            (true, _) => "https://example.invalid/vortex/conformance/0000012",
            (false, "equality") => "label-07",
            (false, "prefix") => "label-1",
            _ => "label-07",
        };
        _filter = Predicate == "equality"
            ? Expr.Eq(Expr.Field(Field), Expr.Literal(FilterLiteral.From(literal)))
            : Expr.StartsWith(Expr.Field(Field), FilterLiteral.From(literal));
    }

    [GlobalCleanup]
    public void Cleanup() => System.IO.File.Delete(_path);

    [Benchmark(Description = "filtered scan")]
    public async Task<long> Scan()
    {
        await using VortexFile file = await VortexFile.OpenAsync(_path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Where(_filter).ExecuteAsync())
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async Task WriteAsync(string path, EncodingHint hint, Func<int, string> value)
    {
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct([Field], [utf8], Nullability.NonNullable);
        VortexWriteOptions options = new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, EncodingHint> { [Field] = hint },
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        const int Batch = 8_192;
        for (int start = 0; start < Rows; start += Batch)
        {
            CanonicalArena arena = new CanonicalArena();
            int column = Strings(arena, utf8, start, Batch, value);
            int root = arena.AddStruct(schema, Batch, Validity.NonNullable, [column]);
            using RecordBatch record = new RecordBatch(arena, root, start);
            await writer.WriteAsync(record, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
    {
        const int ViewSize = 16;
        const int MaxInline = 12;
        byte[][] encoded = new byte[count][];
        int heapBytes = 0;
        for (int i = 0; i < count; i++)
        {
            encoded[i] = System.Text.Encoding.UTF8.GetBytes(value(start + i));
            heapBytes += encoded[i].Length > MaxInline ? encoded[i].Length : 0;
        }

        VortexBuffer views = arena.Allocate(count * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = VortexBuffer.Empty;
        Span<byte> heapSpan = default;
        if (heapBytes > 0)
        {
            heap = arena.Allocate(heapBytes, 1, out heapSpan);
        }

        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = encoded[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= MaxInline)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
            bytes.CopyTo(heapSpan[offset..]);
            offset += bytes.Length;
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, heapBytes == 0 ? [VortexBuffer.Empty] : [heap]);
    }
}
