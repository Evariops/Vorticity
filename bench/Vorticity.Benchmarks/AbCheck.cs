// Two builds of the library, in one process, against one clock.
//
// BENCH-AUDIT.md C1: the repository's own rule -- "a perf change is a bench with its arms in the
// same run" (PERF-AUDIT-v2.md §1.1) -- had exactly one realisation, which was copying the old loop
// into the benchmark by hand. That works for a kernel and not for anything that spans a layout
// reader, the arena or the scan, so those changes were judged across two separate runs: the method
// §1.1 forbids, and the one that has already produced an unjustified revert in this repository.
//
// HOW IT WORKS. `Vorticity.Benchmarks.Scenarios.dll` is built twice -- here, and inside a
// worktree of the older commit (`bench/ab.sh`) -- and each copy is bound to the `Vorticity.dll`
// sitting beside it. The old one is loaded into its own `AssemblyLoadContext` with an
// `AssemblyDependencyResolver` rooted at its own directory, so it resolves ITS library and not
// ours. Two static registries, two JITs, one process.
//
// WHY THE DELEGATE IS `Func<string, Task<long>>` AND NOTHING RICHER. A type defined in the library
// loads twice under two contexts and is then two distinct types with one name; passing one across
// the boundary throws a cast exception that reads as a mystery. `string`, `Task<long>` and `Func<,>`
// come from the shared runtime, so both sides see the same type. The whole contract an old build
// must satisfy is one static `Resolve(string)` returning that delegate.
//
// THE ROUNDS ARE INTERLEAVED AND THE RATIO IS PER ROUND, exactly as `RatioCheck` does it against
// Rust, with the same `Statistics.Bootstrap`: this is `--ratio-check` with our own past as the
// reference. Alternating matters for the same reason -- a fixed order gives one side the cache-cold
// cost every round, which is a stable bias and therefore worse than noise.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks;

/// <summary>Runs a scenario against an older build of the library, interleaved.</summary>
internal static class AbCheck
{
    /// <summary>The assembly built on both sides.</summary>
    private const string ScenarioAssembly = "Vorticity.Benchmarks.Scenarios.dll";

    /// <summary>The type and method an old build has to expose.</summary>
    private const string EntryType = "Vorticity.Bench.Scenarios.ScenarioSet";

    /// <summary>Rounds before the interval is consulted.</summary>
    private const int MinRounds = 21;

    /// <summary>The ceiling on rounds.</summary>
    private const int MaxRounds = 501;

    /// <summary>Half-width, relative to the median, that ends the measurement.</summary>
    private const double Precision = 0.05;

    /// <summary>Seconds per scenario once the minimum is met.</summary>
    private const double Budget = 10.0;

    /// <summary>Microseconds a round must clear before it stops being timer noise.</summary>
    private const double MinRoundMicroseconds = 1_000;

    /// <summary>Warm-up before the first timed round.</summary>
    private static readonly TimeSpan WarmupBudget = TimeSpan.FromSeconds(1);

    /// <summary>Compares the build in <paramref name="beforeDirectory"/> with this one.</summary>
    /// <param name="beforeDirectory">A directory holding the older build's output.</param>
    /// <param name="afterDirectory">
    /// A directory holding the other side's output, or <see langword="null"/> for THIS build.
    /// </param>
    /// <param name="file">The file to run the scenarios on.</param>
    /// <param name="names">Scenario names; empty runs every one both sides know.</param>
    /// <returns>0 when the comparison ran, 2 when it could not be set up.</returns>
    /// <remarks>
    /// BOTH SIDES CAN BE COMMITS, and that is not a luxury: comparing a commit against HEAD answers
    /// "how much faster are we now", which is every commit since, not the one being judged. The
    /// question a perf change has to answer is itself against its own parent.
    /// </remarks>
    internal static async Task<int> RunAsync(
        string beforeDirectory, string? afterDirectory, string file, string[] names)
    {
        // THIS AXIS REFUSES TO REPORT A RATIO IT CANNOT MEAN (BENCH-AUDIT.md B24), and the check
        // comes before the arguments because it is a precondition of the tool, not of a call. Two
        // sides of the SAME commit -- library identical to the byte -- read 3.177 one way round and
        // 0.294 the other, purely on which side the JIT had promoted: one at ~740 us, the other at
        // ~2450, the latter WORSE than the 980 us scalar fallback, which is the signature of
        // vectorised code left at tier-0. Pinned, the same control reads 1.009. There is no API to
        // ask the runtime whether tiering is on, so the variable that turns it off is what gets
        // checked; bench/ab.sh sets it. Refusing rather than warning is the point: a warning above
        // a table of plausible numbers is exactly how this tool's own validation got believed.
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
        {
            Console.Error.WriteLine(
                "--ab needs DOTNET_TieredCompilation=0: without it the ratio measures which side " +
                "the JIT promoted, not the code. A null control -- the same commit on both sides " +
                "-- reads 3.18 or 0.29 instead of 1.00.\n" +
                "Run it through bench/ab.sh, which sets it, or set it yourself. See " +
                "BENCH-AUDIT.md B24.");
            return 2;
        }

        string probe = Path.Combine(beforeDirectory, ScenarioAssembly);
        if (!System.IO.File.Exists(probe))
        {
            Console.Error.WriteLine(
                $"No {ScenarioAssembly} in {beforeDirectory}.\n" +
                "Build the other side first -- bench/ab.sh <commit> does it in a worktree -- or " +
                "point --ab at a directory that already holds one.");
            return 2;
        }

        if (!System.IO.File.Exists(file))
        {
            Console.Error.WriteLine($"No such file: {file}");
            return 2;
        }

        MethodInfo? resolve = Entry(beforeDirectory, "before", out Side beforeSide);
        if (resolve is null)
        {
            return 2;
        }

        MethodInfo? afterResolve = null;
        if (afterDirectory is not null)
        {
            afterResolve = Entry(afterDirectory, "after", out _);
            if (afterResolve is null)
            {
                return 2;
            }
        }

        if (!await BoundItsOwn(beforeSide, file).ConfigureAwait(false))
        {
            return 2;
        }

        string[] wanted = names.Length > 0
            ? names
            : Scenarios.All.Select(scenario => scenario.Name).ToArray();

        Console.Out.WriteLine(
            $"A/B: before = {beforeDirectory}\n" +
            $"     after  = {afterDirectory ?? "this build"}, {RuntimeInformation.FrameworkDescription}\n" +
            $"     file   = {file}");
        Console.Out.WriteLine(
            "  scenario        before     after   after/before  [   95% interval]   n   k     mde");

        foreach (string name in wanted)
        {
            if (resolve.Invoke(null, [name]) is not Func<string, Task<long>> theirs)
            {
                Console.Out.WriteLine($"  {name,-14} unknown in the older build");
                continue;
            }

            // A scenario without a reference counterpart (`filtered`, the indexed writes) is not in
            // `Scenarios.All`, and this build answers for it the way the older one does.
            Func<string, Task<long>>? ours = afterResolve is null
                ? Scenarios.ByName(name)?.Ours ?? Vorticity.Bench.Scenarios.ScenarioSet.Resolve(name)
                : afterResolve.Invoke(null, [name]) as Func<string, Task<long>>;
            if (ours is null)
            {
                Console.Out.WriteLine($"  {name,-14} unknown on the after side");
                continue;
            }

            // A SCENARIO THAT DOES NOT FIT THE FILE SAYS SO, and the others still run: a keyed lookup
            // on a file with no columns is a question without an answer, not a broken harness.
            Measurement m;
            try
            {
                m = await MeasureAsync(theirs, ours, file).ConfigureAwait(false);
            }
            catch (NotSupportedException unfit)
            {
                Console.Out.WriteLine($"  {name,-14} not run: {unfit.Message}");
                continue;
            }

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {name,-14} {m.Before,8:F0}us {m.After,8:F0}us {m.Ratio.Median,13:F3} " +
                $"{m.Ratio} {m.Ratio.Samples,3} {m.Repeats,3} {m.Ratio.MinimumDetectableEffect,6:P1}" +
                $"{Verdict(m.Ratio)}"));
        }

        return 0;
    }

    /// <summary>Loads one side and returns its entry point, or null after saying why.</summary>
    /// <param name="directory">The build's output directory.</param>
    /// <param name="label">`before` or `after`, for the context's name and the message.</param>
    /// <param name="side">The loaded side.</param>
    private static MethodInfo? Entry(string directory, string label, out Side side)
    {
        side = default;
        string probe = Path.Combine(directory, ScenarioAssembly);
        if (!System.IO.File.Exists(probe))
        {
            Console.Error.WriteLine(
                $"No {ScenarioAssembly} in {directory} for the {label} side.\n" +
                "bench/ab.sh builds a side in a worktree; point --ab at a directory that holds one.");
            return null;
        }

        BeforeContext context = new BeforeContext(probe, label);
        Assembly assembly = context.LoadFromAssemblyPath(probe);
        MethodInfo? resolve = assembly.GetType(EntryType)?.GetMethod(
            "Resolve", BindingFlags.Public | BindingFlags.Static);
        if (resolve is null)
        {
            Console.Error.WriteLine(
                $"{probe} has no public static {EntryType}.Resolve(string).\n" +
                "That is the whole contract a build has to satisfy; a commit from before the " +
                "scenario assembly existed cannot be compared without copying today's scenario " +
                "project into its worktree, which is what bench/ab.sh does.");
            return null;
        }

        side = new Side(context, resolve, Path.GetFullPath(directory), label);
        return resolve;
    }

    /// <summary>One loaded build: its context, its entry point, and where it came from.</summary>
    private readonly record struct Side(
        AssemblyLoadContext Context, MethodInfo Resolve, string Root, string Label);

    /// <summary>
    /// Forces the library to load and checks it came from the side's own directory.
    /// </summary>
    /// <param name="side">The side to check.</param>
    /// <param name="file">A file to run one scenario on, which is what triggers the load.</param>
    /// <remarks>
    /// THE FAILURE THIS CATCHES IS INVISIBLE: if the resolver misses, the old scenarios bind OUR
    /// library, every ratio reads 1.00, and the change looks like a nul result rather than a broken
    /// harness.
    /// </remarks>
    private static async Task<bool> BoundItsOwn(Side side, string file)
    {
        if (side.Resolve.Invoke(null, ["fullscan"]) is Func<string, Task<long>> scenario)
        {
            await scenario(file).ConfigureAwait(false);
        }

        Assembly? library = side.Context.Assemblies.FirstOrDefault(
            a => string.Equals(a.GetName().Name, "Vorticity", StringComparison.Ordinal));
        if (library is not null &&
            Path.GetFullPath(library.Location).StartsWith(side.Root, StringComparison.Ordinal))
        {
            return true;
        }

        Console.Error.WriteLine(
            $"The {side.Label} scenarios did not load a Vorticity.dll from {side.Root} " +
            $"(got: {library?.Location ?? "none"}).\n" +
            "Without that, both sides run the same build and every ratio is 1.00 for no reason. " +
            "That side needs its own Vorticity.dll and a " +
            "Vorticity.Benchmarks.Scenarios.deps.json beside it.");
        return false;
    }

    /// <summary>What the interval says, in the words the repository decides with.</summary>
    /// <param name="ratio">After over before.</param>
    /// <remarks>
    /// The same rule as the `MannWhitney(5%)` column and as `--compare`: a difference is called
    /// only when it is both real and larger than 5%. Here "real" is the interval excluding 1
    /// rather than a p-value, because the rounds are PAIRED and a paired interval is the sharper
    /// statement.
    /// </remarks>
    private static string Verdict(Interval ratio) =>
        ratio.High < 0.95 ? "   faster"
        : ratio.Low > 1.05 ? "   slower"
        : ratio.Low > 1 || ratio.High < 1 ? "   same, under 5%"
        : "   same";

    /// <summary>Times both sides on one file, interleaved, until the interval is tight.</summary>
    private static async Task<Measurement> MeasureAsync(
        Func<string, Task<long>> before, Func<string, Task<long>> after, string file)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(WarmupBudget.TotalSeconds * Stopwatch.Frequency);
        double lastBefore = 0;
        double lastAfter = 0;
        do
        {
            lastBefore = await TimeAsync(before, file, 1).ConfigureAwait(false);
            lastAfter = await TimeAsync(after, file, 1).ConfigureAwait(false);
        }
        while (Stopwatch.GetTimestamp() < deadline);

        int repeats = Math.Max(
            1, (int)Math.Ceiling(MinRoundMicroseconds / Math.Max(lastBefore, lastAfter)));

        List<double> ratios = [];
        List<double> befores = [];
        List<double> afters = [];
        long stop = Stopwatch.GetTimestamp() + (long)(Budget * Stopwatch.Frequency);
        Interval interval = default;
        for (int round = 0; round < MaxRounds; round++)
        {
            double b;
            double a;
            if (round % 2 == 0)
            {
                b = await TimeAsync(before, file, repeats).ConfigureAwait(false);
                a = await TimeAsync(after, file, repeats).ConfigureAwait(false);
            }
            else
            {
                a = await TimeAsync(after, file, repeats).ConfigureAwait(false);
                b = await TimeAsync(before, file, repeats).ConfigureAwait(false);
            }

            befores.Add(b);
            afters.Add(a);
            ratios.Add(b == 0 ? 0 : a / b);

            if (round + 1 >= MinRounds)
            {
                interval = Statistics.Bootstrap(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ratios));
                if (interval.MinimumDetectableEffect <= Precision ||
                    Stopwatch.GetTimestamp() >= stop)
                {
                    break;
                }
            }
        }

        return new Measurement(Median(befores), Median(afters), interval, repeats);
    }

    /// <summary>One scenario, both sides, and what the ratio's interval says.</summary>
    private readonly record struct Measurement(
        double Before, double After, Interval Ratio, int Repeats);

    /// <summary>Microseconds per call, timing <paramref name="repeats"/> of them as one round.</summary>
    private static async Task<double> TimeAsync(
        Func<string, Task<long>> scenario, string file, int repeats)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < repeats; i++)
        {
            await scenario(file).ConfigureAwait(false);
        }

        return Stopwatch.GetElapsedTime(start).TotalMicroseconds / repeats;
    }

    private static double Median(List<double> values)
    {
        double[] copy = [.. values];
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    /// <summary>The older build's own world: its scenarios, its library, its statics.</summary>
    /// <remarks>
    /// `AssemblyDependencyResolver` reads the deps file beside the scenario assembly, so the old
    /// `Vorticity.dll` is found there rather than falling back to the default context -- which
    /// would silently load OUR library under the old scenarios and compare a build against itself.
    /// That failure is invisible in the output, which is why the resolver is not optional.
    /// </remarks>
    private sealed class BeforeContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        internal BeforeContext(string entry, string label)
            : base(label, isCollectible: false) => _resolver = new AssemblyDependencyResolver(entry);

        protected override Assembly? Load(AssemblyName name)
        {
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
