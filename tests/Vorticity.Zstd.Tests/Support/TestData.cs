using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// <c>tests/Vorticity.Zstd.Tests/testdata/</c>: zstd's golden files and a decodecorpus corpus, binary
/// files that the repository does not keep and <c>tools/native-ref/testdata.sh</c> writes.
/// </summary>
/// <remarks>
/// A test that reads them calls <see cref="Require"/> first: without them it is skipped, and in CI, whose
/// workflow writes them before the tests, it fails, so that a run without the data never passes for one
/// that read it.
/// </remarks>
internal static class TestData
{
    /// <summary>What a data set's theory receives in place of its cases when the set is missing.</summary>
    public const string Missing = "(not generated)";

    private static readonly Lazy<string> Root = new(() => FindRoot());

    /// <summary>The repository's root: the directory that holds <c>Vorticity.Zstd.slnx</c>.</summary>
    public static string RepositoryRoot => Root.Value;

    public static string Directory => Path.Combine(RepositoryRoot, "tests", "Vorticity.Zstd.Tests", "testdata");

    public static string PathOf(string relative) => Path.Combine(Directory, relative);

    public static byte[] Read(string relative) => File.ReadAllBytes(PathOf(relative));

    /// <summary>Whether <c>tools/native-ref/testdata.sh</c> has written <paramref name="relative"/>.</summary>
    public static bool Has(string relative) => File.Exists(PathOf(relative)) || System.IO.Directory.Exists(PathOf(relative));

    /// <summary>Skips the test when <paramref name="relative"/> is missing, and fails it in CI.</summary>
    public static void Require(string relative)
    {
        if (Has(relative))
        {
            return;
        }

        string message = $"{relative} is missing from {Directory}: run tools/native-ref/testdata.sh";
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
    }

    /// <summary>
    /// The names of the files in the directory <paramref name="relative"/>, in order; <see cref="Missing"/>
    /// alone when it is missing, for the theory to run once and <see cref="Require"/> to report it.
    /// </summary>
    public static IEnumerable<string> Files(string relative) =>
        Has(relative)
            ? System.IO.Directory.GetFiles(PathOf(relative)).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)
            : [Missing];

    private static string FindRoot([CallerFilePath] string here = "")
    {
        string? directory = Path.GetDirectoryName(here);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Vorticity.Zstd.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Vorticity.Zstd.slnx not found above " + here);
    }
}
