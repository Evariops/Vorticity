// What the memory refactoring still has to take out, counted where it lies: each line of Residue.txt is a pattern and its
// occurrences in src, tests, bench (with samples and tools) and docs. A commit that takes some out lowers its line in the same
// commit, a line at zero leaves the file, and nothing may grow. The test goes once the file is empty.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorticity.Tests.Residue;

public sealed class ResidueTests
{
    private const string FileName = "Residue.txt";

    private static readonly string[] Areas = ["src", "tests", "bench", "docs"];

    private static readonly string[] Extensions = [".cs", ".md", ".csproj", ".props", ".targets", ".sh", ".py", ".json"];

    [Fact]
    public void Every_residue_is_where_its_line_says_and_none_grew()
    {
        string here = Here();
        string root = Path.GetFullPath(Path.Combine(here, "..", "..", ".."));
        List<(string Pattern, long[] Counts)> lines = Read(Path.Combine(here, FileName));
        Dictionary<string, string[]> files = Areas.ToDictionary(area => area, area => Files(root, area, here));

        StringBuilder actual = new StringBuilder();
        List<string> differences = [];
        foreach ((string pattern, long[] counts) in lines)
        {
            Regex regex = new Regex(pattern, RegexOptions.CultureInvariant);
            long[] found = [.. Areas.Select(area => files[area].Sum(file => (long)regex.Count(System.IO.File.ReadAllText(file))))];
            if (found.Any(count => count > 0))
            {
                actual.Append(pattern).Append('\t').AppendJoin('\t', found).Append('\n');
            }

            for (int i = 0; i < Areas.Length; i++)
            {
                if (found[i] > counts[i])
                {
                    differences.Add($"{pattern} grew in {Areas[i]}: {found[i]} where the file says {counts[i]}");
                }
                else if (found[i] < counts[i])
                {
                    differences.Add($"{pattern} went down in {Areas[i]}: {found[i]} where the file says {counts[i]}; lower its line");
                }
            }
        }

        if (differences.Count > 0)
        {
            string actualPath = Path.Combine(here, FileName + ".actual");
            System.IO.File.WriteAllText(actualPath, Header + actual);
            Assert.Fail(string.Join('\n', differences) + $"\nThe counts as they stand are in {actualPath}.");
        }
    }

    private const string Header =
        "# What the memory refactoring still has to take out (PLAN-MEMORY, section 7): a pattern, then its occurrences in\n" +
        "# src, tests, bench (with samples and tools) and docs, separated by tabs. Lowered in the commit that takes some out,\n" +
        "# removed at zero, never raised.\n";

    private static List<(string Pattern, long[] Counts)> Read(string path)
    {
        List<(string, long[])> lines = [];
        foreach (string line in System.IO.File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.True(parts.Length == Areas.Length + 1, $"A line of {FileName} is a pattern and {Areas.Length} counts, separated by tabs: {line}");
            lines.Add((parts[0], [.. parts.Skip(1).Select(long.Parse)]));
        }

        return lines;
    }

    private static string[] Files(string root, string area, string here)
    {
        IEnumerable<string> directories = area == "bench"
            ? ["bench", "samples", "tools"]
            : [area];
        return
        [
            .. directories
                .Select(directory => Path.Combine(root, directory))
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
                .Where(file => !Generated(file) && !file.StartsWith(here, StringComparison.Ordinal)),
        ];
    }

    /// <summary>Build output and dependencies, which no commit writes.</summary>
    private static bool Generated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal);

    private static string Here([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"No directory for '{path}'.");
}
