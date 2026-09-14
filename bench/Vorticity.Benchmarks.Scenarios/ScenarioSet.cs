// The scenarios themselves, in their own assembly so that two builds of them can meet.
//
// BENCH-AUDIT.md C1: the repository's rule is "one process, one clock", and its only realisation
// was copying an old loop into the bench by hand -- impossible for a change that spans a layout
// reader or the arena, so those were judged across two runs, which is what v2 §1.1 forbids.
// The A/B mode loads THIS assembly twice: once from the build tree and once from a worktree of an older
// commit, each bound to its own `Vorticity.dll` in its own `AssemblyLoadContext`.
//
// IT DEPENDS ON THE LIBRARY AND ON NOTHING ELSE. Whatever it references has to exist, and compile,
// in every commit anyone ever wants to compare against -- so no FFI reader, no BenchmarkDotNet, no
// corpus helper. The only types crossing the load-context boundary are `string`, `Task<long>` and
// `Func<,>`, which live in the shared runtime and are therefore the same type on both sides; a
// scenario returning anything of the library's own would be two incompatible types with one name.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Bench.Scenarios;

/// <summary>What a scan, a projection, a take, a filter and a write-back ARE.</summary>
public static class ScenarioSet
{
    /// <summary>The column the projection keeps and the filter tests.</summary>
    public const string Field = "monotone";

    /// <summary>Rows a scattered take asks for.</summary>
    public const long TakeCount = 64;

    /// <summary>The gap between taken rows on the 65 536-row file.</summary>
    public const long TakeStride = 1024;

    /// <summary>The low edge of the filter band.</summary>
    public const long BandLow = 1_000_000;

    /// <summary>A band that keeps about one row in a hundred.</summary>
    public const long NarrowBand = 656;

    /// <summary>A band that keeps about half the rows.</summary>
    public const long WideBand = 32_768;

    /// <summary>The scenario names this assembly answers to.</summary>
    public static string[] Names => ["fullscan", "projected", "take", "filtered", "write"];

    /// <summary>
    /// The scenario <paramref name="name"/> names, as a delegate of shared-runtime types only.
    /// </summary>
    /// <param name="name">One of <see cref="Names"/>.</param>
    /// <returns>The scenario, or <see langword="null"/> when the name is unknown.</returns>
    /// <remarks>
    /// THE ENTRY POINT `--ab` REFLECTS ON. It is deliberately the whole contract: one static method
    /// taking a string and returning a `Func&lt;string, Task&lt;long&gt;&gt;`, so an old build only
    /// has to have THIS shape for the comparison to work, whatever else changed inside it.
    /// </remarks>
    public static Func<string, Task<long>>? Resolve(string name) => name switch
    {
        "fullscan" => ScanAll,
        "projected" => ScanProjected,
        "take" => p => ScatteredTake(p, TakeCount, TakeStride),
        "filtered" => p => FilteredScan(p, BandLow, NarrowBand),
        "write" => ReadAndWrite,
        _ => null,
    };

    /// <summary>Every row of every column, canonicalized.</summary>
    /// <param name="path">The file.</param>
    public static async Task<long> ScanAll(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Every row, canonicalized, with <paramref name="degree"/> lanes in flight.</summary>
    /// <param name="path">The file.</param>
    /// <param name="degree">Lanes; 1 is the default scan.</param>
    /// <remarks>
    /// `WithDegreeOfParallelism` existed and nothing measured it (BENCH-AUDIT.md D2). A lane is a
    /// split decoded on the thread pool while another is being consumed, so the interesting number
    /// is not the speed-up alone but where it stops.
    /// </remarks>
    public static async Task<long> ScanAllLanes(string path, int degree)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().WithDegreeOfParallelism(degree)
            .ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>One column of five.</summary>
    /// <param name="path">The file.</param>
    public static async Task<long> ScanProjected(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(Field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>One named column, whichever file it is in.</summary>
    /// <param name="path">The file.</param>
    /// <param name="field">The column to keep.</param>
    public static async Task<long> ScanProjectedField(string path, string field)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Project(field).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>Rows spread through the file, one every <paramref name="stride"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="stride">The gap between them; the row taken is the middle of each gap.</param>
    public static async Task<long> ScatteredTake(string path, long count, long stride)
    {
        long[] indices = new long[count];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (i * stride) + (stride / 2);
        }

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Take(indices).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>A half-open band on <see cref="Field"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="low">The band's low edge, inclusive.</param>
    /// <param name="width">Its width.</param>
    public static async Task<long> FilteredScan(string path, long low, long width)
    {
        VortexExpr band = Expr.And(
            Expr.Ge(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low))),
            Expr.Lt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(low + width))));

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().Where(band).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>The file read back out to a sink that keeps nothing.</summary>
    /// <param name="path">The file.</param>
    /// <remarks>
    /// The read is inside the measurement on both sides and is therefore common-mode, but it is not
    /// small: subtract the scan axis before reading the quotient as a statement about writers.
    /// </remarks>
    public static async Task<long> ReadAndWrite(string path)
    {
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer =
            VortexFileWriter.Create(new DiscardSink(), source.Schema);

        long rows = 0;
        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return rows;
    }

    /// <summary>A sink that counts bytes and keeps none of them.</summary>
    /// <remarks>
    /// A write benchmark that measures the filesystem measures the filesystem, and `vxbench_write`
    /// writes into a `Vec&lt;u8&gt;` for the same reason.
    /// </remarks>
    public sealed class DiscardSink : ISegmentSink
    {
        /// <summary>Bytes written so far.</summary>
        public long Position { get; private set; }

        /// <inheritdoc/>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
