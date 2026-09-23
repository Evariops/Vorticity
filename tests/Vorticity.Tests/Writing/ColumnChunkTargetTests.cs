// A column given a chunk target of its own: its chunks span whole chunks of the file, the other
// columns keep theirs, and every read -- whole, projected, filtered, taken -- meets the same rows.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What <see cref="VortexWriteOptions.ColumnChunkTargetBytes"/> does to a file and to its reads.</summary>
public sealed class ColumnChunkTargetTests
{
    private const int Rows = 60_000;

    /// <summary>Chunks of the file a few blocks long, so that the names gather many of them.</summary>
    private static readonly VortexWriteOptions Small = new VortexWriteOptions
    {
        BlockRows = 1_024,
        ChunkTargetBytes = 16 << 10,
        Identity = new Guid("29292929-2929-4929-8929-292929292929"),
    };

    private static readonly VortexWriteOptions Apart = Small with
    {
        ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add("name", 256 << 10),
    };

    [Fact]
    public async Task AColumnWithATargetOfItsOwnSpansWholeChunksOfTheFile()
    {
        Decoders.EnsureRegistered();
        string path = Temp();
        try
        {
            WriteReport report = await WriteAsync(path, Apart);

            ImmutableArray<int> name = report.ChunkRowsOf(1);
            Assert.True(report.ChunkRows.Length > 4, $"the file has {report.ChunkRows.Length} chunks");
            Assert.True(name.Length < report.ChunkRows.Length / 2, $"'name' has {name.Length} chunks");
            Assert.Equal(name.Length, report.Columns[1].Encodings.Length);
            Assert.Equal(report.ChunkRows, report.ChunkRowsOf(0));
            Assert.Equal(report.ChunkRows.Length, report.Columns[0].Encodings.Length);

            // Every chunk of the column ends where one of the file's does.
            HashSet<long> ends = [];
            long end = 0;
            foreach (int rows in report.ChunkRows)
            {
                end += rows;
                ends.Add(end);
            }

            long at = 0;
            foreach (int rows in name)
            {
                at += rows;
                Assert.Contains(at, ends);
            }

            Assert.Equal(Rows, at);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task EveryReadMeetsTheRowsTheFileChunkedAlikeHolds()
    {
        Decoders.EnsureRegistered();
        string alike = Temp();
        string apart = Temp();
        try
        {
            await WriteAsync(alike, Small);
            await WriteAsync(apart, Apart);

            CancellationToken ct = TestContext.Current.CancellationToken;
            await using VortexFile expected = await VortexFile.OpenAsync(alike, ct);
            await using VortexFile actual = await VortexFile.OpenAsync(apart, ct);

            Assert.Equal(await ReadAsync(expected.ScanBuilder()), await ReadAsync(actual.ScanBuilder()));
            foreach (string column in (string[])["key", "name", "measure"])
            {
                Assert.Equal(await ReadAsync(expected.ScanBuilder().Project(column)), await ReadAsync(actual.ScanBuilder().Project(column)));
            }

            // A range of the key, which crosses the chunks of both kinds; the name alone, which the
            // key's zones cannot prune and the name's own read whole.
            VortexExpr range = Expr.And(
                Expr.Ge(Expr.Field("key"), Expr.Literal(FilterLiteral.From(20_011L))),
                Expr.Lt(Expr.Field("key"), Expr.Literal(FilterLiteral.From(41_017L))));
            Assert.Equal(await ReadAsync(expected.ScanBuilder().Where(range)), await ReadAsync(actual.ScanBuilder().Where(range)));
            VortexExpr named = Expr.Eq(Expr.Field("name"), Expr.Literal(FilterLiteral.From("name-00417")));
            Assert.Equal(await ReadAsync(expected.ScanBuilder().Where(named)), await ReadAsync(actual.ScanBuilder().Where(named)));

            long[] taken = [0, 1, 1_023, 1_024, 7_777, 30_000, 45_678, Rows - 1];
            Assert.Equal(await ReadAsync(expected.ScanBuilder().Take(taken)), await ReadAsync(actual.ScanBuilder().Take(taken)));
            Assert.Equal(await ReadAsync(expected.ScanBuilder().Rows(new RowRange(12_345, 23_456))), await ReadAsync(actual.ScanBuilder().Rows(new RowRange(12_345, 23_456))));
        }
        finally
        {
            System.IO.File.Delete(alike);
            System.IO.File.Delete(apart);
        }
    }

    [Fact]
    public async Task AFlushWritesWhatTheColumnGathers()
    {
        Decoders.EnsureRegistered();
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream stream = new MemoryStream();
        (DType schema, CanonicalArena arena, int root) = Build();
        WriteReport report;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, Apart))
        {
            using (RecordBatch first = new RecordBatch(arena, CanonicalSlice.SliceAcross(arena, arena, root, 0, 20_480), 0))
            {
                await writer.WriteAsync(first, ct);
            }

            await writer.FlushAsync(ct);
            using (RecordBatch rest = new RecordBatch(arena, CanonicalSlice.SliceAcross(arena, arena, root, 20_480, Rows - 20_480), 20_480))
            {
                await writer.WriteAsync(rest, ct);
            }

            report = await writer.CompleteAsync(ct);
        }

        // The flush sealed the whole blocks it had, and what the name gathered went with them.
        long at = 0;
        bool sealedThere = false;
        foreach (int rows in report.ChunkRowsOf(1))
        {
            at += rows;
            sealedThere |= at == 20_480;
        }

        Assert.True(sealedThere, "the name's chunks do not end at the flush");
    }

    [Theory]
    [InlineData("nothing", 1 << 20)]
    [InlineData("name.inner", 1 << 20)]
    public void ATargetNamingNoTopLevelColumnIsRefused(string column, int bytes)
    {
        (DType schema, _, _) = Build();
        VortexWriteOptions options = Small with { ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add(column, bytes) };
        Assert.Throws<ArgumentException>(() => VortexFileWriter.Validate(schema, options));
    }

    [Fact]
    public void ATargetThatIsNotPositiveIsRefused()
    {
        (DType schema, _, _) = Build();
        VortexWriteOptions options = Small with { ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add("name", 0) };
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexFileWriter.Validate(schema, options));
    }

    [Fact]
    public async Task AFileChunkedApartIsNotAppendedToAndNoAppendChunksApart()
    {
        Decoders.EnsureRegistered();
        string alike = Temp();
        string apart = Temp();
        try
        {
            await WriteAsync(alike, Small);
            await WriteAsync(apart, Apart);

            CancellationToken ct = TestContext.Current.CancellationToken;
            await Assert.ThrowsAsync<VortexUnsupportedException>(async () => await VortexFileWriter.AppendAsync(apart, Small, ct));
            await Assert.ThrowsAsync<VortexUnsupportedException>(async () => await VortexFileWriter.AppendAsync(alike, Apart, ct));
        }
        finally
        {
            System.IO.File.Delete(alike);
            System.IO.File.Delete(apart);
        }
    }

    private static async Task<WriteReport> WriteAsync(string path, VortexWriteOptions options)
    {
        (DType schema, CanonicalArena arena, int root) = Build();
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        const int step = 4_000;
        for (int start = 0; start < Rows; start += step)
        {
            int count = Math.Min(step, Rows - start);
            using RecordBatch batch = new RecordBatch(arena, CanonicalSlice.SliceAcross(arena, arena, root, start, count), start);
            await writer.WriteAsync(batch);
        }

        return await writer.CompleteAsync();
    }

    private static async Task<List<string>> ReadAsync(ScanBuilder scan)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ListShapes.Describe(batch, rows);
        }

        return rows;
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-column-chunks-{Guid.NewGuid():N}.vortex");

    /// <summary>A key, a name among a thousand with nulls, and a measure.</summary>
    private static (DType Schema, CanonicalArena Arena, int Root) Build()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.Nullable);

        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row;
            measureValues[row] = (row % 211) * 0.5;
        }

        byte[][] names = new byte[Rows][];
        int heap = 0;
        for (int row = 0; row < Rows; row++)
        {
            names[row] = Encoding.UTF8.GetBytes($"name-{(row * 7_919) % 1_000:D5}");
            heap += names[row].Length > 12 ? names[row].Length : 0;
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            byte[] value = names[row];
            MemoryMarshal.Write(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.AsSpan(0, 4).CopyTo(view[4..]);
            MemoryMarshal.Write(view[12..], offset);
            value.CopyTo(dataBytes[offset..]);
            offset += value.Length;
        }

        VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
        for (int row = 0; row < Rows; row++)
        {
            if (row % 13 != 0)
            {
                bitBytes[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int valid = arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int name = arena.AddVarBinView(utf8, Rows, Validity.Bitmap(valid), views, [data]);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
        DType schema = types.Struct(["key", "name", "measure"], [i64, utf8, f64], Nullability.NonNullable);
        return (schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [key, name, measure]));
    }
}
