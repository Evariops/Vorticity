using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks;

/// <summary>
/// The benchmark page, one place for every figure the bench publishes: each section written by the
/// instrument that measures it, the others left as they are.
/// </summary>
/// <remarks>
/// <para>
/// A section sits between <c>&lt;!-- results: name --&gt;</c> and <c>&lt;!-- /results: name --&gt;</c>.
/// An instrument given <c>--out</c> replaces the sections it measured and nothing else, so the
/// report, the gates, the trade-offs and the kernels each refresh their part of one page, at their
/// own cost, and the text around the sections stays as written. A section the page does not hold
/// yet is added at its end.
/// </para>
/// <para>
/// Each section ends with the line <see cref="Provenance"/> gives: the sections are measured at
/// different times, and a figure has to say which run it belongs to.
/// </para>
/// </remarks>
internal static class ResultsPage
{
    /// <summary>Replaces <paramref name="sections"/> in the page at <paramref name="path"/>, or adds them at its end.</summary>
    /// <param name="path">The page.</param>
    /// <param name="sections">Each section's name and Markdown, without its markers.</param>
    internal static async Task WriteAsync(string path, IReadOnlyList<(string Name, string Markdown)> sections)
    {
        string page = System.IO.File.Exists(path) ? await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty;
        foreach ((string name, string markdown) in sections)
        {
            page = Replace(page, name, markdown);
        }

        await System.IO.File.WriteAllTextAsync(path, page).ConfigureAwait(false);
        Console.Error.WriteLine($"{string.Join(", ", sections.Select(s => s.Name))} written to {path}");
    }

    /// <summary>
    /// The machine, the runtime, the reference, the commit and the date a section was measured on,
    /// as its last line; a tree with uncommitted changes says so.
    /// </summary>
    /// <param name="reference">The build of the other side, when the section compares against one.</param>
    internal static string Provenance(string? reference = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"*Measured on {Processor()} ({RuntimeInformation.OSArchitecture}), {Environment.ProcessorCount} processors, " +
            $"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}" +
            $"{(reference is null ? string.Empty : "; " + reference)}; commit {Commit()}, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.*");

    /// <summary>The processor, by name: "Arm64" does not say which one a ratio belongs to.</summary>
    internal static string Processor()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Ask("sysctl", ["-n", "machdep.cpu.brand_string"]);
            }

            if (OperatingSystem.IsWindows())
            {
                using Microsoft.Win32.RegistryKey? cpu = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (cpu?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
                {
                    return name.Trim();
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && System.IO.File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in System.IO.File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RuntimeInformation.OSArchitecture.ToString();
        }

        return RuntimeInformation.OSArchitecture.ToString();
    }

    /// <summary>The commit the figures belong to, and whether the tree held changes past it.</summary>
    internal static string Commit()
    {
        string head = Ask("git", ["rev-parse", "--short", "HEAD"], AppContext.BaseDirectory);
        if (head == "unknown")
        {
            return head;
        }

        string changes = Ask("git", ["status", "--porcelain"], AppContext.BaseDirectory, empty: string.Empty);
        return changes.Length > 0 && changes != "unknown" ? head + " with uncommitted changes" : head;
    }

    /// <summary>What <paramref name="exe"/> prints, trimmed, or "unknown" when it cannot be run or fails.</summary>
    internal static string Ask(string exe, string[] arguments, string? directory = null, string empty = "unknown")
    {
        ProcessStartInfo start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (directory is not null)
        {
            start.WorkingDirectory = directory;
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? child = Process.Start(start);
            if (child is null)
            {
                return "unknown";
            }

            string answer = child.StandardOutput.ReadToEnd().Trim();
            child.WaitForExit();
            return child.ExitCode != 0 ? "unknown" : answer.Length > 0 ? answer : empty;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string Replace(string page, string name, string markdown)
    {
        string open = $"<!-- results: {name} -->";
        string close = $"<!-- /results: {name} -->";
        string body = open + "\n" + markdown.TrimEnd('\n') + "\n" + close;
        int start = page.IndexOf(open, StringComparison.Ordinal);
        int end = start < 0 ? -1 : page.IndexOf(close, start, StringComparison.Ordinal);
        if (start >= 0 && end >= 0)
        {
            return string.Concat(page.AsSpan(0, start), body, page.AsSpan(end + close.Length));
        }

        // A section of a family, `kernel:` for one, goes after the last of its family, so that a
        // class measured for the first time lands beside the others rather than at the page's end.
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        int sibling = colon < 0 ? -1 : page.LastIndexOf($"<!-- /results: {name[..(colon + 1)]}", StringComparison.Ordinal);
        if (sibling >= 0)
        {
            int after = page.IndexOf("-->", sibling, StringComparison.Ordinal) + 3;
            return string.Concat(page.AsSpan(0, after), "\n\n", body, page.AsSpan(after));
        }

        StringBuilder added = new StringBuilder(page.TrimEnd('\n'));
        if (added.Length > 0)
        {
            added.Append("\n\n");
        }

        return added.Append(body).Append('\n').ToString();
    }
}
