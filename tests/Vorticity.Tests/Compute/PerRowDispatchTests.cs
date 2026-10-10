// The rule "no ReadInteger/ReadUnsigned/WriteInteger inside a per-row loop", made executable.
//
// The three helpers take a `PType` and an index and switch on the type to
// decide how to read one value; calling them per row pays that switch per row, for a property of
// the CALL, not of the row. `ComparisonKernels` is what makes the rule worth having: hoisting the
// type switch, the validity switch and the operator switch out of its loop made `i64 < literal`
// several times faster, and every per-row call counted below has the same shape.
//
// A rule that is only written down holds exactly as long as whoever remembers it is reviewing.
// This is the ratchet that remembers.
//
// WHAT IT COUNTS, AND WHY THAT AND NOT THE RULE ITSELF. "Is this call inside a per-row loop" is not
// decidable by reading a line; it needs the loop around it. So this counts something coarser and
// mechanical -- every CALL to the three helpers in `src/Vorticity` and the Parquet package, per
// file -- and holds each file at a ceiling. The per-row subset is carried in the same table, beside
// what will remove it, and comes down with the total when a site is wired onto a typed kernel.
//
// The coarseness costs one thing and buys another. It costs a false red when a call is added in a
// legitimate category: a bounds check, a single value, an error path. That red is a one-line table
// edit plus a sentence in the commit saying which category the new call is in -- a classification
// demanded at the moment it is cheap, rather than done by hand over the whole library later.
// It buys the case the rule exists for: a `ReadInteger` added inside a loop turns a file red on the
// commit that does it, rather than being found by a profile a month later.
//
// WHAT IT DOES NOT COUNT, deliberately:
//
//   * `CanonicalSupport.cs`, which DEFINES `ReadInteger`. A definition is not a dispatch.
//   * comment lines, including `<c>ReadInteger</c>` in a doc comment. Counting prose would make a
//     ratchet that goes red when someone explains the rule it enforces.
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

/// <summary>Holds the library's per-row type dispatch at a descending count.</summary>
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
    /// commit message to say which category the new call belongs to: a per-row loop, a bound, a
    /// single value or an error path.
    /// </para>
    /// <para>
    /// THE CEILING IS THE EXACT COUNT, with no margin, because this quantity has no variance at all
    /// -- it is a property of the source text. A margin here would only be room for an unnoticed
    /// call to hide in.
    /// </para>
    /// <para>
    /// The <c>PerRow</c> column is a classification made by reading the code, not a
    /// measurement: nothing here can check that a given call is inside a loop. It is asserted only
    /// to be no larger than the file's total, which keeps the table coherent; its real job is to
    /// say what the total is MADE OF, so that lowering it is a decision and not an accident.
    /// </para>
    /// </remarks>
    private static readonly (string File, int Calls, int PerRow, string Point)[] Sites =
    [
        ("Arrays/Decoders/Canonical/CanonicalConcat.cs", 0, 0, "R7 done: a list view's offsets and sizes are read with their types resolved once a chunk"),
        ("Arrays/Decoders/Canonical/ConstantCanonicalizer.cs", 1, 0, "legitimate: one write, then Tile"),
        ("Arrays/Decoders/Canonical/ListDecoder.cs", 2, 0, "legitimate: bounds"),
        ("Arrays/Decoders/Canonical/VarBinDecoder.cs", 2, 0, "legitimate: bounds"),
        ("Arrays/Decoders/Canonical/ViewKernels.cs", 4, 0, "legitimate: four error paths"),
        ("Arrays/Decoders/Compressed/AlpRdDecoder.cs", 0, 0, "R4 done: the patch values are read at their own width"),
        ("Arrays/Decoders/Compressed/CompressedValues.cs", 2, 0, "the definitions of ReadUnsigned/WriteInteger"),
        ("Arrays/Decoders/Compressed/DictDecoder.cs", 1, 0, "legitimate: an error message"),
        ("Arrays/Decoders/Compressed/EncodedNodes.cs", 3, 0, "per run, not per row: the binary search for a slice's run bounds and the rebased ends; one error message"),
        ("Arrays/Decoders/Compressed/FastLanesRleDecoder.cs", 5, 0, "per run, not per row"),
        ("Arrays/Decoders/Compressed/FsstDecoder.cs", 2, 0, "R11 done: a selection's offsets and lengths are read at their types, resolved once; the two left bound the code stream"),
        ("Arrays/Decoders/Compressed/OnPairDecoder.cs", 6, 3, "R2; the dictionary's two walks are typed, and the one call left there refuses a type that is not an integer"),
        ("Arrays/Decoders/Compressed/Patches.cs", 3, 1, "R4 (GetPosition); the other two are per run"),
        ("Arrays/Decoders/Compressed/RunEndDecoder.cs", 2, 0, "per run, not per row: the walk of a shape with no typed kernel, and the search for a range's first run; the ends' checks and a take's searches read them typed"),
        ("Columns/ExtensionColumn.cs", 1, 0, "legitimate: the column API is per row by design"),
        ("Columns/ListColumn.cs", 2, 0, "legitimate: the column API is per row by design"),
        ("Columns/VortexColumn.cs", 1, 0, "legitimate: the column API is per row by design"),
        ("Compute/ComparisonKernels.cs", 0, 0, "F-4 done: the integer-against-float pair joined CompareOp"),
        ("Compute/LiteralReader.cs", 2, 0, "legitimate: a single literal"),
        ("Layouts/DictLayoutReader.cs", 1, 0, "R6 done: the one left is an error path"),
        ("Types/Variant/ParquetVariant.cs", 2, 0, "a local method of the same name, not these"),
        ("Writing/ArrayBlobWriter.cs", 0, 0, "W-13 done: a block with nulls takes the lanes and clears its nulls by their bits"),
        ("Writing/BitPackPlan.cs", 0, 0, "W-11 done: the minimum is read at the element's own width"),
        ("Vorticity.Parquet/Metadata/SchemaElement.cs", 2, 0, "a local method of the same name, not these"),
    ];

    /// <summary>The table's two grand totals, starting with every call it allows.</summary>
    /// <remarks>
    /// A wired site resolves the physical type once, before its walk; the shape it is supposed to
    /// make is its file going to zero calls.
    /// </remarks>
    private const int TotalCalls = 44;

    /// <summary>Calls the table classifies as being inside a per-row or per-patch loop.</summary>
    /// <remarks>
    /// It can drop while a file's ceiling stays put, when a per-row call is wired and the call
    /// left in the file is an error path: that change of COMPOSITION is what this column exists
    /// to record.
    /// </remarks>
    private const int TotalPerRow = 4;

    [Fact]
    public void NoFileDispatchesPerRowMoreOftenThanItsCeiling()
    {
        Dictionary<string, int> counted = Count();
        StringBuilder report = new StringBuilder(
            "PER-ROW DISPATCH: calls to ReadInteger/ReadUnsigned/WriteInteger in src/Vorticity and src/Vorticity.Parquet\n");

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
                    "in the commit message.");
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
                $"{file} has {actual} call(s) and no entry in this table. Classify it (per-row " +
                "loop, bound, single value or error path) and add a row.");
        }

        Assert.Equal(TotalCalls, calls);
        Assert.Equal(TotalPerRow, perRow);

        Console.Out.Write(report.ToString());
        Assert.True(bad.Count == 0, string.Join("\n", bad) + "\n" + report);
    }

    /// <summary>
    /// Every call in <c>src/Vorticity</c>, by path relative to it, and in the Parquet package, which
    /// reads and writes through the same canonical forms, by path behind the package's name.
    /// </summary>
    private static Dictionary<string, int> Count()
    {
        string core = Sources();
        Dictionary<string, int> counted = [];
        Count(core, string.Empty, counted);
        Count(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(core)!, "Vorticity.Parquet"), "Vorticity.Parquet/", counted);
        return counted;
    }

    private static void Count(string root, string prefix, Dictionary<string, int> counted)
    {
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
                counted[prefix + relative] = calls;
            }
        }
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
