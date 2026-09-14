// Which corpus files this build cannot read, and why, as a number that only goes down.
//
// THE GAP IS CLOSED: 856 of the 856 shipped files are readable, and the ceiling is 0. What is left
// is the direction this was always worth more for. The conformance sweep reports "856 of 856
// IN-SCOPE files read back value for value", which is a true statement about a set THIS BUILD
// DEFINES: dropping a registration moves a file out of scope, the file leaves the denominator, and
// the sweep still says 100%. This test counts the files that left, so that failure has somewhere to
// show up.
//
// It was written when 41 files were in the gap, as a ratchet on a backlog, and the header then
// carried a table of what blocked them - vortex.map at 22 files, pco at 4, zstd_buffers at 4,
// fastlanes.delta at 5. All of it is gone. Keeping a ceiling of 8 after the last one landed would
// mean eight decoders could be dropped without a test going red, which is the opposite of what this
// file is for: a ratchet that is never lowered defends nothing.
//
// AT ZERO THE CEILING IS AN EQUALITY, and that is deliberate. Every file the repository ships is
// readable; a new corpus file carrying a component this build does not decode turns this red, which
// is a decision to make rather than a number to relax. docs/01-scope.md §3 is where such a decision
// is written down; raising the ceiling instead would hide it.
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
    /// a clean 100% because the file simply leaves its denominator. It is at the floor: every shipped
    /// file is readable, so the only move left is up, and up is a scope decision (docs/01-scope.md
    /// §3) rather than a test adjustment.
    /// </remarks>
    private const int OutOfScopeCeiling = 0;

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
            .Append(")\n");

        if (unreadable.Count == 0)
        {
            report.Append("  every shipped file is in scope; nothing is blocked.\n");
        }
        else
        {
            report.Append("  blocked by:\n");
            foreach ((string component, int count) in byComponent.OrderByDescending(p => p.Value))
            {
                report.Append("    ")
                    .Append(component.PadRight(34))
                    .Append(count.ToString(CultureInfo.InvariantCulture).PadLeft(4))
                    .Append(" files\n");
            }
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
