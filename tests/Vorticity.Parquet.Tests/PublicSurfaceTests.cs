using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Renders everything Vorticity.Parquet exposes and compares it to the file it tracks, as the core's
/// surface test does for the core's assemblies, with the same renderer: a public member is a promise,
/// and the record of it changes in the commit that changes it.
/// </summary>
public sealed class PublicSurfaceTests
{
    private const string FileName = "PublicSurface.txt";

    [Fact]
    [RequiresUnreferencedCode(SurfaceRenderer.NotTrimmable)]
    public void TheSurfaceIsTheOneOnRecord()
    {
        string actual = SurfaceRenderer.Render(Assemblies());
        string directory = SourceDirectory();
        string expectedPath = System.IO.Path.Combine(directory, FileName);
        if (!System.IO.File.Exists(expectedPath))
        {
            WriteActual(directory, actual);
            Assert.Fail($"{FileName} is missing. The rendered surface was written beside it.");
        }

        string expected = SurfaceRenderer.Normalize(System.IO.File.ReadAllText(expectedPath));
        if (expected == actual)
        {
            return;
        }

        string actualPath = WriteActual(directory, actual);
        Assert.Fail(
            $"The public surface differs from {FileName}.{Environment.NewLine}" +
            $"{SurfaceRenderer.Diff(expected, actual)}{Environment.NewLine}" +
            $"If the change is intended, replace the file with {actualPath} and say in the commit " +
            "what was added or withdrawn and why.");
    }

    /// <summary>
    /// Rule 6 of docs/design/14-public-api.md: a member that starts the work, an awaitable or a stream
    /// to await, ends in <c>Async</c>, and one that does not, does not.
    /// </summary>
    [Fact]
    [RequiresUnreferencedCode(SurfaceRenderer.NotTrimmable)]
    public void AsyncNamesWhatRuns()
    {
        List<string> wrong = [];
        foreach (string line in SurfaceRenderer.Render(Assemblies()).Split('\n'))
        {
            if (!SurfaceRenderer.TryMethod(line, out string returned, out string name))
            {
                continue;
            }

            bool runs = returned.StartsWith("ValueTask", StringComparison.Ordinal)
                || returned.StartsWith("Task", StringComparison.Ordinal)
                || returned.StartsWith("IAsyncEnumerable<", StringComparison.Ordinal);
            if (runs != name.EndsWith("Async", StringComparison.Ordinal) && name != "DisposeAsync")
            {
                wrong.Add(line.Trim());
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    private static Assembly[] Assemblies() => [typeof(ParquetFile).Assembly];

    private static string WriteActual(string directory, string actual)
    {
        string path = System.IO.Path.Combine(directory, FileName + ".actual");
        System.IO.File.WriteAllText(path, actual);
        return path;
    }

    private static string SourceDirectory([CallerFilePath] string callerFilePath = "") =>
        System.IO.Path.GetDirectoryName(callerFilePath)
        ?? throw new InvalidOperationException($"No directory for '{callerFilePath}'.");
}
