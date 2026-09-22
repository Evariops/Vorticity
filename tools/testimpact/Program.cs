using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Tools.TestImpact;

/// <summary>
/// Chooses the test classes a change can affect and runs them, from a map of the methods each test
/// class runs.
/// </summary>
internal static partial class Program
{
    /// <summary>The test assemblies, which the xunit runner in each can filter by class.</summary>
    private static readonly (string Name, string Path)[] TestAssemblies =
    [
        ("Vorticity.Tests", "tests/Vorticity.Tests/bin/Release/net11.0/Vorticity.Tests.dll"),
        ("Vorticity.Conformance", "tests/Vorticity.Conformance/bin/Release/net11.0/Vorticity.Conformance.dll"),
    ];

    /// <summary>The assemblies whose sources a change can touch, beside the test assemblies themselves.</summary>
    private static readonly string[] SourceAssemblies =
    [
        "src/Vorticity/bin/Release/net11.0/Vorticity.dll",
        "src/Vorticity.Dataset/bin/Release/net11.0/Vorticity.Dataset.dll",
        "src/Vorticity.RowEncoding/bin/Release/net11.0/Vorticity.RowEncoding.dll",
    ];

    /// <summary>The classes that hold the public surface to its promises, which a declaration can break without a method changing.</summary>
    private const string ContractNamespace = "Vorticity.Tests.Api.";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                """
                testimpact - run the test classes a change can affect.

                  testimpact map [--jobs N] [--no-build]
                      runs every test class in a process of its own with inlining off and records
                      the methods of this repository the JIT compiled for it: tests/.impact/map.txt
                  testimpact select [--base REF]
                      the classes that ran a method the working tree changed against REF (HEAD)
                  testimpact run [--base REF] [--no-build]
                      builds, then runs those classes

                  GIT names the git executable when the one on the path is not usable.
                """);
            return args.Length == 0 ? 2 : 0;
        }

        string root = FindRoot();
        string baseline = Option(args, "--base") ?? "HEAD";
        int jobs = int.TryParse(Option(args, "--jobs"), out int parsed) && parsed > 0 ? parsed : Math.Max(1, Environment.ProcessorCount / 2);
        bool build = !args.Contains("--no-build");
        string git = Environment.GetEnvironmentVariable("GIT") ?? "git";
        string mapPath = Path.Combine(root, "tests", ".impact", "map.txt");

        switch (args[0])
        {
            case "map":
                if (build && !await BuildAsync(root).ConfigureAwait(false))
                {
                    return 1;
                }

                return await MapAsync(root, git, mapPath, jobs).ConfigureAwait(false);

            case "select":
                await SelectAsync(root, git, mapPath, baseline).ConfigureAwait(false);
                return 0;

            case "run":
                if (build && !await BuildAsync(root).ConfigureAwait(false))
                {
                    return 1;
                }

                Selection selection = await SelectAsync(root, git, mapPath, baseline).ConfigureAwait(false);
                return await RunAsync(root, selection).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"unknown command '{args[0]}'");
                return 2;
        }
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vorticity.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("run from inside the repository");
    }

    private static async Task<bool> BuildAsync(string root)
    {
        Console.WriteLine("building");
        (int exit, string output) = await Processes.RunAsync(
            "dotnet", ["build", "Vorticity.slnx", "-c", "Release", "-v", "q", "--nologo"], root, environment: null, echo: false).ConfigureAwait(false);
        if (exit != 0)
        {
            Console.WriteLine(output);
        }

        return exit == 0;
    }

    private static async Task<List<string>> ClassesAsync(string root, string assembly)
    {
        string listing = await Processes.CaptureAsync("dotnet", [assembly, "-list", "classes", "-noLogo"], root).ConfigureAwait(false);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static async Task<int> MapAsync(string root, string git, string mapPath, int jobs)
    {
        List<(TestClass Class, string Assembly)> work = [];
        foreach ((string name, string path) in TestAssemblies)
        {
            string assembly = Path.Combine(root, path);
            foreach (string type in await ClassesAsync(root, assembly).ConfigureAwait(false))
            {
                work.Add((new TestClass(name, type), assembly));
            }
        }

        string scratch = Path.Combine(Path.GetTempPath(), "testimpact-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(scratch);
        Console.WriteLine($"mapping {work.Count} classes, {jobs} at a time");
        Stopwatch clock = Stopwatch.StartNew();
        HashSet<string>[] ran = new HashSet<string>[work.Count];
        List<string> failed = [];
        using SemaphoreSlim slots = new SemaphoreSlim(jobs);
        await Task.WhenAll(work.Select(async (item, index) =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                string summary = Path.Combine(scratch, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jit");
                Dictionary<string, string> environment = new()
                {
                    ["DOTNET_JitNoInline"] = "1",
                    ["DOTNET_JitDisasmSummary"] = "1",
                    ["DOTNET_JitStdOutFile"] = summary,
                };
                (int exit, _) = await Processes.RunAsync(
                    "dotnet", [item.Assembly, "-class", item.Class.Name, "-noLogo"], root, environment, echo: false).ConfigureAwait(false);
                HashSet<string> methods = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(summary))
                {
                    JitSummary.Read(summary, methods);
                    File.Delete(summary);
                }

                ran[index] = methods;
                if (exit != 0)
                {
                    lock (failed)
                    {
                        failed.Add(item.Class.Name);
                    }
                }
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);

        Dictionary<string, List<int>> byMethod = new(StringComparer.Ordinal);
        for (int i = 0; i < work.Count; i++)
        {
            foreach (string key in ran[i])
            {
                if (!byMethod.TryGetValue(key, out List<int>? runners))
                {
                    runners = [];
                    byMethod[key] = runners;
                }

                runners.Add(i);
            }
        }

        string commit = (await Processes.CaptureAsync(git, ["-C", root, "rev-parse", "--short", "HEAD"], root).ConfigureAwait(false)).Trim();
        new ImpactMap(commit, [.. work.Select(item => item.Class)], byMethod).Save(mapPath);
        Directory.Delete(scratch, recursive: true);
        Console.WriteLine($"{byMethod.Count} methods over {work.Count} classes in {clock.Elapsed.TotalSeconds:F0}s, at {commit}: {Path.GetRelativePath(root, mapPath)}");
        foreach (string name in failed)
        {
            Console.WriteLine($"  failing while mapped: {name}");
        }

        return 0;
    }

    /// <summary>What to run: every test, or the classes per assembly.</summary>
    private sealed class Selection
    {
        internal string? Everything { get; set; }

        internal Dictionary<string, SortedSet<string>> Classes { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> WholeAssemblies { get; } = new(StringComparer.Ordinal);

        internal void Add(TestClass type)
        {
            if (!Classes.TryGetValue(type.Assembly, out SortedSet<string>? set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                Classes[type.Assembly] = set;
            }

            set.Add(type.Name);
        }
    }

    private static async Task<Selection> SelectAsync(string root, string git, string mapPath, string baseline)
    {
        Selection selection = new Selection();
        ImpactMap? map = ImpactMap.Load(mapPath);
        if (map is null)
        {
            selection.Everything = "no map of this format yet: run `testimpact map`";
            Console.WriteLine(selection.Everything);
            return selection;
        }

        SourceMap sources = new SourceMap();
        foreach (string path in SourceAssemblies.Concat(TestAssemblies.Select(item => item.Path)))
        {
            sources.Add(Path.Combine(root, path), root);
        }

        List<Hunk> hunks = await Diff.ReadAsync(git, root, baseline).ConfigureAwait(false);
        Dictionary<string, bool> changed = new Dictionary<string, bool>(StringComparer.Ordinal);
        SortedSet<string> ignored = new SortedSet<string>(StringComparer.Ordinal);
        bool contracts = false;
        foreach (Hunk hunk in hunks)
        {
            string path = hunk.Path;
            if (EveryTestReads(path))
            {
                selection.Everything ??= path;
                continue;
            }

            if (path.StartsWith("tests/MustNotCompile/", StringComparison.Ordinal))
            {
                selection.Add(new TestClass("Vorticity.Tests", ContractNamespace + "MustNotCompileTests"));
                continue;
            }

            if (!path.EndsWith(".cs", StringComparison.Ordinal))
            {
                // A test project's data: the assembly that reads it.
                (string Name, string Path) owner = TestAssemblies.FirstOrDefault(
                    item => path.StartsWith(item.Path[..(item.Path.IndexOf("/bin/", StringComparison.Ordinal) + 1)], StringComparison.Ordinal));
                if (owner.Name is not null)
                {
                    selection.WholeAssemblies.Add(owner.Name);
                }
                else
                {
                    ignored.Add(path);
                }

                continue;
            }

            if (!path.StartsWith("src/", StringComparison.Ordinal) && !path.StartsWith("tests/", StringComparison.Ordinal))
            {
                ignored.Add(path);
                continue;
            }

            if (!hunk.Whole && hunk.Text.All(Inert))
            {
                continue;
            }

            IReadOnlyList<MethodSpan> methods = sources.Methods(path);
            int first = hunk.First;
            int last = hunk.Whole ? int.MaxValue : hunk.First + Math.Max(hunk.Count, 1);
            List<MethodSpan> hit = [.. methods.Where(span => span.First <= last && span.Last >= first)];
            if (hit.Count == 0)
            {
                // A declaration: a constant is copied into every reader, so the whole suite, unless
                // it is private, when its readers are its own type's methods wherever the type's
                // parts are; the signature or attributes of the method below it; else every method
                // of the file.
                List<string> constants = [.. hunk.Text.Where(line => !Inert(line) && line.Contains(" const ", StringComparison.Ordinal))];
                if (constants.Count > 0 && !constants.All(line => line.TrimStart().StartsWith("private const ", StringComparison.Ordinal)))
                {
                    selection.Everything ??= path + " (a constant)";
                    continue;
                }

                MethodSpan? next = methods.Where(span => span.First > last && span.First - last <= 8).OrderBy(span => span.First).Cast<MethodSpan?>().FirstOrDefault();
                hit = constants.Count > 0
                    ? [.. sources.MethodsOf(methods.Select(span => SourceMap.Outermost(span.Key)).ToHashSet(StringComparer.Ordinal))]
                    : next is MethodSpan below ? [below] : [.. methods];
                contracts |= path.StartsWith("src/", StringComparison.Ordinal) && hunk.Text.Any(
                    line => !Inert(line) && (line.Contains("public ", StringComparison.Ordinal) || line.Contains("protected ", StringComparison.Ordinal)));
            }

            foreach (MethodSpan span in hit)
            {
                changed[span.Key] = span.Virtual;
            }
        }

        // A method the map does not know is new. Only an override or an interface method can be
        // called by code that did not change, so only such a method stands for its type; any other
        // is reached through callers that changed with it, which the map does know.
        List<string> unknown = [];
        foreach ((string key, bool isVirtual) in changed)
        {
            IEnumerable<int> runners = map.Knows(key) ? map.Running(key)
                : isVirtual ? map.RunningType(ImpactMap.TypeOf(key))
                : [];
            bool any = false;
            foreach (int index in runners)
            {
                selection.Add(map.Classes[index]);
                any = true;
            }

            if (!any && (map.Knows(key) || isVirtual))
            {
                unknown.Add(key);
            }
        }

        if (contracts)
        {
            foreach (TestClass type in map.Classes.Where(item => item.Name.StartsWith(ContractNamespace, StringComparison.Ordinal)))
            {
                selection.Add(type);
            }
        }

        // A class the map has never seen is new, or renamed: it runs.
        HashSet<TestClass> known = [.. map.Classes];
        foreach ((string name, string path) in TestAssemblies)
        {
            foreach (string type in await ClassesAsync(root, Path.Combine(root, path)).ConfigureAwait(false))
            {
                if (!known.Contains(new TestClass(name, type)))
                {
                    selection.Add(new TestClass(name, type));
                }
            }
        }

        Report(map, selection, changed, unknown, ignored);
        return selection;
    }

    /// <summary>A line whose change cannot change behaviour: blank, a comment, a using directive.</summary>
    private static bool Inert(string line)
    {
        string text = line.Trim();
        return text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal) ||
            text.StartsWith('*') || UsingDirective().IsMatch(text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^using (static )?[A-Za-z_][A-Za-z0-9_.]*( = [A-Za-z_][A-Za-z0-9_.<>, ]*)?;$")]
    private static partial System.Text.RegularExpressions.Regex UsingDirective();

    /// <summary>Files the build of the library and of its tests reads, so every test.</summary>
    private static bool EveryTestReads(string path) =>
        (path.StartsWith("src/", StringComparison.Ordinal) || path.StartsWith("tests/", StringComparison.Ordinal) || !path.Contains('/')) &&
        (path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".props", StringComparison.Ordinal) ||
            path.EndsWith(".targets", StringComparison.Ordinal) || path is ".editorconfig" or "global.json" or "BannedSymbols.txt") ||
        path.StartsWith("src/Vorticity.Generators/", StringComparison.Ordinal);

    private static void Report(ImpactMap map, Selection selection, Dictionary<string, bool> changed, List<string> unknown, SortedSet<string> ignored)
    {
        Console.WriteLine($"map at {map.Commit}; {changed.Count} changed methods");
        foreach (string key in unknown)
        {
            Console.WriteLine($"  no class ran {key} nor anything of its type");
        }

        foreach (string path in ignored)
        {
            Console.WriteLine($"  no test reads {path}");
        }

        if (selection.Everything is not null)
        {
            Console.WriteLine($"every test: {selection.Everything}");
            return;
        }

        foreach (string assembly in selection.WholeAssemblies)
        {
            Console.WriteLine($"all of {assembly}");
        }

        int total = map.Classes.Count;
        int chosen = selection.Classes.Values.Sum(set => set.Count);
        Console.WriteLine($"{chosen} of {total} classes");
        foreach ((string assembly, SortedSet<string> types) in selection.Classes)
        {
            foreach (string type in types)
            {
                Console.WriteLine($"  {assembly}  {type}");
            }
        }
    }

    private static async Task<int> RunAsync(string root, Selection selection)
    {
        int status = 0;
        foreach ((string name, string path) in TestAssemblies)
        {
            List<string> arguments = [Path.Combine(root, path), "-noLogo"];
            if (selection.Everything is null && !selection.WholeAssemblies.Contains(name))
            {
                if (!selection.Classes.TryGetValue(name, out SortedSet<string>? types) || types.Count == 0)
                {
                    continue;
                }

                foreach (string type in types)
                {
                    arguments.Add("-class");
                    arguments.Add(type);
                }
            }

            Console.WriteLine($"running {name}");
            (int exit, _) = await Processes.RunAsync("dotnet", arguments, root, environment: null, echo: true).ConfigureAwait(false);
            status |= exit;
        }

        return status == 0 ? 0 : 1;
    }
}
