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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
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

    [Fact]
    public async Task AFailedResumeLeavesTheOriginalRowsRecoverable()
    {
        // THE CASE THE REVIEW REASONED OUT AND COULD NOT REACH. `AppendAsync` re-emits the chunk it
        // re-opened before it hands the writer back; a failure in there used to be caught by a
        // dispose, which wrote a footer over the kept rows alone. The file then parsed, held fewer
        // rows than it started with, and repair could not see it, because repair looks for a tail
        // that does not parse.
        //
        // A REFUSED WRITE, NOT A CANCELLATION, and that corrects the review: it expected the token
        // passed to `AppendAsync` to be enough, but the writes that re-emit the chunk put rows into
        // transit rather than on the sink, so nothing in them polls the token. What does reach the
        // sink is a block spilling, which needs the re-opened chunk to be large and the append's
        // own block target small -- hence one chunk written wide, continued narrow.
        string path = TempPath();
        try
        {
            await WriteAsync(path, 0, 20_000, OneChunk());
            List<string> before = await RowsOfAsync(path);
            Assert.Equal(20_000, before.Count);

            await Assert.ThrowsAsync<IOException>(async () =>
                await VortexFileWriter.AppendAsync(
                    path,
                    Indexed(),
                    (stream, position) => new RefusingSink(stream, position, allow: 0),
                    CancellationToken.None));

            // Torn, which is the repairable kind, and repair takes it back to every original row.
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

    /// <summary>One chunk for the whole file, so the chunk an append re-opens is the whole file.</summary>
    private static VortexWriteOptions OneChunk() => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = 1L << 24,
    };

    /// <summary>A sink that passes <paramref name="allow"/> writes through, then refuses.</summary>
    private sealed class RefusingSink : ISegmentSink, IAsyncDisposable
    {
        private readonly StreamSegmentSink _inner;
        private readonly int _allow;
        private int _writes;

        internal RefusingSink(Stream stream, long position, int allow)
        {
            _inner = new StreamSegmentSink(stream, ownsStream: true, position);
            _allow = allow;
        }

        public long Position => _inner.Position;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (++_writes > _allow)
            {
                throw new IOException("the sink refused this write");
            }

            return _inner.WriteAsync(data, cancellationToken);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>Options that make an append re-open the file's last chunk: indexed, part-block.</summary>
    private static VortexWriteOptions Indexed() => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = 1L << 14,
        IndexBudgetPerMille = 1_000_000,
        Indexes = WritePolicy.Auto.For("s", IndexPolicy.Postings),
    };

    private static async Task WriteAsync(string path, int start, int end) =>
        await WriteAsync(path, start, end, Options());

    private static async Task WriteAsync(
        string path, int start, int end, VortexWriteOptions options)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, options);
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
