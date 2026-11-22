// The chunks of a column that gathers to a target of its own are each emitted by a task of their own on the core as they
// are due, the blocks after them going through meanwhile, and land in the file at a point of the rows' own order: the file
// is the same bytes at any degree, the plan a chunk is chosen under is the one its column's chunk before left, and the
// distinct table and the widths the ingest counted serve the emission from what it took of them as the chunk left.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class EmissionTests
{
    /// <summary>Rows of the file: three hundred blocks and a part of one.</summary>
    private const int Rows = (300 * Block) + 300;

    /// <summary>The rows of a block of the file.</summary>
    private const int Block = 1_024;

    /// <summary>
    /// The same file at one lane, four and fourteen, its columns gathering to 64 KiB each: its chunks emitted apart as many
    /// times at each degree, the same of them served by their distinct table, and on several lanes a text's zstd frames
    /// compressed as items of its emission.
    /// </summary>
    [Fact]
    public async Task ChunksEmittedApartWriteTheSameFileAtAnyDegree()
    {
        (DType schema, CanonicalArena arena, int root) = Build();
        Written? first = null;
        foreach (int degree in (int[])[1, 4, 14])
        {
            Written written = await WriteAsync(schema, arena, root, degree);
            first ??= written;
            string seen = $"degree {degree}: {written.File.Length} bytes against {first.File.Length}, {written.Apart} chunks apart against {first.Apart}, "
                + $"{written.FromTable} served by their table against {first.FromTable}, {written.WithoutTable} not, {written.Framed} framed ahead";
            Assert.True(first.File.AsSpan().SequenceEqual(written.File), seen);
            Assert.True(first.Apart == written.Apart, seen);
            Assert.True(first.FromTable == written.FromTable, seen);
            Assert.True(written.WithoutTable == 0, seen);
            Assert.True(degree == 1 ? written.Framed == 0 : written.Framed > 0, seen);
        }

        Assert.True(first!.Apart > 5 * 4, $"{first.Apart} chunks emitted apart");
        Assert.True(first.FromTable > 2, $"{first.FromTable} chunks served by their table");
    }

    /// <summary>What a write left: the file, its chunks emitted apart, those its table served and failed, and those whose frames were compressed ahead.</summary>
    private sealed record Written(byte[] File, int Apart, long FromTable, long WithoutTable, int Framed);

    /// <summary>The file of <paramref name="root"/>'s rows written in calls of five thousand rows at <paramref name="degree"/>.</summary>
    private static async Task<Written> WriteAsync(DType schema, CanonicalArena arena, int root, int degree)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        using MemoryStream stream = new MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            Identity = new Guid("e1e1e1e1-e1e1-41e1-81e1-e1e1e1e1e1e1"),
            RowBlockSize = Block,
            GatheredChunkBytes = 64 << 10,
        };

        int apart;
        long fromTable;
        long withoutTable;
        int framed;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, options, session))
        {
            CanonicalArena windows = new CanonicalArena();
            for (int start = 0; start < Rows; start += 5_000)
            {
                int count = Math.Min(5_000, Rows - start);
                int window = CanonicalSlice.SliceAcross(arena, windows, root, start, count);
                using (RecordBatch batch = new RecordBatch(windows, window, start))
                {
                    await writer.WriteAsync(batch);
                }

                windows.Reset();
            }

            await writer.CompleteAsync();
            apart = writer.ChunksEmittedApart;
            fromTable = writer.ChunksFromTable;
            withoutTable = writer.ChunksWithoutTable;
            framed = writer.ColumnChunksFramedAhead;
        }

        return new Written(stream.ToArray(), apart, fromTable, withoutTable, framed);
    }

    /// <summary>A rising key, a measure, a narrow count, a text of few values and a long text of many, each its own chunks.</summary>
    private static (DType Schema, CanonicalArena Arena, int Root) Build()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        VortexBuffer counts = arena.Allocate(Rows * sizeof(int), sizeof(int), out Span<byte> countBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        Span<int> countValues = MemoryMarshal.Cast<byte, int>(countBytes);
        Random random = new Random(0xE317);
        string[] services = ["api", "auth", "billing", "catalog", "checkout", "search", "stock", "users"];
        byte[][] names = new byte[Rows][];
        byte[][] lines = new byte[Rows][];
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row * 3L;
            measureValues[row] = random.NextDouble() * 1_000;
            countValues[row] = random.Next(0, 900);
            names[row] = Encoding.UTF8.GetBytes($"service-{services[random.Next(services.Length)]}-{random.Next(0, 6)}");
            lines[row] = Encoding.UTF8.GetBytes(
                $"{{\"user\":\"u{random.Next(0, 50_000)}\",\"path\":\"/api/v1/items/{random.Next(0, 9_999)}\",\"status\":{(random.Next(20) == 0 ? 500 : 200)}}}");
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
        int count = arena.AddPrimitive(i32, Rows, Validity.NonNullable, PType.I32, counts);
        int name = Text(arena, utf8, names);
        int line = Text(arena, utf8, lines);
        DType schema = types.Struct(["key", "measure", "count", "name", "line"], [i64, f64, i32, utf8, utf8], Nullability.NonNullable);
        return (schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [key, measure, count, name, line]));
    }

    /// <summary>A text column of <paramref name="values"/>, its long values in one data buffer.</summary>
    private static int Text(CanonicalArena arena, DType utf8, byte[][] values)
    {
        int heap = 0;
        foreach (byte[] value in values)
        {
            heap += value.Length > 12 ? value.Length : 0;
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        int offset = 0;
        for (int row = 0; row < values.Length; row++)
        {
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            byte[] value = values[row];
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

        return arena.AddVarBinView(utf8, values.Length, Validity.NonNullable, views, [data]);
    }
}
