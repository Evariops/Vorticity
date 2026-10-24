using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The order inside a slice, by numbers of distinct values or by runs added (<c>--micro runsorder</c>): on
/// a slice's codes for a few mixes of columns, the runs every column adds under each order, the time the
/// greedy order takes measured on every row and on a sample, and whether the sample gives the same order.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class RunsOrderMicro
{
    internal static int Run(int rows, int rounds)
    {
        Console.WriteLine($"{rows} rows a slice; runs summed over the order's columns; best of {rounds}");
        Console.WriteLine("mix            distinct order    greedy runs   by distinct   greedy ms   stride 16 ms (same?)   stride 64 ms (same?)");
        foreach ((string name, (int[] Codes, int Distinct)[] columns) in Mixes(rows))
        {
            int[] byDistinct = [.. Enumerable.Range(0, columns.Length).OrderBy(k => columns[k].Distinct)];
            long distinctRuns = SliceSort.Runs([.. byDistinct.Select(k => columns[k].Codes)], rows);
            (int[] exact, double exactMs) = Timed(() => SliceSort.Greedy(columns, rows), rounds);
            (int[] sixteen, double sixteenMs) = Timed(() => SliceSort.Greedy(columns, rows, 16), rounds);
            (int[] sixtyFour, double sixtyFourMs) = Timed(() => SliceSort.Greedy(columns, rows, 64), rounds);
            long greedyRuns = SliceSort.Runs([.. exact.Select(k => columns[k].Codes)], rows);
            Console.WriteLine(
                $"{name,-14} {string.Join(",", byDistinct),-16} {greedyRuns,12} {distinctRuns,12} {exactMs,11:F1} {sixteenMs,13:F1} ({exact.SequenceEqual(sixteen)}) {sixtyFourMs,13:F1} ({exact.SequenceEqual(sixtyFour)})");
            Console.WriteLine($"{string.Empty,-14} greedy order {string.Join(",", exact)}");
        }

        return 0;
    }

    private static (int[] Order, double Milliseconds) Timed(Func<int[]> greedy, int rounds)
    {
        int[] order = greedy();
        double best = double.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            long begin = Stopwatch.GetTimestamp();
            order = greedy();
            best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        }

        return (order, best);
    }

    /// <summary>
    /// Four mixes: columns independent of each other; a service that fixes its region and its team; a
    /// route nested in its service; and sources of different shapes, each with paths null outside its own
    /// shape.
    /// </summary>
    private static IEnumerable<(string Name, (int[] Codes, int Distinct)[] Columns)> Mixes(int rows)
    {
        Random random = new Random(7);
        int[] Draw(int values) => [.. Enumerable.Range(0, rows).Select(_ => random.Next(values))];

        yield return ("independent", [Column(Draw(5)), Column(Draw(5)), Column(Draw(600))]);

        int[] service = Draw(600);
        yield return ("correlated", [Column(Draw(8)), Column([.. service.Select(s => s % 12)]), Column(service), Column([.. service.Select(s => s % 40)])]);

        // A route that belongs to one service of ten, three a service, beside a method of twenty: the
        // distinct counts put the method second, the runs the route.
        int[] owner = Draw(10);
        yield return ("nested", [Column(owner), Column(Draw(20)), Column([.. owner.Select(s => (s * 3) + random.Next(3))])]);

        // Four sources, each a shape, each with a path of 50 values only it holds; a severity of 6 for all.
        int[] shape = Draw(4);
        (int[], int)[] paths = [.. Enumerable.Range(0, 4).Select(p => Column([.. shape.Select(s => s == p ? random.Next(50) : 50)]))];
        yield return ("shaped", [Column(Draw(6)), Column(shape), .. paths]);
    }

    /// <summary>A column of codes, dense from 0, and how many distinct ones.</summary>
    private static (int[] Codes, int Distinct) Column(int[] codes) => (codes, codes.Distinct().Count());
}
