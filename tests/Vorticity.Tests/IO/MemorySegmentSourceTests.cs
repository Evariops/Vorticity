using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.File;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The in-memory source held to the all-or-nothing rule the other sources follow: a batch either
/// fills every slot or leaves the set as it found it. It is the source tests and small readers run
/// on, so a set it half-fills is a set every caller after it inherits.
/// </summary>
public sealed class MemorySegmentSourceTests
{
    [Fact]
    public async Task A_failing_slot_leaves_no_slot_filled_behind_it()
    {
        ISegmentReader source = new MemorySegmentSource(Pattern(256));
        using SegmentRequestSet set = new SegmentRequestSet();

        int first = set.Add(Spec(0, 32));
        int second = set.Add(Spec(64, 32));
        int escapes = set.Add(Spec(192, 128));

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await source.ReadManyAsync(set, CancellationToken.None));

        Assert.False(set.IsFilled(first));
        Assert.False(set.IsFilled(second));
        Assert.False(set.IsFilled(escapes));
        Assert.False(set.IsPopulated);
    }

    [Fact]
    public async Task The_same_set_reads_again_after_a_failure()
    {
        // The point of releasing them: the set is usable, so a caller that retries against a
        // source able to serve the whole batch gets every slot rather than a mix of two vintages.
        using SegmentRequestSet set = new SegmentRequestSet();
        int first = set.Add(Spec(0, 32));
        int escapes = set.Add(Spec(192, 128));

        ISegmentReader tooShort = new MemorySegmentSource(Pattern(256));
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await tooShort.ReadManyAsync(set, CancellationToken.None));

        ISegmentReader whole = new MemorySegmentSource(Pattern(512));
        await whole.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
        Assert.Equal(32, set.GetBuffer(first).Length);
        Assert.Equal(128, set.GetBuffer(escapes).Length);
    }

    [Fact]
    public async Task Bytes_off_a_boundary_are_copied_once_and_every_read_is_a_view_of_the_copy()
    {
        // Where a byte[] puts them: eight bytes past a 64-byte boundary.
        ReadOnlyMemory<byte> bytes = Placed(Pattern(4096), 8);
        ISegmentReader source = new MemorySegmentSource(bytes);

        using SegmentOwner first = await source.ReadAsync(Spec(256, 512, alignmentExponent: 6), CancellationToken.None);
        using SegmentOwner second = await source.ReadAsync(Spec(256, 512, alignmentExponent: 6), CancellationToken.None);

        Assert.Equal(0u, AddressOf(first.Buffer) % 64);
        Assert.Equal(AddressOf(first.Buffer), AddressOf(second.Buffer));
        Assert.NotEqual(StartOf(bytes.Span) + 256, AddressOf(first.Buffer));
        Assert.True(first.Buffer.Span.SequenceEqual(bytes.Span.Slice(256, 512)));
    }

    [Fact]
    public async Task Bytes_on_a_boundary_are_read_where_they_lie()
    {
        ReadOnlyMemory<byte> bytes = Placed(Pattern(4096), 0);
        ISegmentReader source = new MemorySegmentSource(bytes);

        using SegmentOwner segment = await source.ReadAsync(Spec(128, 256, alignmentExponent: 6), CancellationToken.None);

        Assert.Equal(StartOf(bytes.Span) + 128, AddressOf(segment.Buffer));
    }

    [Fact]
    public async Task A_segment_the_file_lays_off_its_declared_boundary_is_copied_to_one()
    {
        // The tail an open reads starts anywhere; a well-formed file lays nothing else that way.
        ReadOnlyMemory<byte> bytes = Placed(Pattern(4096), 0);
        ISegmentReader source = new MemorySegmentSource(bytes);

        using SegmentOwner segment = await source.ReadAsync(Spec(8, 256, alignmentExponent: 6), CancellationToken.None);

        Assert.Equal(0u, AddressOf(segment.Buffer) % 64);
        Assert.True(segment.Buffer.Span.SequenceEqual(bytes.Span.Slice(8, 256)));
    }

    [Fact]
    public async Task A_copy_is_refused_once_the_bytes_are_let_go()
    {
        // A dispose that lands between the check a read makes and the copy it then takes: the
        // bytes are let go before the read can see the source disposed.
        MemorySegmentSource source = new MemorySegmentSource(Placed(Pattern(4096), 8));
        OwnerOf(source).Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await ((ISegmentReader)source).ReadAsync(Spec(8, 256, alignmentExponent: 6), CancellationToken.None));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_owner")]
    private static extern ref SegmentOwner OwnerOf(MemorySegmentSource source);

    [Fact]
    public void A_source_dropped_undisposed_lets_the_array_it_pinned_go()
    {
        WeakReference array = DropASourceOverAnAlignedArray();
        for (int i = 0; i < 3 && array.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(array.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropASourceOverAnAlignedArray()
    {
        ReadOnlyMemory<byte> bytes = Placed(Pattern(4096), 0);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment));
        _ = new MemorySegmentSource(bytes);
        return new WeakReference(segment.Array);
    }

    [Fact]
    public async Task A_file_whose_tail_lands_off_its_alignment_reads_as_from_its_path()
    {
        // Past 64 KiB an open reads its tail from where the file's length puts it, off an eight-byte
        // boundary for most lengths, and the source copies that window onto one: every structure
        // sliced out of it lands off the alignment its file gave it. Nothing read there in place
        // may depend on that alignment.
        int probed = 0;
        foreach (CorpusEntry entry in CorpusManifest.Entries)
        {
            if (entry.SizeBytes <= 70_000 || !entry.HasDTypeSegment || (entry.SizeBytes - 65_535) % 8 == 0)
            {
                continue;
            }

            probed++;
            string path = CorpusManifest.FullPath(entry);
            (long Rows, long Valid) fromPath;
            await using (VortexFile file = await VortexFile.OpenAsync(path, VortexOpenOptions.Default, TestContext.Current.CancellationToken))
            {
                fromPath = await WalkAsync(file);
            }

            await using MemorySegmentSource source = new MemorySegmentSource(global::System.IO.File.ReadAllBytes(path));
            await using VortexFile memory = await VortexFile.OpenAsync(
                source, new VortexOpenOptions { LeaveSourceOpen = true }, TestContext.Current.CancellationToken);
            Assert.Equal(fromPath, await WalkAsync(memory));
            Assert.Equal(entry.RowCount, fromPath.Rows);
        }

        Assert.True(probed > 30, "the corpus holds files past 64 KiB");
    }

    private static async Task<(long Rows, long Valid)> WalkAsync(VortexFile file)
    {
        long rows = 0;
        long valid = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(TestContext.Current.CancellationToken))
        {
            rows += batch.RowCount;
            for (int field = 0; field < batch.FieldCount; field++)
            {
                VortexColumn column = batch.Column(field);
                for (int row = 0; row < batch.RowCount; row++)
                {
                    valid += column.IsValid(row) ? 1 : 0;
                }
            }
        }

        return (rows, valid);
    }

    [Fact]
    public async Task A_read_allocates_nothing_the_size_of_its_segment()
    {
        ISegmentReader source = new MemorySegmentSource(Placed(Pattern(1 << 20), 8));
        using (await source.ReadAsync(Spec(0, 1 << 20, alignmentExponent: 6), CancellationToken.None))
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using (await source.ReadAsync(Spec(0, 1 << 20, alignmentExponent: 6), CancellationToken.None))
        {
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1_024);
    }

    [Fact]
    public async Task A_populated_set_is_left_alone()
    {
        // The early return the other three sources have: a second pass over a finished set is a
        // no-op, not a re-read, and not a `SetResult` onto a slot that already holds an owner.
        ISegmentReader source = new MemorySegmentSource(Pattern(256));
        using SegmentRequestSet set = new SegmentRequestSet();
        int slot = set.Add(Spec(0, 32));

        await source.ReadManyAsync(set, CancellationToken.None);
        Assert.True(set.IsPopulated);
        SegmentOwner owner = set.GetOwner(slot);

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Same(owner, set.GetOwner(slot));
        Assert.Equal(1, owner.RefCount);
    }

    /// <summary><paramref name="data"/> copied into pinned memory, <paramref name="past"/> bytes past a 64-byte boundary.</summary>
    private static unsafe ReadOnlyMemory<byte> Placed(byte[] data, int past)
    {
        byte[] array = GC.AllocateArray<byte>(data.Length + 128, pinned: true);
        nuint at;
        fixed (byte* first = array)
        {
            at = (nuint)first;
        }

        int start = (int)((64 - (at % 64)) % 64) + past;
        data.CopyTo(array.AsSpan(start));
        return array.AsMemory(start, data.Length);
    }

    private static unsafe nuint StartOf(ReadOnlySpan<byte> span)
    {
        fixed (byte* at = span)
        {
            return (nuint)at;
        }
    }
}
