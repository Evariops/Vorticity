// One definition of what a scan, a projection, a take and a write-back ARE, for every estimator.
//
// BENCH-AUDIT.md A2: `ScanAll` had been written out seven times across this project, `DiscardSink`
// twice, and A1 is what that produces -- a correction applied to one copy. §3's pruning removed
// four of those copies by deleting the classes that held them, which left the two gates and the
// profiler still each carrying their own; the write axis added a third `DiscardSink` the day it
// landed. This file is the one definition, and the table at the bottom is what makes `--profile`
// and `--ratio-check` provably the same scenario rather than two that look alike.
//
// WHY A TABLE AND NOT JUST SHARED METHODS. The methods alone would stop the drift inside one
// scenario; they would not stop the profiler from sampling `Scan().Project("monotone")` while the
// gate measured `Scan().Project("id")`. The profile exists to say where the gate's time goes, and
// that sentence is only true if the two run the same code with the same arguments. So the
// arguments -- the field, the take's count and stride, the band -- live here too.
//
// THE RUST SIDE IS IN THE TABLE AS WELL, because the pairing is the point: an axis is a scenario
// plus the reference's answer to the same question. `RatioCheck` builds its first axes from this
// table and keeps its own entries for the ones the profiler has no use for (the footer-only open,
// the four `rewritten` axes, which need a file written on the spot).
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The scenarios, once, with the arguments that make them comparable.</summary>
internal static class Scenarios
{
    /// <summary>The column the projection keeps and the filter tests.</summary>
    internal const string Field = "monotone";

    /// <summary>Rows a scattered take asks for.</summary>
    internal const long TakeCount = 64;

    /// <summary>The gap between taken rows on the 65 536-row file.</summary>
    internal const long TakeStride = 1024;

    /// <summary>The low edge of the filter band.</summary>
    internal const long BandLow = 1_000_000;

    /// <summary>A band that keeps about one row in a hundred.</summary>
    internal const long NarrowBand = 656;

    /// <summary>A band that keeps about half the rows.</summary>
    internal const long WideBand = 32_768;

    /// <summary>A scenario: a name, an axis label, and both sides of the same question.</summary>
    /// <param name="Name">What `--profile` calls it.</param>
    /// <param name="Axis">What `--ratio-check` calls it; also the key into its reference table.</param>
    /// <param name="Ours">Our reader, returning rows.</param>
    /// <param name="Theirs">The reference, returning rows.</param>
    internal sealed record Scenario(
        string Name, string Axis, Func<string, Task<long>> Ours, Func<string, long> Theirs);

    /// <summary>The scenarios both the gate and the profiler run.</summary>
    internal static readonly Scenario[] All =
    [
        new Scenario(
            "fullscan",
            "full scan",
            ScanAll,
            p => RustReader.Require(RustReader.ScanCanonical(p), "scan")),
        new Scenario(
            "projected",
            "projected scan, 1 of 5 columns",
            ScanProjected,
            p => RustReader.Require(RustReader.ScanProjected(p, Field), "projected scan")),
        new Scenario(
            "take",
            "scattered take, 64 of 64 splits",
            p => ScatteredTake(p, TakeCount, TakeStride),
            p => RustReader.Require(RustReader.Take(p, TakeCount, TakeStride), "take")),
        new Scenario(
            "write",
            "read and write back",
            ReadAndWrite,
            p => RustReader.Require(RustReader.Write(p), "write")),
    ];

    /// <summary>The scenario <paramref name="name"/> names, or null.</summary>
    /// <param name="name">A `--profile` name.</param>
    internal static Scenario? ByName(string name)
    {
        foreach (Scenario scenario in All)
        {
            if (string.Equals(scenario.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return scenario;
            }
        }

        return null;
    }

    /// <summary>Every row of every column, canonicalized.</summary>
    /// <param name="path">The file.</param>
    internal static async Task<long> ScanAll(string path)
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

    /// <summary>One column of five.</summary>
    /// <param name="path">The file.</param>
    internal static async Task<long> ScanProjected(string path)
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

    /// <summary>Rows spread through the file, one every <paramref name="stride"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="stride">The gap between them; the row taken is the middle of each gap.</param>
    internal static async Task<long> ScatteredTake(string path, long count, long stride)
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
    internal static async Task<long> FilteredScan(string path, long low, long width)
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
    internal static async Task<long> ReadAndWrite(string path)
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
    internal sealed class DiscardSink : ISegmentSink
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
