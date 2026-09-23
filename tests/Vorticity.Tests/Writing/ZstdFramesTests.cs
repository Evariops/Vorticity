using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ZstdFramesTests
{
    private const int BlockRows = 8_192;
    private static readonly Guid Pinned = new Guid("5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a");

    [Fact]
    public void FramesCompressedAcrossThreadsAreTheBytesOneThreadWrites()
    {
        // Eight blocks a column, each column's values past the size below which one thread
        // compresses them alone: a nullable column with a block of nulls, a text column, a dense
        // one written as it lies, and noise no frame beats the plain form on.
        Columns columns = Build(8 * BlockRows);
        using ArrayBlobWriter.Workspace alone = new ArrayBlobWriter.Workspace { FrameRows = BlockRows };
        using ArrayBlobWriter.Workspace across = new ArrayBlobWriter.Workspace { FrameRows = BlockRows, Lanes = 4 };
        int[] frameCounts = new int[columns.Nodes.Length];
        for (int column = 0; column < columns.Nodes.Length; column++)
        {
            ZstdPlan? one = ZstdPlan.TryBuild(columns.Arena, columns.Nodes[column], columns.Plain[column], alone);
            ZstdPlan? many = ZstdPlan.TryBuild(columns.Arena, columns.Nodes[column], columns.Plain[column], across);
            Assert.Equal(one.HasValue, many.HasValue);
            if (one is not { } expected || many is not { } actual)
            {
                continue;
            }

            try
            {
                frameCounts[column] = actual.FrameCount;
                Assert.Equal(expected.FrameCount, actual.FrameCount);
                for (int frame = 0; frame < expected.FrameCount; frame++)
                {
                    Assert.Equal(expected.FrameAt(frame), actual.FrameAt(frame));
                }

                Assert.Equal(
                    expected.Data.AsSpan(0, expected.CompressedLength).ToArray(),
                    actual.Data.AsSpan(0, actual.CompressedLength).ToArray());
            }
            finally
            {
                expected.Release();
                actual.Release();
            }
        }

        // The block of nulls has no frame, and the noise none kept.
        Assert.Equal([7, 8, 8, 0], frameCounts);
        Assert.Equal(0, alone.ColumnsAcross);
        Assert.Equal(columns.Nodes.Length, across.ColumnsAcross);
    }

    [Fact]
    public async Task AFileWrittenOnFourThreadsIsTheBytesOfOneWrittenOnOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string onePath = Temp();
        string fourPath = Temp();
        try
        {
            (_, int oneAcross) = await WriteAsync(onePath, 1, ct);
            (WriteReport four, int fourAcross) = await WriteAsync(fourPath, 4, ct);

            Assert.Equal(0, oneAcross);
            Assert.True(fourAcross > 0, "the fixture must compress some column across threads");
            for (int column = 0; column < 3; column++)
            {
                Assert.All(four.Columns[column].Encodings, scheme => Assert.Equal(nameof(EncodingHint.Zstd), scheme));
            }

            Assert.All(four.Columns[3].Encodings, scheme => Assert.NotEqual(nameof(EncodingHint.Zstd), scheme));
            Assert.Equal(await System.IO.File.ReadAllBytesAsync(onePath, ct), await System.IO.File.ReadAllBytesAsync(fourPath, ct));
        }
        finally
        {
            System.IO.File.Delete(onePath);
            System.IO.File.Delete(fourPath);
        }
    }

    private static async Task<(WriteReport Report, int Across)> WriteAsync(string path, int degree, CancellationToken ct)
    {
        Columns columns = Build(16 * BlockRows);
        Dictionary<string, EncodingHint> hints = new Dictionary<string, EncodingHint>
        {
            ["x"] = EncodingHint.Zstd,
            ["t"] = EncodingHint.Zstd,
            ["k"] = EncodingHint.Zstd,
            ["noise"] = EncodingHint.Zstd,
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(
            path,
            columns.Schema,
            new VortexWriteOptions { Identity = Pinned, EncodingHints = hints, DegreeOfParallelism = degree });
        using (RecordBatch batch = new RecordBatch(columns.Arena, columns.Root, 0))
        {
            await writer.WriteAsync(batch, ct);
        }

        WriteReport report = await writer.CompleteAsync(ct);
        return (report, writer.ColumnsCompressedAcross);
    }

    /// <summary>
    /// Sevenths with a null in five and a block of nulls, text with a null in seven, dense keys,
    /// and noise.
    /// </summary>
    private static Columns Build(int rows)
    {
        DTypeArena types = new DTypeArena();
        DType bits = types.Bool(Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.Nullable);
        DType text = types.Utf8(Nullability.Nullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["x", "t", "k", "noise"], [f64, text, i64, i64], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();

        VortexBuffer values = arena.Allocate(rows * sizeof(double), sizeof(double), out Span<byte> valueBytes);
        VortexBuffer valueValidity = arena.Allocate((rows + 7) / 8, 8, out Span<byte> valueBits);
        VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer textValidity = arena.Allocate((rows + 7) / 8, 8, out Span<byte> textBits);
        VortexBuffer heap = arena.Allocate(rows * 20, 8, out Span<byte> heapBytes);
        VortexBuffer keys = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer noise = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> noiseBytes);
        valueBytes.Clear();
        valueBits.Clear();
        viewBytes.Clear();
        textBits.Clear();
        Span<double> x = MemoryMarshal.Cast<byte, double>(valueBytes);
        Span<long> k = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<ulong> random = MemoryMarshal.Cast<byte, ulong>(noiseBytes);
        ulong state = 0x9E3779B97F4A7C15UL;
        int used = 0;
        for (int row = 0; row < rows; row++)
        {
            if (row % 5 != 2 && row / BlockRows != 3)
            {
                valueBits[row >> 3] |= (byte)(1 << (row & 7));
                x[row] = row / 7.0;
            }

            k[row] = (row * 7919L) % 1_000_003;
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            random[row] = state;

            if (row % 7 == 3)
            {
                continue;
            }

            textBits[row >> 3] |= (byte)(1 << (row & 7));
            int length = row % 21;
            for (int b = 0; b < length; b++)
            {
                heapBytes[used + b] = (byte)('a' + ((row + b) % 26));
            }

            Span<byte> view = viewBytes.Slice(row * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                heapBytes.Slice(used, length).CopyTo(view[4..]);
            }
            else
            {
                heapBytes.Slice(used, 4).CopyTo(view[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
                used += length;
            }
        }

        int[] nodes =
        [
            arena.AddPrimitive(f64, rows, Validity.Bitmap(arena.AddBool(bits, rows, Validity.NonNullable, valueValidity, 0)), PType.F64, values),
            arena.AddVarBinView(text, rows, Validity.Bitmap(arena.AddBool(bits, rows, Validity.NonNullable, textValidity, 0)), views, [heap.Slice(0, used)]),
            arena.AddPrimitive(i64, rows, Validity.NonNullable, PType.I64, keys),
            arena.AddPrimitive(i64, rows, Validity.NonNullable, PType.I64, noise),
        ];
        long[] plain = [rows * sizeof(double), (rows * 16L) + used, rows * sizeof(long), rows * sizeof(long)];
        return new Columns(schema, arena, arena.AddStruct(schema, rows, Validity.NonNullable, nodes), nodes, plain);
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-frames-{Guid.NewGuid():N}.vortex");

    private sealed record Columns(DType Schema, CanonicalArena Arena, int Root, int[] Nodes, long[] Plain);
}
