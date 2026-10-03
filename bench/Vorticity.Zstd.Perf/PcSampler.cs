using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// A sampling profiler at the instruction, for the micro-benchmarks (<c>--pcprofile file</c>): a
/// thread suspends the measured one about every 100 microseconds, reads its program counter through
/// Mach's <c>thread_get_state</c>, and counts the addresses, the executable's slide taken off, so that
/// they match its dSYM (<c>bench/zstd-pcprofile.py</c> resolves and annotates them). The address of a
/// suspended thread is the next instruction to retire: one that waits, or the one after it.
/// </summary>
internal static unsafe class PcSampler
{
    private const int ArmThreadState64 = 6;
    private const int ArmThreadState64Count = 68;
    private const int PcOffset = 64;

    /// <summary>The file the samples are appended to, or null when not profiling.</summary>
    public static string? Path { get; set; }

    [DllImport("libSystem.dylib")]
    private static extern uint mach_thread_self();

    [DllImport("libSystem.dylib")]
    private static extern int thread_suspend(uint thread);

    [DllImport("libSystem.dylib")]
    private static extern int thread_resume(uint thread);

    [DllImport("libSystem.dylib")]
    private static extern int thread_get_state(uint thread, int flavor, uint* state, uint* count);

    [DllImport("libSystem.dylib")]
    private static extern nint _dyld_get_image_vmaddr_slide(uint imageIndex);

    /// <summary>Samples the calling thread until disposed, when <see cref="Path"/> is set.</summary>
    public static IDisposable? Start() => Path is null ? null : new Session(Path);

    private sealed class Session : IDisposable
    {
        private readonly string _path;
        private readonly uint _target = mach_thread_self();
        private readonly Dictionary<ulong, int> _counts = [];
        private readonly Thread _thread;
        private volatile bool _stop;

        public Session(string path)
        {
            _path = path;
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
            File.AppendAllLines(_path, _counts.Select(entry => $"{entry.Key:x} {entry.Value}"));
        }

        private void Run()
        {
            uint* state = stackalloc uint[ArmThreadState64Count];
            ulong slide = (ulong)_dyld_get_image_vmaddr_slide(0);
            long interval = Stopwatch.Frequency / 10_000;
            while (!_stop)
            {
                long next = Stopwatch.GetTimestamp() + interval;
                if (thread_suspend(_target) == 0)
                {
                    uint count = ArmThreadState64Count;
                    if (thread_get_state(_target, ArmThreadState64, state, &count) == 0)
                    {
                        ulong pc = *(ulong*)(state + PcOffset) - slide;
                        _counts[pc] = _counts.GetValueOrDefault(pc) + 1;
                    }

                    thread_resume(_target);
                }

                while (Stopwatch.GetTimestamp() < next)
                {
                    Thread.SpinWait(20);
                }
            }
        }
    }
}
