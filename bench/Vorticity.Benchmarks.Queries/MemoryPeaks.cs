using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The most memory the process holds while a window is open, above what it held when the window opened: its private
/// bytes and its working set, sampled every millisecond by a thread that waits on a high-resolution timer between
/// samples, so that the processor time the asks are charged stays theirs, and once more as the window closes. A window
/// of a few milliseconds is sampled a few times, its peak a floor of the true one.
/// </summary>
internal static class MemoryPeaks
{
    private static readonly object s_gate = new object();
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(1);
    private static Window? s_open;
    private static Thread? s_sampler;

    /// <summary>A window's memory: what the process held when it opened, and the most it held since.</summary>
    internal sealed class Window
    {
        private readonly long _startPrivate;
        private readonly long _startWorking;
        private long _private;
        private long _working;

        internal Window((long Private, long Working) start)
        {
            (_startPrivate, _startWorking) = start;
            (_private, _working) = start;
        }

        /// <summary>The most private bytes the process held above what it held when the window opened.</summary>
        internal long Private => Math.Max(0, _private - _startPrivate);

        /// <summary>The largest working set the process had above the one it had when the window opened.</summary>
        internal long Working => Math.Max(0, _working - _startWorking);

        internal void Note((long Private, long Working) now)
        {
            _private = Math.Max(_private, now.Private);
            _working = Math.Max(_working, now.Working);
        }
    }

    /// <summary>Opens a window, the one the sampler notes until it closes.</summary>
    internal static Window Open()
    {
        Window window = new Window(Read());
        lock (s_gate)
        {
            s_open = window;
            if (s_sampler is null)
            {
                s_sampler = new Thread(Sample) { IsBackground = true, Name = "memory peaks", Priority = ThreadPriority.AboveNormal };
                s_sampler.Start();
            }
        }

        return window;
    }

    /// <summary>Closes <paramref name="window"/>, its last sample taken now.</summary>
    internal static Window Close(Window window)
    {
        lock (s_gate)
        {
            window.Note(Read());
            if (s_open == window)
            {
                s_open = null;
            }
        }

        return window;
    }

    private static void Sample()
    {
        WaitHandle? timer = PreciseDelay.NewTimer();
        while (true)
        {
            lock (s_gate)
            {
                s_open?.Note(Read());
            }

            if (timer is not null && PreciseDelay.Arm(timer, Every))
            {
                timer.WaitOne();
            }
            else
            {
                Thread.Sleep(Every);
            }
        }
    }

    /// <summary>The process's private bytes and working set: the working set alone where the counters are Windows'.</summary>
    private static (long Private, long Working) Read()
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessMemoryCounters counters = default;
            counters.Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
            if (K32GetProcessMemoryInfo(new IntPtr(-1), ref counters, counters.Size))
            {
                return ((long)counters.PrivateUsage, (long)counters.WorkingSetSize);
            }
        }

        long working = Environment.WorkingSet;
        return (working, working);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    /// <summary>PROCESS_MEMORY_COUNTERS_EX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }
}
