// A torn tail opens at the last whole version.
//
// WHAT IS HELD: a file an append tore anywhere -- one byte short, in the postscript, in the footer,
// half way through the appended data -- opens at the version before the append, says so, and
// reads as that version; a second append torn opens at the first; a whole file opens as it stands
// with no read more; a file that does not begin as Vortex is refused without a walk; the refusing
// policy keeps the failure, and so does a file with no whole version; and neither an append, an
// index nor a fragment is built behind a tear.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class TornTailTests
{
    private const int Block = 1_024;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["id"], [Types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);

    private static long Id(int row) => (row * 7_919L) % 100_003;

    private static VortexWriteOptions Options => new VortexWriteOptions
    {
        RowBlockSize = Block,
        DataBlockTargetBytes = null,
        WritePolicy = WritePolicy.None.For("id", IndexSpec.Bloom()),
    };

    [Fact]
    public async Task ATornAppendOpensAtTheVersionBeforeItWhereverItTore()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            byte[] first = await WriteAsync(0, 3_000);
            await System.IO.File.WriteAllBytesAsync(path, first, ct);
            await AppendAsync(path, 3_000, 9_000);
            byte[] appended = await System.IO.File.ReadAllBytesAsync(path, ct);
            int added = appended.Length - first.Length;

            // One byte short, inside the end-of-file record, the postscript, the footer, half the
            // appended bytes, all of them but one.
            foreach (int cut in (int[])[1, 5, 40, 300, added / 2, added - 1])
            {
                byte[] torn = appended.AsSpan(0, appended.Length - cut).ToArray();
                await using VortexFile file = await OpenAsync(torn);
                Assert.Equal((torn.LongLength, first.LongLength), (file.TornTail!.FileLength, file.TornTail.ValidLength));
                Assert.False(string.IsNullOrEmpty(file.TornTail.Reason));
                Assert.Equal(first.LongLength, file.FileLength);
                Assert.Equal(3_000, file.RowCount);
                Assert.Equal(Expected(0, 3_000), await ReadAsync(file));

                // Its indexes are the version's own.
                VortexExpr present = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(1_234))));
                Assert.Equal(1, await file.ScanBuilder().Where(present).CountAsync(ct));
                Assert.True(await file.MayMatchAsync(present, ct));
            }

            // Torn beyond the first append's end, the file opens at it.
            await System.IO.File.WriteAllBytesAsync(path, appended.AsSpan(0, appended.Length).ToArray(), ct);
            await AppendAsync(path, 9_000, 10_000);
            byte[] twice = await System.IO.File.ReadAllBytesAsync(path, ct);
            await using VortexFile second = await OpenAsync(twice.AsSpan(0, twice.Length - 11).ToArray());
            Assert.Equal((long)appended.Length, second.TornTail!.ValidLength);
            Assert.Equal(Expected(0, 9_000), await ReadAsync(second));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AWholeFileOpensAsItStandsWithNoReadMore()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        byte[] bytes = await WriteAsync(0, 3_000);
        long[] reads = new long[2];
        int at = 0;
        foreach (VortexTornTailPolicy policy in (VortexTornTailPolicy[])[VortexTornTailPolicy.ReadPrevious, VortexTornTailPolicy.Refuse])
        {
            CountingSource source = new CountingSource(new MemorySegmentSource(bytes));
            await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions { TornTail = policy }, ct);
            Assert.Null(file.TornTail);
            reads[at++] = source.Reads;
        }

        Assert.Equal(reads[1], reads[0]);
    }

    [Fact]
    public async Task AFileThatDoesNotBeginAsVortexIsRefusedWithoutAWalk()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] noise = new byte[8 << 20];
        new Random(25).NextBytes(noise);
        CountingSource source = new CountingSource(new MemorySegmentSource(noise));
        await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(source, VortexOpenOptions.Default, ct));

        // The tail, and the four bytes that say it is not Vortex.
        Assert.InRange(source.Reads, 1, 2);
        Assert.True(source.Bytes < 1 << 17, $"{source.Bytes} bytes read");
    }

    [Fact]
    public async Task TheRefusingPolicyAndAFileWithNoWholeVersionKeepTheFailure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        byte[] whole = await WriteAsync(0, 3_000);
        byte[] torn = whole.AsSpan(0, whole.Length - 9).ToArray();
        VortexFormatException refused = await Assert.ThrowsAsync<VortexFormatException>(async () =>
            await VortexFile.OpenAsync(new MemorySegmentSource(torn), new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse }, ct));

        // A first write torn has no version before it: the open fails with the tail's own reason.
        VortexFormatException fallen = await Assert.ThrowsAsync<VortexFormatException>(async () => await OpenAsync(torn));
        Assert.Equal(refused.Message, fallen.Message);
    }

    [Fact]
    public async Task NothingIsWrittenBehindATear()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, await WriteAsync(0, 3_000), ct);
            await AppendAsync(path, 3_000, 5_000);
            byte[] appended = await System.IO.File.ReadAllBytesAsync(path, ct);
            byte[] torn = appended.AsSpan(0, appended.Length - 21).ToArray();
            await System.IO.File.WriteAllBytesAsync(path, torn, ct);

            VortexFormatException append = await Assert.ThrowsAsync<VortexFormatException>(
                async () => await VortexFileWriter.AppendAsync(path, Options, ct));
            VortexFormatException index = await Assert.ThrowsAsync<VortexFormatException>(
                async () => await VortexFileIndexer.AppendIndexesAsync(
                    path, WritePolicy.None.For("id", IndexSpec.SortedRuns), cancellationToken: ct));
            VortexFormatException fragment;
            await using (VortexFile previous = await VortexFile.OpenAsync(path, ct))
            {
                Assert.NotNull(previous.TornTail);
                fragment = await Assert.ThrowsAsync<VortexFormatException>(
                    async () => await VortexFileIndexer.BuildFragmentAsync(
                        previous, WritePolicy.None.For("id", IndexSpec.SortedRuns), new RowRange(0, previous.RowCount),
                        cancellationToken: ct));
            }

            foreach (VortexFormatException refusal in (VortexFormatException[])[append, index, fragment])
            {
                Assert.Contains("torn tail", refusal.Message, StringComparison.Ordinal);
                Assert.Contains("RepairAsync", refusal.Message, StringComparison.Ordinal);
            }

            // Not a byte moved.
            Assert.Equal(torn, await System.IO.File.ReadAllBytesAsync(path, ct));

            // The repair is the caller's, and after it the file is whole again.
            VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path, ct);
            Assert.True(repaired.Truncated);
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            Assert.Null(file.TornTail);
            Assert.Equal(3_000, file.RowCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>Counts what an open reads.</summary>
    private sealed class CountingSource(ISegmentReader inner) : ISegmentReader
    {
        internal int Reads { get; private set; }

        internal long Bytes { get; private set; }

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
        {
            Reads++;
            Bytes += spec.Length;
            return inner.ReadAsync(spec, cancellationToken);
        }

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            for (int i = 0; i < requests.Count; i++)
            {
                Reads++;
                Bytes += requests.GetSpec(i).Length;
            }

            return inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
        {
            Reads++;
            Bytes += length;
            return inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-torn-{Guid.NewGuid():N}.vortex");

    private static async Task<VortexFile> OpenAsync(byte[] bytes) =>
        await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);

    private static List<long> Expected(int start, int end)
    {
        List<long> ids = [];
        for (int row = start; row < end; row++)
        {
            ids.Add(Id(row));
        }

        return ids;
    }

    private static async Task<List<long>> ReadAsync(VortexFile file)
    {
        List<long> ids = [];
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync())
        {
            CanonicalNode root = batch.Arena.GetNode(batch.RootIndex);
            CanonicalNode column = batch.Arena.GetNode(root.GetFieldIndex(0));
            ids.AddRange(column.Values.Cast<long>()[..batch.RowCount].ToArray());
        }

        return ids;
    }

    private static async Task AppendAsync(string path, int start, int end)
    {
        await using VortexFileWriter writer = await VortexFileWriter.AppendAsync(path, Options);
        await FeedAsync(writer, start, end);
        await writer.CompleteAsync();
    }

    private static async Task<byte[]> WriteAsync(int start, int end)
    {
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), Schema, Options))
        {
            await FeedAsync(writer, start, end);
            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            int count = end - start;
            VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < count; i++)
            {
                values[i] = Id(start + i);
            }

            int column = arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
            using RecordBatch batch = new RecordBatch(arena, arena.AddStruct(Schema, count, Validity.NonNullable, [column]), start);
            await writer.WriteAsync(batch);
        }
        finally
        {
            arena.Reset();
        }
    }
}
