// What finding a page of the commit being built costs, against the pages it holds.
//
// A commit object builder holds `Pages` pages, as a repack or a large batch leaves it. Each of them
// is looked up once, as a relocation that walks the new tree reads them, and as many references of
// an older version are looked up and missed, as the inlining of the top of the tree asks about
// every page it reaches. Run in a checkout of the original and in the tree.
using System;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Pages of the commit being built looked up, against the pages it holds.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class CommitPageLookupBenchmarks
{
    /// <summary>Pages the builder holds.</summary>
    [Params(1_024, 16_384)]
    public int Pages { get; set; }

    private CommitObjectBuilder _builder = null!;
    private PageReference[] _held = null!;
    private PageReference[] _older = null!;

    /// <summary>Fills the builder, and checks every page it holds is found and no other.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _builder = new CommitObjectBuilder(7);
        _held = new PageReference[Pages];
        _older = new PageReference[Pages];
        byte[] page = new byte[128];
        for (int i = 0; i < Pages; i++)
        {
            BitConverter.TryWriteBytes(page, i);
            _held[i] = _builder.AddPage(page);
            if (i % 8 == 0)
            {
                _builder.AddFragment(page.AsSpan(0, 16));
            }

            _older[i] = _held[i] with { Version = 6 };
        }

        if (Lookup() != Pages)
        {
            throw new InvalidOperationException("A page the builder holds was not found, or another was.");
        }
    }

    /// <summary>Every page held looked up, and as many of an older version.</summary>
    [Benchmark]
    public int Lookup()
    {
        int found = 0;
        for (int i = 0; i < _held.Length; i++)
        {
            found += _builder.TryGetPage(_held[i], out _) ? 1 : 0;
            found += _builder.TryGetPage(_older[i], out _) ? 1 : 0;
        }

        return found;
    }
}
