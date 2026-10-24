using System;
using System.Diagnostics;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The most private bytes the process holds while it is open, sampled every 100 ms on a thread of its own, and what of them
/// was native blocks and the collector's committed memory at that sample.
/// </summary>
internal sealed class PeakMemory : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _peak;

    internal PeakMemory()
    {
        using (Process self = Process.GetCurrentProcess())
        {
            First = _peak = self.PrivateMemorySize64;
        }

        _thread = new Thread(Sample) { IsBackground = true, Name = "peak memory" };
        _thread.Start();
    }

    /// <summary>The private bytes the process held when it was opened.</summary>
    internal long First { get; }

    internal long Peak => Interlocked.Read(ref _peak);

    /// <summary>The bytes of native blocks the library held at the peak (<see cref="NativeSegmentOwner.NativeBytes"/>).</summary>
    internal long PeakNative { get; private set; }

    /// <summary>The bytes of <see cref="PeakNative"/> parked in the shared pool, held for a later rent rather than in use.</summary>
    internal long PeakParked { get; private set; }

    /// <summary>The memory the collector had committed at its last collection before the peak.</summary>
    internal long PeakCommitted { get; private set; }

    private void Sample()
    {
        using Process self = Process.GetCurrentProcess();
        while (!_stop)
        {
            self.Refresh();
            long bytes = self.PrivateMemorySize64;
            if (bytes > Interlocked.Read(ref _peak))
            {
                PeakNative = NativeSegmentOwner.NativeBytes;
                PeakParked = AlignedBufferPool.Shared.ParkedBytes;
                PeakCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
                Interlocked.Exchange(ref _peak, bytes);
            }

            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
