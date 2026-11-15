using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests;

/// <summary>
/// Who holds the blocks, test by test and site by site, when the run grows: with <c>VORTICITY_MEMORY_WATCH</c> set to a
/// number of mebibytes, every block of 64 KiB or more the pools rent is counted under the test that rented it and the site
/// above the pool and the arenas that asked for it, until its last holder gives it back; each time the blocks counted pass
/// that many mebibytes more, the tests, the sites and the pairs of both that hold the most are written to a file of
/// <c>VORTICITY_MEMORY_WATCH_DIR</c>, the current directory without it. Off, nothing is counted and nothing costs.
/// </summary>
/// <remarks>
/// A stack is taken at each rent counted, so the run is slower while it watches; the bytes are the same. What the blocks do
/// not hold, the rest of the managed heap, a dump of the heap tells (<c>tests/memory-watch.sh</c>).
/// </remarks>
internal static class MemoryWatch
{
    private const int LeastBytes = 64 * 1024;
    private const int Frames = 6;

    [ModuleInitializer]
    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("VORTICITY_MEMORY_WATCH") is { } step
            && long.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out long mib) && mib > 0)
        {
            string directory = Environment.GetEnvironmentVariable("VORTICITY_MEMORY_WATCH_DIR") ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(directory);
            BlockPool.Watch = new Watcher(mib << 20, directory);
        }
    }

    /// <summary>The blocks alive by test and by site, and a report each time they pass another step.</summary>
    private sealed class Watcher(long every, string directory) : BlockPool.IBlockWatch
    {
        private readonly long _step = every;
        private readonly Lock _gate = new Lock();
        private readonly Dictionary<BlockOwner, (string Test, string Site)> _held = new Dictionary<BlockOwner, (string, string)>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, long> _byTest = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _bySite = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), long> _byPair = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _live;
        private long _next = every;
        private int _reports;

        public void Rented(BlockOwner owner)
        {
            if (owner.Capacity < LeastBytes)
            {
                return;
            }

            string test = TestContext.Current.Test?.TestDisplayName ?? "(outside a test)";
            string site = SiteOf(new StackTrace(2, fNeedFileInfo: false));
            lock (_gate)
            {
                _held[owner] = (test, site);
                Add(test, site, owner.Capacity);
                if (_live >= _next)
                {
                    _next = _live + _step;
                    Write();
                }
            }
        }

        public void Released(BlockOwner owner)
        {
            if (owner.Capacity < LeastBytes)
            {
                return;
            }

            lock (_gate)
            {
                if (_held.Remove(owner, out (string Test, string Site) held))
                {
                    Add(held.Test, held.Site, -owner.Capacity);
                }
            }
        }

        private void Add(string test, string site, long bytes)
        {
            _live += bytes;
            _byTest[test] = _byTest.GetValueOrDefault(test) + bytes;
            _bySite[site] = _bySite.GetValueOrDefault(site) + bytes;
            _byPair[(test, site)] = _byPair.GetValueOrDefault((test, site)) + bytes;
        }

        /// <summary>The blocks held now, the process around them, and the tests, sites and pairs that hold the most.</summary>
        private void Write()
        {
            StringBuilder text = new StringBuilder();
            using Process self = Process.GetCurrentProcess();
            GCMemoryInfo heap = GC.GetGCMemoryInfo();
            text.AppendLine(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds:F0} s: blocks of 64 KiB or more held {Mib(_live)} MiB; every block alive {Mib(BlockOwner.LiveBytes)} MiB, the pool keeping {Mib(MemoryAccount.Pool.ParkedBytes)} MiB of them; heap {Mib(GC.GetTotalMemory(false))} MiB, committed {Mib(heap.TotalCommittedBytes)} MiB; resident {Mib(self.WorkingSet64)} MiB");
            Section(text, "tests", _byTest.Select(static pair => (pair.Key, pair.Value)), 20);
            Section(text, "sites", _bySite.Select(static pair => (pair.Key, pair.Value)), 20);
            Section(text, "tests and sites", _byPair.Select(static pair => ($"{pair.Key.Item1} @ {pair.Key.Item2}", pair.Value)), 30);
            string path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"blocks-{_reports++:D3}.txt"));
            System.IO.File.WriteAllText(path, text.ToString());
        }

        private static void Section(StringBuilder text, string title, IEnumerable<(string Name, long Bytes)> rows, int most)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"the {title} that hold the most:");
            foreach ((string name, long bytes) in rows.Where(static row => row.Bytes > 0).OrderByDescending(static row => row.Bytes).Take(most))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{Mib(bytes),8} MiB  {name}");
            }
        }

        private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);
    }

    /// <summary>The frames of <paramref name="stack"/> above the pool, the arenas, the owners and the accounts, the innermost first.</summary>
    private static string SiteOf(StackTrace stack)
    {
        StringBuilder site = new StringBuilder();
        int taken = 0;
        for (int i = 0; i < stack.FrameCount && taken < Frames; i++)
        {
            MethodBase? method = stack.GetFrame(i)?.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null || Inside(type))
            {
                continue;
            }

            if (taken++ > 0)
            {
                site.Append(" < ");
            }

            site.Append(NameOf(type));
            if (method.Name != "MoveNext")
            {
                site.Append('.').Append(method.Name);
            }
        }

        return site.ToString();
    }

    /// <summary>Whether frames of <paramref name="type"/> are the machinery of a rent rather than the site that asked for it.</summary>
    private static bool Inside(Type type)
    {
        for (Type? outer = type; outer is not null; outer = outer.DeclaringType)
        {
            if (outer == typeof(BlockPool) || outer == typeof(BlockOwner) || outer.Name is "CanonicalArena" or "SegmentOwner"
                || outer.Name.EndsWith("Account", StringComparison.Ordinal) || outer == typeof(MemoryWatch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A type's name, a state machine's by the method it runs, its nesting kept.</summary>
    private static string NameOf(Type type)
    {
        string name = type.Name;
        if (name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is > 1 and int end)
        {
            name = name[1..end];
            return type.DeclaringType is { } outer ? $"{NameOf(outer)}.{name}" : name;
        }

        return type.DeclaringType is { } declaring ? $"{NameOf(declaring)}+{name}" : name;
    }
}
