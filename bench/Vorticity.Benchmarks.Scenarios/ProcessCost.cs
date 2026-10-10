using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vorticity.Bench.Scenarios;

/// <summary>
/// What the current process has cost so far: its processor time and its peak resident set, read
/// the way the reference binary reads its own, so the report pairs like with like.
/// </summary>
public static class ProcessCost
{
    /// <summary>The processor time in milliseconds, user and system, and the peak resident set in bytes.</summary>
    /// <remarks>
    /// <c>getrusage</c> on Unix: the two timevals sit at the start of the structure and
    /// <c>ru_maxrss</c> right after them, at the same offset on macOS and Linux because a timeval
    /// is sixteen bytes on both. <c>ru_maxrss</c> is bytes on macOS and kilobytes elsewhere.
    /// </remarks>
    public static unsafe (long CpuMs, long RssBytes) Read()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using Process self = Process.GetCurrentProcess();
            return ((long)self.TotalProcessorTime.TotalMilliseconds, self.PeakWorkingSet64);
        }

        byte* usage = stackalloc byte[512];
        if (getrusage(0, usage) != 0)
        {
            return (-1, -1);
        }

        long userMicros = (*(long*)usage * 1_000_000) + *(int*)(usage + 8);
        long systemMicros = (*(long*)(usage + 16) * 1_000_000) + *(int*)(usage + 24);
        long maxrss = *(long*)(usage + 32);
        return (
            (userMicros + systemMicros) / 1_000,
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? maxrss : maxrss * 1024);
    }

    /// <summary>
    /// What the process has spent so far, to be read before and after a round: its processor time in
    /// nanoseconds, user and system, and on macOS the instructions it retired and the cycles it ran,
    /// the counters <c>/usr/bin/time -l</c> prints, which a user reads without privileges. Zero where
    /// the platform has no such counters. A round whose time moves while its cycles do not was slowed by
    /// the machine, not by its code.
    /// </summary>
    /// <remarks>
    /// <c>proc_pid_rusage</c> with <c>RUSAGE_INFO_V4</c>: the times at bytes 16 and 24 in Mach absolute
    /// units, the instructions at 248 and the cycles at 256 (<c>sys/resource.h</c>, checked against
    /// <c>offsetof</c> on 2026-10-09). No allocation: a round reads it twice.
    /// </remarks>
    public static unsafe ProcessCounters ReadCounters()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            (long cpuMs, _) = Read();
            return new ProcessCounters(cpuMs * 1_000_000, 0, 0);
        }

        byte* info = stackalloc byte[RusageInfoV4Bytes];
        if (proc_pid_rusage(Environment.ProcessId, RusageInfoV4, info) != 0)
        {
            return default;
        }

        ulong ticks = *(ulong*)(info + 16) + *(ulong*)(info + 24);
        return new ProcessCounters(TicksToNanoseconds(ticks), (long)*(ulong*)(info + 248), (long)*(ulong*)(info + 256));
    }

    private const int RusageInfoV4 = 4;

    /// <summary>The bytes of <c>struct rusage_info_v4</c>, 296 on macOS 26, with room to spare.</summary>
    private const int RusageInfoV4Bytes = 512;

    private static readonly (uint Numer, uint Denom) Timebase = ReadTimebase();

    private static long TicksToNanoseconds(ulong ticks) => (long)(ticks * Timebase.Numer / Timebase.Denom);

    private static unsafe (uint, uint) ReadTimebase()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return (1, 1);
        }

        uint* timebase = stackalloc uint[2];
        return mach_timebase_info(timebase) == 0 && timebase[1] != 0 ? (timebase[0], timebase[1]) : (1, 1);
    }

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern unsafe int getrusage(int who, byte* usage);

    [DllImport("libc", EntryPoint = "proc_pid_rusage")]
    private static extern unsafe int proc_pid_rusage(int pid, int flavor, byte* buffer);

    [DllImport("libc", EntryPoint = "mach_timebase_info")]
    private static extern unsafe int mach_timebase_info(uint* timebase);
}

/// <summary>A process's processor time in nanoseconds, and the instructions and cycles it ran, so far.</summary>
public readonly record struct ProcessCounters(long CpuNanoseconds, long Instructions, long Cycles)
{
    /// <summary>What was spent between <paramref name="before"/> and this reading.</summary>
    public ProcessCounters Since(ProcessCounters before) =>
        new ProcessCounters(CpuNanoseconds - before.CpuNanoseconds, Instructions - before.Instructions, Cycles - before.Cycles);
}
