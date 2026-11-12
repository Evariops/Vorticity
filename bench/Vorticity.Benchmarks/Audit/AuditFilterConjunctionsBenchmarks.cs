// What a filter of several conjunctions costs a row, and which share of it its verdicts take as bytes.
//
// A file of 4,194,304 rows of five 64-bit columns, values drawn among a thousand. The filter is one comparison or
// four (A < 700, B >= 300, C < 700, D >= 300: 70 % each, 24 % together), on one lane; the kept rows' E read.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A filter of one comparison or four, its kept rows' values read.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public partial class AuditFilterConjunctionsBenchmarks
{
    private const int Rows = 1 << 22;

    private string _path = null!;
    private VortexFile _file = null!;
    private VortexExpr _filter = null!;

    /// <summary>The comparisons the filter joins.</summary>
    [Params(1, 4)]
    public int Conjunctions { get; set; }

    /// <summary>A row.</summary>
    [VortexRecord]
    public partial record struct Five(long A, long B, long C, long D, long E);

    /// <summary>Writes the file and opens it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        // Written once and kept, under VORTICITY_AUDIT_DATA when set: a file written by each process was read while the
        // system still scanned it, 7 to 41 ms a question from one process to the next.
        string? kept = Environment.GetEnvironmentVariable("VORTICITY_AUDIT_DATA");
        _path = kept is null
            ? Path.Combine(Path.GetTempPath(), $"vorticity-audit-conjunctions-{Guid.NewGuid():N}.vortex")
            : Path.Combine(kept, "audit-conjunctions.vortex");
        if (kept is null || !System.IO.File.Exists(_path))
        {
            Random random = new Random(31);
            Five[] rows = new Five[Rows];
            for (int i = 0; i < Rows; i++)
            {
                rows[i] = new Five(random.Next(1_000), random.Next(1_000), random.Next(1_000), random.Next(1_000), random.Next(1_000_000));
            }

            WriteAsync(rows).GetAwaiter().GetResult();
        }

        // Read from memory: through the system's cache, a question was the copy of 168 MB, 12 to 31 ms from one process
        // to the next, where the filter's own work is a few.
        _file = VortexFile.OpenAsync(new MemorySegmentSource(System.IO.File.ReadAllBytes(_path)), VortexOpenOptions.Default).AsTask().GetAwaiter().GetResult();
        VortexExpr filter = Expr.Lt(Expr.Field("A"), Expr.Literal(FilterLiteral.From(700L)));
        if (Conjunctions > 1)
        {
            filter = Expr.And(filter, Expr.Ge(Expr.Field("B"), Expr.Literal(FilterLiteral.From(300L))));
            filter = Expr.And(filter, Expr.Lt(Expr.Field("C"), Expr.Literal(FilterLiteral.From(700L))));
            filter = Expr.And(filter, Expr.Ge(Expr.Field("D"), Expr.Literal(FilterLiteral.From(300L))));
        }

        _filter = filter;
        Console.WriteLine($"// {Filter()} rows kept");
    }

    /// <summary>Closes and deletes the file.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("VORTICITY_AUDIT_DATA") is null)
        {
            System.IO.File.Delete(_path);
        }
    }

    /// <summary>The rows the filter keeps, their E read.</summary>
    [Benchmark]
    public long Filter() => FilterAsync().GetAwaiter().GetResult();

    private async Task<long> FilterAsync()
    {
        long rows = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder().Project("E").Where(_filter).WithDegreeOfParallelism(1).ExecuteAsync().ConfigureAwait(false))
        {
            using (batch)
            {
                rows += Vorticity.Bench.Scenarios.Consume.Batch(batch);
            }
        }

        return rows;
    }

    private async Task WriteAsync(Five[] rows)
    {
        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Five>(_path);
        await writer.WriteAsync<Five>(rows, CancellationToken.None).ConfigureAwait(false);
        await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
