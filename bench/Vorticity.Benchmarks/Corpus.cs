// Locating the benchmark inputs.
//
// The data is the conformance corpus, deliberately. docs/05-benchmarks.md §4 wants upstream's own
// datasets so numbers are comparable with published Vortex figures, and those are gigabytes that do
// not belong in a repository -- but the corpus is here, it was written by the Rust writer with real
// distributions, and "read identical bytes with both implementations" is the property that makes a
// comparison honest whatever the bytes are. When the TPC-H and ClickBench inputs are wired up, they
// come through the same seam: a path, resolved once.
using System;
using System.IO;

namespace Vorticity.Benchmarks;

/// <summary>Resolves benchmark input files.</summary>
internal static class Corpus
{
    private static readonly Lazy<string> RootLazy = new Lazy<string>(Locate);

    /// <summary>The corpus directory.</summary>
    internal static string Root => RootLazy.Value;

    /// <summary>The path of one corpus entry.</summary>
    /// <param name="id">e.g. <c>distributions/high_cardinality_i64_r8193</c>.</param>
    internal static string Path(string id) =>
        System.IO.Path.Combine(Root, id.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".vortex");

    /// <summary>
    /// An override for a real dataset, when one is present.
    /// </summary>
    /// <param name="variable">The environment variable naming it.</param>
    /// <param name="fallback">The corpus entry to use when the variable is unset.</param>
    internal static string Dataset(string variable, string fallback)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(path) ? Path(fallback) : path;
    }

    private static string Locate()
    {
        // Walk up from the assembly rather than the working directory: BenchmarkDotNet runs the
        // generated harness from a directory of its own choosing.
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(
                directory.FullName, "tests", "Vorticity.Conformance", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/Vorticity.Conformance/corpus above " + AppContext.BaseDirectory);
    }
}
