// Audit VW-03: what an index fragment built after the write pays in round trips for the array trees of a file's chunks,
// which say which chunks are dictionaries before any row is read. A file of 8 columns (six longs, two texts of 20 values)
// and 524,288 rows in chunks of 256 KiB, kept in memory behind a source that answers each request, one range or many,
// after a millisecond, as an object store would sooner or later; the fragment of the default policy built over it.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.IO;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A fragment built over a file whose reads take a round trip each.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditTreeProbeBenchmarks
{
    private const int Rows = 524_288;

    private byte[] _bytes = [];

    /// <summary>A row of eight columns.</summary>
    [VortexRecord]
    public partial record struct Wide(long A, long B, long C, long D, string E, string F, long G, long H);

    /// <summary>Writes the file and keeps its bytes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tree-probe-{Guid.NewGuid():N}.vortex");
        try
        {
            WriteAsync(path).GetAwaiter().GetResult();
            _bytes = System.IO.File.ReadAllBytes(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        DelayedSource probe = new DelayedSource(new MemorySegmentSource(_bytes));
        int runs = BuildAsync(probe).GetAwaiter().GetResult();
        Console.WriteLine($"// {runs} runs; {probe.Requests} requests of {probe.Ranges} ranges");
    }

    /// <summary>The fragment built; the runs it holds.</summary>
    [Benchmark]
    public int Fragment() => BuildAsync(new DelayedSource(new MemorySegmentSource(_bytes))).GetAwaiter().GetResult();

    private static async Task<int> BuildAsync(DelayedSource source)
    {
        await using VortexFile file = await VortexSession.Default.OpenAsync(source).ConfigureAwait(false);
        IndexFragment fragment = await VortexFileIndexer.BuildFragmentAsync(file, IndexPolicy.Auto).ConfigureAwait(false);
        return fragment.Reports.Count;
    }

    private static async Task WriteAsync(string path)
    {
        Random random = new Random(29);
        string[] names = [.. Enumerable.Range(0, 20).Select(i => $"service-{i:D2}")];
        Wide[] rows = new Wide[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Wide(i, random.Next(1_000), random.NextInt64(), i / 64, names[random.Next(20)], names[i % 20], random.Next(), -i);
        }

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Wide>(
            path, new VortexWriteOptions { ChunkTargetBytes = 1 << 18 });
        await writer.WriteAsync<Wide>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A source that answers every request a millisecond after it comes, however many ranges it asks.</summary>
    private sealed class DelayedSource(ISegmentSource inner) : ISegmentSource
    {
        private int _requests;
        private int _ranges;

        public long Length => inner.Length;

        /// <summary>The requests asked so far.</summary>
        internal int Requests => _requests;

        /// <summary>The ranges they asked.</summary>
        internal int Ranges => _ranges;

        public async ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Increment(ref _ranges);
            await Wait().ConfigureAwait(false);
            return await inner.ReadAsync(range, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _ranges, ranges.Length);
            await Wait().ConfigureAwait(false);
            await inner.ReadAsync(ranges, leases, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        // A millisecond to the microsecond, off the caller's thread, as a round trip that holds no thread.
        private static Task Wait() => Task.Run(static () =>
        {
            long until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 1_000);
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }
        });
    }
}
