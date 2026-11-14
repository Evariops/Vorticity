// The write path must give back every block it rents.
//
// WHY THIS FILE EXISTS. `AlignedBufferPool` blocks are recycled by `CanonicalArena.Reset`. Two write-path owners once
// dropped every block they rented:
//
//   * `ZoneMapWriter.TryBuild` built one `CanonicalArena` PER COLUMN and never reset it;
//   * `VortexFileWriter` never disposed the one or two transit `ScanContext`s it creates.
//
// Neither is a correctness bug, since the collector takes a dropped block back, and neither shows in a round-trip test or
// in a byte comparison. What it cost was a pool that stayed empty because nothing was ever returned to it, so every chunk
// of every column allocated a new block on the pinned heap.
//
// WHAT THIS ASSERTS, and why it is a count and not a timing. A block counts as rented from its rent until it is given
// back, and one dropped stays counted, so "writing a file with many column chunks left nothing rented" is a statement that
// either holds or does not.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A write gives back every pooled block it rents, rather than dropping it to the collector.</summary>
/// <remarks>
/// Shares <see cref="AllocationCollection"/> with the other measuring write tests: the count is process-wide, so a
/// neighbouring class renting blocks of its own would be charged here.
/// </remarks>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class WriteBlockReturnTests
{
    /// <summary>
    /// Many zones over several columns, which is the shape that exercises both leaks: one
    /// `ZoneMapWriter` arena per column at completion, and a transit arena that actually splits.
    /// </summary>
    private const string Entry = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task WritingLeavesNoBlockRented()
    {
        Decoders.EnsureRegistered();

        string path = Corpus.Path(Entry);
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        // Warm up OUTSIDE the measurement: the first write of a process populates the encoding registry and the pool's
        // buckets.
        await Rewrite(path);

        long before = MemoryReport.RentedNow();
        await Rewrite(path);
        long after = MemoryReport.RentedNow();

        Assert.True(
            after == before,
            $"writing {Entry} left {after - before} bytes of blocks rented; every arena the write creates must be reset so "
            + "its blocks return to the pool.");
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
