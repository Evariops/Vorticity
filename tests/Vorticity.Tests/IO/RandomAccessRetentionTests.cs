using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// A coalesced run delivered as views keeps its whole buffer alive for the batch's lifetime.
/// That is the right trade when the run is mostly useful bytes and a memory-amplification bug
/// when it is not, because runs are disjoint: a split of tiny segments spread just under the gap
/// budget apart would otherwise pin one run buffer per gap and hold the entire file to deliver a
/// few hundred bytes. These tests pin which path each shape takes.
/// </summary>
public sealed class RandomAccessRetentionTests
{
    private const int FileLength = 4 * 1024 * 1024;

    [Fact]
    public async Task A_dense_run_is_delivered_as_zero_copy_views()
    {
        using TempFile file = new TempFile(Pattern(FileLength));
        await using FileSegmentSource source = FileSegmentSource.Open(file.Path_);
        using SegmentRequestSet set = new SegmentRequestSet();

        // 16 contiguous 64 KiB segments: one 1 MiB run, 100% useful.
        int[] slots = new int[16];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = set.Add(Spec((ulong)(i * 65536), 65536, 6));
        }

        await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(1, source.SlicedRunCount);
        Assert.Equal(0, source.CopiedRunCount);
        Assert.True(Pattern(FileLength).AsSpan(0, 65536).SequenceEqual(set.GetBuffer(slots[0]).Span));
    }

    [Fact]
    public async Task A_sparse_run_is_copied_out_so_the_run_buffer_can_be_given_back()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        await using FileSegmentSource source = FileSegmentSource.Open(file.Path_);
        using SegmentRequestSet set = new SegmentRequestSet();

        // Eight 8-byte segments spread half a MiB apart: one ~3.5 MiB run carrying 64 useful
        // bytes. Slicing it would pin 3.5 MiB for the life of the batch.
        int[] slots = new int[8];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = set.Add(Spec((ulong)(i * 512 * 1024), 8, 3));
        }

        await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(0, source.SlicedRunCount);
        Assert.Equal(1, source.CopiedRunCount);

        for (int i = 0; i < slots.Length; i++)
        {
            Assert.True(
                content.AsSpan(i * 512 * 1024, 8).SequenceEqual(set.GetBuffer(slots[i]).Span),
                $"segment {i}");
            Assert.Equal(3, set.GetBuffer(slots[i]).AlignmentExponent);
            Assert.Equal((nuint)0, AddressOf(set.GetBuffer(slots[i])) % 8);
        }
    }

    [Fact]
    public async Task A_small_run_is_never_copied_however_sparse_it_is()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        await using FileSegmentSource source = FileSegmentSource.Open(file.Path_);
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(0, 8, 3));
        int b = set.Add(Spec(32768, 8, 3));

        await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(1, source.SlicedRunCount);
        Assert.Equal(0, source.CopiedRunCount);
        Assert.True(content.AsSpan(0, 8).SequenceEqual(set.GetBuffer(a).Span));
        Assert.True(content.AsSpan(32768, 8).SequenceEqual(set.GetBuffer(b).Span));
    }

    [Fact]
    public async Task Both_publish_paths_survive_a_release_and_a_second_read()
    {
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        await using FileSegmentSource source = FileSegmentSource.Open(file.Path_);
        using SegmentRequestSet set = new SegmentRequestSet();

        for (int round = 0; round < 3; round++)
        {
            int sparse = set.Add(Spec(0, 8, 3));
            int alsoSparse = set.Add(Spec(2 * 1024 * 1024, 8, 3));
            int dense = set.Add(Spec(3 * 1024 * 1024, 512 * 1024, 6));

            await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);

            Assert.True(content.AsSpan(0, 8).SequenceEqual(set.GetBuffer(sparse).Span));
            Assert.True(content.AsSpan(2 * 1024 * 1024, 8).SequenceEqual(set.GetBuffer(alsoSparse).Span));
            Assert.True(
                content.AsSpan(3 * 1024 * 1024, 512 * 1024).SequenceEqual(set.GetBuffer(dense).Span));

            set.Release();
        }
    }
}
