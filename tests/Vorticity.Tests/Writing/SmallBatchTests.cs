// Batches of a few rows, as a merge of keys that interleave row by row hands them over. They wait
// with the next ones and go through the writer together: held one by one, a chunk of one-row
// batches would be a node a row in transit, and the file they write must not tell the difference.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What the writer does with batches of a few rows.</summary>
public sealed class SmallBatchTests
{
    /// <summary>Whole batches of every size below, so that no last batch waits where the others do not.</summary>
    private const int Rows = 4_032;

    /// <summary>The same identity for every file, so that two writes of the same rows compare byte for byte.</summary>
    private static readonly VortexWriteOptions Options = new VortexWriteOptions
    {
        Identity = new Guid("29292929-2929-4929-8929-292929292929"),
    };

    public static TheoryData<string> Shapes => [.. ListShapes.Contiguous];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task OneRowBatchesOfListsWaitAndReadBackAsOneBatch(string shape)
    {
        // A one-row batch of a list shares its chunk's elements whole: what waits is narrowed to its
        // own rows before it is copied. The chunks follow the bytes a batch holds, which a window
        // sharing its elements overstates, so the files are compared by their rows.
        Decoders.EnsureRegistered();
        ListShape rows = ListShapes.Build(shape, 1_000);

        (byte[] whole, _) = await WriteAsync(rows.Schema, rows.Arena, rows.Root, rows.Rows, rows.Rows);
        (byte[] single, int held) = await WriteAsync(rows.Schema, rows.Arena, rows.Root, rows.Rows, 1);

        Assert.Equal(await ReadAsync(whole), await ReadAsync(single));
        Assert.True(held <= rows.Rows / 256, $"{held} nodes in transit for {rows.Rows} one-row batches");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public async Task FixedWidthBatchesWaitBelowSixtyFourRows(int batchRows, bool wait)
    {
        Decoders.EnsureRegistered();
        (DType schema, CanonicalArena arena, int root) = Build(text: false);

        (byte[] whole, _) = await WriteAsync(schema, arena, root, Rows, Rows);
        (byte[] small, int held) = await WriteAsync(schema, arena, root, Rows, batchRows);

        Assert.Equal(whole, small);
        AssertHeld(held, batchRows, wait);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task BatchesOfTextWaitBelowSixRows(int batchRows, bool wait)
    {
        // A copy of values of their own length walks every row, where a fixed width moves blocks:
        // waiting pays for fewer rows.
        Decoders.EnsureRegistered();
        (DType schema, CanonicalArena arena, int root) = Build(text: true);

        (byte[] whole, _) = await WriteAsync(schema, arena, root, Rows, Rows);
        (byte[] small, int held) = await WriteAsync(schema, arena, root, Rows, batchRows);

        Assert.Equal(await ReadAsync(whole), await ReadAsync(small));
        AssertHeld(held, batchRows, wait);
    }

    [Fact]
    public async Task UnderABlockTargetTheRowsWaitingGoThroughAtTheBlockTheyComplete()
    {
        // A writer given a block's rows and bytes emits the blocks in transit as soon as a batch
        // completes one: rows still waiting then would come later in a bigger chunk, and the sink of
        // a compaction that rolls its outputs by size would not move while an object grows.
        Decoders.EnsureRegistered();
        (DType schema, CanonicalArena arena, int root) = Build(text: false);
        VortexWriteOptions blocks = Options with { RowBlockSize = 64, DataBlockTargetBytes = 512 };

        (byte[] whole, _) = await WriteAsync(schema, arena, root, Rows, 64, blocks);
        (byte[] single, _) = await WriteAsync(schema, arena, root, Rows, 1, blocks);

        Assert.Equal(whole, single);
    }

    private static void AssertHeld(int held, int batchRows, bool wait)
    {
        int batches = Rows / batchRows;
        if (wait)
        {
            Assert.True(held <= Rows / 256, $"{held} nodes in transit for {batches} batches of {batchRows} rows");
        }
        else
        {
            Assert.Equal(batches, held);
        }
    }

    /// <summary>
    /// Writes <paramref name="rows"/> rows of <paramref name="root"/> in batches of
    /// <paramref name="batchRows"/>, returning the file and how many nodes waited in transit once
    /// every batch was in.
    /// </summary>
    private static async Task<(byte[] File, int Held)> WriteAsync(
        DType schema, CanonicalArena arena, int root, int rows, int batchRows, VortexWriteOptions? options = null)
    {
        using MemoryStream stream = new MemoryStream();
        int held;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options ?? Options))
        {
            CanonicalArena windows = new CanonicalArena();
            for (int start = 0; start < rows; start += batchRows)
            {
                int count = Math.Min(batchRows, rows - start);
                int window = CanonicalSlice.SliceAcross(arena, windows, root, start, count);
                using (RecordBatch batch = new RecordBatch(windows, window, start))
                {
                    await writer.WriteAsync(batch);
                }

                windows.Reset();
            }

            held = writer.HeldNodes;
            await writer.CompleteAsync();
        }

        return (stream.ToArray(), held);
    }

    private static async Task<List<string>> ReadAsync(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-small-batches-{Guid.NewGuid():N}.vortex");
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            List<string> rows = [];
            await using VortexFile file = await VortexFile.OpenAsync(path);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
            {
                ListShapes.Describe(batch, rows);
            }

            return rows;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A key and a measure, or a key and text with nulls, short values and long ones.</summary>
    private static (DType Schema, CanonicalArena Arena, int Root) Build(bool text)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row * 3L;
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        if (!text)
        {
            DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
            VortexBuffer measures = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < Rows; row++)
            {
                measureValues[row] = (row % 97) * 0.25;
            }

            int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
            DType pair = types.Struct(["key", "measure"], [i64, f64], Nullability.NonNullable);
            return (pair, arena, arena.AddStruct(pair, Rows, Validity.NonNullable, [key, measure]));
        }

        DType utf8 = types.Utf8(Nullability.Nullable);
        byte[][] values = new byte[Rows][];
        int heap = 0;
        for (int row = 0; row < Rows; row++)
        {
            values[row] = Encoding.UTF8.GetBytes(row % 3 == 0 ? $"a value long enough to spill, number {row}" : $"v{row % 50}");
            heap += values[row].Length > 12 ? values[row].Length : 0;
        }

        VortexBuffer data = arena.Allocate(heap, 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        int offset = 0;
        for (int row = 0; row < Rows; row++)
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

        VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> bitBytes);
        for (int row = 0; row < Rows; row++)
        {
            if (row % 7 != 0)
            {
                bitBytes[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int valid = arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
        int textNode = arena.AddVarBinView(utf8, Rows, Validity.Bitmap(valid), views, [data]);
        DType schema = types.Struct(["key", "text"], [i64, utf8], Nullability.NonNullable);
        return (schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [key, textNode]));
    }
}
