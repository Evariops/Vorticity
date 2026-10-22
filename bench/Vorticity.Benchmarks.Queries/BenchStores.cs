using System;
using System.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// Where a bench writes the stores it keeps when no <c>--store</c> names one: a directory of its own under
/// <c>~/.cache/vorticity</c>, beside the corpus of the query matrix, or under <c>VORTICITY_BENCH_STORES</c> when that is
/// set, so that no default names a drive one machine has and another lacks.
/// </summary>
internal static class BenchStores
{
    /// <summary>The directory of the store named <paramref name="name"/>.</summary>
    internal static string Of(string name) => Path.Combine(
        Environment.GetEnvironmentVariable("VORTICITY_BENCH_STORES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vorticity"),
        name);
}
