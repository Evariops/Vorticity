using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// Renders everything the three shipped assemblies expose and compares it to a tracked file.
/// </summary>
/// <remarks>
/// <para>
/// A public member is a promise: it can be called by code this repository will never see, and
/// taking it back is a breaking change. Without a record of what has been promised, the surface
/// grows by accident — a helper written for one caller inside the library, left public because
/// nothing asked. This test is the record. Any change to it, in either direction, has to be
/// written into the file in the same commit as the code, which is where a reviewer sees it.
/// </para>
/// <para>
/// The alternative was <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c>. It does the same job and
/// more, at the price of a package reference; reflecting over the assemblies the test already
/// loads costs nothing and keeps the check in the suite with the other ratchets.
/// </para>
/// <para>
/// The file is a ratchet like any other: when the surface legitimately changes, regenerate it and
/// say in the commit what was added or withdrawn and why. The failure message names the file that
/// was written for that purpose.
/// </para>
/// </remarks>
public sealed class PublicSurfaceTests
{
    private const string FileName = "PublicSurface.txt";

    /// <summary>
    /// Why this file declares itself unfit for trimming rather than silencing the analyzer.
    /// </summary>
    /// <remarks>
    /// Walking every exported type of an assembly is the one thing a trimmer cannot follow, and the
    /// analyzer is right to say so. <c>RequiresUnreferencedCode</c> is the declaration for that: it
    /// records the fact and hands it to every caller, where a suppression would hide it. The test
    /// project is never trimmed or published, so nothing downstream inherits a problem.
    /// </remarks>
    private const string NotTrimmable = SurfaceRenderer.NotTrimmable;

    [Fact]
    [RequiresUnreferencedCode(NotTrimmable)]
    public void TheShippedSurfaceIsTheOneOnRecord()
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
    /// to await, ends in <c>Async</c>; a member that only describes a query does not.
    /// </summary>
    [Fact]
    [RequiresUnreferencedCode(NotTrimmable)]
    public void AsyncNamesWhatRunsAndNoBuilderCarriesIt()
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
            bool builds = Builders.Any(builder => returned == builder || returned.StartsWith(builder + "<", StringComparison.Ordinal));
            if (runs && !name.EndsWith("Async", StringComparison.Ordinal))
            {
                wrong.Add($"runs without Async: {line.Trim()}");
            }

            if (builds && name.EndsWith("Async", StringComparison.Ordinal))
            {
                wrong.Add($"builds with Async: {line.Trim()}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>The types a member returns when it describes a query rather than running it.</summary>
    private static readonly string[] Builders = ["Scan", "GroupedScan", "OrderedGroupedScan", "Aggregation", "Projection", "KeyCursorBuilder"];

    /// <summary>
    /// A count per assembly, printed on every run so the totals come from the test output rather
    /// than from a count made by hand.
    /// </summary>
    [Fact]
    [RequiresUnreferencedCode(NotTrimmable)]
    public void ThePublicTypeCountIsReported()
    {
        foreach (Assembly assembly in Assemblies())
        {
            int types = assembly.GetExportedTypes().Length;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"PUBLIC SURFACE: {assembly.GetName().Name} exposes {types} types."));
        }
    }

    private static Assembly[] Assemblies() =>
    [
        typeof(Vorticity.Types.DType).Assembly,
        typeof(Vorticity.Dataset.VortexDataset).Assembly,
        typeof(Vorticity.RowEncoding.RowKeyEncoder).Assembly,
    ];

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
