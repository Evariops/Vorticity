// Files a test class writes once and every one of its tests reads.
//
// A fixture written for each test of a class is the same bytes written again: the index and key
// cursor classes would write theirs over a hundred times a run, and writing is more than half of
// what they cost. A file is written here once per key, the first time a test asks for it, and deleted
// when the run ends. Each test still opens its own handle on it: the segment cache is a handle's,
// so what one test reads or counts is never warm for another.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Xunit;

[assembly: AssemblyFixture(typeof(Vorticity.Tests.SharedFiles))]

namespace Vorticity.Tests;

/// <summary>The run's shared fixture files, and the directory that holds them until the run ends.</summary>
public sealed class SharedFiles : IDisposable
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), $"vorticity-shared-{Environment.ProcessId}-{Guid.NewGuid():N}");

    private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> Written = new(StringComparer.Ordinal);

    /// <summary>
    /// The file written under <paramref name="key"/>, and what writing it returned. The first test to
    /// ask writes it; the others wait for that write, its failure included.
    /// </summary>
    /// <param name="key">Everything the bytes depend on: the class, and every option it writes with.</param>
    /// <param name="write">Writes the file at the path it is given.</param>
    internal static async Task<(string Path, T Result)> GetAsync<T>(string key, Func<string, Task<T>> write)
    {
        string path = Path.Combine(Root, Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(key))) + ".vortex");
        object? result = await Written.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () =>
        {
            Directory.CreateDirectory(Root);
            return await write(path).ConfigureAwait(false);
        })).Value.ConfigureAwait(false);
        return (path, (T)result!);
    }

    /// <summary>The file written under <paramref name="key"/>, by a writer that returns nothing.</summary>
    internal static async Task<string> GetAsync(string key, Func<string, Task> write) =>
        (await GetAsync(key, async path =>
        {
            await write(path).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false)).Path;

    /// <summary>A policy as a key: its default and every column's and composite key's index, field by field.</summary>
    internal static string Describe(WritePolicy policy) =>
        "default=" + Describe(policy.Default)
        + ";" + string.Join(";", policy.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + Describe(c.Value)))
        + ";keys=" + string.Join(";", policy.Keys.Select(k => string.Join(",", k.Paths) + "=" + Describe(k.Policy)));

    /// <summary>An index as a key: every field its equality compares.</summary>
    internal static string Describe(IndexSpec spec) => string.Create(
        CultureInfo.InvariantCulture,
        $"{spec.Kind}:{spec.FalsePositivePpm}:{spec.Resolutions}:{spec.MaxBlocks}:{spec.MinDistinct}:{spec.Hash}:{spec.CaseInsensitive}:{spec.SegmentEntries}:{spec.Required}");

    /// <summary>Deletes the files once every test has run.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // No test asked for a shared file.
        }
    }
}
