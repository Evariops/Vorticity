// usage: dotnet run tools/comment-charter.cs [--dump <path>] [--fingerprint <path>]
//
// Reports the comments that depart from the repository's comment charter, per rule and per
// directory. --dump writes one line per file, worst first, for a sweep that works file by file.
// --fingerprint writes a hash per file of the source with every comment removed: taken before and
// after a sweep, the two files must be identical, which is what proves no line of code moved.
//
// Not a test on purpose: it walks seven hundred files off the disk, and the suite runs on every
// gate and every job.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

string[] roots = ["src", "tests", "bench", "tools", "spec"];
const int shown = 12;

string? dumpPath = Argument(args, "--dump");
string? fingerprintPath = Argument(args, "--fingerprint");

string repository = RepositoryRoot();
Charter charter = Charter.Load(Path.Combine(repository, "tools", "comment-charter.txt"));

List<Site> sites = [];
List<string> fingerprints = [];
int files = 0;

foreach (string path in SourceFiles(repository, roots))
{
    files++;
    string[] lines = File.ReadAllLines(path);
    string relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
    bool script = Path.GetExtension(path) == ".sh";

    if (script)
    {
        InspectScript(relative, lines, charter, sites);
    }
    else
    {
        InspectSource(relative, lines, charter, sites);
    }

    if (fingerprintPath is not null)
    {
        fingerprints.Add($"{Fingerprint(lines, script)}  {relative}");
    }
}

Report(files, sites, shown);

if (dumpPath is not null)
{
    Dump(sites, dumpPath);
    Console.WriteLine($"  per-file report written to {dumpPath}");
}

if (fingerprintPath is not null)
{
    fingerprints.Sort(StringComparer.Ordinal);
    File.WriteAllLines(fingerprintPath, fingerprints);
    Console.WriteLine($"  {fingerprints.Count} code fingerprints written to {fingerprintPath}");
}

return sites.Count == 0 ? 0 : 1;

static string? Argument(string[] args, string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static void Report(int files, List<Site> sites, int shown)
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
    foreach (IGrouping<string, Site> group in sites.GroupBy(Bucket).OrderByDescending(g => g.Count()))
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
        foreach (Site site in ofRule.Take(shown))
        {
            report.Append("    ")
                .Append(site.Path)
                .Append(':')
                .Append(site.Line.ToString(CultureInfo.InvariantCulture))
                .Append("  ")
                .AppendLine(Clip(site.Text));
        }

        if (ofRule.Count > shown)
        {
            report.Append("    and ")
                .Append((ofRule.Count - shown).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" more");
        }
    }

    Console.Write(report.ToString());
}

static void Dump(List<Site> sites, string path)
{
    StringBuilder dump = new();
    foreach (IGrouping<string, Site> file in sites.GroupBy(s => s.Path).OrderByDescending(g => g.Count()))
    {
        dump.Append(file.Key).Append('\t').Append(file.Count().ToString(CultureInfo.InvariantCulture)).Append('\t');
        foreach (Rule rule in Enum.GetValues<Rule>())
        {
            int count = file.Count(s => s.Rule == rule);
            if (count > 0)
            {
                dump.Append(rule.ToString().ToLowerInvariant())
                    .Append('=')
                    .Append(count.ToString(CultureInfo.InvariantCulture))
                    .Append(' ');
            }
        }

        dump.AppendLine();
    }

    File.WriteAllText(path, dump.ToString());
}

static string Bucket(Site site)
{
    string[] parts = site.Path.Split('/');
    return parts.Length <= 2 ? parts[0] : string.Join('/', parts.Take(3).SkipLast(1));
}

static string Clip(string text)
{
    string flat = text.Trim();
    return flat.Length <= 84 ? flat : string.Concat(flat.AsSpan(0, 81), "...");
}

/// <summary>
/// A hash of the file with every comment removed and whitespace flattened. Two runs that differ
/// only in their comments produce the same value, so a sweep that changes one line of code shows up
/// as a changed fingerprint and nothing else does.
/// </summary>
static string Fingerprint(string[] lines, bool script)
{
    string code = script ? ScriptCode(lines) : Scanner.Code(lines);
    string flat = Regex.Replace(code, @"\s+", " ").Trim();
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flat)))[..16];
}

static string ScriptCode(string[] lines)
{
    StringBuilder code = new();
    foreach (string line in lines)
    {
        if (!line.TrimStart().StartsWith('#'))
        {
            code.AppendLine(line);
        }
    }

    return code.ToString();
}

static void InspectSource(string relative, string[] lines, Charter charter, List<Site> sites)
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

    foreach ((int line, string text) in Scanner.Comments(lines))
    {
        Inspect(relative, line, text, charter, sites);
    }

    InspectInheritdoc(relative, lines, sites);
}

static void InspectScript(string relative, string[] lines, Charter charter, List<Site> sites)
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

static void Inspect(string relative, int line, string text, Charter charter, List<Site> sites)
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

static void InspectInheritdoc(string relative, string[] lines, List<Site> sites)
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

static int FirstCodeLine(string[] lines)
{
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].Trim().Length != 0 && !IsCommentLine(lines[i]))
        {
            return i;
        }
    }

    return lines.Length;
}

static bool IsCommentLine(string line)
{
    string trimmed = line.TrimStart();
    return trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal)
        || trimmed.StartsWith('*');
}

static IEnumerable<string> SourceFiles(string repository, string[] roots)
{
    foreach (string relative in roots)
    {
        string directory = Path.Combine(repository, relative);
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

static bool Generated(string path)
{
    string normalized = path.Replace('\\', '/');
    return normalized.Contains("/bin/", StringComparison.Ordinal)
        || normalized.Contains("/obj/", StringComparison.Ordinal)

        // This file keeps its usage banner: it is run by hand, and a banner is how you learn the
        // flags. It is the instrument rather than the subject.
        || normalized.EndsWith("tools/comment-charter.cs", StringComparison.Ordinal);
}

static string RepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vorticity.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName
        ?? throw new InvalidOperationException("Run this from inside the repository.");
}

internal enum Rule
{
    Header,
    Capitals,
    Reference,
    History,
    Inheritdoc,
}

internal sealed record Site(string Path, int Line, Rule Rule, string Text);

/// <summary>
/// One pass over a C# file that separates comments from code. A string holding two slashes is not a
/// comment, which is why this walks characters rather than matching a pattern per line.
/// </summary>
internal static class Scanner
{
    internal static IEnumerable<(int Line, string Text)> Comments(string[] lines) => Walk(lines).Comments;

    internal static string Code(string[] lines) => Walk(lines).Code;

    private static (List<(int Line, string Text)> Comments, string Code) Walk(string[] lines)
    {
        List<(int, string)> comments = [];
        StringBuilder code = new();
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
                        code.Append(line.AsSpan(c));
                        break;
                    }

                    if (quote + 1 < line.Length && line[quote + 1] == '"')
                    {
                        code.Append(line.AsSpan(c, quote + 2 - c));
                        c = quote + 2;
                        continue;
                    }

                    inVerbatim = false;
                    code.Append(line.AsSpan(c, quote + 1 - c));
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
                    code.Append(line.AsSpan(c, 2));
                    c += 2;
                    continue;
                }

                int next = ch switch
                {
                    '"' => SkipQuoted(line, c, '"'),
                    '\'' => SkipQuoted(line, c, '\''),
                    _ => c + 1,
                };
                code.Append(line.AsSpan(c, next - c));
                c = next;
            }

            code.Append('\n');
            if (found is not null)
            {
                comments.Add((i + 1, found.ToString()));
            }
        }

        return (comments, code.ToString());
    }

    private static int SkipQuoted(string line, int open, char terminator)
    {
        int c = open + 1;
        while (c < line.Length)
        {
            if (line[c] == '\\')
            {
                c += 2;
                continue;
            }

            if (line[c] == terminator)
            {
                return c + 1;
            }

            c++;
        }

        return line.Length;
    }
}

/// <summary>The patterns, as the file beside this one spells them.</summary>
internal sealed class Charter
{
    internal static readonly Regex Capitalised = new(@"\b[A-Z][A-Z0-9]{3,}\b", RegexOptions.Compiled);

    private readonly IReadOnlyList<Regex> _acronyms;

    private Charter(IReadOnlyList<Regex> acronyms, IReadOnlyList<Regex> reference, IReadOnlyList<Regex> history)
    {
        _acronyms = acronyms;
        Reference = reference;
        History = history;
    }

    internal IReadOnlyList<Regex> Reference { get; }

    internal IReadOnlyList<Regex> History { get; }

    internal bool IsAcronym(string word) => _acronyms.Any(a => a.IsMatch(word));

    internal static Charter Load(string path)
    {
        Dictionary<string, List<Regex>> sections = new(StringComparer.Ordinal)
        {
            ["acronyms"] = [],
            ["reference"] = [],
            ["history"] = [],
        };

        string section = string.Empty;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            string pattern = section == "acronyms" ? $"^(?:{line})$" : line;
            sections[section].Add(new Regex(pattern, RegexOptions.Compiled));
        }

        return new Charter(sections["acronyms"], sections["reference"], sections["history"]);
    }
}
