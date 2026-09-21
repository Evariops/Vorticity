// A VortexFile is thread-safe, concurrent scans on one open file are supported and expected, and
// decoders are pure functions over borrowed memory with no shared mutable state. A DTypeArena is
// neither thread-safe nor immutable - asking it for a node it does not hold grows three arrays,
// bumps a counter and may rehash the dedup table, all unsynchronized - so the promise holds only
// while NOTHING on a scan path derives into the FILE's arena.
//
// `DType.WithNullability` is the trap: on a non-leaf node it calls DTypeArena.CloneWithNullability,
// which writes to the arena the handle came from. Layout nodes carry the file schema's own dtypes
// verbatim (LayoutParser stores `record.DType = dtype`), so `nodeDType.WithNullability(...)` on a
// decode or parse path is a write to state every concurrent scan shares.
//
// A thread race is a flaky test. This asserts the invariant that makes the race impossible instead:
// after a scan is planned and fully drained, the file's arena holds exactly the nodes it held at
// open. The fixtures below cover the two places a derivation can hide: decode time (masked) and
// parse time (zoned).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanArenaIsolationTests
{
    // encodings/masked.vortex: root dtype i32?, one vortex.flat layout, a vortex.masked array node.
    // MaskedDecoder flips that i32? to i32 once per masked flow, and does it in the flow's own
    // arena, never in the file's.
    [Theory]
    [InlineData("encodings/masked")]
    [InlineData("distributions/float_specials_f32_r8193")]
    [InlineData("distributions/high_cardinality_i64_r8193")]
    public async Task ScanningNeverDerivesIntoTheFileArena(string entry)
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);

        int atOpen = file.Types.NodeCount;

        // Planning: LayoutTree.Parse runs per scan, and the zone-map schema is derived there.
        IAsyncEnumerable<RecordBatch> batches = file.Scan().WithMaxBatchRows(1024).ExecuteAsync();
        int afterPlan = file.Types.NodeCount;

        long rows = 0;
        await foreach (RecordBatch batch in batches)
        {
            rows += batch.RowCount;
            batch.Dispose();
        }

        int afterDrain = file.Types.NodeCount;

        Assert.True(rows > 0, "the fixture must actually produce rows");
        Assert.Equal(atOpen, afterPlan);
        Assert.Equal(atOpen, afterDrain);
    }

    // The same invariant under the shape that would actually corrupt the arena: several flows
    // decoding the same masked node at once. With the derivation kept in each flow's own arena this
    // is deterministic; without it, two threads read the same _nodeCount and one write is lost.
    [Fact]
    public async Task ConcurrentScansOfAMaskedFileLeaveTheFileArenaAlone()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(
            Corpus.Path("encodings/masked"), CancellationToken.None);

        int atOpen = file.Types.NodeCount;

        Task<long>[] flows = new Task<long>[8];
        for (int i = 0; i < flows.Length; i++)
        {
            flows[i] = Task.Run(async () =>
            {
                long rows = 0;
                await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(512).ExecuteAsync())
                {
                    rows += batch.RowCount;
                    batch.Dispose();
                }

                return rows;
            });
        }

        long[] counts = await Task.WhenAll(flows);
        for (int i = 1; i < counts.Length; i++)
        {
            Assert.Equal(counts[0], counts[i]);
        }

        Assert.Equal(atOpen, file.Types.NodeCount);
    }
}
