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
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>A batch's arena is recycled, so an index into it is meaningless once the batch is gone.</summary>
public sealed class ArenaLifetimeTests
{
    /// <summary>8 193 i32 rows, which the default batch size splits into more than one chunk.</summary>
    private const string Entry = "types/i32_nonnull_r8193";

    /// <summary>
    /// Reading through a previous batch's canonical index yields the CURRENT batch's values.
    /// </summary>
    /// <remarks>
    /// Deliberately asserts what the stale index DOES return rather than merely that it differs.
    /// "The values changed" would also pass if the arena had been freed and the read were returning
    /// garbage; the point being fixed is narrower and worth stating exactly - the storage is intact,
    /// addressable and refilled, so a stale index reads live, plausible, WRONG data rather than
    /// failing. That is why the shared dictionary produced a valid file full of permuted values
    /// instead of throwing.
    /// </remarks>
    [Fact]
    public async Task AnIndexFromAnEarlierBatchReadsTheLaterBatchsRows()
    {
        string path = Corpus.Path(Entry);
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

        CanonicalArena? captured = null;
        int capturedRoot = -1;
        List<int> first = [];
        List<int> second = [];
        List<int> throughStaleIndex = [];
        int laterRoot = -1;

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

            // The second batch is resident NOW, and the read below holds no reference to the first
            // batch at all - only the arena and the index it handed out. Inside the loop on purpose:
            // disposing the enumerator resets the arena to zero records, so a stale index read after
            // the scan throws instead of lying, and it is the lying that matters here.
            Assert.Same(captured, batch.Arena);
            laterRoot = batch.RootIndex;
            Read(batch, second);

            using RecordBatch stale = new RecordBatch(captured, capturedRoot, 0);
            Read(stale, throughStaleIndex);
            break;
        }

        Assert.NotNull(captured);
        Assert.NotEmpty(second);

        // The index itself is stable - it is the STORAGE BEHIND IT that moved, which is what makes
        // this silent rather than loud.
        Assert.Equal(laterRoot, capturedRoot);
        Assert.Equal(second, throughStaleIndex);
        Assert.NotEqual(first, throughStaleIndex);
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
