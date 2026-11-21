using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The residues of a mechanism being taken out of the sources: for each, the lines of the sources that still name it, which
/// only ever go down. A step that takes some out lowers its count here in the same commit; a count at zero stays, so that
/// nothing brings the mechanism back by the same name.
/// </summary>
/// <remarks>
/// Each residue is a pattern over the lines of one file or of the files of one directory, not its subdirectories, so that a
/// name the mechanism shares with another one elsewhere is counted where the mechanism lives only.
/// </remarks>
public sealed class ResidueTests
{
    private const string Writer = "src/Vorticity/Vortex/Writing";
    private const string WriterFile = "src/Vorticity/Vortex/Writing/VortexFileWriter.cs";
    private const string Emissions = "src/Vorticity/Vortex/Writing/VortexFileWriter.Emissions.cs";
    private const string Compactor = "src/Vorticity.Dataset/DatasetCompactor.cs";
    private const string Dataset = "src/Vorticity.Dataset";

    /// <summary>
    /// The writer's estimate of its own size, which a job ended its objects on, and what kept it right: the lag of the
    /// flights read back, the late measure of the columns no chunk measured, the rate of the rows that wait, the end of the
    /// file counted ahead (PLAN-COMPACTION-GROUPS, lot A). A job now ends an object on what the file holds at the end of a
    /// group of rows.
    /// </summary>
    private static readonly (string Where, string Pattern, int Lines)[] Residues =
    [
        (Writer, @"\bSizeWith\b", 0),
        (Dataset, @"\bSizeWith\b", 0),
        (Writer, @"\bHeld\(out\b", 0),
        (Writer, @"\bHeldMeasured\b", 0),
        (Writer, @"\bMeasuresLate\b", 0),
        (Dataset, @"\bMeasuresLate\b", 0),
        (WriterFile, @"\bMeasureAsync\b", 0),
        (Compactor, @"\bMeasureAsync\b", 0),
        (Writer, @"\b_restRate\b", 0),
        (Writer, @"\bAsItCame\b", 0),
        (Emissions, @"\bRecount\b", 0),
        (Writer, @"\b_emittingBytes\b", 0),
        (Writer, @"\bSizeAtFlight\b", 0),
        (Writer, @"\b_sized\b", 0),
        (Writer, @"\b_sizedBytes\b", 0),
        (Writer, @"\b_sizedRate\b", 0),
        (Writer, @"\bLanding\b", 0),
        (Writer, @"\bLanded\(", 0),
        (Writer, @"\bLandedAtCut\b", 0),
        (Writer, @"\bRecordLand\b", 0),
        (Writer, @"\b_flownBytes\b", 0),
        (Writer, @"\b_landBase\b", 0),
        (Writer, @"\b_landStart\b", 0),
        (Writer, @"\b_lands\b", 0),
        (Writer, @"\bLandRing\b", 0),
        (Writer, @"\binternal long Size\b", 0),
        (Writer, @"\bLandLag\b", 0),
        (Dataset, @"\bLandLag\b", 0),
        (Dataset, @"\bLandLagOf\b", 0),
        (WriterFile, @"\bEndBytes\b", 0),
        (Compactor, @"\bEndBytes\b", 0),
        (Compactor, @"\b_endBytes\b", 0),
        (Compactor, @"\b_endRows\b", 0),
        (Compactor, @"\bRollsUnmeasured\b", 0),
        (Compactor, @"\bMeasuredRollAsync\b", 0),
        (Compactor, @"\bRestarted\b", 0),
        (Compactor, @"\b_measured\b", 0),

        // The ranges of a job's merge, each written by a writer of its own into objects of its own, side by side, and the rule
        // that cut a merge in as many as divided its objects evenly: given up on 2026-11-21, a job writing one stream.
        (Dataset, @"\bRangesOf\b", 0),
        (Dataset, @"\bRangeRows\b", 0),
        (Dataset, @"\bMostRanges\b", 0),
        (Dataset, @"\bRangesAsync\b", 0),
        (Dataset, @"\bgatherShare\b", 0),

        // The readings of an object of a line's blocks a quarter of its rows apart, placed by what a row of the job's inputs
        // weighed and by the last stretch's weight of a row: given up on 2026-11-21 for what the object's blocks name.
        (Compactor, @"\b_stretchRate\b", 0),
        (Compactor, @"\b_inputBytesPerRow\b", 0),

        // What a job priced an input by: the measure of one input before a handle's first merge, what the last job's inputs
        // had held a row of their batch, the first guess before any, and the batches repriced as the inputs opened. Given up
        // on 2026-11-21 for what each object's entry says a reader holds of it open, summed over the inputs open at once.
        (Dataset, @"\bMeasureInputAsync\b", 0),
        (Dataset, @"\bPriceAsync\b", 0),
        (Dataset, @"\bHeldPerRow\b", 0),
        (Dataset, @"\bFirstGuessPerBatchByte\b", 0),
        (Dataset, @"\bInputCost\b", 0),
        (Dataset, @"\bInputsBytes\b", 0),
        (Dataset, @"\bReprice\b", 0),
        (Dataset, @"\bInputRows\b", 0),
        (Dataset, @"\bOpenAtOnce\b", 0),
        (Dataset, @"\bCapJobWidth\b", 0),
        (Dataset, @"\bLearnInputCost\b", 0),

        // What gave a job up and narrowed the next: a job refused a block, its exception, the caps it set on the next job's
        // inputs and batches, the costs learned from the job before, a guess of three times its open chunks for an object
        // whose entry says nothing. Given up on 2026-11-21: each input of a job reserves what it is about to hold as it
        // reads it in its file, and a merge short of room ends at the key it reached and commits what it merged.
        (Dataset, @"\bJobTooWideException\b", 0),
        (Dataset, @"\bNarrowJobs\b", 0),
        (Dataset, @"\bNarrowed\b", 0),
        (Dataset, @"\bJobWidthCap\b", 0),
        (Dataset, @"\bJobSizingCap\b", 0),
        (Dataset, @"\bLearnJobCosts\b", 0),
        (Dataset, @"\bInputJobBytes\b", 0),
        (Dataset, @"\bJobBatchRows\b", 0),
        (Dataset, @"\bRefusalReason\b", 0),
        (Dataset, @"\bUnsaidHeldPerEncoded\b", 0),
        (Dataset, @"\bLearnOpen\b", 0),
    ];

    [Fact]
    public void EveryResidueIsCountedAtWhatTheSourcesStillHold()
    {
        string root = RepositoryRoot();
        List<string> moved = [];
        foreach ((string where, string pattern, int lines) in Residues)
        {
            int found = Count(root, where, new Regex(pattern, RegexOptions.CultureInvariant));
            if (found != lines)
            {
                moved.Add($"{where} {pattern}: {found} line(s) where {lines} are counted");
            }
        }

        Assert.True(moved.Count == 0, "Residues moved; a count only goes down, lowered here as the code goes:\n" + string.Join('\n', moved));
    }

    /// <summary>The lines of the file <paramref name="where"/>, or of the files of the directory, that match <paramref name="pattern"/>.</summary>
    private static int Count(string root, string where, Regex pattern)
    {
        string path = Path.Combine(root, where);
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.cs", SearchOption.TopDirectoryOnly)
            : System.IO.File.Exists(path) ? [path] : [];
        int count = 0;
        foreach (string file in files)
        {
            count += System.IO.File.ReadLines(file).Count(line => pattern.IsMatch(line));
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "Vorticity.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No Vorticity.slnx above {AppContext.BaseDirectory}.");
    }
}
