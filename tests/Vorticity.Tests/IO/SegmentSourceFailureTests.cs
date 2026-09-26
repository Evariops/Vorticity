using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The four points of the segment source contract under adversity: partial failure,
/// ownership, cache consistency and cancellation. Failures are injected rather than waited for.
/// </summary>
public sealed class SegmentSourceFailureTests
{
    // ---- partial failure, observed at the refcount ---------------------------------------------

    /// <summary>
    /// A source that fills slots one at a time and then fails, keeping every owner it created so
    /// the test can prove each one was given back.
    /// </summary>
    private sealed class RecordingSegmentSource : ISegmentReader
    {
        private readonly byte[] _content;
        private readonly int _failAfter;

        internal RecordingSegmentSource(byte[] content, int failAfter)
        {
            _content = content;
            _failAfter = failAfter;
        }

        internal List<SegmentOwner> Handed { get; } = new List<SegmentOwner>();

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            new ValueTask<long>(_content.Length);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            try
            {
                int filled = 0;
                for (int slot = 0; slot < requests.Count; slot++)
                {
                    if (requests.IsFilled(slot))
                    {
                        continue;
                    }

                    if (filled == _failAfter)
                    {
                        throw new IOException("injected transport failure");
                    }

                    SegmentSpec spec = requests.GetSpec(slot);
                    PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(
                        _content.AsSpan((int)spec.Offset, (int)spec.Length),
                        1 << spec.AlignmentExponent);

                    Handed.Add(owner);
                    requests.SetResult(slot, owner);
                    filled++;
                }

                requests.Complete();
            }
            catch
            {
                requests.AbandonPending();
                throw;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_mid_list_failure_releases_every_buffer_the_call_had_already_acquired()
    {
        byte[] content = Pattern(8192);
        RecordingSegmentSource source = new RecordingSegmentSource(content, failAfter: 3);
        using SegmentRequestSet set = new SegmentRequestSet();

        for (int i = 0; i < 6; i++)
        {
            set.Add(Spec((ulong)(i * 128), 64, 6));
        }

        await Assert.ThrowsAsync<IOException>(
            async () => await source.ReadManyAsync(set, CancellationToken.None));

        Assert.Equal(3, source.Handed.Count);
        foreach (SegmentOwner owner in source.Handed)
        {
            Assert.Equal(0, owner.RefCount);
        }

        Assert.False(set.IsPopulated);
        for (int slot = 0; slot < set.Count; slot++)
        {
            Assert.False(set.IsFilled(slot));
            Assert.Throws<InvalidOperationException>(() => set.GetBuffer(slot));
        }
    }

    [Fact]
    public async Task A_retry_after_a_failure_succeeds_on_the_same_set()
    {
        byte[] content = Pattern(8192);
        using SegmentRequestSet set = new SegmentRequestSet();

        int[] slots = new int[4];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = set.Add(Spec((ulong)(i * 128), 64, 6));
        }

        RecordingSegmentSource failing = new RecordingSegmentSource(content, failAfter: 2);
        await Assert.ThrowsAsync<IOException>(
            async () => await failing.ReadManyAsync(set, CancellationToken.None));

        RecordingSegmentSource working = new RecordingSegmentSource(content, failAfter: int.MaxValue);
        await working.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.True(content.AsSpan(i * 128, 64).SequenceEqual(set.GetBuffer(slots[i]).Span));
        }
    }

    // ---- the same, through the reference HTTP source ---------------------------------------------

    private static SegmentReadOptions NoCoalescing => new SegmentReadOptions { CoalesceGapBytes = 0 };

    private static (int[] Slots, SegmentRequestSet Set) FarApartSplit(int count)
    {
        SegmentRequestSet set = new SegmentRequestSet();
        int[] slots = new int[count];
        for (int i = 0; i < count; i++)
        {
            slots[i] = set.Add(Spec((ulong)(i * 4096), 64, 6));
        }

        return (slots, set);
    }

    [Fact]
    public async Task An_injected_transport_failure_leaves_the_set_untouched_and_retryable()
    {
        byte[] content = Pattern(4096 * 5);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content) { LatencyMilliseconds = 1 };
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport, NoCoalescing);

        (int[] slots, SegmentRequestSet set) = FarApartSplit(4);
        using SegmentRequestSet _ = set;

        transport.BeforeRequest = ordinal =>
        {
            if (ordinal == 2)
            {
                throw new IOException("injected 503");
            }
        };

        await Assert.ThrowsAsync<IOException>(
            async () => await source.ReadManyAsync(set, CancellationToken.None));

        Assert.False(set.IsPopulated);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.False(set.IsFilled(slots[i]));
        }

        // Contract point 3: the reader never requires a segment to still be cached, and the source
        // is free to serve the whole thing again.
        transport.BeforeRequest = null;
        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.True(set.IsPopulated);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.True(content.AsSpan(i * 4096, 64).SequenceEqual(set.GetBuffer(slots[i]).Span));
        }
    }

    [Fact]
    public async Task A_cancellation_mid_read_leaves_no_half_populated_slot()
    {
        byte[] content = Pattern(4096 * 5);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content) { LatencyMilliseconds = 1 };
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport, NoCoalescing);

        (int[] slots, SegmentRequestSet set) = FarApartSplit(4);
        using SegmentRequestSet _ = set;

        using CancellationTokenSource cts = new CancellationTokenSource();
        transport.BeforeRequest = ordinal =>
        {
            if (ordinal == 1)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await source.ReadManyAsync(set, cts.Token));

        Assert.False(set.IsPopulated);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.False(set.IsFilled(slots[i]));
            Assert.Throws<InvalidOperationException>(() => set.GetBuffer(slots[i]));
        }
    }

    // ---- coalescing is observable ------------------------------------------------------------------

    [Fact]
    public async Task Nearby_segments_become_one_range_request()
    {
        byte[] content = Pattern(16384);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content);
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport);

        using SegmentRequestSet set = new SegmentRequestSet();
        for (int i = 0; i < 8; i++)
        {
            set.Add(Spec((ulong)(i * 128), 64, 6));
        }

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(1, transport.RequestCount);
        Assert.Equal(8, set.Count);
    }

    [Fact]
    public async Task A_zero_gap_budget_issues_one_request_per_non_adjacent_segment()
    {
        byte[] content = Pattern(16384);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content);
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport, NoCoalescing);

        using SegmentRequestSet set = new SegmentRequestSet();
        for (int i = 0; i < 8; i++)
        {
            set.Add(Spec((ulong)(i * 128), 64, 6));
        }

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(8, transport.RequestCount);
    }

    [Fact]
    public async Task The_size_budget_splits_a_run_that_the_gap_budget_would_have_merged()
    {
        byte[] content = Pattern(16384);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content);

        SegmentReadOptions options = new SegmentReadOptions
        {
            CoalesceGapBytes = 1 << 20,
            MaxCoalescedReadBytes = 4096,
        };

        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport, options);
        using SegmentRequestSet set = new SegmentRequestSet();

        int a = set.Add(Spec(0, 64, 6));
        int b = set.Add(Spec(8192, 64, 6));

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(2, transport.RequestCount);
        Assert.True(content.AsSpan(0, 64).SequenceEqual(set.GetBuffer(a).Span));
        Assert.True(content.AsSpan(8192, 64).SequenceEqual(set.GetBuffer(b).Span));

        // 64 bytes wanted twice, and never the 8 KiB in between.
        Assert.Equal(128, transport.BytesServed);
    }

    [Fact]
    public async Task A_duplicated_segment_costs_one_request_even_across_a_coalesced_run()
    {
        byte[] content = Pattern(16384);
        InMemoryRangeTransport transport = new InMemoryRangeTransport(content);
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport, NoCoalescing);

        using SegmentRequestSet set = new SegmentRequestSet();
        int values = set.Add(Spec(4096, 256, 6));
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(values, set.Add(Spec(4096, 256, 6)));
        }

        await source.ReadManyAsync(set, CancellationToken.None);

        Assert.Equal(1, transport.RequestCount);
        Assert.Equal(256, transport.BytesServed);
    }

    // ---- a file that is shorter than it claims ------------------------------------------------------

    [Fact]
    public async Task A_read_past_a_file_that_shrank_under_us_is_a_format_error()
    {
        // The "read that returns fewer bytes than asked" trap: the file is shorter than its own
        // footer claims. Reached by truncating the file after the source recorded its length,
        // which is the only portable way to force a short positional read.
        byte[] content = Pattern(8192);
        using TempFile file = new TempFile(content);

        SafeFileHandle handle = global::System.IO.File.OpenHandle(
            file.Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);

        await using FileSegmentSource source =
            new FileSegmentSource(handle, ownsHandle: true, SegmentReadOptions.Default);

        Assert.Equal(8192, source.Length);

        using (SafeFileHandle writer = global::System.IO.File.OpenHandle(
            file.Path_, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            RandomAccess.SetLength(writer, 64);
        }

        VortexFormatException error = await Assert.ThrowsAsync<VortexFormatException>(
            async () => await ((ISegmentReader)source).ReadAsync(Spec(4096, 128, 6), CancellationToken.None));

        Assert.Contains("shorter than its own footer", error.Message, StringComparison.Ordinal);
    }

    // ---- a path the source refuses -------------------------------------------------------------------

    [Fact]
    public async Task A_path_that_cannot_be_read_positionally_is_closed_as_it_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a named pipe is not opened by path there");
        string fifo = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using (Process make = Process.Start("mkfifo", fifo))
        {
            await make.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, make.ExitCode);
        }

        try
        {
            // Opening a named pipe waits for its other end.
            Task<FileStream> writer = Task.Factory.StartNew(
                () => new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1),
                TaskCreationOptions.LongRunning);
            Assert.Throws<NotSupportedException>(() => new FileSegmentSource(fifo));

            // With no reader left, a write breaks the pipe.
            using FileStream end = await writer;
            Assert.Throws<IOException>(() => end.Write([1]));
        }
        finally
        {
            global::System.IO.File.Delete(fifo);
        }
    }
}
