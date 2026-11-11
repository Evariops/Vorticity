// What one batch pays, under the lock its scan's lanes share, to take its segments from what the scan holds.
//
// Each operation is a batch of a scan over a file whose chunks are a block each, wide: 64 segments of its own, none held
// yet. It claims them, fills them as its read would, publishes them for the batches after it, and the batch delivered
// as many batches before it as there are lanes is released: the scan holds the segments of the batches in flight, 64 at
// one lane, about a thousand at sixteen.
using BenchmarkDotNet.Attributes;

using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A batch's claim, publish and release among the segments a scan holds.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditScanSegmentsClaimBenchmarks
{
    private const int Columns = 64;
    private const int Length = 64;

    /// <summary>The batches in flight.</summary>
    [Params(1, 16)]
    public int Lanes { get; set; }

    private ScanSegments _segments = null!;
    private SegmentRequestSet[] _sets = null!;
    private NativeSegmentOwner _owner = null!;
    private long _batch;

    /// <summary>Makes the set and the batches' request sets.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _segments = new ScanSegments(Lanes);
        _owner = AlignedBufferPool.Shared.Rent(Length, 64);
        _sets = new SegmentRequestSet[Lanes];
        for (int l = 0; l < Lanes; l++)
        {
            _sets[l] = new SegmentRequestSet(Columns);
        }
    }

    /// <summary>Releases what is held.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (SegmentRequestSet set in _sets)
        {
            set.Release();
        }

        _segments.Dispose();
        _owner.Release();
    }

    /// <summary>One batch: its claim, its read's results, its publish, and the release of the batch a lane's turn before.</summary>
    [Benchmark(OperationsPerInvoke = 16)]
    public int Batch()
    {
        int filled = 0;
        for (int n = 0; n < 16; n++)
        {
            long batch = _batch++;
            SegmentRequestSet set = _sets[batch % Lanes];
            set.Release();
            for (int c = 0; c < Columns; c++)
            {
                set.Add(new SegmentSpec((ulong)(((batch * Columns) + c) * Length), Length, 6, 0, 0));
            }

            _segments.Claim(set, batch, null);
            for (int slot = 0; slot < set.Count; slot++)
            {
                if (!set.IsFilled(slot))
                {
                    set.SetSharedResult(slot, _owner, _owner.Buffer);
                    filled++;
                }
            }

            set.Complete();
            _segments.Publish(set, batch);
            _segments.Release(batch - Lanes + 1);
        }

        return filled;
    }
}
