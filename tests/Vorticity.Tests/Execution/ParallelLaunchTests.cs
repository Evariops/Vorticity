// The rule "only the core starts work beside its caller", made executable.
//
// Every place the library starts work beside its caller is a place the session's degree may not see: a `Task.Run`, a
// `Parallel.For*`, a work item queued on the pool, a thread, or a lane taken of the session's budget by hand. They all go
// through `src/Vorticity/Execution`, which tells what computes from what waits: the core's pumps and pieces
// (`FlowCore`, `CoreFor`), the workers that hold a lane of the session's each (`LaneWorkers`), and what waits on the store
// or the disk beside the degree (`Waits`). This counts the others, in every file of `src` outside it, which the plan of the
// parallel flows brought to zero (PLAN-FLUX-PARALLELES, steps B1 to D1).
//
// It counts calls on lines of code, never in comments, the way `PerRowDispatchTests` does: a call added outside the core
// turns its file red on the commit that adds it, which then goes through one of the core's entries instead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorticity.Tests.Execution;

/// <summary>Holds the library's starts of parallel work outside its core at zero.</summary>
public sealed partial class ParallelLaunchTests
{
    /// <summary>A start of work beside the caller, not a mention of one.</summary>
    [GeneratedRegex(@"Task\.Run\(|Parallel\.(For|ForAsync|ForEach|ForEachAsync)\(|QueueUserWorkItem\(|new Thread\(|\.TryEnter\(out ")]
    private static partial Regex Launch();

    /// <summary>No file of <c>src</c> outside the core starts parallel work: each goes through one of the core's entries.</summary>
    [Fact]
    public void NoFileOutsideTheCoreStartsParallelWork()
    {
        StringBuilder over = new StringBuilder();
        foreach ((string file, int count) in Count().OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            over.Append(file).Append(": ").Append(count).Append(" starts\n");
        }

        Assert.True(over.Length == 0, "Parallel work started outside the core (src/Vorticity/Execution):\n" + over);
    }

    private static Dictionary<string, int> Count()
    {
        string sources = Sources();
        Dictionary<string, int> counted = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sources, path).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.StartsWith("Vorticity/Execution/", StringComparison.Ordinal))
            {
                continue;
            }

            int starts = 0;
            foreach (string line in System.IO.File.ReadLines(path))
            {
                string text = line.TrimStart();
                if (text.StartsWith("//", StringComparison.Ordinal) ||
                    text.StartsWith('*') ||
                    text.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                starts += Launch().Matches(line).Count;
            }

            if (starts > 0)
            {
                counted[relative] = starts;
            }
        }

        return counted;
    }

    // `src`, found by walking up from this source file rather than from the output directory.
    private static string Sources([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Vorticity", "Execution")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src above '" + thisFile + "'.");
    }
}
