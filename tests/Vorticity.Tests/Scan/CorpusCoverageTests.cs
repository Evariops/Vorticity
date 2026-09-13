// Which corpus files this build cannot read, and why, as a number that only goes down.
//
// The conformance sweep reports "774 of 774 in-scope files read back value for value", which is a
// true statement about a set this build defines. 821 files ship. The 41 in the gap are the ones
// whose components are not implemented, and until now the only way to know what they were was to
// read the scope rule and work it out - so the cost of NOT implementing a decoder was invisible
// while the success rate stayed at 100%.
//
// A ratchet on the gap fixes that asymmetry. It goes down when a decoder lands and it goes red if a
// file the build used to read stops being in scope, which is the failure an in-scope-only sweep
// cannot see: dropping a registration moves a file out of scope and the sweep still says 100%.
//
// WHAT THE NUMBER IS NOT is a backlog. docs/01-scope.md §3 defers most of it BY DECISION, and
// reading the counts without reading that table gets the priorities exactly backwards - which is
// what happened when this test was first written:
//
//   vortex.map, vortex.variant, vortex.parquet.variant   35 files   "target 1.1"
//   vortex.pco                                            4 files   "target 1.1"
//   vortex.zstd_buffers                                   4 files   draft edition, decode once stable
//   fastlanes.delta                                       5 files   belongs to NO core edition
//   vortex.list layout, vortex.patched                    2 files   experimental upstream
//
// fastlanes.delta's four left the gap when it gained a decoder (49 iterations in), so 39 of the
// 41 are scope, not debt, and the largest single component - vortex.map at 22 files -
// is the one the scope document defers most explicitly. The ratchet is worth keeping for the
// regression direction; it is not a work queue.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>How much of the shipped corpus this build can read.</summary>
public sealed class CorpusCoverageTests
{
    /// <summary>
    /// Corpus files this build cannot read, at most.
    /// </summary>
    /// <remarks>
    /// LOWER THIS when a decoder or layout reader lands, in the same commit. Raising it means a file
    /// that used to be readable no longer is, which is a regression the conformance sweep reports as
    /// a clean 100% because the file simply leaves its denominator.
    /// </remarks>
    private const int OutOfScopeCeiling = 18;

    [Fact]
    public void TheUnreadablePartOfTheCorpusStaysWithinItsRatchet()
    {
        Decoders.EnsureRegistered();

        List<(string Id, string[] Missing)> unreadable = [];
        Dictionary<string, int> byComponent = [];
        foreach (CorpusEntry entry in CorpusManifest.All)
        {
            if (CorpusManifest.IsInScope(entry))
            {
                continue;
            }

            string[] missing = Missing(entry);
            unreadable.Add((entry.Id, missing));
            foreach (string component in missing)
            {
                byComponent[component] = byComponent.GetValueOrDefault(component) + 1;
            }
        }

        System.Text.StringBuilder report = new System.Text.StringBuilder();
        report.Append("CORPUS COVERAGE: ")
            .Append((CorpusManifest.All.Count - unreadable.Count).ToString(CultureInfo.InvariantCulture))
            .Append(" of ")
            .Append(CorpusManifest.All.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" files readable; ")
            .Append(unreadable.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" not (ceiling ")
            .Append(OutOfScopeCeiling.ToString(CultureInfo.InvariantCulture))
            .Append(")\n  blocked by:\n");

        foreach ((string component, int count) in byComponent.OrderByDescending(p => p.Value))
        {
            report.Append("    ")
                .Append(component.PadRight(34))
                .Append(count.ToString(CultureInfo.InvariantCulture).PadLeft(4))
                .Append(" files\n");
        }

        Console.Out.Write(report.ToString());

        Assert.True(
            unreadable.Count <= OutOfScopeCeiling,
            $"{unreadable.Count} corpus files are unreadable, ceiling {OutOfScopeCeiling}.\n{report}");
    }

    /// <summary>The components of <paramref name="entry"/> this build does not implement.</summary>
    private static string[] Missing(CorpusEntry entry)
    {
        List<string> missing = [];
        foreach (string id in entry.ArrayIds)
        {
            if (!CorpusManifest.IsInScope(Single(entry, array: id)))
            {
                missing.Add(id);
            }
        }

        foreach (string id in entry.LayoutIds)
        {
            if (!CorpusManifest.IsInScope(Single(entry, layout: id)))
            {
                missing.Add(id);
            }
        }

        foreach (string id in entry.ExtensionIds)
        {
            if (!CorpusManifest.IsInScope(Single(entry, extension: id)))
            {
                missing.Add(id);
            }
        }

        return [.. missing];
    }

    /// <summary>An entry carrying one component, so the scope rule answers about that component.</summary>
    private static CorpusEntry Single(
        CorpusEntry entry, string? array = null, string? layout = null, string? extension = null) =>
        new CorpusEntry(
            entry.Id,
            rowCount: 0,
            dtype: string.Empty,
            hasDTypeSegment: false,
            array is null ? [] : [array],
            layout is null ? [] : [layout],
            extension is null ? [] : [extension]);
}
