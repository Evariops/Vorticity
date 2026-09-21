// One definition of what a scan, a projection, a take and a write-back ARE, for every estimator.
//
// `ScanAll` had been written out seven times across this project, `DiscardSink`
// twice, and what that produces is a correction applied to one copy. This file is the one
// table; the scenarios THEMSELVES live in `Vorticity.Benchmarks.Scenarios`, a separate assembly,
// because `--ab` loads two builds of them in one process and an assembly that referenced the
// FFI reader or BenchmarkDotNet could not be built inside a worktree of an arbitrary old commit.
//
// WHAT STAYS HERE is the pairing: an axis is a scenario plus the REFERENCE's answer to the same
// question, and the reference is `RustReader`, which belongs to this assembly. `RatioCheck` builds
// its first axes from this table and keeps its own entries for the ones the profiler has no use for
// (the footer-only open, the four `rewritten` axes, which write a file on the spot).
using System;
using System.Threading.Tasks;

using Set = Vorticity.Bench.Scenarios.ScenarioSet;

namespace Vorticity.Benchmarks;

/// <summary>The scenarios paired with the reference's answer to the same question.</summary>
internal static class Scenarios
{
    /// <summary>The column the projection keeps and the filter tests.</summary>
    internal const string Field = Set.Field;

    /// <summary>Rows a scattered take asks for.</summary>
    internal const long TakeCount = Set.TakeCount;

    /// <summary>The gap between taken rows on the 65 536-row file.</summary>
    internal const long TakeStride = Set.TakeStride;

    /// <summary>The low edge of the filter band.</summary>
    internal const long BandLow = Set.BandLow;

    /// <summary>A band that keeps about one row in a hundred.</summary>
    internal const long NarrowBand = Set.NarrowBand;

    /// <summary>A band that keeps about half the rows.</summary>
    internal const long WideBand = Set.WideBand;

    /// <summary>A scenario: a name, an axis label, and both sides of the same question.</summary>
    /// <param name="Name">What `--profile` and `--ab` call it.</param>
    /// <param name="Axis">What `--ratio-check` calls it; also the key into its reference table.</param>
    /// <param name="Ours">Our reader, returning rows.</param>
    /// <param name="Theirs">The reference, returning rows.</param>
    internal sealed record Scenario(
        string Name, string Axis, Func<string, Task<long>> Ours, Func<string, long> Theirs);

    /// <summary>The scenarios the gate, the profiler and the A/B all run.</summary>
    internal static readonly Scenario[] All =
    [
        new Scenario(
            "fullscan",
            "full scan",
            ScanAll,
            p => RustReader.Require(RustReader.ScanCanonical(p), "scan")),
        new Scenario(
            "projected",
            "projected scan, 1 of 5 columns",
            ScanProjected,
            p => RustReader.Require(
                RustReader.ScanProjectedCanonical(p, Field), "projected scan")),
        new Scenario(
            "take",
            "scattered take, 64 of 64 splits",
            p => ScatteredTake(p, TakeCount, TakeStride),
            p => RustReader.Require(RustReader.Take(p, TakeCount, TakeStride), "take")),
        new Scenario(
            "write",
            "read and write back",
            ReadAndWrite,
            p => RustReader.Require(RustReader.Write(p), "write")),
    ];

    /// <summary>The scenario <paramref name="name"/> names, or null.</summary>
    /// <param name="name">A `--profile` name.</param>
    internal static Scenario? ByName(string name)
    {
        foreach (Scenario scenario in All)
        {
            if (string.Equals(scenario.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return scenario;
            }
        }

        return null;
    }

    /// <inheritdoc cref="Set.ScanAll"/>
    internal static Task<long> ScanAll(string path) => Set.ScanAll(path);

    /// <inheritdoc cref="Set.ScanProjected"/>
    internal static Task<long> ScanProjected(string path) => Set.ScanProjected(path);

    /// <inheritdoc cref="Set.ScatteredTake"/>
    internal static Task<long> ScatteredTake(string path, long count, long stride) =>
        Set.ScatteredTake(path, count, stride);

    /// <inheritdoc cref="Set.FilteredScan"/>
    internal static Task<long> FilteredScan(string path, long low, long width) =>
        Set.FilteredScan(path, low, width);

    /// <inheritdoc cref="Set.ReadAndWrite"/>
    internal static Task<long> ReadAndWrite(string path) => Set.ReadAndWrite(path);
}
