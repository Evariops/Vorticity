namespace Vorticity.Dataset;

/// <summary>
/// What each part of a compaction job may hold, dealt once from the jobs' budget before the job reads a row: its inputs, what
/// their merge takes ahead of them, and its writer. A part holds to its share: a full share makes the part write a chunk
/// earlier, hold a read, a slice or a flight back, or wait, and never fail the job.
/// </summary>
/// <param name="Inputs">What the inputs hold: their reads, their decoded windows and the merge's keys, what they take ahead included.</param>
/// <param name="MergeAhead">Of <paramref name="Inputs"/>, what the reads and the slices ahead may take: the whole share until the windows of the open inputs are counted apart.</param>
/// <param name="Writer">What the writer holds: what its columns gather, the chunks it compresses and their copies, the shredding's, its flights and its sinks.</param>
/// <param name="GroupsWaiting">What groups of rows encoded ahead of their turn hold: none while a writer writes its groups in their order.</param>
internal readonly record struct JobShares(long Inputs, long MergeAhead, long Writer, long GroupsWaiting)
{
    /// <summary>
    /// The writer's share of a job's budget, in fifths: two. Measured under 2 GiB at degree 14, the jobs' budget 672 MiB, a
    /// tier merge of four outputs of log records held 428 MiB in its inputs at their peak and 279 in its writer at its own,
    /// and a consolidation of ten inputs of log records 361 to 385 MiB in its writer; at the job's peak, the blocks of the
    /// first job were two thirds its inputs' (2026-11-20 and 2026-11-21, plan step C0).
    /// </summary>
    internal const int WriterFifths = 2;

    /// <summary>
    /// What a job's writer holds at its peak for each byte its columns may gather together: past what they gather, the chunks
    /// detached for their emission, the copies its shredding takes, its flights and its lanes' workspaces. Measured at 3.3
    /// on the tier merge of log records and 4.3 to 4.6 on the consolidation, each under a gathering ceiling of 84 MiB.
    /// </summary>
    internal const double WriterPerGathered = 4.5;

    /// <summary>The shares of a job whose budget is <paramref name="budget"/> bytes.</summary>
    internal static JobShares Of(long budget)
    {
        long writer = budget / 5 * WriterFifths;
        long inputs = budget - writer;
        return new JobShares(inputs, inputs, writer, 0);
    }

    /// <summary>What the writer's columns gather together at most, the rest of its share for what it holds past them.</summary>
    internal long Gathered => (long)(Writer / WriterPerGathered);
}
