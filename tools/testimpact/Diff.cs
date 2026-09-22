using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Vorticity.Tools.TestImpact;

/// <summary>A run of lines a change touched in one file.</summary>
/// <param name="Path">The file, from the repository root.</param>
/// <param name="First">The first line of the run in the new version; for a pure removal, the line it was removed after.</param>
/// <param name="Count">Lines of the run in the new version, zero for a pure removal.</param>
/// <param name="Text">The lines added and removed, without their sign.</param>
/// <param name="Whole">Whether the whole file is new, so that every line of it counts.</param>
internal sealed record Hunk(string Path, int First, int Count, IReadOnlyList<string> Text, bool Whole);

/// <summary>The working tree's changes against a commit, as <c>git diff -U0</c> reports them, and the untracked files.</summary>
internal static class Diff
{
    internal static async Task<List<Hunk>> ReadAsync(string git, string root, string baseline)
    {
        string text = await Processes.CaptureAsync(
            git, ["-C", root, "diff", "-U0", "--no-color", "--no-ext-diff", baseline], root).ConfigureAwait(false);
        List<Hunk> hunks = Parse(text);
        string untracked = await Processes.CaptureAsync(
            git, ["-C", root, "ls-files", "--others", "--exclude-standard"], root).ConfigureAwait(false);
        foreach (string path in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hunks.Add(new Hunk(path, 1, int.MaxValue, [], Whole: true));
        }

        return hunks;
    }

    internal static List<Hunk> Parse(string text)
    {
        List<Hunk> hunks = [];
        string? path = null;
        bool header = false;
        int first = 0;
        int count = 0;
        List<string>? lines = null;

        void Flush()
        {
            if (path is not null && lines is not null)
            {
                hunks.Add(new Hunk(path, first, count, lines, Whole: false));
            }

            lines = null;
        }

        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                header = true;
                path = null;
                continue;
            }

            if (header)
            {
                // The new side names the file; a deleted file has only the old side, and its lines
                // are gone with the methods they held, whose callers changed too.
                if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                {
                    path = line[6..];
                }
                else if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    header = false;
                }
                else
                {
                    continue;
                }
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                (first, count) = NewSide(line);
                lines = [];
            }
            else if (lines is not null && line.Length > 0 && (line[0] == '+' || line[0] == '-'))
            {
                lines.Add(line[1..]);
            }
        }

        Flush();
        return hunks;
    }

    /// <summary>The start and length of a hunk's new side, from <c>@@ -a,b +c,d @@</c>.</summary>
    private static (int First, int Count) NewSide(string header)
    {
        int plus = header.IndexOf(" +", StringComparison.Ordinal);
        int end = header.IndexOf(' ', plus + 2);
        string side = header[(plus + 2)..end];
        int comma = side.IndexOf(',');
        return comma < 0
            ? (int.Parse(side, CultureInfo.InvariantCulture), 1)
            : (int.Parse(side[..comma], CultureInfo.InvariantCulture), int.Parse(side[(comma + 1)..], CultureInfo.InvariantCulture));
    }
}
