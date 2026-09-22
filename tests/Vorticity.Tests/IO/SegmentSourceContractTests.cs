using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// One suite, three implementations. Everything asserted here is a rule of
/// the segment source contract rather than a property of any one source, and the reference
/// <see cref="HttpRangeSegmentSource"/> is held to exactly the same bar as the two built-ins.
/// </summary>
public sealed class SegmentSourceContractTests
{
    private const int FileLength = 16384;

    private static byte[] Content() => Pattern(FileLength);

    // ---- reading one segment ------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task GetLengthAsync_reports_the_file_length(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        Assert.Equal(FileLength, await harness.Source.GetLengthAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped, 0)]
    [InlineData(SegmentSourceKind.MemoryMapped, 1)]
    [InlineData(SegmentSourceKind.MemoryMapped, 1023)]
    [InlineData(SegmentSourceKind.MemoryMapped, 1024)]
    [InlineData(SegmentSourceKind.MemoryMapped, 1025)]
    [InlineData(SegmentSourceKind.MemoryMapped, 8191)]
    [InlineData(SegmentSourceKind.MemoryMapped, 8192)]
    [InlineData(SegmentSourceKind.MemoryMapped, 8193)]
    [InlineData(SegmentSourceKind.RandomAccess, 0)]
    [InlineData(SegmentSourceKind.RandomAccess, 1)]
    [InlineData(SegmentSourceKind.RandomAccess, 1023)]
    [InlineData(SegmentSourceKind.RandomAccess, 1024)]
    [InlineData(SegmentSourceKind.RandomAccess, 1025)]
    [InlineData(SegmentSourceKind.RandomAccess, 8191)]
    [InlineData(SegmentSourceKind.RandomAccess, 8192)]
    [InlineData(SegmentSourceKind.RandomAccess, 8193)]
    [InlineData(SegmentSourceKind.HttpRange, 0)]
    [InlineData(SegmentSourceKind.HttpRange, 1)]
    [InlineData(SegmentSourceKind.HttpRange, 1023)]
    [InlineData(SegmentSourceKind.HttpRange, 1024)]
    [InlineData(SegmentSourceKind.HttpRange, 1025)]
    [InlineData(SegmentSourceKind.HttpRange, 8191)]
    [InlineData(SegmentSourceKind.HttpRange, 8192)]
    [InlineData(SegmentSourceKind.HttpRange, 8193)]
    public async Task A_segment_reads_back_byte_for_byte_at_the_boundary_lengths(
        SegmentSourceKind kind, int length)
    {
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);

        SegmentOwner owner = await harness.Source.ReadAsync(Spec(64, (uint)length, 6), CancellationToken.None);

        try
        {
            Assert.Equal(length, owner.Buffer.Length);
            Assert.True(content.AsSpan(64, length).SequenceEqual(owner.Buffer.Span));
        }
        finally
        {
            owner.Release();
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_zero_length_segment_reads_as_the_empty_buffer(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        SegmentOwner owner = await harness.Source.ReadAsync(Spec(4096, 0, 6), CancellationToken.None);

        Assert.True(owner.Buffer.IsEmpty);
        owner.Release();
    }

    // ---- reading a whole split ------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_split_reads_back_byte_for_byte(SegmentSourceKind kind)
    {
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);
        using SegmentRequestSet set = new SegmentRequestSet();

        SegmentSpec[] specs =
        [
            Spec(0, 64, 6), Spec(64, 8, 3), Spec(200, 1, 0), Spec(4096, 4096, 6),
            Spec(12288, 0, 6), Spec(12288, 100, 2),
        ];

        int[] slots = new int[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            slots[i] = set.Add(specs[i]);
        }

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);

        for (int i = 0; i < specs.Length; i++)
        {
            VortexBuffer buffer = set.GetBuffer(slots[i]);
            Assert.Equal((int)specs[i].Length, buffer.Length);
            Assert.True(
                content.AsSpan((int)specs[i].Offset, (int)specs[i].Length).SequenceEqual(buffer.Span),
                $"segment {i} at {specs[i].Offset}");
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Every_buffer_satisfies_its_own_declared_alignment(SegmentSourceKind kind)
    {
        // Offsets {64, 65, 127, 128, 4096} with exponents {6, 0, 0, 6, 4}, asserting every
        // buffer's base address satisfies its own alignment: the coalescing-versus-alignment
        // proof, end to end.
        SegmentSpec[] specs =
        [
            Spec(64, 8, 6), Spec(65, 3, 0), Spec(127, 1, 0), Spec(128, 64, 6), Spec(4096, 32, 4),
        ];

        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        int[] slots = new int[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            slots[i] = set.Add(specs[i]);
        }

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        for (int i = 0; i < specs.Length; i++)
        {
            VortexBuffer buffer = set.GetBuffer(slots[i]);
            nuint address = AddressOf(buffer);
            int alignment = 1 << specs[i].AlignmentExponent;

            Assert.True(
                address % (nuint)alignment == 0,
                $"segment {i} at file offset {specs[i].Offset} needs {alignment}-byte alignment " +
                $"but landed at 0x{address:x}");
            Assert.True(buffer.IsAligned, $"segment {i} reports itself misaligned");
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_run_whose_first_offset_is_not_64_aligned_still_aligns_its_members(
        SegmentSourceKind kind)
    {
        // The list above happens to start at 64. This one does not, so the tempting
        // simplification `Start = specs[0].Offset` would put the 64-aligned segment at 128 onto
        // a byte-63 boundary.
        SegmentSpec[] specs = [Spec(65, 1, 0), Spec(128, 64, 6), Spec(256, 16, 4)];

        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        int[] slots = new int[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            slots[i] = set.Add(specs[i]);
        }

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        for (int i = 0; i < specs.Length; i++)
        {
            nuint address = AddressOf(set.GetBuffer(slots[i]));
            Assert.Equal((nuint)0, address % (nuint)(1 << specs[i].AlignmentExponent));
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Segments_registered_out_of_order_still_read_correctly(SegmentSourceKind kind)
    {
        // A layout tree does not walk the file in offset order, so the set never arrives sorted.
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);
        using SegmentRequestSet set = new SegmentRequestSet();

        SegmentSpec[] specs = [Spec(8192, 64, 6), Spec(0, 64, 6), Spec(4096, 64, 6), Spec(128, 64, 6)];
        int[] slots = new int[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            slots[i] = set.Add(specs[i]);
        }

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        for (int i = 0; i < specs.Length; i++)
        {
            Assert.True(content.AsSpan((int)specs[i].Offset, 64)
                .SequenceEqual(set.GetBuffer(slots[i]).Span));
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task An_empty_set_completes(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_set_of_only_zero_length_segments_completes_without_reading(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(0, 0, 6));
        int b = set.Add(Spec(4096, 0, 6));

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
        Assert.True(set.GetBuffer(a).IsEmpty);
        Assert.True(set.GetBuffer(b).IsEmpty);
        Assert.Equal(0, harness.Transport?.RequestCount ?? 0);
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Reading_an_already_populated_set_leaves_it_untouched(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        int slot = set.Add(Spec(0, 64, 6));
        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        SegmentOwner owner = set.GetOwner(slot);
        int before = harness.Transport?.RequestCount ?? 0;

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.Same(owner, set.GetOwner(slot));
        Assert.Equal(before, harness.Transport?.RequestCount ?? 0);
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_released_set_can_be_registered_and_read_again(SegmentSourceKind kind)
    {
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);
        using SegmentRequestSet set = new SegmentRequestSet();

        set.Add(Spec(0, 64, 6));
        await harness.Source.ReadManyAsync(set, CancellationToken.None);
        set.Release();

        int slot = set.Add(Spec(8192, 128, 6));
        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(content.AsSpan(8192, 128).SequenceEqual(set.GetBuffer(slot).Span));
    }

    // ---- deduplication --------------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_duplicated_segment_is_read_once(SegmentSourceKind kind)
    {
        // A vortex.dict layout points several children at one values segment.
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(
            kind, content, new SegmentReadOptions { CoalesceGapBytes = 0 });
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(4096, 256, 6));
        int b = set.Add(Spec(4096, 256, 6));
        int c = set.Add(Spec(4096, 256, 6));

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(1, set.Count);

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(content.AsSpan(4096, 256).SequenceEqual(set.GetBuffer(a).Span));

        // One registered segment means one range request, whatever the layout asked for.
        Assert.Equal(1, harness.Transport?.RequestCount ?? 1);
    }

    // ---- malformed and out-of-range requests -----------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_segment_past_the_end_of_the_file_is_a_format_error(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadAsync(
                Spec(FileLength - 8, 16, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_split_containing_a_segment_past_the_end_fails_whole(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using SegmentRequestSet set = new SegmentRequestSet();

        int good = set.Add(Spec(0, 64, 6));
        set.Add(Spec(FileLength - 8, 4096, 3));

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadManyAsync(set, CancellationToken.None));

        Assert.False(set.IsPopulated);
        Assert.False(set.IsFilled(good));
        Assert.Throws<InvalidOperationException>(() => set.GetBuffer(good));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task An_alignment_exponent_above_the_cap_is_a_format_error(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadAsync(Spec(0, 8, 7), CancellationToken.None));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_range_that_overflows_u64_is_a_format_error(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadAsync(
                Spec(ulong.MaxValue, 16), CancellationToken.None));
    }

    // ---- arbitrary ranges (the open path) --------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task ReadRangeAsync_returns_the_tail_and_clamps_to_the_file(SegmentSourceKind kind)
    {
        byte[] content = Content();
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);

        // The open path asks for 64 KiB of a 16 KiB file, from an offset aligned to nothing.
        const long start = 3;
        SegmentOwner owner = await harness.Source.ReadRangeAsync(
            start, 65536, 8, CancellationToken.None);

        try
        {
            Assert.Equal(FileLength - (int)start, owner.Buffer.Length);
            Assert.True(content.AsSpan((int)start).SequenceEqual(owner.Buffer.Span));
            Assert.Equal((nuint)0, AddressOf(owner.Buffer) % 8);
        }
        finally
        {
            owner.Release();
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task ReadRangeAsync_honours_the_requested_alignment_at_an_odd_offset(
        SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        SegmentOwner owner = await harness.Source.ReadRangeAsync(1, 128, 64, CancellationToken.None);

        try
        {
            Assert.Equal((nuint)0, AddressOf(owner.Buffer) % 64);
            Assert.True(Pattern(FileLength).AsSpan(1, 128).SequenceEqual(owner.Buffer.Span));
        }
        finally
        {
            owner.Release();
        }
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task ReadRangeAsync_rejects_a_start_past_the_end(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadRangeAsync(
                FileLength + 1, 16, 1, CancellationToken.None));

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadRangeAsync(
                FileLength, 16, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task ReadRangeAsync_rejects_caller_errors_as_argument_exceptions(
        SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await harness.Source.ReadRangeAsync(-1, 16, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await harness.Source.ReadRangeAsync(0, -1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await harness.Source.ReadRangeAsync(0, 16, 3, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await harness.Source.ReadRangeAsync(0, 16, 128, CancellationToken.None));
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task A_zero_length_range_at_the_very_end_is_legal(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        SegmentOwner owner = await harness.Source.ReadRangeAsync(
            FileLength, 0, 1, CancellationToken.None);

        Assert.True(owner.Buffer.IsEmpty);
        owner.Release();
    }

    // ---- cancellation ------------------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task An_already_cancelled_token_stops_every_entry_point(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());
        using CancellationTokenSource cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using SegmentRequestSet set = new SegmentRequestSet();
        set.Add(Spec(0, 64, 6));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Source.GetLengthAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Source.ReadAsync(Spec(0, 64, 6), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Source.ReadManyAsync(set, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Source.ReadRangeAsync(0, 64, 1, cts.Token));

        Assert.False(set.IsPopulated);
        Assert.False(set.IsFilled(0));
    }

    // ---- disposal -------------------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Disposing_twice_is_safe_and_further_reads_are_refused(SegmentSourceKind kind)
    {
        SegmentSourceHarness harness = new SegmentSourceHarness(kind, Content());

        await harness.Source.DisposeAsync();
        await harness.Source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await harness.Source.ReadAsync(Spec(0, 8), CancellationToken.None));

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task A_mapping_outlives_the_source_while_a_batch_still_holds_a_slice()
    {
        // Returned buffers are owned by the source until the batch that requested them is
        // disposed, so disposing the source early must not unmap.
        byte[] content = Content();
        using TempFile file = new TempFile(content);

        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(file.Path_);
        using SegmentRequestSet set = new SegmentRequestSet();
        int slot = set.Add(Spec(1024, 512, 6));

        await ((ISegmentReader)source).ReadManyAsync(set, CancellationToken.None);
        await source.DisposeAsync();

        Assert.True(content.AsSpan(1024, 512).SequenceEqual(set.GetBuffer(slot).Span));
    }

    // ---- empty files ------------------------------------------------------------------------------

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task An_empty_file_reports_zero_and_refuses_every_non_empty_read(SegmentSourceKind kind)
    {
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, []);

        Assert.Equal(0, await harness.Source.GetLengthAsync(CancellationToken.None));

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await harness.Source.ReadAsync(Spec(0, 1), CancellationToken.None));

        SegmentOwner empty = await harness.Source.ReadAsync(Spec(0, 0), CancellationToken.None);
        Assert.True(empty.Buffer.IsEmpty);
        empty.Release();
    }
}
