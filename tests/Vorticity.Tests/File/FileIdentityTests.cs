// The identity of a version of a file's bytes - docs/13-dataset.md §7, step 20 of IMPL-PLAN.md.
//
// WHAT IS HELD: every postscript this library writes names sixteen bytes that change with every
// write, append and post-hoc indexing; a caller can pin them; the entry sits right before the
// postscript, inside the tail an open reads, so asking costs no request; and a file with no entry,
// or with one that is not sixteen bytes, has no identity rather than a wrong one.
using System;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class FileIdentityTests
{
    private const int Rows = 20_000;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["id"], [Types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);

    [Fact]
    public async Task EveryWriteMintsItsOwnIdentity()
    {
        Decoders.EnsureRegistered();
        byte[] first = await WriteAsync(0, Rows);
        byte[] second = await WriteAsync(0, Rows);

        await using VortexFile a = await VortexFile.OpenAsync(new MemorySegmentSource(first), VortexOpenOptions.Default);
        await using VortexFile b = await VortexFile.OpenAsync(new MemorySegmentSource(second), VortexOpenOptions.Default);
        Assert.NotNull(a.Identity);
        Assert.NotNull(b.Identity);
        Assert.NotEqual(a.Identity, b.Identity);

        // The same rows, the same length: the two files differ in the identity's sixteen bytes only.
        Assert.Equal(first.Length, second.Length);
        int differing = 0;
        for (int i = 0; i < first.Length; i++)
        {
            differing += first[i] != second[i] ? 1 : 0;
        }

        Assert.InRange(differing, 1, FileIdentity.Length);
    }

    [Fact]
    public async Task APinnedIdentityIsWrittenVerbatimAndMakesTheWriteAPureFunctionAgain()
    {
        Decoders.EnsureRegistered();
        Guid pinned = Guid.NewGuid();
        byte[] first = await WriteAsync(0, Rows, pinned);
        byte[] second = await WriteAsync(0, Rows, pinned);

        Assert.Equal(first, second);
        await using VortexFile file = await VortexFile.OpenAsync(new MemorySegmentSource(first), VortexOpenOptions.Default);
        Assert.Equal(pinned, file.Identity);
    }

    [Fact]
    public async Task TheEntryIsTheLastBytesBeforeThePostscriptAndAskingCostsNoRequest()
    {
        Decoders.EnsureRegistered();
        Guid pinned = Guid.NewGuid();
        byte[] bytes = await WriteAsync(0, Rows, pinned);
        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default);

        int reads = source.TotalReads;
        Assert.Equal(pinned, file.Identity);
        Assert.Equal(pinned, file.Identity);
        Assert.Equal(reads, source.TotalReads);

        Assert.True(file.TryGetMetadataIndex(FileIdentity.MetadataKeyUtf8, out int index));
        SegmentSpec entry = file.GetMetadataSegment(index);
        int postscriptLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(bytes.Length - VortexFileFormat.EofSize + 2));
        Assert.Equal((ulong)FileIdentity.Length, entry.Length);
        Assert.Equal((ulong)(bytes.Length - VortexFileFormat.EofSize - postscriptLength), entry.Offset + entry.Length);
        Assert.True(bytes.Length - (long)entry.Offset <= VortexFileFormat.InitialReadSize);

        // The metadata API reads the same bytes back, from the tail.
        SegmentOwner owner = await file.ReadMetadataAsync(index, default);
        try
        {
            Assert.Equal(pinned, new Guid(owner.Buffer.Span));
        }
        finally
        {
            owner.Release();
        }

        Assert.Equal(reads, source.TotalReads);
    }

    [Fact]
    public async Task AnAppendAndAPostHocIndexingEachMintANewVersion()
    {
        Decoders.EnsureRegistered();
        string path = TempPath();
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, await WriteAsync(0, Rows, policy: WritePolicy.None));
            Guid? written = await IdentityOf(path);

            await using (VortexFileWriter writer = await VortexFileWriter.AppendAsync(path))
            {
                await FeedAsync(writer, Rows, Rows + 5_000);
                await writer.CompleteAsync();
            }

            Guid? appended = await IdentityOf(path);

            Guid pinned = Guid.NewGuid();
            await VortexFileIndexer.AppendIndexesAsync(
                path, WritePolicy.None.For("id", IndexPolicy.Postings), new VortexWriteOptions { Identity = pinned });
            Guid? indexed = await IdentityOf(path);

            Assert.NotNull(written);
            Assert.NotNull(appended);
            Assert.NotEqual(written, appended);
            Assert.Equal(pinned, indexed);

            // Repairing a torn tail returns to the last whole version, and to its identity.
            byte[] whole = await System.IO.File.ReadAllBytesAsync(path);
            await System.IO.File.WriteAllBytesAsync(path, whole.AsSpan(0, whole.Length - 3).ToArray());
            VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path);
            Assert.True(repaired.Truncated);
            Assert.Equal(appended, await IdentityOf(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileWithoutTheEntryHasNoIdentity()
    {
        Decoders.EnsureRegistered();

        // Written by the reference: no entry at all.
        await using VortexFile reference = await VortexFile.OpenAsync(Corpus.Path("containers/zoned_many_zones_nulls"));
        Assert.Null(reference.Identity);

        // An entry of the wrong length is no identity, and the file still opens.
        byte[] bytes = await WriteAsync(0, 1_000, Guid.NewGuid());
        await using VortexFile file = await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);
        Assert.True(file.TryGetMetadataIndex(FileIdentity.MetadataKeyUtf8, out int index));
        SegmentSpec entry = file.GetMetadataSegment(index);
        Assert.Null(FileIdentity.Find(
            [FileIdentity.MetadataKeyUtf8.ToArray()],
            [new SegmentSpec(entry.Offset, entry.Length - 1, 0, 0, 0)],
            bytes,
            0));

        // An entry outside the window the open read is not fetched: it is no identity either.
        Assert.Null(FileIdentity.Find(
            [FileIdentity.MetadataKeyUtf8.ToArray()],
            [entry],
            bytes.AsSpan((int)entry.Offset + 1),
            (long)entry.Offset + 1));
    }

    [Fact]
    public async Task ADisposedFileRefusesTheQuestion()
    {
        Decoders.EnsureRegistered();
        byte[] bytes = await WriteAsync(0, 100);
        VortexFile file = await VortexFile.OpenAsync(new MemorySegmentSource(bytes), VortexOpenOptions.Default);
        await file.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => file.Identity);
    }

    // ------------------------------------------------------------------------------ helpers

    private static async Task<Guid?> IdentityOf(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path);
        return file.Identity;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-identity-{Guid.NewGuid():N}.vortex");

    private static async Task<byte[]> WriteAsync(int start, int end, Guid? identity = null, WritePolicy? policy = null)
    {
        using MemoryStream stream = new MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = 1_024,
            Identity = identity,
            Indexes = policy ?? WritePolicy.Auto,
        };
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream, ownsStream: false), Schema, options))
        {
            await FeedAsync(writer, start, end);
            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    private static async Task FeedAsync(VortexFileWriter writer, int start, int end)
    {
        const int batch = 3_000;
        for (int row = start; row < end; row += batch)
        {
            int count = Math.Min(batch, end - row);
            CanonicalArena arena = new CanonicalArena();
            try
            {
                VortexBuffer buffer = arena.Allocate(count * 8, 8, out Span<byte> bytes);
                Span<long> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < count; i++)
                {
                    values[i] = (row + i) * 7L % 1_000;
                }

                int column = arena.AddPrimitive(Schema.GetField(0), count, Validity.NonNullable, PType.I64, buffer);
                int root = arena.AddStruct(Schema, count, Validity.NonNullable, [column]);
                using RecordBatch record = new RecordBatch(arena, root, row);
                await writer.WriteAsync(record);
            }
            finally
            {
                arena.Reset();
            }
        }
    }
}
