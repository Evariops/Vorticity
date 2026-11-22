// A group of rows ended where the caller asks: every column writes what it holds there, the file says what it then holds,
// and the groups the writer ends by itself stay where they were.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What <see cref="VortexFileWriter.EndGroupAsync"/> does to a file.</summary>
public sealed partial class GroupEndTests
{
    private const int BlockRows = 1_024;

    /// <summary>Forty blocks: five groups of eight.</summary>
    private const int Rows = 40 * BlockRows;

    /// <summary>The rows after which the writer ends a group by itself: eight blocks.</summary>
    private const long GroupRows = 8 * BlockRows;

    /// <summary>The rows handed over before the end asked for: inside the third group, inside a block.</summary>
    private const int AskedAt = 20_000;

    private const int BatchRows = 1_000;

    private static readonly Guid Pinned = new Guid("5e5e5e5e-5e5e-4e5e-8e5e-5e5e5e5e5e5e");

    /// <summary>
    /// Every column gathers to a megabyte, which none reaches here, so that each column's chunks end only where a group
    /// does; a text of 600 bytes a row makes each block a chunk of the file, as a dataset's wide rows do.
    /// </summary>
    private static VortexWriteOptions Options(int degree) => new VortexWriteOptions
    {
        BlockRows = BlockRows,
        GatheredChunkBytes = 1 << 20,
        GatheredGroupRows = GroupRows,
        DegreeOfParallelism = degree,
        Identity = Pinned,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public async Task AGroupEndedOnDemandEndsEveryColumnThereAndMovesNoGroupAfterIt(int degree)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        (DType schema, CanonicalArena arena, int root) = Build();
        string plain = Temp();
        string asked = Temp();
        try
        {
            (WriteReport expected, _) = await WriteAsync(plain, schema, arena, root, degree, askAt: -1, ct);
            (WriteReport report, GroupEnd end) = await WriteAsync(asked, schema, arena, root, degree, AskedAt, ct);

            // The end holds the whole blocks handed over, the block being staged left for the chunks after.
            Assert.Equal(AskedAt / BlockRows * BlockRows, end.Rows);
            Assert.True(end.DataBytes > 0 && end.DataBytes < report.Bytes.Data, $"the end read {end.DataBytes} bytes of {report.Bytes.Data}");

            // Every column's chunks end there, and the groups the writer ended by itself stay where they were without it: the
            // key's chunks end at the groups alone, the text's also where its first rate made its first chunk due.
            Assert.Equal(new long[] { GroupRows, 2 * GroupRows, 3 * GroupRows, 4 * GroupRows, Rows }, Ends(expected, 0));
            for (int column = 0; column < 3; column++)
            {
                SortedSet<long> without = Ends(expected, column);
                Assert.DoesNotContain(end.Rows, without);
                Assert.Equal([.. without, end.Rows], Ends(report, column));
            }

            // What the file holds reads as the file written without the end.
            await using VortexFile one = await VortexFile.OpenAsync(plain, ct);
            await using VortexFile other = await VortexFile.OpenAsync(asked, ct);
            Assert.Equal(await RowsAsync(one), await RowsAsync(other));
        }
        finally
        {
            System.IO.File.Delete(plain);
            System.IO.File.Delete(asked);
        }
    }

    [Fact]
    public async Task AGroupEndedOnDemandIsTheSameBytesAtAnyDegree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        (DType schema, CanonicalArena arena, int root) = Build();
        string one = Temp();
        string many = Temp();
        try
        {
            (_, GroupEnd first) = await WriteAsync(one, schema, arena, root, 1, AskedAt, ct);
            byte[] bytes = await System.IO.File.ReadAllBytesAsync(one, ct);
            foreach (int degree in new[] { 4, 14 })
            {
                (_, GroupEnd end) = await WriteAsync(many, schema, arena, root, degree, AskedAt, ct);
                Assert.Equal(first, end);
                Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(many, ct));
            }
        }
        finally
        {
            System.IO.File.Delete(one);
            System.IO.File.Delete(many);
        }
    }

    /// <summary>
    /// Rows taken as they come, a desk among five and a measure, ended inside a block, by an end asked for or by a flush,
    /// each column gathering to its own chunks: every row reads back as written, the rows short of a block that stay in
    /// transit past the end among them. A dataset's object, whose desks go against its dictionary, is the case that broke
    /// (<c>DatasetGroupTests</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowsInTransitPastAnEndReadBackAsTheyWereWritten(bool flush)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string[] desks = ["rates", "fx", "credit", "equity", "commodities"];
        Random random = new Random(7);
        Desk[] rows = [.. Enumerable.Range(0, 30_000).Select(i => new Desk(desks[random.Next(desks.Length)], i * 0.5))];
        string path = Temp();
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 1_024,
                ChunkTargetBytes = 16 << 10,
                GatheredChunkBytes = 16 << 10,
                GatheredGroupRows = 8_192,
                Identity = Pinned,
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Desk>(path, options))
            {
                for (int start = 0; start < rows.Length; start += 1_000)
                {
                    if (start is 7_000 or 19_000)
                    {
                        if (flush)
                        {
                            await writer.FlushAsync(ct);
                        }
                        else
                        {
                            await writer.EndGroupAsync(ct);
                        }
                    }

                    await writer.WriteAsync<Desk>(rows.AsSpan(start, Math.Min(1_000, rows.Length - start)), ct);
                }

                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            List<Desk> read = [];
            await foreach (Desk row in file.Scan<Desk>().ToRecordsAsync(ct))
            {
                read.Add(row);
            }

            Assert.Equal(rows, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Desk(string Name, double Measure);

    /// <summary>Where the chunks of <paramref name="column"/> end, by rows from the file's first.</summary>
    private static SortedSet<long> Ends(WriteReport report, int column)
    {
        SortedSet<long> ends = [];
        long at = 0;
        foreach (int rows in report.ChunkRowsOf(column))
        {
            at += rows;
            ends.Add(at);
        }

        return ends;
    }

    /// <summary>Writes the table in batches, a group ended after <paramref name="askAt"/> rows unless it is negative.</summary>
    private static async Task<(WriteReport Report, GroupEnd End)> WriteAsync(
        string path, DType schema, CanonicalArena arena, int root, int degree, int askAt, CancellationToken ct)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, Options(degree));
        GroupEnd end = default;
        for (int start = 0; start < Rows; start += BatchRows)
        {
            if (start == askAt)
            {
                end = await writer.EndGroupAsync(ct);
            }

            int count = Math.Min(BatchRows, Rows - start);
            using RecordBatch batch = new RecordBatch(arena, CanonicalSlice.SliceAcross(arena, arena, root, start, count), start);
            await writer.WriteAsync(batch, ct);
        }

        return (await writer.CompleteAsync(ct), end);
    }

    private static async Task<List<string>> RowsAsync(VortexFile file)
    {
        List<string> rows = [];
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            ListShapes.Describe(batch, rows);
        }

        return rows;
    }

    private static string Temp() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-group-end-{Guid.NewGuid():N}.vortex");

    /// <summary>A key, a text of about 600 bytes among a hundred, and a measure.</summary>
    private static (DType Schema, CanonicalArena Arena, int Root) Build()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType utf8 = types.Utf8(Nullability.NonNullable);

        VortexBuffer keys = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < Rows; row++)
        {
            keyValues[row] = row;
            measureValues[row] = (row % 211) * 0.5;
        }

        byte[][] texts = new byte[100][];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = Encoding.UTF8.GetBytes($"{i:D3}:{new string((char)('a' + (i % 26)), 596)}");
        }

        VortexBuffer data = arena.Allocate(Rows * texts[0].Length, 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            byte[] value = texts[(row * 37) % texts.Length];
            MemoryMarshal.Write(view, value.Length);
            value.AsSpan(0, 4).CopyTo(view[4..]);
            MemoryMarshal.Write(view[12..], offset);
            value.CopyTo(dataBytes[offset..]);
            offset += value.Length;
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keys);
        int text = arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [data]);
        int measure = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, measures);
        DType schema = types.Struct(["key", "text", "measure"], [i64, utf8, f64], Nullability.NonNullable);
        return (schema, arena, arena.AddStruct(schema, Rows, Validity.NonNullable, [key, text, measure]));
    }
}
