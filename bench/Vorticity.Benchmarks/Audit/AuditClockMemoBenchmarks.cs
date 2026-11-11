// What a lookup of a dataset handle's synopsis memo and page cache costs, alone and with sixteen threads,
// and what the memo keeps of a hot set while a stream of synopses read once goes through it.
//
// `Synopsis` looks up 4,096 synopses kept in a SynopsisMemo, `Page` 4,096 pages of 1 KiB kept in a PageCache; each
// thread walks every key, and the time is per lookup. The setup also prints the misses of a workload: 200 synopses of
// 200 KB asked at every round (40 MB, within the memo's 64 MiB), and 50 new ones of 200 KB kept at every round and never
// asked again, over 100 rounds: a memo kept first in first out lets the hot ones go to the stream.
using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Lookups of the synopsis memo and the page cache, against the threads asking.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditClockMemoBenchmarks
{
    private const int Keys = 4_096;

    /// <summary>Threads asking at once.</summary>
    [Params(1, 16)]
    public int Threads { get; set; }

    private SynopsisMemo _synopses = null!;
    private PageCache _pages = null!;
    private PageReference[] _references = null!;

    /// <summary>Keeps the synopses and the pages, and prints the misses of the hot set under a stream.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _synopses = new SynopsisMemo();
        _pages = new PageCache(64L << 20);
        _references = new PageReference[Keys];
        for (int i = 0; i < Keys; i++)
        {
            _synopses.Keep((UInt128)(uint)i + 1, (ulong)i * 64, new object(), 1_024);
            _references[i] = new PageReference(1, (long)i * 1_024, 1_024, (UInt128)(uint)i * 0x9E37_79B9 + 7);
            _pages.Add(_references[i], new byte[1_024]);
        }

        Console.WriteLine($"// hot set under a stream: {HotMisses()} misses of 20,000 asks");
    }

    /// <summary>Every synopsis looked up.</summary>
    [Benchmark(OperationsPerInvoke = Keys)]
    public long Synopsis() => Run(thread =>
    {
        long found = 0;
        for (int n = 0; n < Keys; n++)
        {
            int i = (n * 7 + thread * 131) % Keys;
            found += _synopses.TryGet((UInt128)(uint)i + 1, (ulong)i * 64, out object? _) ? 1 : 0;
        }

        return found;
    });

    /// <summary>Every page looked up.</summary>
    [Benchmark(OperationsPerInvoke = Keys)]
    public long Page() => Run(thread =>
    {
        long found = 0;
        for (int n = 0; n < Keys; n++)
        {
            found += _pages.TryGet(_references[(n * 7 + thread * 131) % Keys], out ReadOnlyMemory<byte> page) ? page.Length : 0;
        }

        return found;
    });

    private long Run(Func<int, long> walk)
    {
        if (Threads == 1)
        {
            return walk(0);
        }

        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, thread => Interlocked.Add(ref total, walk(thread)));
        return total;
    }

    /// <summary>The misses of 200 hot synopses asked at every round, while 50 cold ones a round go through the memo.</summary>
    private static long HotMisses()
    {
        SynopsisMemo memo = new SynopsisMemo();
        long misses = 0;
        ulong cold = 1_000_000;
        for (int round = 0; round < 100; round++)
        {
            for (int hot = 0; hot < 200; hot++)
            {
                if (!memo.TryGet((UInt128)(uint)hot + 1, 0, out object? _))
                {
                    misses++;
                    memo.Keep((UInt128)(uint)hot + 1, 0, new object(), 200_000);
                }
            }

            for (int c = 0; c < 50; c++)
            {
                memo.Keep(++cold, 0, new object(), 200_000);
            }
        }

        return misses;
    }
}
