using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// "Implementations MUST be thread-safe" (docs/09-contracts.md §1): concurrent splits read through
/// one source. Nothing here depends on an interleaving, only on every reader getting its own
/// correct bytes whatever the interleaving turns out to be.
/// </summary>
public sealed class SegmentSourceConcurrencyTests
{
    private const int FileLength = 1 << 18;
    private const int Readers = 8;
    private const int Rounds = 12;

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Concurrent_splits_each_get_their_own_correct_bytes(SegmentSourceKind kind)
    {
        byte[] content = Pattern(FileLength);
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);

        Task[] readers = new Task[Readers];
        for (int r = 0; r < Readers; r++)
        {
            int reader = r;
            readers[r] = Task.Run(async () =>
            {
                // One request set per flow: the set is affine to its consumer, the source is not.
                using SegmentRequestSet set = new SegmentRequestSet();

                for (int round = 0; round < Rounds; round++)
                {
                    int[] slots = new int[6];
                    int[] offsets = new int[slots.Length];

                    for (int i = 0; i < slots.Length; i++)
                    {
                        // Deterministic per (reader, round, i): no shared RNG, no clock.
                        int offset = ((reader * 4099) + (round * 1031) + (i * 257)) % (FileLength - 512);
                        offset &= ~7;
                        offsets[i] = offset;
                        slots[i] = set.Add(Spec((ulong)offset, 128, 3));
                    }

                    await harness.Source.ReadManyAsync(set, CancellationToken.None);

                    for (int i = 0; i < slots.Length; i++)
                    {
                        VortexBuffer buffer = set.GetBuffer(slots[i]);
                        Assert.True(
                            content.AsSpan(offsets[i], 128).SequenceEqual(buffer.Span),
                            $"reader {reader} round {round} segment {i} at {offsets[i]}");
                    }

                    set.Release();
                }
            });
        }

        await Task.WhenAll(readers);
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Concurrent_single_segment_reads_are_safe(SegmentSourceKind kind)
    {
        byte[] content = Pattern(FileLength);
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);

        Task[] readers = new Task[Readers];
        for (int r = 0; r < Readers; r++)
        {
            int reader = r;
            readers[r] = Task.Run(async () =>
            {
                for (int round = 0; round < Rounds; round++)
                {
                    int offset = (((reader * 8191) + (round * 4093)) % (FileLength - 1024)) & ~63;
                    SegmentOwner owner = await harness.Source.ReadAsync(
                        Spec((ulong)offset, 256, 6), CancellationToken.None);

                    try
                    {
                        Assert.True(content.AsSpan(offset, 256).SequenceEqual(owner.Buffer.Span));
                    }
                    finally
                    {
                        owner.Release();
                    }
                }
            });
        }

        await Task.WhenAll(readers);
    }

    [Theory]
    [InlineData(SegmentSourceKind.MemoryMapped)]
    [InlineData(SegmentSourceKind.RandomAccess)]
    [InlineData(SegmentSourceKind.HttpRange)]
    public async Task Overlapping_segments_each_get_their_own_correct_window(SegmentSourceKind kind)
    {
        byte[] content = Pattern(FileLength);
        await using SegmentSourceHarness harness = new SegmentSourceHarness(kind, content);
        using SegmentRequestSet set = new SegmentRequestSet();

        // Distinct specs that overlap: not what a writer emits, but nothing rejects it and the
        // planner's `end = max(end, nextEnd)` has to be right for both of them.
        int a = set.Add(Spec(1024, 512, 6));
        int b = set.Add(Spec(1280, 512, 6));
        int c = set.Add(Spec(1024, 1024, 6));

        Assert.Equal(3, set.Count);

        await harness.Source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(content.AsSpan(1024, 512).SequenceEqual(set.GetBuffer(a).Span));
        Assert.True(content.AsSpan(1280, 512).SequenceEqual(set.GetBuffer(b).Span));
        Assert.True(content.AsSpan(1024, 1024).SequenceEqual(set.GetBuffer(c).Span));
    }
}
