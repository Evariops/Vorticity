// A coalesced batch issues its reads together (docs/13-dataset.md §11), so one of them failing
// leaves the others running. Each carries an ObjectRange holding a pooled array, and a range
// nobody disposes is a buffer that reaches the finalizer instead of the pool.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectSegmentSourceFailureTests
{
    private const string Key = "data/one.vortex";

    [Fact]
    public async Task A_failing_read_does_not_abandon_the_ones_still_running()
    {
        // Two runs, because the gap is wider than the coalescer is allowed to bridge. The first
        // fails at once; the second is held open, so at the moment the batch gives up it is still
        // in flight -- the case that used to be skipped outright.
        using Held held = new Held();
        ObjectSegmentSource source = new ObjectSegmentSource(
            held, Key, new SegmentReadOptions { CoalesceGapBytes = 0 });
        using SegmentRequestSet set = new SegmentRequestSet();
        set.Add(new SegmentSpec(0, 64, 0, 0, 0));
        set.Add(new SegmentSpec(4_096, 64, 0, 0, 0));

        Task read = source.ReadManyAsync(set, CancellationToken.None).AsTask();

        // The batch cannot finish until the held read lands, which is the point: it waits for it
        // rather than walking away from the buffer it is about to produce.
        held.Release();
        await Assert.ThrowsAsync<IOException>(async () => await read);

        Assert.NotNull(held.Handed);
        Assert.Throws<ObjectDisposedException>(() => held.Handed!.Bytes);
        Assert.False(set.IsPopulated);
        Assert.False(set.IsFilled(0));
        Assert.False(set.IsFilled(1));
    }

    /// <summary>A store whose first range fails and whose second waits to be let go.</summary>
    private sealed class Held : IObjectStore, IDisposable
    {
        private readonly TaskCompletionSource _gate =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        /// <summary>The range the second read produced, so the test can see it was disposed.</summary>
        internal ObjectRange? Handed { get; private set; }

        internal void Release() => _gate.TrySetResult();

        public async ValueTask<ObjectRange> GetRangeAsync(
            string key, long offset, int length, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new IOException("the store refused this range");
            }

            await _gate.Task.ConfigureAwait(false);
            Handed = ObjectRange.CopyOf(new byte[length], "token");
            return Handed;
        }

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            new ValueTask<ObjectHead?>(new ObjectHead(1 << 20, "token", DateTimeOffset.UnixEpoch));

        public ValueTask<PutOutcome> PutIfAbsentAsync(
            string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            new ValueTask<PutOutcome>(PutOutcome.Created);

        public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            new ValueTask<bool>(false);

        public ValueTask<IReadOnlyList<string>> ListAsync(
            string prefix, string? startAfter, int max, CancellationToken cancellationToken) =>
            new ValueTask<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() => Handed?.Dispose();
    }
}
