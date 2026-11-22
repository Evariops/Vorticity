// A writer of a table of several columns takes its caller's rows a block at a time, as a merge hands its writer its
// blocks, and sends them through its line, several at once, when it has lanes: the file is the same bytes at any degree
// and whatever the calls it was written in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class BlockWriteTests
{
    /// <summary>Rows of the file: a hundred blocks and a part of one.</summary>
    private const int Rows = 100 * Block + 300;

    /// <summary>The rows of a block of the file.</summary>
    private const int Block = 1_024;

    /// <summary>The same identity for every file, so that two writes of the same rows compare byte for byte.</summary>
    private static readonly VortexWriteOptions Options = new VortexWriteOptions
    {
        Identity = new Guid("31313131-3131-4131-8131-313131313131"),
        RowBlockSize = Block,
    };

    /// <summary>
    /// The rows written at once, in calls of five thousand and in calls of a hundred, each on one lane, four and fourteen:
    /// the file of each is the same bytes at every degree, and on four lanes and fourteen the blocks went through the
    /// writer's line. The calls of a block or more write the bytes of one call, their blocks cut where they lie; those of a
    /// hundred share a text that repeats the row before within their own rows alone, as they always did.
    /// </summary>
    [Fact]
    public async Task AFileIsTheSameBytesAtAnyDegree()
    {
        (DType schema, CanonicalArena arena, int root) = Build();
        byte[]? once = null;
        foreach (int calls in (int[])[Rows, 5_000, 100])
        {
            byte[]? first = null;
            foreach (int degree in (int[])[1, 4, 14])
            {
                (byte[] file, int onLine) = await WriteAsync(schema, arena, root, calls, degree);
                first ??= file;
                Assert.True(first.AsSpan().SequenceEqual(file), $"calls of {calls} rows, degree {degree}: {file.Length} bytes against {first.Length}");
                Assert.True(degree == 1 ? onLine == 0 : onLine > 0, $"calls of {calls} rows, degree {degree}: {onLine} blocks through the line");
            }

            byte[] written = first!;
            once ??= written;
            if (calls >= Block)
            {
                Assert.True(once.AsSpan().SequenceEqual(written), $"calls of {calls} rows: {written.Length} bytes against {once.Length} at once");
            }
        }
    }

    /// <summary>
    /// The rows written on four lanes by a writer whose work admits no block in flight past the one that must go (its write
    /// advance refused): each block goes through once the one before landed, and the file is the bytes of one lane.
    /// </summary>
    [Fact]
    public async Task AWriterAdmittedNoBlockAheadWritesTheBytesOfOneLane()
    {
        (DType schema, CanonicalArena arena, int root) = Build();
        (byte[] alone, _) = await WriteAsync(schema, arena, root, Rows, degree: 1);
        int heldBack = 0;
        (byte[] held, int onLine) = await WriteAsync(
            schema, arena, root, Rows, degree: 4, Options with { Room = new NothingAhead() }, writer => heldBack = writer.FlightsHeldBack);
        Assert.True(onLine > 0, "no block went through the line");
        Assert.True(heldBack > 0, "no block was held back");
        Assert.True(alone.AsSpan().SequenceEqual(held), $"{held.Length} bytes against {alone.Length}");
    }

    /// <summary>
    /// A work that admits nothing of its writer past what must go and does not hold what the writer holds: every block
    /// goes through alone, every chunk in emission ends before the next leaves, and no arena keeps its blocks.
    /// </summary>
    private sealed class NothingAhead : Vorticity.Writing.WriteRoom
    {
        internal override bool Admits(long bytes) => false;

        internal override bool Holds => false;
    }

    /// <summary>The file of <paramref name="root"/>'s rows written in calls of <paramref name="calls"/> rows at <paramref name="degree"/>, and the blocks that went through its line.</summary>
    private static async Task<(byte[] File, int OnLine)> WriteAsync(
        DType schema, CanonicalArena arena, int root, int calls, int degree, VortexWriteOptions? options = null, Action<VortexFileWriter>? seen = null)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        using MemoryStream stream = new MemoryStream();
        int onLine;
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, options ?? Options, session))
        {
            CanonicalArena windows = new CanonicalArena();
            for (int start = 0; start < Rows; start += calls)
            {
                int count = Math.Min(calls, Rows - start);
                int window = CanonicalSlice.SliceAcross(arena, windows, root, start, count);
                using (RecordBatch batch = new RecordBatch(windows, window, start))
                {
                    await writer.WriteAsync(batch);
                }

                windows.Reset();
            }

            onLine = writer.BlocksOnLine;
            await writer.CompleteAsync();
            seen?.Invoke(writer);
        }

        return (stream.ToArray(), onLine);
    }

    /// <summary>A rising key, a measure, and text with nulls, short values and long ones, whose widths change along the rows.</summary>
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
        Random random = new Random(0xB10C);
        byte[][] values = new byte[Rows][];
        int heap = 0;
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row * 7L;
            measureValues[row] = random.NextDouble() * (1 + (row / 10_000));
            values[row] = Encoding.UTF8.GetBytes(row % 5 == 0 ? $"a value long enough to spill, number {row} of {random.Next(0, 1 + (row / 1_000))}" : $"v{random.Next(0, 40)}");
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
            if (row % 11 != 0)
            {
                bitBytes[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
        int valid = arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
        int text = arena.AddVarBinView(utf8, Rows, Validity.Bitmap(valid), views, [data]);
        DType schema = types.Struct(["key", "measure", "text"], [i64, f64, utf8], Nullability.NonNullable);
        return (schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [key, measure, text]));
    }
}
