// Giving up on a file - docs/11-write-strategy.md §3.8.
//
// THE ORACLE IS WHAT SURVIVES ON DISK. Disposal completes an unfinished file, so a producer that
// fails half way leaves one that exists, opens and reports no torn tail; the rows it managed to
// write become the file, and nothing afterwards can tell that from a file meant to end there.
// These cases pin both halves: what `Abandon` removes, and what disposal alone still does.
//
// THE APPEND CASE IS THE ONE THAT LOSES ROWS. An append re-emits the file's last chunk before its
// own, so a footer written before that happens describes the kept rows alone and drops the rest
// while still parsing -- which is exactly what repair cannot see. Abandoning leaves a tail that
// does not parse, and repair takes the file back to its last whole version.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class AbandonTests
{
    private const int Block = 1_024;

    private static readonly string[] Names = ["id", "s"];

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        Names,
        [
            Types.Primitive(PType.I64, Nullability.NonNullable),
            Types.Utf8(Nullability.NonNullable),
        ],
        Nullability.NonNullable);

    [Fact]
    public async Task AbandoningAFileThisWriterCreatedLeavesNothingBehind()
    {
        string path = TempPath();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, Options());
                try
                {
                    await FeedAsync(writer, 0, 1_000);
                    await FeedAsync(writer, 1_000, 2_000);
                    throw new InvalidOperationException("producer failed at batch 2");
                }
                catch
                {
                    writer.Abandon();
                    throw;
                }
            });

            Assert.False(System.IO.File.Exists(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task DisposingWithoutAbandoningStillCompletesTheFile()
    {
        // The trap, pinned so the remark on `DisposeAsync` and the behaviour cannot drift apart.
        // Two thousand rows and a clean open, from a producer that failed.
        string path = TempPath();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, Options());
                await FeedAsync(writer, 0, 1_000);
                await FeedAsync(writer, 1_000, 2_000);
                throw new InvalidOperationException("producer failed at batch 2");
            });

            Assert.True(System.IO.File.Exists(path));
            VortexFile file = await VortexFile.OpenAsync(path);
            await using (file.ConfigureAwait(false))
            {
                Assert.Equal(2_000, file.RowCount);
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task AbandoningIsIdempotentAndRefusesFurtherRows()
    {
        string path = TempPath();
        try
        {
            VortexFileWriter writer = VortexFileWriter.Create(path, Schema, Options());
            await using (writer.ConfigureAwait(false))
            {
                await FeedAsync(writer, 0, 500);
                writer.Abandon();
                writer.Abandon();
                await Assert.ThrowsAsync<ObjectDisposedException>(
                    async () => await FeedAsync(writer, 500, 600));
            }

            Assert.False(System.IO.File.Exists(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task AbandoningAnAppendKeepsTheRowsTheFileAlreadyHad()
    {
        // 5 000 rows over blocks of 1 024 leaves a part block, so the append re-opens the last
        // chunk: the case where completing instead of abandoning drops those rows for good.
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 5_000);
            List<string> before = await RowsOfAsync(path);

            VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, Options());
            await using (writer.ConfigureAwait(false))
            {
                await FeedAsync(writer, 5_000, 5_700);
                writer.Abandon();
            }

            // The tail this leaves is the repairable kind, unlike a completed one.
            await VortexFileRepair.RepairAsync(path);
            Assert.Equal(before, await RowsOfAsync(path));
        }
        finally
        {
            Delete(path);
        }
    }

    private static VortexWriteOptions Options() => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = 1L << 14,
    };

    private static async Task WriteAsync(string path, int start, int end)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, Options());
        await FeedAsync(writer, start, end);
        await writer.CompleteAsync();
    }

    private static async Task FeedAsync(
        VortexFileWriter writer, int start, int end, CancellationToken cancellationToken = default)
    {
        int row = start;
        while (row < end)
        {
            int count = Math.Min(700, end - row);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                int root = arena.AddStruct(
                    Schema,
                    count,
                    Validity.NonNullable,
                    [Longs(arena, row, count), Strings(arena, row, count)]);
                using RecordBatch batch = new RecordBatch(arena, root, row);
                await writer.WriteAsync(batch, cancellationToken);
            }
            finally
            {
                arena.Reset();
            }

            row += count;
        }
    }

    private static int Longs(CanonicalArena arena, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
        Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = start + i;
        }

        return arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
    }

    private static int Strings(CanonicalArena arena, int start, int count)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> bytes);
        bytes.Clear();
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(
                "s" + ((start + i) % 211).ToString(CultureInfo.InvariantCulture));
            Span<byte> view = bytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)utf8.Length);
            utf8.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(
            Schema.GetField(1), count, Validity.NonNullable, views, [VortexBuffer.Empty]);
    }

    private static async Task<List<string>> RowsOfAsync(string path)
    {
        VortexFile file = await VortexFile.OpenAsync(path);
        await using (file.ConfigureAwait(false))
        {
            List<string> rows = [];
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
            {
                Values.DescribeRows(batch, rows);
            }

            return rows;
        }
    }

    private static string TempPath() =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-abandon-{Guid.NewGuid():N}.vortex");

    private static void Delete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (System.IO.IOException)
        {
        }
    }
}
