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
    public void AColumnsZstdFramesAreItsBlocks()
    {
        // Eight blocks a column: a nullable column with a block of nulls, a text column, a dense one written as it lies, and
        // noise no frame beats the plain form on.
        Columns columns = Build(8 * BlockRows);
        using ArrayBlobWriter.Workspace workspace = new ArrayBlobWriter.Workspace { FrameRows = BlockRows };
        int[] frameCounts = new int[columns.Nodes.Length];
        for (int column = 0; column < columns.Nodes.Length; column++)
        {
            if (ZstdPlan.TryBuild(columns.Arena, columns.Nodes[column], columns.Plain[column], workspace) is { } plan)
            {
                frameCounts[column] = plan.FrameCount;
                plan.Release();
            }
        }

        // The block of nulls has no frame, and the noise none kept.
        Assert.Equal([7, 8, 8, 0], frameCounts);
    }

    [Fact]
    public void FramesCompressedAheadAreThePlanATrialMakes()
    {
        // Each column as a trial prices it on one thread, then from its frames compressed ahead on several: a column
        // with nulls and a block of them, text with nulls in one data buffer and in two, dense keys, and noise no frame
        // beats the plain form on, whose frames are compressed ahead and refused alike.
        Columns columns = Build(8 * BlockRows);
        int[] nodes = [.. columns.Nodes, SplitHeap(columns.Arena, columns.Nodes[1])];
        long[] plain = [.. columns.Plain, columns.Plain[1]];
        using ArrayBlobWriter.Workspace workspace = new ArrayBlobWriter.Workspace { FrameRows = BlockRows };
        int taken = 0;
        for (int column = 0; column < nodes.Length; column++)
        {
            ZstdPlan.Ahead ahead = new ZstdPlan.Ahead();
            workspace.Ahead = ahead;

            // Watched: the trial on one thread notes what it priced, and the next chunk is compressed ahead.
            Assert.Equal(0, ahead.Plan(columns.Arena, nodes[column], BlockRows, 0, lanes: 1));
            ZstdPlan? alone = ZstdPlan.TryBuild(columns.Arena, nodes[column], plain[column], workspace);
            Assert.False(ahead.Taken);
            ahead.Finish();

            int items = ahead.Plan(columns.Arena, nodes[column], BlockRows, 0, lanes: 4);
            Assert.Equal(4, items);
            Parallel.For(0, items, ahead.Run);
            ZstdPlan? framed = ZstdPlan.TryBuild(columns.Arena, nodes[column], plain[column], workspace);
            Assert.Equal(framed is not null, ahead.Taken);
            ahead.Finish();
            workspace.Ahead = null;

            Assert.Equal(alone is null, framed is null);
            if (alone is { } one && framed is { } many)
            {
                taken++;
                Assert.Equal(one.FrameCount, many.FrameCount);
                Assert.Equal(one.Data.AsSpan(0, one.CompressedLength).ToArray(), many.Data.AsSpan(0, many.CompressedLength).ToArray());
                for (int frame = 0; frame < one.FrameCount; frame++)
                {
                    Assert.Equal(one.FrameAt(frame), many.FrameAt(frame));
                }

                one.Release();
                many.Release();
            }
        }

        Assert.Equal(4, taken);
    }

    [Fact]
    public void FramesAreCompressedAheadOnlyForTheValuesATrialPriced()
    {
        // A trial on other values than the column's notes nothing, and the next chunk is not compressed ahead; nor is a
        // column too small to be worth handing out.
        Columns columns = Build(8 * BlockRows);
        using ArrayBlobWriter.Workspace workspace = new ArrayBlobWriter.Workspace { FrameRows = BlockRows };
        ZstdPlan.Ahead ahead = new ZstdPlan.Ahead();
        workspace.Ahead = ahead;
        ahead.Plan(columns.Arena, columns.Nodes[0], BlockRows, 0, lanes: 1);
        ZstdPlan.TryBuild(columns.Arena, columns.Nodes[2], columns.Plain[2], workspace)?.Release();
        ahead.Finish();
        Assert.Equal(0, ahead.Plan(columns.Arena, columns.Nodes[0], BlockRows, 0, lanes: 4));
        ahead.Finish();

        // Another level is other frames.
        ahead.Plan(columns.Arena, columns.Nodes[2], BlockRows, 0, lanes: 1);
        ZstdPlan.TryBuild(columns.Arena, columns.Nodes[2], columns.Plain[2], workspace)?.Release();
        ahead.Finish();
        Assert.Equal(4, ahead.Plan(columns.Arena, columns.Nodes[2], BlockRows, 9, lanes: 4));
        Assert.False(ahead.Serves(columns.Arena, columns.Arena.GetNode(columns.Nodes[2]), BlockRows, 0));
        Assert.True(ahead.Serves(columns.Arena, columns.Arena.GetNode(columns.Nodes[2]), BlockRows, 9));
        ahead.Finish();

        Columns small = Build(2 * BlockRows);
        ahead.Forget();
        ahead.Plan(small.Arena, small.Nodes[2], BlockRows, 0, lanes: 1);
        ZstdPlan.TryBuild(small.Arena, small.Nodes[2], small.Plain[2], workspace)?.Release();
        ahead.Finish();
        Assert.Equal(0, ahead.Plan(small.Arena, small.Nodes[2], BlockRows, 0, lanes: 4));
        ahead.Finish();
        workspace.Ahead = null;
    }

    [Fact]
    public async Task AFileWrittenOnSeveralThreadsIsTheBytesOfOneWrittenOnOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string onePath = Temp();
        string fourPath = Temp();
        string sixteenPath = Temp();
        try
        {
            (_, int oneAcross, int oneAhead) = await WriteAsync(onePath, 1, ct);
            (WriteReport four, int fourAcross, _) = await WriteAsync(fourPath, 4, ct);
            (_, _, int sixteenAhead) = await WriteAsync(sixteenPath, 16, ct);

            Assert.Equal(0, oneAcross);
            Assert.Equal(0, oneAhead);
            Assert.True(fourAcross > 0, "the fixture must compress some column across threads");

            // Seven chunks of four or five blocks: the first written on one thread and watched, and a zstd column of the
            // chunks after it that takes more than a sixteenth of the four has its frames compressed ahead, when the
            // chunk before priced enough of them -- the nullable column's 210 KB of values in four blocks, 262 KB in five.
            Assert.Equal(7, four.Columns[0].Encodings.Length);
            Assert.InRange(sixteenAhead, 1, 3 * 6);
            for (int column = 0; column < 3; column++)
            {
                Assert.All(four.Columns[column].Encodings, scheme => Assert.Equal(nameof(EncodingHint.Zstd), scheme));
            }

            Assert.All(four.Columns[3].Encodings, scheme => Assert.NotEqual(nameof(EncodingHint.Zstd), scheme));
            byte[] one = await System.IO.File.ReadAllBytesAsync(onePath, ct);
            Assert.Equal(one, await System.IO.File.ReadAllBytesAsync(fourPath, ct));
            Assert.Equal(one, await System.IO.File.ReadAllBytesAsync(sixteenPath, ct));
        }
        finally
        {
            System.IO.File.Delete(onePath);
            System.IO.File.Delete(fourPath);
            System.IO.File.Delete(sixteenPath);
        }
    }

    private static async Task<(WriteReport Report, int Across, int Ahead)> WriteAsync(string path, int degree, CancellationToken ct)
    {
        Columns columns = Build(32 * BlockRows);
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
        return (report, writer.ColumnChunksAcross, writer.ColumnChunksFramedAhead);
    }

    /// <summary>
    /// The text column <paramref name="node"/> again, its values out of line split between two data buffers at the first
    /// value past the middle: the frames lay what the first buffer does not hold through the views' accessor.
    /// </summary>
    private static int SplitHeap(CanonicalArena arena, int node)
    {
        CanonicalNode text = arena.GetNode(node);
        ReadOnlySpan<byte> heap = text.GetDataBuffer(0).Span;
        int rows = text.Length;
        VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> viewBytes);
        text.Views.Span[..(rows * 16)].CopyTo(viewBytes);
        int split = -1;
        for (int row = 0; row < rows; row++)
        {
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view);
            int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view[12..]);
            if (length <= 12)
            {
                continue;
            }

            if (split < 0 && offset >= heap.Length / 2)
            {
                split = offset;
            }

            if (split >= 0 && offset >= split)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[8..], 1);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset - split);
            }
        }

        VortexBuffer whole = text.GetDataBuffer(0);
        return arena.AddVarBinView(
            text.DType, rows, text.Validity, views, [whole.Slice(0, split), whole.Slice(split, heap.Length - split)]);
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
