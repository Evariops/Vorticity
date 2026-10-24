using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>
/// Where the tables of a file's synopsis go as they are built, a chunk at a time: the writer of the file, which
/// encodes each chunk as it comes and holds it aside until the file's end, when the choice keeps the tables worth
/// their bytes. A table its builder rules out is given up as it grows, its chunks never written, and still weighed
/// by the choice, as a grain it was asked to answer, with the reason the report gives.
/// </summary>
internal interface ISynopsisChunkSink
{
    /// <summary>A new table, by the number its chunks are held under.</summary>
    int OpenTable();

    /// <summary>
    /// Encodes the rows <paramref name="node"/> holds, a struct of <paramref name="arena"/>, as a chunk of
    /// <paramref name="table"/> whose buckets run from <paramref name="firstBucket"/> to <paramref name="lastBucket"/>,
    /// and whose first key's values, for a table of values, from <paramref name="leastKey"/> to
    /// <paramref name="greatestKey"/>, null when it does not say; and holds it. For a table of sketches that lays its
    /// distinct sketches apart, <paramref name="sketches"/> is the struct of those of the same rows, which the chunk
    /// writes in a region of their own, and -1 for any other. The caller may reuse the arena once it completes.
    /// </summary>
    /// <returns>The chunk's bytes, its sketches' among them.</returns>
    ValueTask<long> SpoolChunkAsync(
        int table, CanonicalArena arena, int node, int sketches, long firstBucket, long lastBucket, byte[]? leastKey, byte[]? greatestKey,
        CancellationToken cancellationToken);

    /// <summary>Gives up <paramref name="table"/>, for the reason <paramref name="verdict"/> the report gives: none of its chunks is written.</summary>
    void DropTable(int table, string verdict);

    /// <summary>
    /// The bytes the synopsis may take over the data written so far, <see cref="long.MaxValue"/> while that data
    /// is too little to say: a table whose chunks take more alone is not worth its bytes, and is given up.
    /// </summary>
    long BudgetSoFar { get; }

    /// <summary>The rows of the data so far, those of the chunks written: what <see cref="BudgetSoFar"/> weighs.</summary>
    long RowsSoFar { get; }

    /// <summary>Where what the tables are built from may wait, as the file's own scratch does: its memory, its directory, its sealing.</summary>
    (long Memory, string? Directory, bool Seal) Scratch { get; }
}
