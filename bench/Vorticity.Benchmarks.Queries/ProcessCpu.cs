using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The processor time the process has taken so far, in milliseconds, precise to the microsecond on Windows: its times
/// there move at the clock's tick, 15.6 ms, which a question of a few milliseconds reads as 0, 15.6 or 31.2. Its cycle
/// time moves at every switch of its threads, counted at the rate of the timestamp counter, which runs at one rate
/// whatever the cores' clocks do: the cycles over that rate, measured once against the wall clock by a thread that
/// spins. Elsewhere, the runtime's processor time.
/// </summary>
internal static class ProcessCpu
{
    private static readonly double s_cyclesPerMillisecond = OperatingSystem.IsWindows() ? Calibrate() : 0;

    /// <summary>The rate the cycles are counted at, in GHz; 0 where the runtime's processor time is read.</summary>
    internal static double GigaHertz => s_cyclesPerMillisecond / 1e6;

    /// <summary>The processor time of every thread of the process so far, in milliseconds.</summary>
    internal static double Milliseconds() =>
        s_cyclesPerMillisecond > 0 && QueryProcessCycleTime(new IntPtr(-1), out ulong cycles)
            ? cycles / s_cyclesPerMillisecond
            : Environment.CpuUsage.TotalTime.TotalMilliseconds;

    /// <summary>
    /// The cycles a thread that spins counts in a millisecond of the wall clock: the most of five spins of 50 ms, as a
    /// spin that the scheduler set aside counts fewer.
    /// </summary>
    private static double Calibrate()
    {
        double best = 0;
        IntPtr thread = new IntPtr(-2);
        for (int trial = 0; trial < 5; trial++)
        {
            if (!QueryThreadCycleTime(thread, out ulong start))
            {
                return 0;
            }

            long begin = Stopwatch.GetTimestamp();
            long until = begin + (Stopwatch.Frequency / 20);
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(64);
            }

            QueryThreadCycleTime(thread, out ulong end);
            double milliseconds = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            best = Math.Max(best, (end - start) / milliseconds);
        }

        return best;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
