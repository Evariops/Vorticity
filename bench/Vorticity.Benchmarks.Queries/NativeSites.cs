using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The native blocks a run holds, by the site that rented them (<c>--native-sites FILE</c>, with any <c>--micro</c>): each
/// block of 64 KiB or more the pools rent out is counted at the frames above the pool and the arena that asked for it,
/// until its last holder gives it back. At each peak of what the counted blocks hold, past the last one kept by 64 MiB,
/// the sites are kept; the file says those of the highest, most bytes first, beside what the process held natively and
/// what the shared pool kept parked then. A stack is taken at each rent counted: the run is slower, its bytes the same.
/// </summary>
internal sealed class NativeSites : AlignedBufferPool.INativeWatch, IDisposable
{
    private const int LeastBytes = 64 * 1024;
    private const long Step = 64L << 20;
    private const int Frames = 6;

    private readonly string _path;
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<NativeSegmentOwner, Site> _held = new Dictionary<NativeSegmentOwner, Site>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Site> _sites = new Dictionary<string, Site>(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _live;
    private long _kept;
    private (string Site, long Bytes, int Blocks)[] _peak = [];
    private double _peakSeconds;
    private long _peakNative;
    private long _peakParked;

    private NativeSites(string path) => _path = path;

    /// <summary>Counts the blocks rented from now on, by site, until the result is disposed, which writes them to <paramref name="path"/>.</summary>
    internal static NativeSites Watch(string path)
    {
        NativeSites sites = new NativeSites(path);
        AlignedBufferPool.Watch = sites;
        return sites;
    }

    public void Rented(NativeSegmentOwner owner)
    {
        if (owner.Capacity < LeastBytes)
        {
            return;
        }

        string name = SiteOf(new StackTrace(2, fNeedFileInfo: false));
        lock (_gate)
        {
            if (!_sites.TryGetValue(name, out Site? site))
            {
                _sites[name] = site = new Site(name);
            }

            site.Bytes += owner.Capacity;
            site.Blocks++;
            _held[owner] = site;
            _live += owner.Capacity;
            if (_live >= _kept + Step)
            {
                _kept = _live;
                _peak = [.. _sites.Values.Where(static site => site.Bytes > 0).Select(static site => (site.Name, site.Bytes, site.Blocks))];
                _peakSeconds = _clock.Elapsed.TotalSeconds;
                _peakNative = NativeSegmentOwner.NativeBytes;
                _peakParked = AlignedBufferPool.Shared.ParkedBytes;
            }
        }
    }

    public void Released(NativeSegmentOwner owner)
    {
        if (owner.Capacity < LeastBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_held.Remove(owner, out Site? site))
            {
                site.Bytes -= owner.Capacity;
                site.Blocks--;
                _live -= owner.Capacity;
            }
        }
    }

    /// <summary>Stops counting and writes the sites of the highest peak kept.</summary>
    public void Dispose()
    {
        AlignedBufferPool.Watch = null;
        StringBuilder text = new StringBuilder();
        long held = _peak.Sum(static site => site.Bytes);
        text.AppendLine(CultureInfo.InvariantCulture, $"the native blocks of {LeastBytes >> 10} KiB or more held at their peak, {_peakSeconds:F0} s in: {held / 1e6:F0} MB, of {_peakNative / 1e6:F0} MB native in all, {_peakParked / 1e6:F0} MB of them parked by the shared pool");
        text.AppendLine("      MB  share  blocks  site (the frames above the pool and the arena, the innermost first)");
        foreach ((string site, long bytes, int blocks) in _peak.OrderByDescending(static site => site.Bytes))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{bytes / 1e6,8:F1} {100.0 * bytes / Math.Max(held, 1),5:F1}% {blocks,7}  {site}");
        }

        System.IO.File.WriteAllText(_path, text.ToString());
        Console.WriteLine($"native sites written to {_path}");
    }

    /// <summary>The frames of <paramref name="stack"/> above the pool, the arenas and the owners, the innermost first.</summary>
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

            // A state machine's step is the method it runs, which its type names.
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
            if (outer == typeof(AlignedBufferPool) || outer == typeof(NativeSegmentOwner) || outer.Name is "CanonicalArena" or "SegmentOwner")
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

    private sealed class Site(string name)
    {
        internal string Name { get; } = name;

        internal long Bytes { get; set; }

        internal int Blocks { get; set; }
    }
}
