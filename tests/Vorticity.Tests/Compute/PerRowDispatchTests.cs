// The rule "no ReadInteger/ReadUnsigned/WriteInteger inside a per-row loop", made executable.
//
// PERF-AUDIT-v2.md F3. The three helpers take a `PType` and an index and switch on the type to
// decide how to read one value; calling them per row pays that switch per row, for a property of
// the CALL, not of the row. `ComparisonKernels` is the measurement that makes the rule worth
// having: hoisting the type switch, the validity switch and the operator switch out of the loop was
// 97.2 us to 16.1 us on 65 536 rows of `i64 < literal` -- six times -- and the same shape is still
// present in 27 other places.
//
// The rule was written down in the audit and enforced nowhere, which means it held exactly as long
// as whoever remembered it was reviewing. This is the ratchet that remembers.
//
// WHAT IT COUNTS, AND WHY THAT AND NOT THE RULE ITSELF. "Is this call inside a per-row loop" is not
// decidable by reading a line; it needs the loop around it. So this counts something coarser and
// mechanical -- every CALL to the three helpers in `src/Vorticity`, per file -- and holds each
// file at a ceiling. The per-row subset is carried in the same table, beside the point that will
// remove it, and comes down with the total when phase 2 wires a site onto a typed kernel.
//
// The coarseness costs one thing and buys another. It costs a false red when a call is added in a
// legitimate category: a bounds check, a single value, an error path. That red is a one-line table
// edit plus a sentence in the commit saying which category the new call is in -- which is the
// classification the audit's annexe A had to do by hand, now demanded at the moment it is cheap.
// It buys the case the rule exists for: a `ReadInteger` added inside a loop turns a file red on the
// commit that does it, rather than being found by a profile a month later.
//
// WHAT IT DOES NOT COUNT, deliberately:
//
//   * `CanonicalSupport.cs`, which DEFINES `ReadInteger`. A definition is not a dispatch.
//   * comment lines, including `<c>ReadInteger</c>` in a doc comment. Counting prose would make a
//     ratchet that goes red when someone explains the rule it enforces, and annexe A's own "66"
//     was reproduced here only after excluding them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>Holds the per-row type dispatch of PERF-AUDIT-v2.md annexe A at a descending count.</summary>
public sealed partial class PerRowDispatchTests
{
    /// <summary>A call to one of the three, not a mention of one.</summary>
    [GeneratedRegex(@"\b(ReadInteger|ReadUnsigned|WriteInteger)\s*\(")]
    private static partial Regex Call();

    /// <summary>
    /// Per file: the calls it may contain, how many of those are in a per-row loop, and the point
    /// that will remove them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LOWER BOTH NUMBERS BY HAND when a site is wired, in the same commit, exactly as
    /// <c>WrittenSizeTests</c> and <c>PathAllocationTests</c> are lowered. Raising one needs the
    /// commit message to say which of annexe A's four categories the new call belongs to.
    /// </para>
    /// <para>
    /// THE CEILING IS THE EXACT COUNT, with no margin, because this quantity has no variance at all
    /// -- it is a property of the source text. A margin here would only be room for an unnoticed
    /// call to hide in.
    /// </para>
    /// <para>
    /// The <c>PerRow</c> column is the audit's classification, not a measurement: nothing here can
    /// check that a given call is inside a loop. It is asserted only to be no larger than the
    /// file's total, which keeps the table coherent; its real job is to say what the total is
    /// MADE OF, so that lowering it is a decision and not an accident.
    /// </para>
    /// </remarks>
    private static readonly (string File, int Calls, int PerRow, string Point)[] Sites =
    [
        ("Arrays/Decoders/Canonical/CanonicalConcat.cs", 4, 4, "R7"),
        ("Arrays/Decoders/Canonical/ConstantCanonicalizer.cs", 1, 0, "legitimate: one write, then Tile"),
        ("Arrays/Decoders/Canonical/ListDecoder.cs", 2, 0, "legitimate: bounds"),
        ("Arrays/Decoders/Canonical/VarBinDecoder.cs", 2, 0, "legitimate: bounds"),
        ("Arrays/Decoders/Canonical/ViewKernels.cs", 4, 0, "legitimate: one bound, three error paths"),
        ("Arrays/Decoders/Compressed/AlpRdDecoder.cs", 1, 1, "R4"),
        ("Arrays/Decoders/Compressed/CompressedValues.cs", 2, 0, "the definitions of ReadUnsigned/WriteInteger"),
        ("Arrays/Decoders/Compressed/DictDecoder.cs", 1, 0, "legitimate: an error message"),
        ("Arrays/Decoders/Compressed/FastLanesRleDecoder.cs", 5, 0, "per run, not per row"),
        ("Arrays/Decoders/Compressed/FsstDecoder.cs", 5, 3, "R11"),
        ("Arrays/Decoders/Compressed/OnPairDecoder.cs", 9, 3, "R2"),
        ("Arrays/Decoders/Compressed/Patches.cs", 3, 1, "R4 (GetPosition); the other two are per run"),
        ("Arrays/Decoders/Compressed/RunEndDecoder.cs", 4, 0, "per run, not per row"),
        ("Columns/ExtensionColumn.cs", 1, 0, "legitimate: the column API is per row by design"),
        ("Columns/ListColumn.cs", 2, 0, "legitimate: the column API is per row by design"),
        ("Columns/VortexColumn.cs", 1, 0, "legitimate: the column API is per row by design"),
        ("Compute/ComparisonKernels.cs", 2, 2, "F-4"),
        ("Compute/LiteralReader.cs", 2, 0, "legitimate: a single literal"),
        ("Layouts/DictLayoutReader.cs", 1, 0, "R6 done: the one left is an error path"),
        ("Types/Variant/ParquetVariant.cs", 2, 0, "a local method of the same name, not these"),
        ("Writing/ArrayBlobWriter.cs", 1, 1, "W-13"),
        ("Writing/BitPackPlan.cs", 4, 4, "W-11"),
    ];

    /// <summary>The grand totals, which are annexe A's two headline numbers.</summary>
    /// <remarks>
    /// 66 when this test was written; **64 since R7** removed both calls from
    /// <c>ListViewDecoder.ValidateRanges</c>, which no longer appears here at all — the file went
    /// to zero calls and its row went with it, which is the shape a wired site is supposed to make.
    ///
    /// **62 since W-9**, which did the same to <c>SequencePlan</c>: its `Read` helper was the two
    /// calls, and typing the walk removed the helper.
    ///
    /// **60 since stage 1 of docs/11-write-strategy.md §8**, which deleted <c>ZoneStatistics</c>
    /// outright. Its replacement, <c>BlockStatsPass</c>, resolves the physical type once into a
    /// generic instantiation and reads the constant form's single element through
    /// <c>BinaryPrimitives</c>, so the file does not appear in this table at all — the shape W-12
    /// asked for.
    ///
    /// **59 since stage 2g**: <c>BitPackPlan.Collect</c> counted the values a width cannot hold by
    /// walking the column, to size the arrays it was about to fill by walking it again. The count
    /// was already in the histogram the chooser had just priced the width from, so the first walk
    /// went — and with it the last site in this file that was not the encode itself.
    /// </remarks>
    private const int TotalCalls = 59;

    /// <summary>Calls annexe A classifies as being inside a per-row or per-patch loop.</summary>
    /// <remarks>
    /// 27 when this test was written; **26 since R6** wired `DictLayoutReader.Gather` onto
    /// `RowKernels`. The file still has one call and the ceiling is still 1, but its COMPOSITION
    /// changed -- what is left is `ThrowCode`, an error path -- and that is the change this column
    /// exists to record. A total that only ever moved with the ceiling would have missed it.
    ///
    /// **24 since R7**: `ListViewDecoder.ValidateRanges` resolves both physical types before its
    /// loop, so its two per-row calls went away with its ceiling.
    ///
    /// **22 since W-9**: `SequencePlan.TryBuild` resolves its physical type before the walk, and
    /// its two calls went the same way. This is the first WRITE-side site the ratchet has seen
    /// leave.
    ///
    /// **20 since stage 1 of docs/11-write-strategy.md §8**: `ZoneStatistics` was two per-row calls
    /// in a two-pass summariser, and the whole file is gone. W-12 is closed.
    ///
    /// **19 since stage 2g**: the counting walk of `BitPackPlan.Collect` was one of them.
    /// </remarks>
    private const int TotalPerRow = 19;

    [Fact]
    public void NoFileDispatchesPerRowMoreOftenThanItsCeiling()
    {
        Dictionary<string, int> counted = Count();
        StringBuilder report = new StringBuilder(
            "PER-ROW DISPATCH: calls to ReadInteger/ReadUnsigned/WriteInteger in src/Vorticity\n");

        List<string> bad = [];
        int calls = 0;
        int perRow = 0;
        foreach ((string file, int ceiling, int rows, string point) in Sites)
        {
            int actual = counted.GetValueOrDefault(file);
            counted.Remove(file);
            calls += ceiling;
            perRow += rows;

            report.Append("    ")
                .Append(file.PadRight(58))
                .Append(actual.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append(" calls   ceiling ")
                .Append(ceiling.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append("   per-row ")
                .Append(rows.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append("   ")
                .Append(point)
                .Append('\n');

            if (actual > ceiling)
            {
                bad.Add(
                    $"{file} has {actual} calls against a ceiling of {ceiling}. If the new one is " +
                    "inside a per-row loop it is the thing this test exists to stop; if it is a " +
                    "bound, a single value or an error path, raise the ceiling here and say which " +
                    "in the commit message (PERF-AUDIT-v2.md annexe A has the four categories).");
            }

            if (actual < ceiling)
            {
                bad.Add(
                    $"{file} has {actual} calls against a ceiling of {ceiling}: a site went away " +
                    "and the ratchet did not follow. Lower it here, and lower the per-row count " +
                    $"with it if the site that went was one of the {rows} ({point}).");
            }

            if (rows > ceiling)
            {
                bad.Add($"{file} claims {rows} per-row calls out of {ceiling}, which cannot be.");
            }
        }

        // A file that has calls and no row in the table: a new dispatch site in a file nobody
        // classified. It is red for the same reason a new one in a listed file is.
        foreach ((string file, int actual) in counted.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            bad.Add(
                $"{file} has {actual} call(s) and no entry in this table. Classify it against " +
                "PERF-AUDIT-v2.md annexe A and add a row.");
        }

        Assert.Equal(TotalCalls, calls);
        Assert.Equal(TotalPerRow, perRow);

        Console.Out.Write(report.ToString());
        Assert.True(bad.Count == 0, string.Join("\n", bad) + "\n" + report);
    }

    /// <summary>Every call in <c>src/Vorticity</c>, by path relative to it.</summary>
    private static Dictionary<string, int> Count()
    {
        string root = Sources();
        Dictionary<string, int> counted = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) ||
                relative.StartsWith("bin/", StringComparison.Ordinal) ||
                relative == "Arrays/Decoders/Canonical/CanonicalSupport.cs")
            {
                continue;
            }

            int calls = 0;
            foreach (string line in System.IO.File.ReadLines(path))
            {
                string text = line.TrimStart();
                if (text.StartsWith("//", StringComparison.Ordinal) ||
                    text.StartsWith('*') ||
                    text.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                calls += Call().Matches(line).Count;
            }

            if (calls > 0)
            {
                counted[relative] = calls;
            }
        }

        return counted;
    }

    // `src/Vorticity`, found the way `Corpus` finds the corpus: by walking up from this source
    // file rather than from the output directory, which says nothing about where the sources are.
    private static string Sources([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "src", "Vorticity");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Vorticity above '" + thisFile + "'.");
    }
}
