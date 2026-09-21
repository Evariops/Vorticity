// WHY THIS FILE EXISTS: a batch's arena is recycled, and a dictionary shared across chunks that
// keeps indices into it reads back PERMUTED -- with correct codes, a correct reader and a correct
// width, so the fault is nowhere but in the writer's data model. It needs no writer at all: the
// invariant it documents belongs to the SCAN, and the writer merely consumes it.
//
// THE INVARIANT. `ColumnCompressor.Dictionary` represents a dictionary entry as the ROW INDEX of
// its first occurrence - `firstOccurrences[code] = row` - and gathers the entries by walking that
// list in code order. Correct, and cheap, and valid for exactly as long as the arena those indices
// address still holds the rows they were taken from. A dictionary shared ACROSS chunks outlives
// that: `BatchAsyncEnumerable` resets its arena per batch and refills it, and the corpus is written
// by reading reference files and writing them back. So at `CompleteAsync` the shared entries are
// gathered from whatever batch happens to be resident, and every code names the wrong row.
//
// WHAT THIS COSTS ANYONE WHO SHARES STATE ACROSS BATCHES. Coalescing small chunks needs the same
// thing from the other direction: `CanonicalConcat` across arenas is blocked because batch
// arenas reset on the next `MoveNextAsync`. A deep copy of the node's bytes at adoption time is
// mandatory for both, and it is one requirement, not two.
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A batch's arena is recycled, so an index into it is meaningless once the batch is gone.</summary>
public sealed class ArenaLifetimeTests
{
    /// <summary>8 193 i32 rows in one chunk, read as two batches: chunk larger than batch.</summary>
    private const string Entry = "types/i32_nonnull_r8193";

    /// <summary>
    /// An index from an earlier batch names whatever the later batch put at that slot, never the
    /// earlier batch's data.
    /// </summary>
    /// <remarks>
    /// A stale canonical index reads live, plausible, WRONG data rather than failing -- the
    /// mechanism behind a shared dictionary reading back permuted. An oversized chunk is decoded
    /// straight into the arena that retains it, so the batch arena holds the same handful of window
    /// records in every batch and a batch-1 index addresses a perfectly real batch-2 node. A
    /// violation of the lifetime rule is not caught: the arena has no generation and a raw index
    /// carries none, so there is no index for which the library could promise to notice.
    ///
    /// So the assertion is the hazard itself, stated as strongly as the model permits: a stale
    /// root index yields the CURRENT batch's data, never the batch it was captured from.
    ///
    /// There is ONE batch arena, reset and refilled per batch, and `Assert.Same` below is the part
    /// that matters. Anything wanting canonical data to outlive its batch must own its own arena --
    /// `CanonicalArena.CopyFrom` for the bytes, `ReferenceFrom` when the source outlives the
    /// borrower.
    /// </remarks>
    [Fact]
    public async Task AnIndexFromAnEarlierBatchNoLongerAddressesTheLaterBatch()
    {
        string path = Corpus.Path(Entry);
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        CanonicalArena? captured = null;
        int capturedRoot = -1;
        List<int> first = [];
        List<int> second = [];
        bool checkedStale = false;

        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            if (captured is null)
            {
                captured = batch.Arena;
                capturedRoot = batch.RootIndex;
                Read(batch, first);
                continue;
            }

            // The second batch is resident now, and the arena is the same object it always was.
            Assert.Same(captured, batch.Arena);
            Read(batch, second);

            List<int> stale = [];
            using (RecordBatch reused = new RecordBatch(captured, capturedRoot, 0))
            {
                Read(reused, stale);
            }

            // The stale index is meaningless, and this is what "meaningless" looks like: it names
            // whatever node happens to sit at that slot NOW. Never the first batch's values.
            Assert.NotEqual(first, stale);
            Assert.Equal(second, stale);
            checkedStale = true;
            break;
        }

        Assert.NotNull(captured);
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.NotEqual(first, second);
        Assert.True(checkedStale, "the file must produce more than one batch for this to mean anything");
    }

    private static void Read(RecordBatch batch, List<int> into)
    {
        VortexColumn column = batch.Root;
        if (column.Kind == CanonicalKind.Struct)
        {
            column = column.AsStruct().GetField(0);
        }

        PrimitiveColumn<int> values = column.AsPrimitive<int>();
        for (int i = 0; i < values.Length; i++)
        {
            into.Add(values[i]);
        }
    }
}
