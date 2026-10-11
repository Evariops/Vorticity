// A coalesced batch issues its reads together, so one of them failing
// leaves the others running. Each writes into a block the batch rented, and a block given back while
// a read still writes into it is memory another reader then holds.
using System;
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
        // in flight.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        Assert.Equal(PutOutcome.Created, await inner.PutIfAbsentAsync(Key, new byte[8_192], ct));
        await using Held held = new Held(inner);
        await using ObjectStoreSource source = new ObjectStoreSource(
            held, Key, 8_192, new SegmentReadOptions { CoalesceGapBytes = 0 });
        using SegmentRequestSet set = new SegmentRequestSet();
        set.Add(new SegmentSpec(0, 64, 0, 0, 0));
        set.Add(new SegmentSpec(4_096, 64, 0, 0, 0));

        Task read = source.ReadManyAsync(set, CancellationToken.None).AsTask();

        // The batch cannot finish until the held read lands, which is the point: it waits for it
        // rather than giving back the block the read is about to write into.
        await held.Waiting.Task.WaitAsync(ct);
        await Task.Delay(20, ct);
        Assert.False(read.IsCompleted);
        held.Release();
        await Assert.ThrowsAsync<IOException>(async () => await read);

        Assert.True(held.Landed);
        Assert.False(set.IsPopulated);
        Assert.False(set.IsFilled(0));
        Assert.False(set.IsFilled(1));
    }

    /// <summary>A store whose first read fails and whose second waits to be let go.</summary>
    private sealed class Held(IObjectStore inner) : ForwardingStore(inner)
    {
        private readonly TaskCompletionSource _gate =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        /// <summary>Set once the second read waits.</summary>
        internal TaskCompletionSource Waiting { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the second read wrote its bytes.</summary>
        internal bool Landed { get; private set; }

        internal void Release() => _gate.TrySetResult();

        public override async ValueTask<ObjectRead> ReadAsync(
            string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new IOException("the store refused this range");
            }

            Waiting.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            ObjectRead read = await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken).ConfigureAwait(false);
            Landed = true;
            return read;
        }
    }
}
