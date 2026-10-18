using System;

namespace Vorticity.Dataset;

/// <summary>
/// What a dataset of periods takes in and keeps, as the length of its periods weighs it: its rate in
/// bytes and rows of data time, its columns, the bytes a row of its widest column takes, the largest
/// object its store takes, and the delays that rewrite or drop a period.
/// </summary>
/// <param name="BytesPerSecond">Bytes stored per second of the instant column.</param>
/// <param name="RowsPerSecond">Rows per second of the instant column.</param>
/// <param name="Columns">The leaf columns a row holds.</param>
/// <param name="WidestBytesPerRow">The bytes of a row in its widest column, or a bound on them.</param>
/// <param name="MaxObjectBytes">The most bytes an object may take; <see cref="long.MaxValue"/> for no bound.</param>
/// <param name="ErasureDelay">How long a deleted row may stay in the store, past which a purge rewrites its period; null for none.</param>
/// <param name="Retention">How long a row is kept, past which a period expires whole; null for no expiry.</param>
internal readonly record struct PeriodLoad(
    double BytesPerSecond,
    double RowsPerSecond,
    int Columns,
    double WidestBytesPerRow,
    long MaxObjectBytes,
    TimeSpan? ErasureDelay,
    TimeSpan? Retention);

/// <summary>
/// The length of a dataset's periods: among the calendar units that hold four constraints, the one
/// that costs a window least. The store takes the period's object, the footer stays under its cap at
/// a chunk size that keeps selective reads in bounds, a purge of the period runs within half the
/// erasure delay, and a period stays a small share of the retention, since expiry drops whole periods.
/// </summary>
/// <remarks>
/// <para>
/// The cost of a unit is what a window's questions pay for each object they touch, against what a
/// period's rewrites cost: a second seal for late rows once a period, a purge once an erasure delay.
/// A seal's cost per second does not depend on the length, a purge's grows with it, and the questions'
/// shrinks, so without purges the longest unit the constraints allow wins.
/// </para>
/// <para>
/// Every constant here is provisional, set from the design's example until the benches of appends in
/// order and of questions over windows measure them: the widest chunk, the rewrite throughput, the
/// share of the retention, the rate of questions, the price of an object touched, and the window.
/// </para>
/// </remarks>
internal static class PeriodLengths
{
    /// <summary>The widest chunk a selective read may fetch of one column: provisional.</summary>
    internal const double MaxWidestChunkBytes = 16 << 20;

    /// <summary>The bytes a compaction rewrites per second: provisional.</summary>
    internal const double RewriteBytesPerSecond = 100e6;

    /// <summary>The share of the retention a period may span: provisional.</summary>
    internal const double RetentionShare = 0.1;

    /// <summary>The questions a dataset answers per second: provisional, until the telemetry reports it.</summary>
    internal const double QuestionsPerSecond = 1;

    /// <summary>What a question pays for each object it touches, in seconds: a round trip and an open, provisional.</summary>
    internal const double ObjectPriceSeconds = 0.05;

    /// <summary>The window a question spans, in seconds: a day, provisional.</summary>
    internal const double WindowSeconds = 86_400;

    /// <summary>The rows of a block, the least a chunk holds.</summary>
    private const double BlockRows = 8_192;

    /// <summary>The units a period may span, from the finest.</summary>
    private static readonly SliceWidth[] Units = [SliceWidth.Minute, SliceWidth.Hour, SliceWidth.Day, SliceWidth.Month];

    /// <summary>The length for <paramref name="load"/>: the cheapest unit that holds every constraint, or a minute when none does.</summary>
    internal static SliceWidth Choose(PeriodLoad load)
    {
        SliceWidth best = SliceWidth.Minute;
        double cheapest = double.PositiveInfinity;
        foreach (SliceWidth unit in Units)
        {
            if (!Holds(load, unit))
            {
                continue;
            }

            double cost = Cost(load, unit);
            if (cost <= cheapest)
            {
                cheapest = cost;
                best = unit;
            }
        }

        return best;
    }

    /// <summary>Whether a period of <paramref name="unit"/> holds the four constraints under <paramref name="load"/>.</summary>
    internal static bool Holds(PeriodLoad load, SliceWidth unit)
    {
        double seconds = Seconds(unit);
        double bytes = load.BytesPerSecond * seconds;
        if (bytes > load.MaxObjectBytes)
        {
            return false;
        }

        // The footer's cap sets the least chunk: 2 × rows × columns / 10⁶ rows, in whole blocks.
        double rows = load.RowsPerSecond * seconds;
        double chunkRows = Math.Max(BlockRows, 2 * rows * Math.Max(load.Columns, 1) / 1e6);
        if (chunkRows * load.WidestBytesPerRow > MaxWidestChunkBytes)
        {
            return false;
        }

        if (load.ErasureDelay is { } delay && bytes / RewriteBytesPerSecond > delay.TotalSeconds / 2)
        {
            return false;
        }

        return load.Retention is not { } retention || seconds <= RetentionShare * retention.TotalSeconds;
    }

    /// <summary>
    /// What a unit costs per second: the objects a window's questions touch, a period and one more
    /// for the window's misalignment, at their price; a purge of the period once an erasure delay at
    /// the rewrite throughput. A second seal costs the same at every length, and is left out.
    /// </summary>
    internal static double Cost(PeriodLoad load, SliceWidth unit)
    {
        double seconds = Seconds(unit);
        double objects = Math.Ceiling(WindowSeconds / seconds) + 1;
        double questions = QuestionsPerSecond * ObjectPriceSeconds * objects;
        double purges = load.ErasureDelay is { } delay
            ? load.BytesPerSecond * seconds / RewriteBytesPerSecond / Math.Max(delay.TotalSeconds, 1)
            : 0;
        return questions + purges;
    }

    /// <summary>A unit in seconds; a month as thirty days.</summary>
    private static double Seconds(SliceWidth unit) => unit switch
    {
        SliceWidth.Second => 1,
        SliceWidth.Minute => 60,
        SliceWidth.Hour => 3_600,
        SliceWidth.Day => 86_400,
        _ => 30 * 86_400,
    };
}
