using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// Builds the projects under <c>tests/MustNotCompile</c> and checks that each case raises the
/// diagnostic its marker names, on its line, and that nothing else is reported.
/// </summary>
/// <remarks>
/// What the scan cannot push down, what would outlive a borrowed batch and what the generator
/// cannot bind are refused by the compiler, by an <c>[Obsolete]</c> of ours or by an analyzer.
/// A refusal that stops firing turns a compile error into a runtime surprise, and nothing else in
/// the suite would notice: the cases are code that must not build, so they can only be checked by
/// building them.
/// </remarks>
[Trait("Category", "ApiContract")]
public sealed partial class MustNotCompileTests
{
    [Theory]
    [InlineData("Declarations")]
    [InlineData("Bodies")]
    public async Task EachCaseRaisesTheDiagnosticItsMarkerNamesAndNothingElse(string project)
    {
        string directory = Path.Combine(RepositoryRoot(), "tests", "MustNotCompile", project);
        Dictionary<string, string?> expected = Markers(directory);
        Assert.NotEmpty(expected);

        (Dictionary<string, string> reported, string output) = await BuildAsync(directory, TestContext.Current.CancellationToken);

        StringBuilder problems = new StringBuilder();
        foreach ((string site, string? fragment) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!reported.TryGetValue(site, out string? message))
            {
                problems.AppendLine($"missing   {site}");
            }
            else if (fragment is not null && !message.Contains(fragment, StringComparison.Ordinal))
            {
                problems.AppendLine($"message   {site}: expected \"{fragment}\" in \"{message}\"");
            }
        }

        foreach ((string site, string message) in reported.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!expected.ContainsKey(site))
            {
                problems.AppendLine($"unexpected {site}: {message}");
            }
        }

        Assert.True(problems.Length == 0, $"{problems}{Environment.NewLine}{output}");
    }

    /// <summary>The <c>// expect: ID "fragment"</c> markers, keyed by <c>file:line:ID</c>.</summary>
    private static Dictionary<string, string?> Markers(string directory)
    {
        Dictionary<string, string?> markers = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string[] lines = System.IO.File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = Marker().Match(lines[i]);
                if (match.Success)
                {
                    string site = $"{Path.GetFileName(path)}:{i + 1}:{match.Groups["id"].Value}";
                    markers.Add(site, match.Groups["text"].Success ? match.Groups["text"].Value : null);
                }
            }
        }

        return markers;
    }

    /// <summary>What the build reports, keyed like the markers, and its whole output for the failure message.</summary>
    private static async Task<(Dictionary<string, string> Reported, string Output)> BuildAsync(string directory, CancellationToken cancellationToken)
    {
        string configuration = typeof(MustNotCompileTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        ProcessStartInfo start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        foreach (string argument in (string[])["build", directory, "-c", configuration, "-nologo", "-v", "q", "-nodeReuse:false", "-clp:NoSummary;ForceNoAlign"])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        output += await error;

        Dictionary<string, string> reported = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            Match match = Diagnostic().Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                string file = Path.GetRelativePath(directory, match.Groups["file"].Value);
                reported.TryAdd($"{file}:{match.Groups["line"].Value}:{match.Groups["id"].Value}", match.Groups["message"].Value);
            }
        }

        return (reported, output);
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

    [GeneratedRegex("""// expect: (?<id>[A-Z]+[0-9]+)(?: "(?<text>[^"]*)")?\s*$""")]
    private static partial Regex Marker();

    [GeneratedRegex("""^(?<file>[^(]+)\((?<line>[0-9]+),[0-9]+\): (?:error|warning) (?<id>[A-Z]+[0-9]+): (?<message>.*?)(?: \[[^\]]+\])?$""")]
    private static partial Regex Diagnostic();
}
