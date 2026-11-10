// Audit IN-07: what finding a column's synopsis region in an object's table of contents costs, which a plan asks of
// every object for every column a question reads: an object of 16 or 128 columns, each with its sheet, counts and
// statistics over its granules, every column's three regions looked up.
using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Audit;

/// <summary>An object's synopsis regions found by column.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditContentsLookupBenchmarks
{
    private ObjectContents _contents = null!;

    /// <summary>The object's columns.</summary>
    [Params(16, 128)]
    public int Columns { get; set; }

    /// <summary>Makes the table of contents.</summary>
    [GlobalSetup]
    public void Setup()
    {
        List<(int Field, IndexSegment Region)> sheets = [];
        List<(int Field, IndexSegment Region)> counts = [];
        List<(int Field, IndexSegment Region)> stats = [];
        for (int f = 0; f < Columns; f++)
        {
            sheets.Add((f, new IndexSegment((ulong)(f * 3 * 4096), 4096, 0)));
            counts.Add((f, new IndexSegment((ulong)(((f * 3) + 1) * 4096), 4096, 0)));
            stats.Add((f, new IndexSegment((ulong)(((f * 3) + 2) * 4096), 4096, 0)));
        }

        _contents = new ObjectContents([], [], 0, []) { Sheets = sheets, Counts = counts, GranuleStats = stats };
        Lookups();
    }

    /// <summary>Every column's three regions, by column.</summary>
    [Benchmark]
    public long Lookups()
    {
        long found = 0;
        for (int f = 0; f < Columns; f++)
        {
            found += (long)(_contents.SheetOf(f)?.Offset ?? 0);
            found += (long)(_contents.CountsOf(f)?.Offset ?? 0);
            found += (long)(_contents.GranuleStatsOf(f)?.Offset ?? 0);
        }

        return found;
    }
}
