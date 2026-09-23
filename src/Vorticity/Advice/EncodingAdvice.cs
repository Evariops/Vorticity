using System.Collections.Immutable;
using Vorticity.Advice;

namespace Vorticity;

/// <summary>What a column's sample holds, as the advice counted it.</summary>
/// <param name="Rows">The rows sampled.</param>
/// <param name="Nulls">Of those, the null ones.</param>
/// <param name="Distinct">The distinct values among the sample's rows that are not null, counted exactly.</param>
/// <param name="RowsPerDistinctInChunk">
/// The mean number of rows per distinct value inside a chunk of the writer's own size: under about
/// a hundred, a dictionary does not pay for its entries within one chunk.
/// </param>
/// <param name="AverageRun">The mean number of rows in a run of equal values.</param>
/// <param name="Ascending">Whether the values that are not null never decrease; false for text, binary and booleans.</param>
/// <param name="AverageLength">The mean length of a text or binary value, in bytes; 0 for other kinds.</param>
public sealed record ColumnProfile(
    long Rows, long Nulls, long Distinct, double RowsPerDistinctInChunk, double AverageRun, bool Ascending, double AverageLength);

/// <summary>One way to write a column, measured on its sample.</summary>
/// <param name="Hint">The hint written under; <see cref="EncodingHint.Auto"/> for the writer's own choice.</param>
/// <param name="ChunkTargetBytes">The chunk target written under; 0 for the writer's own.</param>
/// <param name="WrittenAs">What the sample's chunks became, as <see cref="WriteReport"/> says: a hint that does not apply falls back.</param>
/// <param name="BytesPerValue">The bytes written, per row of the sample.</param>
/// <param name="ScanNanosecondsPerValue">Decoding every value to its plain form and reading it once, in memory, per row.</param>
/// <param name="LookupMicroseconds">Reading one row, the chunk it lies in decoded as a take decodes it.</param>
/// <param name="Cost">The candidate's cost under the goal, per row of a scan: nanoseconds for <see cref="EncodingObjective.ReadTime"/>, bytes for <see cref="EncodingObjective.Size"/>.</param>
/// <param name="CrossesAtBytesPerSecond">
/// The storage throughput at which this candidate and the recommended one read the column whole in
/// the same time, when one is smaller and the other decodes faster; null otherwise.
/// </param>
public sealed record EncodingCandidate(
    EncodingHint Hint,
    int ChunkTargetBytes,
    ImmutableArray<string> WrittenAs,
    double BytesPerValue,
    double ScanNanosecondsPerValue,
    double LookupMicroseconds,
    double Cost,
    long? CrossesAtBytesPerSecond);

/// <summary>The advice for one column: what its sample holds, every way to write it measured, and the one to take.</summary>
/// <param name="Path">The column's name, which is the key of its hint.</param>
/// <param name="Profile">What the sample holds.</param>
/// <param name="Candidates">
/// Every candidate measured, the recommended one first and the others by their cost under the goal.
/// A column tried at larger chunks has all its candidates measured on whole chunks of the largest,
/// when the data holds more rows than the sample.
/// </param>
/// <param name="Recommended">The candidate to take.</param>
/// <param name="Reason">Why, in one sentence with the numbers that decided it.</param>
public sealed record ColumnEncodingAdvice(
    string Path, ColumnProfile Profile, ImmutableArray<EncodingCandidate> Candidates, EncodingCandidate Recommended, string Reason);

/// <summary>
/// How to write the columns of some data for the reads a goal describes, measured on a sample of
/// the data: per column the candidates and the one to take, and the options that take them.
/// </summary>
/// <param name="Goal">The goal the candidates were ranked by.</param>
/// <param name="Rows">The rows of the data advised on.</param>
/// <param name="SampledRows">The rows each column was profiled on, and its candidates measured on unless it was tried at larger chunks.</param>
/// <param name="ChunkTargetBytes">The chunk target to write with; 0 for the writer's own.</param>
/// <param name="Columns">The advice for each column the advice could measure, in the schema's order.</param>
public sealed record EncodingAdvice(
    EncodingGoal Goal, long Rows, long SampledRows, int ChunkTargetBytes, ImmutableArray<ColumnEncodingAdvice> Columns)
{
    /// <summary>
    /// <paramref name="baseline"/> with the advice taken: the profile the candidates were written
    /// under, <see cref="CompressionProfile.Smallest"/> for <see cref="EncodingObjective.Size"/> and
    /// <see cref="CompressionProfile.Auto"/> otherwise; a hint for each column whose recommendation
    /// is not the writer's own choice; and the chunk target when it is not the writer's.
    /// </summary>
    /// <param name="baseline">The options to start from; null for the defaults.</param>
    /// <returns>The options.</returns>
    public VortexWriteOptions ToWriteOptions(VortexWriteOptions? baseline = null)
    {
        VortexWriteOptions options = baseline ?? VortexWriteOptions.Default;
        ImmutableDictionary<string, EncodingHint> hints = options.Hints;
        foreach (ColumnEncodingAdvice column in Columns)
        {
            if (column.Recommended.Hint != EncodingHint.Auto)
            {
                hints = hints.SetItem(column.Path, column.Recommended.Hint);
            }
        }

        return options with
        {
            Compression = AdviceChoice.ProfileFor(Goal.Objective),
            Hints = hints,
            ChunkTargetBytes = ChunkTargetBytes != 0 ? ChunkTargetBytes : options.ChunkTargetBytes,
        };
    }
}
