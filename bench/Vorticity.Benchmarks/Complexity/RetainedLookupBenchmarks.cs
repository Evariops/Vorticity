// What a scan pays to find the chunks it retains, as the columns it reads grow.
//
// A chunk larger than a batch is decoded once and every batch that reads it looks it up in the
// scan's table of retained chunks, once per column. The table hashes its entries by key past sixteen
// of them; when it kept them in a dense array searched in order, a lookup cost the entries before it
// and a batch of C columns C squared over two comparisons, 1.45 ms where it takes 89 µs at 1,024
// columns (6dcd5e4a, the measure against a checkout of the original).
//
// Measure it with tiering off as well as on: in one process the cases share the JIT's profile, and
// the path the small tables take first shapes the code the wide ones run next.
using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One chunk's worth of batches, each looking up every column and releasing the batch before.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class RetainedLookupBenchmarks
{
    /// <summary>Columns the scan reads, each holding one retained chunk at a time.</summary>
    [Params(4, 16, 64, 256, 1_024)]
    public int Columns { get; set; }

    // Batches a chunk spans: the first claims and publishes every column's new chunk, the others
    // find them, and each release after the first batch evicts the chunks of the chunk before.
    private const int BatchesPerChunk = 4;

    private ScanContext _claimant = null!;
    private RetainedChunks _library = null!;
    private long _batch;
    private long _chunk;

    /// <summary>The table, sized for the columns as a scan sizes its own.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _claimant = new ScanContext(["vortex.primitive"]);
        _library = RetainedChunks.Create(Columns, lanes: 1);
    }

    /// <summary>Gives the entries back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _library.Dispose();
        _claimant.Dispose();
    }

    /// <summary>The table of the library.</summary>
    [Benchmark]
    public int Library()
    {
        int found = 0;
        long chunk = ++_chunk;
        for (int b = 0; b < BatchesPerChunk; b++)
        {
            long batch = ++_batch;
            for (int c = 0; c < Columns; c++)
            {
                if (_library.TryGet(Key(chunk, c), _claimant, batch, out RetainedChunk? claim, out _, out _))
                {
                    found++;
                }
                else
                {
                    _library.Publish(claim!, 0, batch);
                }
            }

            _library.Release(batch);
        }

        return found;
    }

    // The chunk's segment, numbered as a writer numbers them: every column's chunks in turn.
    private static long Key(long chunk, int column) => (column * 4096L) + (chunk & 4095);
}
