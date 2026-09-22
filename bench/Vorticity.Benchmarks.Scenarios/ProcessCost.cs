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

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern unsafe int getrusage(int who, byte* usage);
}
