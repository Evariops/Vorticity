using System;
using System.Linq;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// The lanes of a scan wait for one another and block on the locks they share without allocating:
/// a lock made per scan allocates the first time two threads meet in it or one blocks on it, 40 to
/// 160 bytes, at whatever batch that happens, while one the process lends pays it once. Every
/// thread's allocations are counted, so the class runs alone, and each case takes the floor over a
/// few rounds, a fresh table or set each, as a scan makes them.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class LaneGateAllocationTests : IDisposable
{
    private const int Rounds = 8;

    private readonly Lane _lane = new Lane();

    public void Dispose() => _lane.Dispose();

    [Fact]
    public void ALaneWaitingForAChunkAnotherLaneDecodesAllocatesNothing()
    {
        using ScanContext first = new ScanContext(["vortex.primitive"]);
        using ScanContext second = new ScanContext(["vortex.primitive"]);
        long[] rounds = new long[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            using RetainedChunks table = RetainedChunks.Create(columns: 2, lanes: 2);
            Assert.False(table.TryGet(7, first, batch: 1, out RetainedChunk? claim, out _, out _));
            Action wait = () => table.TryGet(7, second, batch: 1, out _, out _, out _);

            // The second lane asks for the chunk the first is decoding, and waits until it is published.
            long start = GC.GetTotalAllocatedBytes(precise: true);
            _lane.StartAndBlock(wait);
            table.Publish(claim!, nodeIndex: 0, batch: 1);
            _lane.Finish();
            rounds[round] = GC.GetTotalAllocatedBytes(precise: true) - start;
        }

        Assert.True(rounds.Min() == 0, $"a wait allocated at least {rounds.Min()} B: {string.Join(" ", rounds)}");
    }

    [Fact]
    public void ALaneBlockedOnTheRetainedTableAllocatesNothing()
    {
        using ScanContext first = new ScanContext(["vortex.primitive"]);
        using ScanContext second = new ScanContext(["vortex.primitive"]);
        long[] rounds = new long[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            using RetainedChunks table = RetainedChunks.Create(columns: 2, lanes: 2);
            Assert.False(table.TryGet(7, first, batch: 1, out RetainedChunk? claim, out _, out _));
            table.Publish(claim!, nodeIndex: 0, batch: 1);
            Action find = () => table.TryGet(7, second, batch: 1, out _, out _, out _);

            long start = GC.GetTotalAllocatedBytes(precise: true);
            Monitor.Enter(table.Gate);
            _lane.StartAndBlock(find);
            Monitor.Exit(table.Gate);
            _lane.Finish();
            rounds[round] = GC.GetTotalAllocatedBytes(precise: true) - start;
        }

        Assert.True(rounds.Min() == 0, $"a lane blocked on the table allocated at least {rounds.Min()} B: {string.Join(" ", rounds)}");
    }

    [Fact]
    public void ALaneBlockedOnTheSegmentSetAllocatesNothing()
    {
        long[] rounds = new long[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            using ScanSegments set = new ScanSegments(lanes: 2);
            Lock gate = set.SharedGate!;
            Action abandon = () => set.Abandon(1);

            long start = GC.GetTotalAllocatedBytes(precise: true);
            gate.Enter();
            _lane.StartAndBlock(abandon);
            gate.Exit();
            _lane.Finish();
            rounds[round] = GC.GetTotalAllocatedBytes(precise: true) - start;
        }

        Assert.True(rounds.Min() == 0, $"a lane blocked on the segment set allocated at least {rounds.Min()} B: {string.Join(" ", rounds)}");
    }

    [Fact]
    public void TheNextScanGetsTheLockTheLastGaveBack()
    {
        Lock first;
        using (ScanSegments set = new ScanSegments(lanes: 2))
        {
            first = set.SharedGate!;
        }

        using ScanSegments next = new ScanSegments(lanes: 2);
        Assert.True(next.SharedGate == first, "the next scan got another lock than the one given back");

        using ScanSegments alone = new ScanSegments(lanes: 1);
        Assert.True(alone.SharedGate is null, "a set of one lane was lent a lock");
        using RetainedChunks single = RetainedChunks.Create(columns: 2, lanes: 1);
        Assert.Same(single, single.Gate);
    }

    /// <summary>
    /// A thread made once, which runs what it is given when told and is seen blocked before the
    /// test goes on: nothing it does between two rounds allocates.
    /// </summary>
    private sealed class Lane : IDisposable
    {
        private readonly Thread _thread;
        private Action? _work;
        private int _asked;
        private int _done;
        private volatile bool _stopped;

        internal Lane()
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        /// <summary>Runs <paramref name="work"/> on the lane, and returns once the lane is blocked in it.</summary>
        internal void StartAndBlock(Action work)
        {
            Volatile.Write(ref _work, work);
            Interlocked.Increment(ref _asked);
            while ((_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                Thread.SpinWait(20);
            }
        }

        /// <summary>Returns once the lane has run what it was given.</summary>
        internal void Finish()
        {
            while (Volatile.Read(ref _done) < Volatile.Read(ref _asked))
            {
                Thread.SpinWait(20);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            Interlocked.Increment(ref _asked);
            _thread.Join();
        }

        private void Loop()
        {
            for (int round = 1; ; round++)
            {
                while (Volatile.Read(ref _asked) < round)
                {
                    Thread.SpinWait(20);
                }

                if (_stopped)
                {
                    return;
                }

                Volatile.Read(ref _work)!();
                Volatile.Write(ref _done, round);
            }
        }
    }
}
