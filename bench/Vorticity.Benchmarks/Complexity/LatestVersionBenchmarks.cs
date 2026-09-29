// What finding a dataset's latest version on a directory of files costs, against the commits kept.
//
// A dataset on a FileObjectStore holds `Commits` versions, empty commits on top of its creation.
// The latest version is found three ways: by listing the commits (`Listed`, what an open does and
// what a refresh or a commit attempt did), from the latest version itself (`Known`, a refresh with
// nothing newer), and from ten versions behind (`Behind`, a writer that lost to others).
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A dataset's latest version found on a file store, against its commits.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class LatestVersionBenchmarks
{
    /// <summary>Versions the dataset holds.</summary>
    [Params(100, 3_000)]
    public int Commits { get; set; }

    private string _root = null!;
    private FileObjectStore _store = null!;

    /// <summary>Creates the dataset and its commits, and checks the three ways agree.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vorticity-latest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileObjectStore(_root);
        BuildAsync().GetAwaiter().GetResult();
        if (Listed() != (ulong)Commits || Known() != (ulong)Commits || Behind() != (ulong)Commits)
        {
            throw new InvalidOperationException("The three ways disagree about the latest version.");
        }
    }

    /// <summary>Deletes the store's directory.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>The commits listed, the latest opened.</summary>
    [Benchmark(Baseline = true)]
    public ulong Listed() => DatasetCommitter.LatestAsync(_store, CancellationToken.None).AsTask().GetAwaiter().GetResult().Version;

    /// <summary>From the latest version: nothing newer.</summary>
    [Benchmark]
    public ulong Known() =>
        DatasetCommitter.LatestAsync(_store, (ulong)Commits, CancellationToken.None).AsTask().GetAwaiter().GetResult().Version;

    /// <summary>From ten versions behind.</summary>
    [Benchmark]
    public ulong Behind() =>
        DatasetCommitter.LatestAsync(_store, (ulong)Commits - 10, CancellationToken.None).AsTask().GetAwaiter().GetResult().Version;

    private async Task BuildAsync()
    {
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(["key"], [types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(_store, schema, new DatasetOptions()).ConfigureAwait(false);
        while (dataset.Version < (ulong)Commits)
        {
            await dataset.CommitAsync([], CancellationToken.None).ConfigureAwait(false);
        }
    }
}
