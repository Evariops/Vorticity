using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A file read as an object store serves it: every request waits a round trip before its bytes, a
/// set of ranges asked together waiting once. It counts the requests, and the dependent steps: the
/// requests that start while none is in flight, each a round trip the query waited on before it
/// could ask for more. What a query costs on a store is about its steps times the round trip, and
/// its requests are what the store bills.
/// </summary>
internal sealed class LatencySource : ISegmentSource
{
    private readonly SafeFileHandle _file;
    private readonly TimeSpan _latency;
    private int _inFlight;
    private long _requests;
    private long _steps;

    internal LatencySource(string path, TimeSpan latency)
    {
        _file = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_file);
        _latency = latency;
    }

    public long Length { get; }

    /// <summary>The requests so far: a single range, or a set asked together.</summary>
    internal long Requests => Interlocked.Read(ref _requests);

    /// <summary>The dependent steps so far: the requests that found none in flight.</summary>
    internal long Steps => Interlocked.Read(ref _steps);

    public async ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
            return Read(range);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
            for (int r = 0; r < ranges.Length; r++)
            {
                leases.Span[r] = Read(ranges.Span[r]);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public ValueTask DisposeAsync()
    {
        _file.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Begin()
    {
        Interlocked.Increment(ref _requests);
        if (Interlocked.Increment(ref _inFlight) == 1)
        {
            Interlocked.Increment(ref _steps);
        }
    }

    private SegmentLease Read(SegmentRange range)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(range.Length);
        int read = RandomAccess.Read(_file, bytes, range.Offset);
        if (read != range.Length)
        {
            throw new EndOfStreamException($"{read} of {range.Length} bytes at {range.Offset}.");
        }

        return new SegmentLease(bytes);
    }
}
