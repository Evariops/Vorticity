// The write path must never hand a pooled native block to the finalizer thread.
//
// WHY THIS FILE EXISTS. `AlignedBufferPool` blocks are recycled by `CanonicalArena.Reset`, and
// `~NativeSegmentOwner` exists only as a leak backstop for a caller that dropped one on the floor
// (SegmentOwnerTests documents both halves). Two write-path owners were dropping every block they
// rented:
//
//   * `ZoneMapWriter.TryBuild` built one `CanonicalArena` PER COLUMN and never reset it;
//   * `VortexFileWriter` never disposed the one or two transit `ScanContext`s it creates.
//
// Neither is a correctness bug -- the backstop frees the memory -- and neither shows up in a
// round-trip test, in a byte comparison, or in `WriteAllocationTests`, because the leaked blocks
// are NATIVE and that file counts MANAGED bytes. What it cost was a write profile with 38% of its
// samples under `GC.RunFinalizers`, and a pool that stayed empty because nothing was ever returned
// to it, so every chunk of every column re-entered `NativeMemory.AlignedAlloc`.
//
// WHAT THIS ASSERTS, and why it is a count and not a timing. The finalizer counter is exact,
// process-wide and monotonic, so "writing a file with many column chunks finalized nothing" is a
// statement that either holds or does not - unlike a profile share, which needs a quiet machine
// and a large enough file to be readable at all. `A_disposed_block_is_never_finalized` in
// SegmentOwnerTests makes the same assertion about one block; this makes it about a whole write.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A write returns every pooled block it rents, rather than leaking it to the finalizer.</summary>
/// <remarks>
/// Shares <see cref="AllocationCollection"/> with the other measuring write tests: the counter is
/// process-wide, so a neighbouring class dropping owners of its own would be charged here.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class WriteFinalizerTests
{
    /// <summary>
    /// Many zones over several columns, which is the shape that exercises both leaks: one
    /// `ZoneMapWriter` arena per column at completion, and a transit arena that actually splits.
    /// </summary>
    private const string Entry = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task WritingFinalizesNoPooledBlock()
    {
        Decoders.EnsureRegistered();

        string path = Corpus.Path(Entry);
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        // Warm up OUTSIDE the measurement. The first write of a process populates the encoding
        // registry and the pool's buckets, and anything it drops would otherwise be charged to the
        // run under test.
        await Rewrite(path);
        Collect();

        long before = NativeSegmentOwner.FinalizedBlockCount;

        await Rewrite(path);

        // Collect twice with a WaitForPendingFinalizers between: the first collection queues the
        // unreachable owners, the wait runs them, the second reclaims. One round would let a block
        // that IS leaking go uncounted and pass the test.
        Collect();

        Assert.True(
            NativeSegmentOwner.FinalizedBlockCount == before,
            $"writing {Entry} sent {NativeSegmentOwner.FinalizedBlockCount - before} pooled " +
            "block(s) through ~NativeSegmentOwner; every arena the write creates must be reset " +
            "so its blocks return to AlignedBufferPool.Shared.");
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Reads every batch of a file and writes it back through a discarding sink.</summary>
    private static async Task Rewrite(string path)
    {
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(new NullSink(), source.DType);
        await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    /// <summary>A sink that keeps nothing: the writer is what is under test, not a stream.</summary>
    private sealed class NullSink : ISegmentSink
    {
        public long Position { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
