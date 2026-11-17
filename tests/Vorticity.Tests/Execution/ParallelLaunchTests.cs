// The rule "only a pump of the core computes in parallel", made executable as a ratchet.
//
// Every place the library starts work beside its caller is a place the session's degree may not see: a `Task.Run`, a
// `Parallel.For*`, a work item queued on the pool, a thread, or a lane taken of the session's budget by hand. The
// core (`src/Vorticity/Execution`) is where they belong: its pumps hold a lane each and run units. This counts the
// others, per file of `src`, and holds each file at a ceiling that only comes down as the plan of the parallel flows
// moves its work onto the core (PLAN-FLUX-PARALLELES, steps B1 to D2).
//
// It counts calls on lines of code, never in comments, the way `PerRowDispatchTests` does: a call added beside the
// core turns its file red on the commit that adds it, which then either moves it onto the core or raises the ceiling
// with a sentence in the commit saying why it cannot go there yet.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorticity.Tests.Execution;

/// <summary>Holds the library's starts of parallel work outside its core at a descending count.</summary>
public sealed partial class ParallelLaunchTests
{
    /// <summary>A start of work beside the caller, not a mention of one.</summary>
    [GeneratedRegex(@"Task\.Run\(|Parallel\.(For|ForAsync|ForEach|ForEachAsync)\(|QueueUserWorkItem\(|new Thread\(|\.TryEnter\(out ")]
    private static partial Regex Launch();

    /// <summary>
    /// Per file of <c>src</c>, the starts it may contain. LOWER IT BY HAND when a start moves onto the core, in the same
    /// commit; a file that reaches zero leaves the table.
    /// </summary>
    /// <remarks>
    /// On a compaction job's path: the plan's step that removes each. <c>DatasetCompactor.Ranges.cs</c>, the ranges' lanes
    /// (B3). <c>DatasetCompactor.cs</c>, the inputs opened side by side and the census's lanes (B2, C5).
    /// <c>VortexFileWriter.cs</c>, the assembly and the flight of a staged block (B2, B4a). <c>KeyIndexBuilder.cs</c>,
    /// the sort of a spilled bucket ahead (B5). <c>BatchAsyncEnumerable.cs</c>, a split's decode on a lane it takes (C1).
    /// The questions' starts go with D1, a part's with D2.
    /// </remarks>
    private static readonly Dictionary<string, int> Ceilings = new(StringComparer.Ordinal)
    {
        ["Vorticity.Dataset/DatasetCompactor.Ranges.cs"] = 2,
        ["Vorticity.Dataset/DatasetCompactor.cs"] = 3,
        ["Vorticity.Dataset/DatasetKeyCursor.cs"] = 1,
        ["Vorticity.Dataset/DatasetRowChanges.cs"] = 1,
        ["Vorticity.Dataset/DatasetScanBuilder.Answers.cs"] = 1,
        ["Vorticity.Dataset/DatasetScanBuilder.Paths.cs"] = 1,
        ["Vorticity.Dataset/DatasetScanSource.Top.cs"] = 1,
        ["Vorticity.Dataset/LevelZeroMemo.cs"] = 1,
        ["Vorticity.Dataset/ObjectCache.cs"] = 1,
        ["Vorticity/Aggregation/AggregationEngine.cs"] = 3,
        ["Vorticity/Aggregation/AggregationEstimates.cs"] = 1,
        ["Vorticity/Aggregation/DistinctQuery.cs"] = 1,
        ["Vorticity/Aggregation/GroupBatches.cs"] = 1,
        ["Vorticity/Aggregation/GroupCore.cs"] = 3,
        ["Vorticity/Aggregation/GrowthReads.cs"] = 1,
        ["Vorticity/Aggregation/MergedParts.cs"] = 1,
        ["Vorticity/Aggregation/PackedKeys.cs"] = 2,
        ["Vorticity/Aggregation/Slots.cs"] = 2,
        ["Vorticity/Aggregation/StreamingGroups.cs"] = 1,
        ["Vorticity/Buffers/HeldMemory.cs"] = 1,
        ["Vorticity/IO/LocalFileSource.cs"] = 1,
        ["Vorticity/Parquet/Codecs/PageLanes.cs"] = 1,
        ["Vorticity/Parquet/Reading/FieldPipeline.cs"] = 1,
        ["Vorticity/Parquet/Reading/ParquetBatches.cs"] = 2,
        ["Vorticity/Scan/BatchAsyncEnumerable.cs"] = 1,
        ["Vorticity/Scan/KeyOrderedBatches.cs"] = 1,
        ["Vorticity/Scan/Scan.Parts.cs"] = 1,
        ["Vorticity/Scan/TerminalScan.cs"] = 1,
        ["Vorticity/Session/SynopsisCache.cs"] = 1,
        ["Vorticity/Vortex/Writing/KeyIndexBuilder.cs"] = 2,
        ["Vorticity/Vortex/Writing/VortexFileWriter.Parts.cs"] = 1,
        ["Vorticity/Vortex/Writing/VortexFileWriter.cs"] = 4,
    };

    /// <summary>
    /// No file of <c>src</c> outside the core starts more parallel work than its ceiling, and a file that starts fewer
    /// says so, so that the ceiling comes down with it.
    /// </summary>
    [Fact]
    public void NoFileStartsMoreParallelWorkThanItsCeiling()
    {
        Dictionary<string, int> counted = Count();
        StringBuilder over = new StringBuilder();
        StringBuilder under = new StringBuilder();
        foreach ((string file, int count) in counted.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            int ceiling = Ceilings.GetValueOrDefault(file);
            if (count > ceiling)
            {
                over.Append(file).Append(": ").Append(count).Append(" starts, ceiling ").Append(ceiling).Append('\n');
            }
            else if (count < ceiling)
            {
                under.Append(file).Append(": ").Append(count).Append(" starts, ceiling ").Append(ceiling).Append(" (lower it)\n");
            }
        }

        foreach ((string file, int ceiling) in Ceilings)
        {
            if (!counted.ContainsKey(file))
            {
                under.Append(file).Append(": 0 starts, ceiling ").Append(ceiling).Append(" (remove it)\n");
            }
        }

        Assert.True(over.Length == 0, "Parallel work started outside the core:\n" + over);
        Assert.True(under.Length == 0, "A ceiling to lower:\n" + under);
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
