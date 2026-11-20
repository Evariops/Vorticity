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
        (Writer, @"\bSizeWith\b", 2),
        (Dataset, @"\bSizeWith\b", 2),
        (Writer, @"\bHeld\(out\b", 3),
        (Writer, @"\bHeldMeasured\b", 3),
        (Writer, @"\bMeasuresLate\b", 1),
        (Dataset, @"\bMeasuresLate\b", 2),
        (WriterFile, @"\bMeasureAsync\b", 4),
        (Compactor, @"\bMeasureAsync\b", 7),
        (Writer, @"\b_restRate\b", 7),
        (Writer, @"\bAsItCame\b", 20),
        (Emissions, @"\bRecount\b", 4),
        (Writer, @"\bSizeAtFlight\b", 3),
        (Writer, @"\b_sized\b", 12),
        (Writer, @"\b_sizedBytes\b", 4),
        (Writer, @"\b_sizedRate\b", 4),
        (Writer, @"\bLanding\b", 9),
        (Writer, @"\bLanded\(", 3),
        (Writer, @"\bLandedAtCut\b", 2),
        (Writer, @"\bRecordLand\b", 6),
        (Writer, @"\b_flownBytes\b", 4),
        (Writer, @"\b_landBase\b", 6),
        (Writer, @"\b_landStart\b", 3),
        (Writer, @"\b_lands\b", 6),
        (Writer, @"\binternal long Size\b", 1),
        (Writer, @"\bLandLag\b", 9),
        (Dataset, @"\bLandLag\b", 2),
        (Dataset, @"\bLandLagOf\b", 2),
        (WriterFile, @"\bEndBytes\b", 2),
        (Compactor, @"\bEndBytes\b", 5),
        (Compactor, @"\b_endBytes\b", 5),
        (Compactor, @"\b_endRows\b", 4),
        (Compactor, @"\bRollsUnmeasured\b", 2),
        (Compactor, @"\bMeasuredRollAsync\b", 2),
        (Compactor, @"\b_endsBySize\b", 5),
        (Compactor, @"\bRestarted\b", 4),
        (Compactor, @"\b_measured\b", 5),
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
