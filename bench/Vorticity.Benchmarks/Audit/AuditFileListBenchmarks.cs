// What the newest version costs on a local disk that holds a long history.
//
// A file store whose commit directory holds 20,000 commit objects of a few bytes, written once in a directory of the temp
// folder. A listing of the newest commit, one key, as an open or a refresh that does not trust the version it knows asks
// it; and the walk of the directory alone, its file names counted, the floor any listing of an unordered directory pays.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The newest commit of a long history on a local disk.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditFileListBenchmarks
{
    private const int Commits = 20_000;

    private string _root = null!;
    private FileObjectStore _store = null!;

    /// <summary>Writes the commits.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "vorticity-audit-list");
        string commits = Path.Combine(_root, "commit");
        if (!Directory.Exists(commits) || Directory.GetFiles(commits).Length != Commits)
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            Directory.CreateDirectory(commits);
            for (ulong version = 1; version <= Commits; version++)
            {
                System.IO.File.WriteAllBytes(Path.Combine(_root, CommitKey.For(version).Replace('/', Path.DirectorySeparatorChar)), [1, 2, 3]);
            }
        }

        _store = new FileObjectStore(_root);
        Console.WriteLine($"// newest {Newest()}, {Walk()} files walked");
    }

    /// <summary>Disposes the store; the files stay for the next process.</summary>
    [GlobalCleanup]
    public void Cleanup() => _store.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The newest commit's version, from a listing of one key.</summary>
    [Benchmark]
    public ulong Newest() => DatasetCommitter.NewestVersionAsync(_store, default).AsTask().GetAwaiter().GetResult();

    /// <summary>The commit directory walked, its files counted.</summary>
    [Benchmark]
    public int Walk() => Directory.EnumerateFiles(Path.Combine(_root, "commit"), "*", SearchOption.AllDirectories).Count();
}
