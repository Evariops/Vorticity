using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Columns;

/// <summary>
/// An owned batch's blocks: given back to the library's pool when the batch is disposed, and taken by the collector with a
/// batch nobody disposes. Run alone: the count is the pool's watch, which every rent of the process tells.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class OwnedBatchBlocksTests
{
    [Fact]
    public void AnOwnedBatchGivesItsBlocksBackWhenDisposedAndTheCollectorTakesOneDropped()
    {
        using ColumnFixture f = new ColumnFixture();
        int node = f.Int32Node(new int[100_000], Validity.NonNullable);

        Counting watch = new Counting();
        BlockPool.Watch = watch;
        try
        {
            RecordBatch owned = RecordBatch.Own(f.Arena, node, 0, null, VortexSession.Default, default, 0);
            owned.Dispose();
        }
        finally
        {
            BlockPool.Watch = null;
        }

        Assert.InRange(Interlocked.Read(ref watch.Given), 400_000L, long.MaxValue);

        // A batch nobody disposes is garbage like any other object: its blocks are managed arrays, which the collector takes
        // back with it.
        WeakReference dropped = DropAnOwnedBatch(f.Arena, node);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        Assert.False(dropped.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropAnOwnedBatch(CanonicalArena source, int node) =>
        new WeakReference(RecordBatch.Own(source, node, 0, null, VortexSession.Default, default, 0));

    /// <summary>The bytes of the blocks given back while it watches.</summary>
    private sealed class Counting : BlockPool.IBlockWatch
    {
        internal long Given;

        public void Rented(BlockOwner owner)
        {
        }

        public void Released(BlockOwner owner) => Interlocked.Add(ref Given, owner.Capacity);
    }
}
