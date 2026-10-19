using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A delay to the tenth of a millisecond, for a store that waits a round trip per request: on Windows,
/// <see cref="Task.Delay(TimeSpan)"/> rounds up to the system's timer, 15.6 ms by default, so that a
/// round trip of 5 ms costs three times its length. One thread waits on a high-resolution waitable
/// timer for the earliest deadline and completes every delay due, so that no thread spins and the
/// process's processor time stays the work's.
/// </summary>
internal static class PreciseDelay
{
    private const uint HighResolution = 0x2;
    private const uint AllAccess = 0x1F0003;

    private static readonly object s_gate = new object();
    private static readonly PriorityQueue<TaskCompletionSource, long> s_waiting = new PriorityQueue<TaskCompletionSource, long>();
    private static readonly AutoResetEvent s_changed = new AutoResetEvent(false);
    private static readonly TimerHandle? s_timer = OperatingSystem.IsWindows() ? TimerHandle.Create() : null;

    static PreciseDelay()
    {
        if (s_timer is not null)
        {
            new Thread(Loop) { IsBackground = true, Name = "precise-delay", Priority = ThreadPriority.AboveNormal }.Start();
        }
    }

    /// <summary>A task that completes once <paramref name="delay"/> has passed, its continuations on the thread pool.</summary>
    internal static Task Delay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        if (s_timer is null)
        {
            return Task.Delay(delay);
        }

        TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long deadline = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        lock (s_gate)
        {
            bool earliest = !s_waiting.TryPeek(out _, out long first) || deadline < first;
            s_waiting.Enqueue(done, deadline);
            if (earliest)
            {
                s_changed.Set();
            }
        }

        return done.Task;
    }

    /// <summary>A task that completes once the clock passes <paramref name="timestamp"/>, a <see cref="Stopwatch"/> timestamp.</summary>
    internal static Task Until(long timestamp)
    {
        long left = timestamp - Stopwatch.GetTimestamp();
        return left <= 0 ? Task.CompletedTask : Delay(TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency));
    }

    private static void Loop()
    {
        WaitHandle[] handles = [s_timer!, s_changed];
        List<TaskCompletionSource> due = [];
        while (true)
        {
            long wait;
            lock (s_gate)
            {
                long now = Stopwatch.GetTimestamp();
                while (s_waiting.TryPeek(out TaskCompletionSource? next, out long deadline) && deadline <= now)
                {
                    due.Add(s_waiting.Dequeue());
                }

                wait = s_waiting.TryPeek(out _, out long first) ? first - now : -1;
            }

            foreach (TaskCompletionSource done in due)
            {
                done.TrySetResult();
            }

            due.Clear();
            if (wait < 0)
            {
                s_changed.WaitOne();
                continue;
            }

            // Relative due times are negative, in units of 100 ns.
            long relative = -Math.Max(1, wait * 10_000_000 / Stopwatch.Frequency);
            if (!SetWaitableTimerEx(s_timer!.SafeWaitHandle, ref relative, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                Thread.Sleep(1);
                continue;
            }

            WaitHandle.WaitAny(handles);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

    /// <summary>A high-resolution waitable timer, as a handle to wait on.</summary>
    private sealed class TimerHandle : WaitHandle
    {
        internal static TimerHandle? Create()
        {
            SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, AllAccess);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return new TimerHandle { SafeWaitHandle = handle };
        }
    }
}
