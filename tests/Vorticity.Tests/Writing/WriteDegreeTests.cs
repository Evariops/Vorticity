using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A file written on several threads is the bytes of one written on one.</summary>
public sealed class WriteDegreeTests
{
    /// <summary>Three chunks the writer sizes and a tail, so that a dictionary lives past the first.</summary>
    private const int Rows = (3 * 65_536) + 5_000;

    /// <summary>Batches that end inside a block, so that every batch but the first starts inside one.</summary>
    private const int BatchRows = 10_000;

    private static readonly Guid Pinned = new Guid("3c3c3c3c-3c3c-4c3c-8c3c-3c3c3c3c3c3c");

    private static readonly string[] Cities =
    [
        "Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes",
        "Montpellier", "Strasbourg", "Bordeaux", "Lille", "Rennes", "Saint-Etienne",
    ];

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task AFileWrittenOnSeveralThreadsIsTheBytesOfOneWrittenOnOne(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        Table table = Build();
        string onePath = Temp();
        string manyPath = Temp();
        try
        {
            (int oneAcross, long oneFromTable) = await WriteAsync(table, onePath, 1, ct);
            (int manyAcross, long manyFromTable) = await WriteAsync(table, manyPath, degree, ct);
            Assert.Equal(0, oneAcross);
            Assert.True(manyAcross > 0, "the batches must be ingested across threads");
            Assert.True(manyFromTable > 0, "a distinct table must serve a chunk");
            Assert.Equal(oneFromTable, manyFromTable);
            Assert.Equal(await System.IO.File.ReadAllBytesAsync(onePath, ct), await System.IO.File.ReadAllBytesAsync(manyPath, ct));
        }
        finally
        {
            System.IO.File.Delete(onePath);
            System.IO.File.Delete(manyPath);
        }
    }

    /// <summary>
    /// Writes the table in batches and returns how many were ingested across threads, and the
    /// column chunks a distinct table served.
    /// </summary>
    private static async Task<(int Across, long FromTable)> WriteAsync(Table table, string path, int degree, CancellationToken ct)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, table.Schema, new VortexWriteOptions { Identity = Pinned, DegreeOfParallelism = degree });
        for (int start = 0; start < Rows; start += BatchRows)
        {
            int count = Math.Min(BatchRows, Rows - start);
            int slice = CanonicalSlice.SliceAcross(table.Arena, table.Arena, table.Root, start, count);
            using RecordBatch batch = new RecordBatch(table.Arena, slice, start);
            await writer.WriteAsync(batch, ct);
        }

        await writer.CompleteAsync(ct);
        return (writer.BatchesIngestedAcross, writer.ChunksFromTable);
    }

    /// <summary>
    /// Integers, a nullable text column a dictionary wins on, a text column of distinct values,
    /// nullable floats, a struct of an integer and a short text, a list of integers and nullable
    /// booleans: bounds, tables, widths and nested columns, every accumulator of the ingest.
    /// </summary>
    private static Table Build()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType i32 = types.Primitive(PType.I32, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.Nullable);
        DType text = types.Utf8(Nullability.Nullable);
        DType plainText = types.Utf8(Nullability.NonNullable);
        DType flag = types.Bool(Nullability.Nullable);
        DType bits = types.Bool(Nullability.NonNullable);
        DType point = types.Struct(["x", "y"], [i32, plainText], Nullability.NonNullable);
        DType tags = types.List(i64, Nullability.NonNullable);
        DType schema = types.Struct(
            ["id", "city", "name", "score", "point", "tags", "flag"],
            [i64, text, plainText, f64, point, tags, flag],
            Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();

        int id = Longs(arena, i64, row => (row * 3L) + (row % 5));
        int city = Strings(arena, bits, text, row => row % 11 == 4 ? null : Cities[(row * 7) % Cities.Length]);
        int name = Strings(arena, bits, plainText, row => "name-" + ((row * 7_919L) % 100_003).ToString(System.Globalization.CultureInfo.InvariantCulture));
        int score = Doubles(arena, bits, f64, row => row % 13 == 6 ? null : (row % 1_000) / 8.0);
        int x = Ints(arena, i32, row => row % 97);
        int y = Strings(arena, bits, plainText, row => Cities[row % 3]);
        int pointNode = arena.AddStruct(point, Rows, Validity.NonNullable, [x, y]);
        int tagsNode = Lists(arena, tags, i64);
        int flagNode = Flags(arena, bits, flag);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [id, city, name, score, pointNode, tagsNode, flagNode]);
        return new Table(schema, arena, root);
    }

    private static int Longs(CanonicalArena arena, DType dtype, Func<int, long> value)
    {
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = value(row);
        }

        return arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Ints(CanonicalArena arena, DType dtype, Func<int, int> value)
    {
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            values[row] = value(row);
        }

        return arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I32, buffer);
    }

    private static int Doubles(CanonicalArena arena, DType bits, DType dtype, Func<int, double?> value)
    {
        VortexBuffer buffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
        VortexBuffer validity = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> valid);
        bytes.Clear();
        valid.Clear();
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int row = 0; row < Rows; row++)
        {
            if (value(row) is { } present)
            {
                values[row] = present;
                valid[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int mask = arena.AddBool(bits, Rows, Validity.NonNullable, validity, 0);
        return arena.AddPrimitive(dtype, Rows, Validity.Bitmap(mask), PType.F64, buffer);
    }

    private static int Strings(CanonicalArena arena, DType bits, DType dtype, Func<int, string?> value)
    {
        byte[]?[] utf8 = new byte[Rows][];
        int heap = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (value(row) is { } present)
            {
                utf8[row] = Encoding.UTF8.GetBytes(present);
                heap += utf8[row]!.Length > 12 ? utf8[row]!.Length : 0;
            }
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> view);
        VortexBuffer validity = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> valid);
        view.Clear();
        valid.Clear();
        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            if (utf8[row] is not { } bytes)
            {
                continue;
            }

            valid[row >> 3] |= (byte)(1 << (row & 7));
            Span<byte> one = view.Slice(row * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(one[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(one[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(one[12..], offset);
            bytes.CopyTo(dataBytes[offset..]);
            offset += bytes.Length;
        }

        Validity validityOf = dtype.IsNullable
            ? Validity.Bitmap(arena.AddBool(bits, Rows, Validity.NonNullable, validity, 0))
            : Validity.NonNullable;
        return arena.AddVarBinView(dtype, Rows, validityOf, views, [data]);
    }

    /// <summary>Row <c>r</c> holds <c>r % 4</c> integers, the elements in row order.</summary>
    private static int Lists(CanonicalArena arena, DType dtype, DType element)
    {
        VortexBuffer offsets = arena.Allocate(Rows * sizeof(int), sizeof(int), out Span<byte> offsetBytes);
        VortexBuffer sizes = arena.Allocate(Rows * sizeof(int), sizeof(int), out Span<byte> sizeBytes);
        Span<int> offsetValues = MemoryMarshal.Cast<byte, int>(offsetBytes);
        Span<int> sizeValues = MemoryMarshal.Cast<byte, int>(sizeBytes);
        int elements = 0;
        for (int row = 0; row < Rows; row++)
        {
            offsetValues[row] = elements;
            sizeValues[row] = row % 4;
            elements += row % 4;
        }

        VortexBuffer values = arena.Allocate(elements * sizeof(long), sizeof(long), out Span<byte> valueBytes);
        Span<long> longs = MemoryMarshal.Cast<byte, long>(valueBytes);
        for (int e = 0; e < elements; e++)
        {
            longs[e] = 1_000 + (e % 257);
        }

        int child = arena.AddPrimitive(element, elements, Validity.NonNullable, PType.I64, values);
        return arena.AddListView(dtype, Rows, Validity.NonNullable, child, offsets, PType.I32, sizes, PType.I32);
    }

    private static int Flags(CanonicalArena arena, DType bits, DType dtype)
    {
        VortexBuffer values = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> set);
        VortexBuffer validity = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> valid);
        set.Clear();
        valid.Clear();
        for (int row = 0; row < Rows; row++)
        {
            if (row % 17 == 9)
            {
                continue;
            }

            valid[row >> 3] |= (byte)(1 << (row & 7));
            if (row % 3 == 0)
            {
                set[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int mask = arena.AddBool(bits, Rows, Validity.NonNullable, validity, 0);
        return arena.AddBool(dtype, Rows, Validity.Bitmap(mask), values, 0);
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-degree-{Guid.NewGuid():N}.vortex");

    private sealed record Table(DType Schema, CanonicalArena Arena, int Root);
}
