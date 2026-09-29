// What a batch pays to claim the segments it reads, as the segments per batch grow.
//
// A scan holds the segments its batches read, for the batches that ask for them again, in a set
// every lane shares. A batch first takes what the set holds, then claims each segment it still
// lacks, and `Original` (ScanSegmentsBefore.cs) looks for each of those by a walk over everything
// the set holds: a batch that opens G new segments while the set holds the G of the batch before
// pays G squared comparisons, under the lock every lane takes. `Library` is the set itself.
using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One batch: register its segments, claim, read, publish, release the batch before.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class ScanSegmentsBenchmarks
{
    /// <summary>Segments a batch reads, about one a column.</summary>
    [Params(16, 64, 256, 1_024)]
    public int Segments { get; set; }

    /// <summary>
    /// Whether every batch opens segments of its own, as a scan whose chunks are a batch long does,
    /// or asks for the segments the batch before read, as a scan inside a chunk does.
    /// </summary>
    [Params(Reads.New, Reads.Held)]
    public Reads Pattern { get; set; }

    /// <summary>What a batch asks for.</summary>
    public enum Reads
    {
        /// <summary>Segments no batch has read.</summary>
        New,

        /// <summary>The segments of the batch before.</summary>
        Held,
    }

    private const uint SegmentBytes = 64;

    private NativeSegmentOwner _block = null!;
    private SegmentRequestSet _requests = null!;
    private ScanSegmentsBefore _original = null!;
    private ScanSegments _library = null!;
    private long _batch;

    /// <summary>One set of each kind, and one block every segment is read into.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _block = NativeSegmentOwner.Allocate((int)SegmentBytes, 64);
        _requests = new SegmentRequestSet(Segments);
        _original = new ScanSegmentsBefore(lanes: 1);
        _library = new ScanSegments(lanes: 1);
    }

    /// <summary>Releases what the sets hold.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _original.Dispose();
        _library.Dispose();
        _requests.Release();
        _block.Release();
    }

    /// <summary>The set that walks what it holds for each segment it claims.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        long batch = ++_batch;
        Register(batch);
        _original.Claim(_requests, batch, waiter: null);
        int read = Read();
        _original.Publish(_requests, batch);
        _original.Release(batch);
        _requests.Release();
        return read;
    }

    /// <summary>The set of the library.</summary>
    [Benchmark]
    public int Library()
    {
        long batch = ++_batch;
        Register(batch);
        _library.Claim(_requests, batch, waiter: null);
        int read = Read();
        _library.Publish(_requests, batch);
        _library.Release(batch);
        _requests.Release();
        return read;
    }

    private void Register(long batch)
    {
        long first = Pattern == Reads.New ? batch * Segments : 0;
        for (int i = 0; i < Segments; i++)
        {
            _requests.Add(new SegmentSpec((ulong)(first + i) * SegmentBytes, SegmentBytes, 0, 0, 0));
        }
    }

    // What a source does with the slots the set could not fill: one block for all of them.
    private int Read()
    {
        int read = 0;
        for (int slot = 0; slot < _requests.Count; slot++)
        {
            if (!_requests.IsFilled(slot))
            {
                _requests.SetSharedResult(slot, _block, _block.Buffer);
                read++;
            }
        }

        _requests.Complete();
        return read;
    }
}
