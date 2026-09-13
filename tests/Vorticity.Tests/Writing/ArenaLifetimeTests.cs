// WHY THIS FILE EXISTS: the shared-dictionary layout (§3a) was attempted twice, produced a file
// SMALLER than the reference's both times, and both times read back PERMUTED. Three causes were
// eliminated one after another - the reader, the compressor, the code width - and the fourth was
// never found, because it was not being looked for in the writer's data model.
//
// This is that fourth cause, isolated from the 200-line change that exposed it and from the
// 8 193-row corpus file it was being debugged through. It needs no writer change at all: the
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
// Permuted values, with correct codes, a correct reader and a correct width - which is the exact
// symptom, and the reason the three eliminations were all true and all beside the point.
//
// WHAT THIS COSTS ANYONE WHO SHARES STATE ACROSS BATCHES. §3c's coalescing needs the same thing
// from the other direction: `CanonicalConcat` across arenas is blocked because batch arenas reset
// on the next `MoveNextAsync`. A deep copy of the node's bytes at adoption time is mandatory for
// both, and it is one requirement, not two.
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A batch's arena is recycled, so an index into it is meaningless once the batch is gone.</summary>
public sealed class ArenaLifetimeTests
{
    /// <summary>8 193 i32 rows in one chunk, read as two batches: chunk larger than batch.</summary>
    private const string Entry = "types/i32_nonnull_r8193";

    /// <summary>
    /// An index from an earlier batch no longer addresses the later batch's arena at all.
    /// </summary>
    /// <remarks>
    /// THIS TEST USED TO ASSERT THE OPPOSITE, and the change is the point. It was written to show
    /// that a stale canonical index reads live, plausible, WRONG data rather than failing -- the
    /// mechanism behind the shared-dictionary layout coming back permuted twice, with three
    /// unrelated causes eliminated in between.
    ///
    /// `FlatLayoutReader` now decodes a chunk larger than a batch ONCE, into an arena
    /// `ResetBatch` does not touch, and copies only the batch's window into the batch's own arena.
    /// So the batch arena no longer holds the decoded tree, and an index captured from it in batch 1
    /// is out of range in batch 2: the failure went from silent to loud, which is strictly better.
    ///
    /// WHAT IS UNCHANGED, and is what the hazard actually was: there is still ONE arena, reset and
    /// refilled per batch. `Assert.Same` below is the part that still matters. Anything wanting
    /// canonical data to outlive its batch must copy the bytes -- `CanonicalArena.CopyFrom` -- and
    /// that is as true now as when this file was written.
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

            Assert.Throws<VortexFormatException>(
                () => new RecordBatch(captured, capturedRoot, 0).Dispose());
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
