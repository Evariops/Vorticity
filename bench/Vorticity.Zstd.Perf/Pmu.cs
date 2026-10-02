using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Zstd.Bench;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// The hardware counters of the calling thread, through tools/pmu (Apple's private kperf
/// frameworks). Configuring them takes root: <c>sudo Vorticity.Zstd.Perf --pmu ...</c>. Native code belongs
/// here, in the measurement, and never in the library.
/// </summary>
internal sealed unsafe class Pmu
{
    /// <summary>Counted when <c>--pmu</c> names none: cycles and instructions, then where they go.</summary>
    public static readonly string[] DefaultEvents =
    [
        "FIXED_CYCLES", "FIXED_INSTRUCTIONS", "BRANCH_COND_MISPRED_NONSPEC", "MAP_STALL",
        "MAP_DISPATCH_BUBBLE", "FLUSH_RESTART_OTHER_NONSPEC", "L1D_CACHE_MISS_LD", "SCHEDULE_EMPTY",
    ];

    private readonly delegate* unmanaged<ulong*, int> _read;

    private Pmu(string[] events, delegate* unmanaged<ulong*, int> read)
    {
        Events = events;
        _read = read;
    }

    public string[] Events { get; }

    public static string LibraryPath => Path.Combine(BenchFrames.RepositoryRoot, "tools", "pmu", "out", "libpmu.dylib");

    /// <summary>The counters of <paramref name="events"/> (kpep names), or null with the reason printed.</summary>
    public static Pmu? TryCreate(string[] events)
    {
        if (!File.Exists(LibraryPath) || !NativeLibrary.TryLoad(LibraryPath, out nint library))
        {
            Console.WriteLine($"no {LibraryPath}: run tools/pmu/build.sh");
            return null;
        }

        var init = (delegate* unmanaged<byte**, int, int>)NativeLibrary.GetExport(library, "pmu_init");
        var read = (delegate* unmanaged<ulong*, int>)NativeLibrary.GetExport(library, "pmu_read");
        var names = new nint[events.Length];
        try
        {
            for (int i = 0; i < events.Length; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUTF8(events[i]);
            }

            fixed (nint* pointers = names)
            {
                int result = init((byte**)pointers, events.Length);
                if (result != 0)
                {
                    Console.WriteLine($"pmu_init failed ({result})");
                    return null;
                }
            }
        }
        finally
        {
            foreach (nint name in names)
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        return new Pmu(events, read);
    }

    public void Read(Span<ulong> values)
    {
        fixed (ulong* p = values)
        {
            _read(p);
        }
    }

    /// <summary>
    /// Decodes <paramref name="frame"/> <paramref name="reps"/> times between two reads of the counters,
    /// and prints each event per decode, per output byte and per <paramref name="unit"/>.
    /// </summary>
    public void Measure(string name, Func<byte[], byte[], int> decode, byte[] frame, byte[] output, int reps, double units, string unit)
    {
        for (int i = 0; i < 50; i++)
        {
            decode(frame, output);
        }

        Span<ulong> before = stackalloc ulong[Events.Length];
        Span<ulong> after = stackalloc ulong[Events.Length];
        Read(before);
        for (int i = 0; i < reps; i++)
        {
            decode(frame, output);
        }

        Read(after);
        var text = new StringBuilder();
        text.AppendLine($"{name}: {reps} decodes, {output.Length} bytes each" + (units > 0 ? $", {units:F0} {unit} each" : string.Empty));
        double cycles = 0;
        for (int i = 0; i < Events.Length; i++)
        {
            double perDecode = (double)(after[i] - before[i]) / reps;
            if (i == 0)
            {
                cycles = perDecode;
            }

            string perUnit = units > 0 ? $"{perDecode / units,10:F3} per {unit}" : string.Empty;
            string share = i > 0 && cycles > 0 && Events[i].Contains("CYCLE", StringComparison.Ordinal) is false
                && (Events[i].StartsWith("MAP_", StringComparison.Ordinal) || Events[i].StartsWith("SCHEDULE", StringComparison.Ordinal))
                ? $"  ({perDecode / cycles:P1} of cycles)" : string.Empty;
            text.AppendLine($"  {Events[i],-34} {perDecode,14:F0} per decode {perUnit}{share}");
        }

        Console.Write(text.ToString());
    }
}
