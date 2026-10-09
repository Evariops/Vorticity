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
/// thread suspends the measured one about every 100 microseconds, reads its program counter, and
/// counts the addresses. On macOS through Mach's <c>thread_get_state</c>, the addresses as they are,
/// after a header that places the images: the executable's slide, and where a symbol of each native
/// library registered loaded (<c>bench/zstd-pcprofile.py</c> resolves them, against the executable's
/// dSYM or the library's symbols, and annotates them). On Windows through <c>GetThreadContext</c>,
/// resolved in the process by DbgHelp against the PDBs (see <see cref="WindowsSymbols"/>), which
/// appends a report to the file. The address of a suspended thread is the next instruction to
/// retire: one that waits, or the one after it.
/// </summary>
internal static unsafe class PcSampler
{
    private const int ArmThreadState64 = 6;
    private const int ArmThreadState64Count = 68;
    private const int PcOffset = 64;

    /// <summary>The file the samples are appended to, or null when not profiling.</summary>
    public static string? Path { get; set; }

    /// <summary>
    /// On Windows, the functions whose samples the report also gives instruction by instruction:
    /// those whose name contains this (<c>--pcfunc</c>).
    /// </summary>
    public static string? Function { get; set; }

    /// <summary>
    /// On Windows, the functions whose samples the report also attributes to their callers: those
    /// whose name contains this (<c>--pccaller</c>). The caller is the first word of the stack, at
    /// the sample, that follows a call instruction into another function: exact in a function that
    /// keeps the return address within its first eight words, as a leaf such as memmove does.
    /// </summary>
    public static string? Callers { get; set; }

    /// <summary>What the next session measures, for the report's heading.</summary>
    public static string Label { get; set; } = string.Empty;

    /// <summary>Native libraries to place: their path, a symbol, and where it is loaded.</summary>
    public static List<(string Library, string Symbol, nint Address)> Libraries { get; } = [];

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
    public static IDisposable? Start() =>
        Path is null ? null : OperatingSystem.IsWindows() ? new WindowsSession(Path) : new Session(Path);

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
            var lines = new List<string> { $"# slide {(ulong)_dyld_get_image_vmaddr_slide(0):x}" };
            lines.AddRange(Libraries.Select(library => $"# library {library.Library} {library.Symbol} {(ulong)library.Address:x}"));
            lines.AddRange(_counts.Select(entry => $"{entry.Key:x} {entry.Value}"));
            File.AppendAllLines(_path, lines);
        }

        private void Run()
        {
            uint* state = stackalloc uint[ArmThreadState64Count];
            long interval = Stopwatch.Frequency / 10_000;
            while (!_stop)
            {
                long next = Stopwatch.GetTimestamp() + interval;
                if (thread_suspend(_target) == 0)
                {
                    uint count = ArmThreadState64Count;
                    if (thread_get_state(_target, ArmThreadState64, state, &count) == 0)
                    {
                        ulong pc = *(ulong*)(state + PcOffset);
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

    /// <summary>
    /// The Windows sampler: the measured thread suspended, its <c>CONTEXT</c>'s instruction pointer
    /// read, the thread resumed, and only then the address counted, so that nothing allocates while
    /// the thread may hold a lock.
    /// </summary>
    private sealed class WindowsSession : IDisposable
    {
        private const uint ContextControl = 0x0010_0001; // CONTEXT_AMD64 | CONTEXT_CONTROL
        private const int ContextSize = 1232;
        private const int ContextFlagsOffset = 0x30;
        private const int RipOffset = 0xF8;
        private const int RspOffset = 0x98;
        private const int StackWords = 8;
        private const uint DuplicateSameAccess = 2;

        private readonly string _path;
        private readonly string _label = Label;
        private readonly nint _target;
        private readonly Dictionary<ulong, int> _counts = [];
        private readonly List<(ulong Rip, ulong[] Stack)> _stacks = [];
        private readonly bool _callers = Callers is not null;
        private readonly Thread _thread;
        private volatile bool _stop;

        public WindowsSession(string path)
        {
            _path = path;
            nint process = GetCurrentProcess();
            if (!DuplicateHandle(process, GetCurrentThread(), process, out _target, 0, false, DuplicateSameAccess))
            {
                throw new InvalidOperationException("DuplicateHandle failed: " + Marshal.GetLastPInvokeError());
            }

            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
            _ = CloseHandle(_target);
            string report = WindowsSymbols.Report(_counts, _label, Function) + WindowsSymbols.CallerReport(_stacks, Callers);
            File.AppendAllText(_path, report);
            Console.Write(report);
        }

        private void Run()
        {
            byte* context = (byte*)NativeMemory.AlignedAlloc(ContextSize, 64);
            ulong* window = stackalloc ulong[StackWords];
            long interval = Stopwatch.Frequency / 10_000;
            try
            {
                while (!_stop)
                {
                    long next = Stopwatch.GetTimestamp() + interval;
                    if (SuspendThread(_target) != uint.MaxValue)
                    {
                        *(uint*)(context + ContextFlagsOffset) = ContextControl;
                        bool read = GetThreadContext(_target, context);
                        if (read && _callers)
                        {
                            // The top of the stack, copied while the thread cannot change it.
                            ulong* top = (ulong*)*(ulong*)(context + RspOffset);
                            for (int k = 0; k < StackWords; k++)
                            {
                                window[k] = top[k];
                            }
                        }

                        _ = ResumeThread(_target);
                        if (read)
                        {
                            ulong rip = *(ulong*)(context + RipOffset);
                            _counts[rip] = _counts.GetValueOrDefault(rip) + 1;
                            if (_callers)
                            {
                                _stacks.Add((rip, new ReadOnlySpan<ulong>(window, StackWords).ToArray()));
                            }
                        }
                    }

                    while (Stopwatch.GetTimestamp() < next)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
            finally
            {
                NativeMemory.AlignedFree(context);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(nint thread);

        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(nint thread);

        [DllImport("kernel32.dll")]
        private static extern bool GetThreadContext(nint thread, byte* context);
    }
}

/// <summary>
/// The samples of <see cref="PcSampler"/> on Windows, resolved in the process by DbgHelp: the
/// executable's PDB, which Native AOT writes beside it, names its functions and their lines, and
/// a native library's names what it exports. The report gives the functions by samples, the lines
/// by samples, and for the functions <c>--pcfunc</c> names, every sampled instruction by its
/// relative virtual address, which <c>dumpbin /disasm</c> prints the code at.
/// </summary>
internal static unsafe class WindowsSymbols
{
    private const uint SymOptUndname = 0x2;
    private const uint SymOptDeferredLoads = 0x4;
    private const uint SymOptLoadLines = 0x10;
    private const uint SymOptFailCriticalErrors = 0x200;
    private const int MaxName = 1024;
    private const int SymbolInfoSize = 88;
    private const int LineSize = 40;

    private static bool s_initialized;

    public static string Report(Dictionary<ulong, int> counts, string label, string? function)
    {
        nint process = -1;
        if (!s_initialized)
        {
            SymSetOptions(SymOptUndname | SymOptDeferredLoads | SymOptLoadLines | SymOptFailCriticalErrors);
            s_initialized = SymInitializeW(process, AppContext.BaseDirectory, true);
        }
        else
        {
            SymRefreshModuleList(process);
        }

        long total = counts.Values.Sum();
        var functions = new Dictionary<string, long>();
        var lines = new Dictionary<(string Function, string Line), long>();
        var instructions = new List<(string Function, ulong Rva, ulong Offset, string Line, int Samples)>();
        byte* symbol = stackalloc byte[SymbolInfoSize + (MaxName * 2)];
        byte* line = stackalloc byte[LineSize];
        foreach ((ulong address, int samples) in counts)
        {
            new Span<byte>(symbol, SymbolInfoSize).Clear();
            *(uint*)symbol = SymbolInfoSize;
            *(uint*)(symbol + 80) = MaxName;
            string name;
            ulong displacement = 0;
            ulong rva = 0;
            if (SymFromAddrW(process, address, out displacement, symbol))
            {
                int length = (int)*(uint*)(symbol + 76);
                name = new string((char*)(symbol + 84), 0, Math.Min(length, MaxName - 1));
                rva = address - *(ulong*)(symbol + 32);
            }
            else
            {
                name = "?";
            }

            new Span<byte>(line, LineSize).Clear();
            *(uint*)line = LineSize;
            string where = SymGetLineFromAddrW64(process, address, out _, line)
                ? $"{System.IO.Path.GetFileName(new string(*(char**)(line + 24)))}:{*(uint*)(line + 16)}"
                : "?";
            functions[name] = functions.GetValueOrDefault(name) + samples;
            lines[(name, where)] = lines.GetValueOrDefault((name, where)) + samples;
            if (function is not null && name.Contains(function, StringComparison.Ordinal))
            {
                instructions.Add((name, rva, displacement, where, samples));
            }
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"== {label}: {total} samples");
        report.AppendLine("  samples      %  function");
        foreach ((string name, long samples) in functions.OrderByDescending(f => f.Value).Take(30))
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {samples,7} {100.0 * samples / total,6:F2}  {name}");
        }

        report.AppendLine("  -- lines");
        foreach (((string name, string where), long samples) in lines.OrderByDescending(l => l.Value).Take(40))
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {samples,7} {100.0 * samples / total,6:F2}  {where,-40} {name}");
        }

        foreach (var group in instructions.GroupBy(i => i.Function))
        {
            long inFunction = group.Sum(i => (long)i.Samples);
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  -- {group.Key}: {inFunction} samples, by instruction (rva, offset)");
            foreach (var i in group.OrderBy(i => i.Rva))
            {
                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"  {i.Rva:x8} +{i.Offset,-6:x} {i.Samples,7} {100.0 * i.Samples / inFunction,6:F2}  {i.Line}");
            }
        }

        return report.ToString();
    }

    /// <summary>
    /// The samples of the functions <paramref name="function"/> names, by caller: for each, the first
    /// stack word that resolves into another function and follows a call (a <c>call rel32</c>, or an
    /// indirect call of two, three or six bytes), with the line of the call.
    /// </summary>
    public static string CallerReport(List<(ulong Rip, ulong[] Stack)> samples, string? function)
    {
        if (function is null || samples.Count == 0)
        {
            return string.Empty;
        }

        nint process = -1;
        byte* symbol = stackalloc byte[SymbolInfoSize + (MaxName * 2)];
        byte* line = stackalloc byte[LineSize];
        var names = new Dictionary<ulong, string?>();
        string? NameOf(ulong address)
        {
            if (names.TryGetValue(address, out string? known))
            {
                return known;
            }

            new Span<byte>(symbol, SymbolInfoSize).Clear();
            *(uint*)symbol = SymbolInfoSize;
            *(uint*)(symbol + 80) = MaxName;
            string? name = SymFromAddrW(process, address, out _, symbol)
                ? new string((char*)(symbol + 84), 0, Math.Min((int)*(uint*)(symbol + 76), MaxName - 1))
                : null;
            names[address] = name;
            return name;
        }

        var callers = new Dictionary<string, long>();
        long total = 0;
        foreach ((ulong rip, ulong[] stack) in samples)
        {
            string? name = NameOf(rip);
            if (name is null || !name.Contains(function, StringComparison.Ordinal))
            {
                continue;
            }

            total++;
            string caller = "?";
            foreach (ulong word in stack)
            {
                string? callerName = NameOf(word);
                if (callerName is null || callerName == name || !FollowsCall(word))
                {
                    continue;
                }

                new Span<byte>(line, LineSize).Clear();
                *(uint*)line = LineSize;
                string where = SymGetLineFromAddrW64(process, word - 1, out _, line)
                    ? $"{System.IO.Path.GetFileName(new string(*(char**)(line + 24)))}:{*(uint*)(line + 16)}"
                    : "?";
                caller = $"{where,-40} {callerName}";
                break;
            }

            callers[caller] = callers.GetValueOrDefault(caller) + 1;
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  -- callers of {function}: {total} samples");
        foreach ((string caller, long count) in callers.OrderByDescending(c => c.Value).Take(30))
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {count,7} {100.0 * count / total,6:F2}  {caller}");
        }

        return report.ToString();

        // A return address follows the call that pushed it: E8 rel32, or FF /2 in its short forms.
        static bool FollowsCall(ulong address)
        {
            byte* p = (byte*)address;
            return p[-5] == 0xE8 || (p[-2] == 0xFF && (p[-1] & 0x38) == 0x10)
                || (p[-3] == 0xFF && (p[-2] & 0x38) == 0x10) || (p[-6] == 0xFF && (p[-5] & 0x38) == 0x10);
        }
    }

    [DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)]
    private static extern bool SymInitializeW(nint process, string? searchPath, bool invadeProcess);

    [DllImport("dbghelp.dll")]
    private static extern bool SymRefreshModuleList(nint process);

    [DllImport("dbghelp.dll")]
    private static extern bool SymFromAddrW(nint process, ulong address, out ulong displacement, byte* symbol);

    [DllImport("dbghelp.dll")]
    private static extern bool SymGetLineFromAddrW64(nint process, ulong address, out uint displacement, byte* line);
}
