using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using Xunit;

namespace Vorticity.Tests.Style;

/// <summary>
/// Counts, per rule, the comments that depart from the repository's comment charter.
/// </summary>
/// <remarks>
/// <para>
/// A comment informs whoever reads or calls the code. It is not the project's record of how the
/// code got here: that belongs in the commit and in the maintainers' journals, and a reader of a
/// published library has neither. This test finds the five shapes that cross the line, prints
/// where each one is, and reports rather than failing, so that a sweep can be done directory by
/// directory against a number that goes down.
/// </para>
/// <para>
/// Its patterns live in a file beside it rather than in this source, for two reasons. A whitelist
/// of acronyms grows as the vocabulary does, and that should be a one-line change a reviewer can
/// read. And the patterns themselves are pointers and dates, which is exactly what rule three
/// forbids in a comment: keeping them here would make the detector fail itself.
/// </para>
/// </remarks>
public sealed class CommentCharterTests
{
    private const string RulesFileName = "CommentCharter.txt";

    /// <summary>The directories the charter covers, relative to the repository root.</summary>
    private static readonly string[] Roots = ["src", "tests", "bench", "tools", "spec"];

    /// <summary>How many sites to print per rule before the list is cut.</summary>
    private const int Shown = 12;

    private enum Rule
    {
        Header,
        Capitals,
        Reference,
        History,
        Inheritdoc,
    }

    private sealed record Site(string Path, int Line, Rule Rule, string Text);

    [Fact]
    public void EveryDepartureFromTheCharterIsCounted()
    {
        Charter charter = Charter.Load(Path.Combine(SourceDirectory(), RulesFileName));
        string root = RepositoryRoot();

        List<Site> sites = [];
        int files = 0;
        foreach (string path in SourceFiles(root))
        {
            files++;
            string[] lines = System.IO.File.ReadAllLines(path);
            string relative = Path.GetRelativePath(root, path);
            if (Path.GetExtension(path) == ".sh")
            {
                InspectScript(relative, lines, charter, sites);
            }
            else
            {
                InspectSource(relative, lines, charter, sites);
            }
        }

        Report(files, sites);

        Assert.True(files > 700, $"Only {files} files were inspected; the walk found the wrong tree.");
    }

    /// <summary>
    /// The whole sweep rests on telling a comment from a string that looks like one, so that
    /// distinction is checked here rather than assumed.
    /// </summary>
    [Fact]
    public void TheCommentReaderTellsACommentFromAStringThatLooksLikeOne()
    {
        string[] lines =
        [
            @"var a = ""// not a comment"";",
            @"var b = @""/* still not one */"";",
            @"// a comment",
            @"var c = '\''; // after a character literal",
            @"/* opens here",
            @"   and closes here */ var d = 1;",
        ];

        (int Line, string Text)[] found = Comments(lines).ToArray();

        Assert.Equal(new[] { 3, 4, 5, 6 }, found.Select(f => f.Line).ToArray());
        Assert.DoesNotContain(found, f => f.Text.Contains("not a comment", StringComparison.Ordinal));
        Assert.DoesNotContain(found, f => f.Text.Contains("still not one", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Text.Contains("after a character literal", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Text.Contains("and closes here", StringComparison.Ordinal));
    }

    /// <summary>Prints the counts per rule, then per directory, then a sample of each rule.</summary>
    private static void Report(int files, List<Site> sites)
    {
        StringBuilder report = new StringBuilder()
            .Append("COMMENT CHARTER: ")
            .Append(sites.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" site(s) over ")
            .Append(files.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" files.");

        foreach (Rule rule in Enum.GetValues<Rule>())
        {
            report.Append("  ")
                .Append(rule.ToString().ToLowerInvariant().PadRight(12))
                .Append(sites.Count(s => s.Rule == rule).ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        report.AppendLine("  by directory:");
        foreach (IGrouping<string, Site> group in sites
            .GroupBy(Bucket)
            .OrderByDescending(g => g.Count()))
        {
            report.Append("    ")
                .Append(group.Key.PadRight(44))
                .Append(group.Count().ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        foreach (Rule rule in Enum.GetValues<Rule>())
        {
            List<Site> ofRule = sites.Where(s => s.Rule == rule).ToList();
            if (ofRule.Count == 0)
            {
                continue;
            }

            report.Append("  ").Append(rule.ToString().ToLowerInvariant()).AppendLine(":");
            foreach (Site site in ofRule.Take(Shown))
            {
                report.Append("    ")
                    .Append(site.Path)
                    .Append(':')
                    .Append(site.Line.ToString(CultureInfo.InvariantCulture))
                    .Append("  ")
                    .AppendLine(Clip(site.Text));
            }

            if (ofRule.Count > Shown)
            {
                report.Append("    and ")
                    .Append((ofRule.Count - Shown).ToString(CultureInfo.InvariantCulture))
                    .AppendLine(" more");
            }
        }

        Console.Out.Write(report.ToString());
    }

    /// <summary>The directory a site is attributed to: two path segments, which is where a sweep works.</summary>
    private static string Bucket(Site site)
    {
        string[] parts = site.Path.Split('/', '\\');
        return parts.Length <= 2 ? parts[0] : string.Join('/', parts.Take(3).SkipLast(1));
    }

    private static string Clip(string text)
    {
        string flat = text.Trim();
        return flat.Length <= 84 ? flat : string.Concat(flat.AsSpan(0, 81), "...");
    }

    /// <summary>Applies the four comment rules, plus the header rule, to one C# file.</summary>
    private static void InspectSource(string relative, string[] lines, Charter charter, List<Site> sites)
    {
        int firstCode = FirstCodeLine(lines);
        for (int i = 0; i < firstCode; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                sites.Add(new Site(relative, i + 1, Rule.Header, lines[i]));
                break;
            }
        }

        foreach ((int line, string text) in Comments(lines))
        {
            Inspect(relative, line, text, charter, sites);
        }

        InspectInheritdoc(relative, lines, sites);
    }

    /// <summary>
    /// Applies the same rules to a shell script, where a shebang and a usage line are the header
    /// the charter allows.
    /// </summary>
    private static void InspectScript(string relative, string[] lines, Charter charter, List<Site> sites)
    {
        int start = lines.Length > 0 && lines[0].StartsWith("#!", StringComparison.Ordinal) ? 1 : 0;
        int run = 0;
        for (int i = start; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                run++;
                if (run == 3)
                {
                    sites.Add(new Site(relative, i + 1, Rule.Header, lines[i]));
                    break;
                }

                continue;
            }

            if (lines[i].Trim().Length > 0)
            {
                break;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#') && !trimmed.StartsWith("#!", StringComparison.Ordinal))
            {
                Inspect(relative, i + 1, trimmed, charter, sites);
            }
        }
    }

    private static void Inspect(string relative, int line, string text, Charter charter, List<Site> sites)
    {
        foreach (Match match in Charter.Capitalised.Matches(text))
        {
            if (!charter.IsAcronym(match.Value))
            {
                sites.Add(new Site(relative, line, Rule.Capitals, text));
                break;
            }
        }

        foreach (Regex pattern in charter.Reference)
        {
            Match match = pattern.Match(text);
            if (match.Success && !charter.IsAcronym(match.Value))
            {
                sites.Add(new Site(relative, line, Rule.Reference, text));
                break;
            }
        }

        if (charter.History.Any(p => p.IsMatch(text)))
        {
            sites.Add(new Site(relative, line, Rule.History, text));
        }
    }

    /// <summary>
    /// Finds an inherited summary on a member that no caller outside the assembly can reach.
    /// </summary>
    private static void InspectInheritdoc(string relative, string[] lines, List<Site> sites)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("<inheritdoc", StringComparison.Ordinal))
            {
                continue;
            }

            for (int j = i + 1; j < lines.Length; j++)
            {
                string declaration = lines[j].TrimStart();
                if (declaration.Length == 0
                    || declaration.StartsWith("//", StringComparison.Ordinal)
                    || declaration.StartsWith('[')
                    || declaration.StartsWith('*'))
                {
                    continue;
                }

                if (!declaration.StartsWith("public ", StringComparison.Ordinal)
                    && !declaration.StartsWith("protected ", StringComparison.Ordinal))
                {
                    sites.Add(new Site(relative, i + 1, Rule.Inheritdoc, declaration));
                }

                break;
            }
        }
    }

    /// <summary>The first line that carries code, so that anything commented above it is a header.</summary>
    private static int FirstCodeLine(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length != 0 && !IsCommentLine(lines[i]))
            {
                return i;
            }
        }

        return lines.Length;
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    /// <summary>
    /// Every comment in a C# file, with its line. A string literal that holds two slashes is not a
    /// comment, which is why this walks characters rather than matching a pattern per line.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> Comments(string[] lines)
    {
        bool inBlock = false;
        bool inVerbatim = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            StringBuilder? found = null;
            int c = 0;
            while (c < line.Length)
            {
                if (inBlock)
                {
                    int close = line.IndexOf("*/", c, StringComparison.Ordinal);
                    (found ??= new StringBuilder()).Append(line.AsSpan(c, (close < 0 ? line.Length : close) - c));
                    if (close < 0)
                    {
                        break;
                    }

                    inBlock = false;
                    c = close + 2;
                    continue;
                }

                if (inVerbatim)
                {
                    int quote = line.IndexOf('"', c);
                    if (quote < 0)
                    {
                        break;
                    }

                    if (quote + 1 < line.Length && line[quote + 1] == '"')
                    {
                        c = quote + 2;
                        continue;
                    }

                    inVerbatim = false;
                    c = quote + 1;
                    continue;
                }

                char ch = line[c];
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                {
                    (found ??= new StringBuilder()).Append(line.AsSpan(c));
                    break;
                }

                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '*')
                {
                    inBlock = true;
                    c += 2;
                    continue;
                }

                if (ch == '@' && c + 1 < line.Length && line[c + 1] == '"')
                {
                    inVerbatim = true;
                    c += 2;
                    continue;
                }

                if (ch == '"')
                {
                    c = SkipString(line, c);
                    continue;
                }

                if (ch == '\'')
                {
                    c = SkipChar(line, c);
                    continue;
                }

                c++;
            }

            if (found is not null)
            {
                yield return (i + 1, found.ToString());
            }
        }
    }

    private static int SkipString(string line, int open)
    {
        int c = open + 1;
        while (c < line.Length)
        {
            if (line[c] == '\\')
            {
                c += 2;
                continue;
            }

            if (line[c] == '"')
            {
                return c + 1;
            }

            c++;
        }

        return line.Length;
    }

    private static int SkipChar(string line, int open)
    {
        int c = open + 1;
        while (c < line.Length)
        {
            if (line[c] == '\\')
            {
                c += 2;
                continue;
            }

            if (line[c] == '\'')
            {
                return c + 1;
            }

            c++;
        }

        return line.Length;
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (string relative in Roots)
        {
            string directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> found = Directory
                .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(p => Path.GetExtension(p) is ".cs" or ".sh")
                .Where(p => !Generated(p))
                .OrderBy(p => p, StringComparer.Ordinal);
            foreach (string path in found)
            {
                yield return path;
            }
        }
    }

    private static bool Generated(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>Walks up from this source file to the directory that holds the solution.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(SourceDirectory());
        while (directory is not null
            && !System.IO.File.Exists(Path.Combine(directory.FullName, "Vorticity.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No directory above this one holds Vorticity.slnx.");
    }

    private static string SourceDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath)
        ?? throw new InvalidOperationException($"No directory for '{callerFilePath}'.");

    /// <summary>The patterns, as the file beside this one spells them.</summary>
    private sealed class Charter
    {
        /// <summary>A run of four or more capitals, which is either an acronym or emphasis.</summary>
        internal static readonly Regex Capitalised = new(@"\b[A-Z][A-Z0-9]{3,}\b", RegexOptions.Compiled);

        private Charter(IReadOnlyList<Regex> acronyms, IReadOnlyList<Regex> reference, IReadOnlyList<Regex> history)
        {
            _acronyms = acronyms;
            Reference = reference;
            History = history;
        }

        private readonly IReadOnlyList<Regex> _acronyms;

        internal IReadOnlyList<Regex> Reference { get; }

        internal IReadOnlyList<Regex> History { get; }

        internal bool IsAcronym(string word) => _acronyms.Any(a => a.IsMatch(word));

        internal static Charter Load(string path)
        {
            Assert.True(System.IO.File.Exists(path), $"The pattern file is missing at '{path}'.");

            Dictionary<string, List<Regex>> sections = new(StringComparer.Ordinal)
            {
                ["acronyms"] = [],
                ["reference"] = [],
                ["history"] = [],
            };

            string section = string.Empty;
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1];
                    Assert.True(sections.ContainsKey(section), $"Unknown section '{section}' in '{path}'.");
                    continue;
                }

                string pattern = section == "acronyms" ? $"^(?:{line})$" : line;
                sections[section].Add(new Regex(pattern, RegexOptions.Compiled));
            }

            return new Charter(sections["acronyms"], sections["reference"], sections["history"]);
        }
    }
}
