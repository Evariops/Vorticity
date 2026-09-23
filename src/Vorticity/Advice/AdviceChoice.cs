using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Vorticity.Advice;

/// <summary>A way to write a column, as measured on its sample, before a goal ranks it.</summary>
/// <param name="Hint">The hint written under.</param>
/// <param name="ChunkTargetBytes">The chunk target written under; 0 for the writer's own.</param>
/// <param name="WrittenAs">What the chunks became, each encoding with its count, the most frequent first.</param>
/// <param name="BytesPerValue">The file's bytes over the sample's rows.</param>
/// <param name="ScanNanosecondsPerValue">A full decode and read of the column, per row.</param>
/// <param name="LookupMicroseconds">One row read, the chunk it lies in decoded as a take decodes it.</param>
/// <param name="ChunkBytes">The data bytes of one chunk of the column: what a lookup reads.</param>
internal readonly record struct MeasuredCandidate(
    EncodingHint Hint,
    int ChunkTargetBytes,
    ImmutableArray<string> WrittenAs,
    double BytesPerValue,
    double ScanNanosecondsPerValue,
    double LookupMicroseconds,
    double ChunkBytes);

/// <summary>
/// Ranks measured candidates under a goal. A pure function of the numbers, so that the tests hold it
/// to numbers of their own and never to a measured time.
/// </summary>
internal static class AdviceChoice
{
    /// <summary>
    /// How much a candidate must beat the writer's own choice by, and a chunk target the writer's
    /// own size, to be advised: the advice should not move with the noise of a measurement, and an
    /// option not given cannot go stale.
    /// </summary>
    internal const double Margin = 0.05;

    /// <summary>
    /// The profile the candidates are written under, the writer's own choice among them: the one
    /// that prices by bytes alone when the objective is the bytes.
    /// </summary>
    internal static CompressionProfile ProfileFor(EncodingObjective objective) =>
        objective == EncodingObjective.Size ? CompressionProfile.Smallest : CompressionProfile.Auto;

    /// <summary>
    /// A candidate's cost per row of a scan: for read time, its decode, its bytes at the storage's
    /// throughput and its share of the lookups; for size, its bytes.
    /// </summary>
    internal static double Cost(in MeasuredCandidate candidate, EncodingGoal goal, long rows)
    {
        if (goal.Objective == EncodingObjective.Size)
        {
            return candidate.BytesPerValue;
        }

        double bytesPerNanosecond = goal.StorageBytesPerSecond / 1e9;
        double read = candidate.ScanNanosecondsPerValue + (candidate.BytesPerValue / bytesPerNanosecond);
        if (goal.LookupsPerScan <= 0)
        {
            return read;
        }

        double lookup = (candidate.LookupMicroseconds * 1_000) + (candidate.ChunkBytes / bytesPerNanosecond);
        return read + (goal.LookupsPerScan * lookup / Math.Max(rows, 1));
    }

    /// <summary>
    /// The candidate to take among those written at <paramref name="target"/>: the least costly,
    /// the scan breaking a tie, unless the writer's own choice is within <see cref="Margin"/> of it.
    /// </summary>
    /// <returns>Its index, or -1 when none was written at that target.</returns>
    internal static int Recommend(IReadOnlyList<MeasuredCandidate> candidates, int target, EncodingGoal goal, long rows)
    {
        int best = -1;
        int auto = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].ChunkTargetBytes != target)
            {
                continue;
            }

            if (candidates[i].Hint == EncodingHint.Auto && auto < 0)
            {
                auto = i;
            }

            if (best < 0 || Before(candidates[i], candidates[best], goal, rows))
            {
                best = i;
            }
        }

        if (auto >= 0 && best >= 0
            && Cost(candidates[auto], goal, rows) <= Cost(candidates[best], goal, rows) * (1 + Margin))
        {
            return auto;
        }

        return best;
    }

    /// <summary>
    /// The chunk target for the file: the one that makes the columns' best candidates least costly
    /// together, a column with nothing written at a target counting at its best at the writer's own
    /// size, and a larger one only when it saves <see cref="Margin"/> or more.
    /// </summary>
    internal static int ChooseTarget(IReadOnlyList<IReadOnlyList<MeasuredCandidate>> columns, EncodingGoal goal, long rows)
    {
        double own = 0;
        List<int> targets = [];
        foreach (IReadOnlyList<MeasuredCandidate> column in columns)
        {
            own += BestCost(column, 0, goal, rows);
            foreach (MeasuredCandidate candidate in column)
            {
                if (candidate.ChunkTargetBytes != 0 && !targets.Contains(candidate.ChunkTargetBytes))
                {
                    targets.Add(candidate.ChunkTargetBytes);
                }
            }
        }

        int chosen = 0;
        double chosenCost = own;
        foreach (int target in targets)
        {
            double cost = 0;
            foreach (IReadOnlyList<MeasuredCandidate> column in columns)
            {
                double atTarget = BestCost(column, target, goal, rows);
                cost += double.IsPositiveInfinity(atTarget) ? BestCost(column, 0, goal, rows) : atTarget;
            }

            if (cost < chosenCost)
            {
                chosen = target;
                chosenCost = cost;
            }
        }

        return chosenCost <= own * (1 - Margin) ? chosen : 0;
    }

    /// <summary>
    /// The throughput at which <paramref name="candidate"/> and <paramref name="recommended"/> read
    /// the column whole in the same time, when one is smaller and the other decodes faster.
    /// </summary>
    internal static long? Crossing(in MeasuredCandidate candidate, in MeasuredCandidate recommended)
    {
        double saved = recommended.BytesPerValue - candidate.BytesPerValue;
        double lost = candidate.ScanNanosecondsPerValue - recommended.ScanNanosecondsPerValue;
        bool trade = (saved > 0 && lost > 0) || (saved < 0 && lost < 0);
        return trade ? (long)(saved / lost * 1e9) : null;
    }

    /// <summary>Every candidate, the least costly first, as the public record carries it.</summary>
    internal static ImmutableArray<EncodingCandidate> Rank(
        IReadOnlyList<MeasuredCandidate> candidates, int recommended, EncodingGoal goal, long rows)
    {
        int[] order = new int[candidates.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
            a == recommended ? -1
            : b == recommended ? 1
            : Before(candidates[a], candidates[b], goal, rows) ? -1
            : Before(candidates[b], candidates[a], goal, rows) ? 1
            : a.CompareTo(b));

        ImmutableArray<EncodingCandidate>.Builder ranked = ImmutableArray.CreateBuilder<EncodingCandidate>(order.Length);
        foreach (int i in order)
        {
            ranked.Add(Public(candidates[i], candidates[recommended], i == recommended, goal, rows));
        }

        return ranked.MoveToImmutable();
    }

    /// <summary>The public form of a measured candidate, its cost and its crossing with the recommended one.</summary>
    internal static EncodingCandidate Public(
        in MeasuredCandidate candidate, in MeasuredCandidate recommended, bool isRecommended, EncodingGoal goal, long rows) =>
        new EncodingCandidate(
            candidate.Hint,
            candidate.ChunkTargetBytes,
            candidate.WrittenAs,
            candidate.BytesPerValue,
            candidate.ScanNanosecondsPerValue,
            candidate.LookupMicroseconds,
            Cost(candidate, goal, rows),
            isRecommended ? null : Crossing(candidate, recommended));

    /// <summary>
    /// Why the recommended candidate, in one sentence: what the column holds, what the candidate
    /// writes and costs, and what the writer's own choice would.
    /// </summary>
    internal static string Reason(
        string kind, ColumnProfile profile, IReadOnlyList<MeasuredCandidate> candidates, int recommended, EncodingGoal goal)
    {
        MeasuredCandidate chosen = candidates[recommended];
        int auto = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Hint == EncodingHint.Auto && candidates[i].ChunkTargetBytes == 0)
            {
                auto = i;
                break;
            }
        }

        StringBuilder text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{kind}, {profile.Distinct:N0} distinct values over {profile.Rows:N0} rows, ")
            .Append(CultureInfo.InvariantCulture, $"{profile.RowsPerDistinctInChunk:F1} rows each in a chunk: ");
        text.Append(Describe(chosen))
            .Append(CultureInfo.InvariantCulture, $", {chosen.BytesPerValue:F2} B a value and {chosen.ScanNanosecondsPerValue:F1} ns to scan");
        if (goal.LookupsPerScan > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", {chosen.LookupMicroseconds:F1} µs a lookup");
        }

        if (auto >= 0 && auto != recommended)
        {
            MeasuredCandidate own = candidates[auto];
            text.Append(CultureInfo.InvariantCulture, $", against {own.BytesPerValue:F2} B and {own.ScanNanosecondsPerValue:F1} ns for the writer's own choice");
        }
        else if (auto == recommended)
        {
            text.Append(", the writer's own choice");
        }

        return text.Append('.').ToString();
    }

    /// <summary>Whether <paramref name="a"/> ranks before <paramref name="b"/>: the lesser cost, then the faster scan.</summary>
    private static bool Before(in MeasuredCandidate a, in MeasuredCandidate b, EncodingGoal goal, long rows)
    {
        double costA = Cost(a, goal, rows);
        double costB = Cost(b, goal, rows);
        return costA < costB || (costA == costB && a.ScanNanosecondsPerValue < b.ScanNanosecondsPerValue);
    }

    private static double BestCost(IReadOnlyList<MeasuredCandidate> column, int target, EncodingGoal goal, long rows)
    {
        double best = double.PositiveInfinity;
        foreach (MeasuredCandidate candidate in column)
        {
            if (candidate.ChunkTargetBytes == target)
            {
                best = Math.Min(best, Cost(candidate, goal, rows));
            }
        }

        return best;
    }

    private static string Describe(in MeasuredCandidate candidate)
    {
        string written = candidate.WrittenAs.IsDefaultOrEmpty ? "nothing" : string.Join(", ", candidate.WrittenAs);
        string how = candidate.Hint == EncodingHint.Auto ? written : string.Create(CultureInfo.InvariantCulture, $"hint {candidate.Hint} ({written})");
        return candidate.ChunkTargetBytes == 0
            ? how
            : string.Create(CultureInfo.InvariantCulture, $"{how} at {candidate.ChunkTargetBytes >> 20} MiB chunks");
    }
}
