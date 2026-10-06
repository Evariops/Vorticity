// What a whole read path allocates, from open to disposal, held against a ratchet.
//
// ScanAllocationTests already pins the figure the architecture makes a contract: zero managed
// bytes per batch in steady state, beyond the one RecordBatch that a scan cannot recycle because
// it is a sealed class with readonly fields. That is the number the design promises. It is not
// the number a caller pays.
//
// A caller pays for the open as well - the footer, the layout tree, the segment map, the first
// batch's arenas - and the per-batch figure guards none of that. The benchmarks report it, and
// reporting is all they do. A figure in a benchmark report is noticed when someone runs the
// benchmark and reads the column, which is to say on the commit that introduces a regression
// roughly never.
//
// WHY THIS IS A TEST RATHER THAN AN ASSERTION IN THE BENCHMARK. Allocations are the one performance
// quantity that is deterministic: the same build allocates the same bytes on any machine, so it can
// be a hard barrier rather than a barrier with margin. (With one clause, recorded on
// AllocationCollection below: deterministic for a process that is not contending on
// ArrayPool<T>.Shared.) A hard barrier is only worth having where it runs, and what CI runs is
// `dotnet test`, not BenchmarkDotNet. So the ceilings live here, next to WrittenSizeTests, whose
// shape this copies deliberately:
//
//   * the ceiling sits JUST above the measured value, so a regression is red and an improvement is
//     green;
//   * lowering it is a manual edit in the commit that earned it, which is the only part a human
//     should have to do;
//   * every axis is printed with its headroom, so a ceiling that has drifted far above what the
//     code actually costs is visible rather than silently inert.
//
// WHY THE FLOOR OF SEVERAL RUNS AND NOT ONE. Tiered JIT promotes methods on a call-count threshold,
// and the promotion allocates. Measuring once means measuring whichever run happened to absorb it -
// ScanAllocationTests records the same trap, where a rejit inside a measured window reads as a
// plausible per-batch regression. The minimum over several runs is the honest steady-state
// figure, and it is a floor rather than an average so that anything allocated on EVERY run still
// raises it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// Runs alone, because the figure it measures is not private to this thread after all.
/// </summary>
/// <remarks>
/// The ceilings rest on the premise that allocations are deterministic - same build, same bytes,
/// any machine - and therefore lockable without margin. Measured alone the premise holds
/// exactly: byte-for-byte identical over three runs of every axis. Measured inside the full suite
/// it does not, and the six axes disagree in a way that names the cause. `open, footer only`,
/// `projected scan` and `selective filter` do not move; `full scan` gains 2 880 B and
/// `take 64 rows from 64 splits` gains 576 B.
///
/// The ones that move are the ones that decode the most, and the decode path takes its transients
/// from <c>ArrayPool&lt;T&gt;.Shared</c> (see <c>Arrays/Decoders/Canonical/Scratch.cs</c>). That pool
/// is process-global: when neighbouring test classes have drained its per-core stacks, a Rent that
/// would have been free allocates a fresh array instead, and the bytes are charged to whoever
/// rented. Nothing regressed and nothing is non-deterministic about the library - the process is
/// simply a shared resource, the same reason <c>SegmentOwnerTests</c> runs alone.
///
/// So the premise needs one clause it did not have: allocations are deterministic **for a process
/// that is not contending on the shared pool**. A ceiling tight enough to be worth having is only
/// measurable in isolation, and the alternative - widening every ceiling until suite contention fits
/// underneath - would set the margin by how busy CI happens to be.
/// </remarks>
[CollectionDefinition(nameof(AllocationCollection), DisableParallelization = true)]
public sealed class AllocationCollection
{
}

/// <summary>The configuration guard every allocation ceiling in this repository sits behind.</summary>
/// <remarks>
/// EVERY CEILING IN THESE THREE FILES IS A RELEASE FIGURE, and a Debug build allocates more -- the
/// JIT optimizer is off, so the escape analysis that keeps a decode path's transients off the heap
/// does not run. The gap is not small and it is not uniform: it scales with how much code the
/// axis runs, which is exactly what makes it look like a regression.
/// <para>
/// WHY THIS GUARD EXISTS AT ALL. `dotnet test` with no argument builds Debug, so without it the
/// plain, documented, obvious command turns every axis red at once, with numbers that are each
/// plausible and say nothing about the library -- the kind of failure that sends someone
/// bisecting commits before the configuration is suspected. CI and `bench/gate.sh` both pass
/// `-c Release`, so the ratchet still bites where it is meant to; a Debug run SAYS SO instead of
/// lying quantitatively.
/// </para>
/// <para>
/// It reads the attribute of the assembly UNDER TEST rather than <c>#if DEBUG</c> here, because
/// what decides the figure is how <c>Vorticity</c> was compiled, not how this project was.
/// </para>
/// </remarks>
internal static class ReleaseOnlyCeilings
{
    /// <summary>Skips the calling test, with the reason, unless the library is optimized.</summary>
    internal static void Require()
    {
        DebuggableAttribute? debuggable =
            typeof(VortexFile).Assembly.GetCustomAttribute<DebuggableAttribute>();
        bool optimized = debuggable is null || !debuggable.IsJITOptimizerDisabled;

        Assert.SkipUnless(
            optimized,
            "Allocation ceilings are Release figures: a Debug build has the JIT optimizer off and "
                + "allocates more on every axis, by 500 B to 32 kB depending on how much the axis "
                + "decodes. Nothing is wrong with the library. Run `dotnet test -c Release`, which "
                + "is what CI and bench/gate.sh run.");
    }
}

/// <summary>Whole-path allocation ceilings, on the <see cref="WrittenSizeTests"/> ratchet model.</summary>
[Collection(nameof(AllocationCollection))]
public sealed class PathAllocationTests
{
    /// <summary>
    /// The file the whole-path axes read: the same one the benchmarks use, so the two sets of
    /// numbers are about the same work. 65 536 rows over five mixed columns in 64 splits.
    /// </summary>
    private const string File = "containers/zoned_many_zones_nulls";

    /// <summary>Runs discarded before measuring: JIT, statics, and the decoder registry.</summary>
    private const int Warmup = 5;

    /// <summary>Runs measured, of which the minimum is the answer.</summary>
    private const int Runs = 10;

    /// <summary>
    /// What the runtime's own file handle weighs on Windows beyond the platform the ceilings were
    /// measured on: every axis opens one file, and every axis, the footer-only one included, reads
    /// 24 bytes more there. The handle is the one allocation of an open that differs by platform:
    /// Windows' <c>SafeFileHandle</c> carries its file type, its cached length and its thread-pool
    /// binding, 80 bytes. The library allocates the same bytes on both.
    /// </summary>
    private static readonly long HandleAllowance = OperatingSystem.IsWindows() ? 24 : 0;

    /// <summary>Samples taken at each end of the retention check, of which the minimum counts.</summary>
    private const int Samples = 3;

    /// <summary>
    /// The ceilings, in bytes. LOWER THESE BY HAND when a change earns it, in the same commit, and
    /// never raise one without saying in the commit message what grew and why that is acceptable.
    /// </summary>
    /// <remarks>
    /// Each is the measured figure rounded up to the next 512 bytes. That margin is not for machine
    /// variance - there is none to absorb, the same build allocates the same bytes everywhere - it
    /// is so that an unrelated framework change of a few dozen bytes does not turn every axis red
    /// at once while saying nothing about this library.
    ///
    /// These need not equal the benchmark's figures, and a gap is not a defect to reconcile: the
    /// measuring wrapper is inside this measurement and outside that one. The corpus path is outside
    /// both, since the benchmarks resolve it in their setup and <see cref="Measure"/> is handed it
    /// resolved. What matters for a ratchet is that the overhead is constant, which it is: the
    /// number moves when the path moves and at no other time. Compare a run of this test with
    /// another run of this test, never with the recorded benchmark figures.
    /// </remarks>
    /// <seealso cref="AllocationCollection"/>
    /// <remarks>
    /// EACH AXIS NAMES ITS OWN FILE, because axes over one file would leave every component that
    /// file does not contain -- `fastlanes.delta`, `vortex.pco`, `vortex.zstd`, `vortex.map`,
    /// `vortex.variant` -- watched by no allocation ratchet at all, on either side. They are the
    /// decoders most likely to allocate per page or per chunk and the least exercised, which is the
    /// wrong pair of properties. One scan axis each is the cheapest thing that makes a regression
    /// there red.
    /// </remarks>
    private static readonly (string Axis, string File, long Ceiling, Func<string, ValueTask<long>> Path)[] Axes =
    [
        // A file opened from a path reads its tail positionally and is mapped by the first scan
        // that reads data: an open that reads nothing more makes no mapping, 328 bytes less.
        //
        // Its reader carries the session's kept mappings and the name the file was opened under,
        // which lets a file replaced under that name go at once: 16 bytes an open.
        ("open, footer only", File, 13_904, FooterOnly),
        // `VortexFile` holds one reference to the lazily parsed `LayoutTree` that every scan of
        // an open file shares instead of re-deriving: eight bytes once per OPEN, against a
        // layout-tree parse once per `ExecuteAsync`. This axis opens the file and reads one batch,
        // so it pays the eight and collects none of the saving.
        //
        // A scan context holds, behind one reference rather than inline, the comparison an
        // encoding may answer instead of decoding, so that only a scan that pushes one pays for it.
        //
        // A lane holds the delegate that runs its body, bound once at its first split rather than
        // rebuilt at each, which pays for itself from the second split of a parallel scan.
        //
        // A DType and a ScalarValue carry the generation of the arena or store that issued them,
        // and ScalarStore a field for it, so that reading a handle held across a Clear throws
        // instead of silently answering about whatever node has since taken that index.
        //
        // A lane keeps the segments its last batch read in one pooled array, and the next batch of
        // the same chunk reuses them: a segment spans every block of its chunk, so without this the
        // scan asks the source for the same bytes once per block.
        //
        // A file carries the session it was opened in and, once asked for, the public views of its
        // schema, statistics, metadata, edition and components; and each column's statistics carry
        // the column type and the sum type they read their values back as: 64 bytes an open and 16 a
        // column, which the footer-only axis pays inside its headroom.
        //
        // A scan carries what its prefetching lanes run on, even with one lane: on the lane two
        // reusable value-task sources, its loop and a segment waiter, beside the rows a filter kept
        // and the root as decoded; the segments the scan holds for its next batches and the lock the
        // lanes take them under; the blocks decoded and pruned, counted in the plan's units; the
        // switches of prefetch, compaction and encoded delivery. 328 bytes a scan, and 8 an arena for
        // its table of the words a typed reader asks for.
        //
        // A batch carries its session, its public schema, the rows a filter kept when it is
        // delivered whole, and whether it owns its arena: 40 bytes more on every batch.
        //
        // The chunks a scan decodes and holds across batches are one table for the scan, shared by
        // its lanes, where each lane held its own: the table, 56 bytes a scan, and an entry that
        // also names who decodes it and the next spare, 16 bytes a chunk.
        //
        // Every zstd frame a lane decompresses goes through one decoder its context builds once,
        // where each chunk built and tore down its own: 41 KB less on a full scan and on the take.
        //
        // The layout tree is gathered in rented arrays and kept as exact copies of them, where it
        // grew its own by doubling and left every generation behind, and it keeps the file's copy
        // of the layout rather than making another: 60 520 bytes less on every open of this file,
        // 1 300 on a file of one column.
        //
        // A lane's scan context is the process's, taken bound to the file and given back recycled,
        // where every scan built a dozen arrays per lane -- the node and canonical arenas, the
        // scalars, the segment set: 16 496 bytes less on every axis that scans, a new arena for the
        // dtypes the scan derives excepted.
        //
        // A zone map's column names are written as UTF-8 into the arena that interns them, and the
        // table's scratch is rented: no string and no array for them per zone map.
        //
        // A dtype arena allocates its nodes up front and each of its other arrays at the first
        // dtype that has some; a scan's context makes its arena at the first dtype the scan
        // derives, as large as its last scan's needed, and a layout tree at the first it derives.
        //
        // An open sizes the schema's arena for eight nodes and the statistics' store for a minimum
        // and a maximum a field, and the statistics read the fields' dtypes from the schema rather
        // than from a copy of them.
        //
        // The segments a scan holds are their own lock, which a scan of one lane never contends
        // for, rather than a lock object of their own.
        //
        // The split plan gathers its boundaries in a rented array and keeps an exact copy of the
        // ones the sort leaves: on this file of many chunks the list no longer doubles its way
        // there, 7 544 bytes less on every axis that plans the whole file.
        //
        // Parses share one layout-tree builder and the scratch it grew, and the zone maps of a tree
        // read their aggregates, packed an int each, from one table the tree keeps rather than
        // from two arrays each: 408 bytes less on this file of five zoned columns. A text column's
        // bounded maximum takes its two fields in an array on the stack.
        //
        // A scan's enumerator keeps neither the split of its current batch nor its batch cap, which
        // the batch's lane and the plan hold, and counts in two integers the decodes its degree
        // still allows and the splits waiting for one: 8 bytes less on every axis that scans.
        //
        // A scan of a file the session keeps mapped takes the mapping over rather than making one,
        // with no file object, view and owner of its own: 312 bytes less on every axis that scans.
        //
        // A scan of one lane reads its next splits while it decodes one, over a source whose read
        // is a round trip (PLAN-HIGH-CARDINALITY, R6): what it holds for that is behind one
        // reference of the enumerator, null over a mapping, 8 bytes on every axis that scans.
        ("open, first batch", File, 43_536, FirstBatch),
        // A scan binds each batch into the object its previous batch was rather than allocating
        // one: 120 bytes less a batch after the first, on every axis below that reads more than
        // one -- 7 560 over the 64 batches of this file, 120 over the two the pruned filter reads.
        // A full scan now costs what its first batch does.
        ("full scan", File, 43_536, FullScan),
        // A projection of one whole column is held in the mask itself, with no node and no arrays:
        // 104 bytes less.
        ("projected scan, 1 of 5 columns", File, 45_880, ProjectedScan),
        // A take or a filter goes through the filtered delivery, whose enumerable and enumerator hold
        // one more field each: 16 bytes a scan.
        ("take 64 rows from 64 splits", File, 44_784, ScatteredTake),
        // The filter's field references hold one more field each, and the zone column the pruning
        // pass reads one more: 8 bytes a reference and 8 for the column, besides the arena of the
        // context that reads the zone map.
        //
        // The evaluator holds the zone maps the pruning read, so that a split they prove whole is
        // not evaluated: 8 bytes a filtered scan. A field reference encodes its path without
        // splitting it into strings, and the pruning locates the filter's own references rather
        // than building new ones, which more than pays for it on both axes.
        //
        // A context holds the zstd decoder it builds for its frames, whether or not it meets one: 8
        // bytes for the lane's and 8 for the one the pruning reads the zone map through, which the
        // 41 KB the full scan and the take no longer spend on decoders more than pay for.
        //
        // A numeric column's zones are held as columns rather than one summary each, a third of
        // the bytes, and the file keeps them for its next scan in a holder it makes then: 2 352
        // bytes under what the summaries cost.
        //
        // The filter's own column is read first and the projection over the rows it keeps, under a
        // mask of one column that is the mask itself; the pushed comparison's mask is no longer a
        // builder's and a record's; the evaluator holds the share of rows kept so far, which the
        // second pass reads its columns by: 168 bytes less on both axes.
        ("selective filter, pruning on", File, 55_216, PrunedFilter),

        // THE SAME FILTER WITH PRUNING OFF, because it is a different path and not a slower one:
        // pruning on reads the zone map and skips whole splits, pruning off decodes every split and
        // masks. The two allocate differently by construction, so each has its own ceiling.
        // The first thing the pair says is not what one would guess: on this file PRUNING ALLOCATES
        // MORE than not pruning while keeping ~100 rows of 65 536.
        //
        // WHAT THE GAP IS. `ZonePruner.MayMatch` allocates nothing; the gap is the `ScanContext`
        // the plan builds to read the zone map, plus the zone decode itself, and no part of it
        // grows with the number of zones. Keep `ZoneColumn.Zones` a range rather than an iterator,
        // and that context's arenas sized for what they hold rather than for a batch: either one
        // undone costs more than the whole gap that remains. The lane's context holds its zstd
        // decoder's field, 8 bytes, as above.
        ("selective filter, pruning off", File, 47_296, UnprunedFilter),

        // One scan per late component. They are single-column files of 4 096 rows, so the figure is
        // dominated by the decoder rather than by the open, which is the point of putting them here
        // rather than adding columns to the file above.
        // This ceiling also carries the read contract's per-scan state -- the mask of live blocks
        // and the metrics sink, a reference each on the enumerable, the enumerator and the lane's
        // context -- and the headroom the neighbouring axes have.
        //
        // The mapping these scans read through is made by the scan rather than by the open, and
        // the reader that makes it is smaller than the mapped source was: 48 bytes less on each.
        ("scan, fastlanes.delta", "encodings/fastlanes_delta", 2_880, FullScan),
        // The page sizes are read in place from the node's metadata rather than into a list a
        // chunk, and the chunk metadata with its ANS tables and the latent states are kept from one
        // decode to the next rather than built by each: 1 536 bytes less on this file's one chunk,
        // and on a column of many chunks of many-bin tables a few hundred kilobytes a chunk.
        ("scan, vortex.pco", "encodings/pco", 2_832, FullScan),
        // A node's frames go through one Vorticity.Zstd decompressor rather than one each, and the
        // decompressor is the process's, taken by the scan's context and given back when it is
        // disposed, so a warm scan builds none: its tables and buffers are 160 KB at least.
        ("scan, vortex.zstd", "encodings/zstd", 2_832, FullScan),
        // The tail an open reads is 64 KiB, which puts this file's tail at an offset the mapping can
        // lend as it is: the open holds a 48-byte owner of the view where it would copy the tail.
        ("scan, vortex.map", "encodings/map", 3_616, FullScan),
        ("scan, vortex.variant", "encodings/variant", 3_256, FullScan),
    ];

    [Fact]
    public void EveryReadPathAllocatesWithinItsCeiling()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();

        StringBuilder report = new StringBuilder("PATH ALLOCATIONS: floor of ")
            .Append(Runs.ToString(CultureInfo.InvariantCulture))
            .Append(" runs after ")
            .Append(Warmup.ToString(CultureInfo.InvariantCulture))
            .Append(" warm-ups\n");

        List<string> over = [];
        foreach ((string axis, string file, long measured, Func<string, ValueTask<long>> path) in Axes)
        {
            long ceiling = measured + HandleAllowance;
            long floor = Floor(path, Corpus.Path(file));
            long headroom = ceiling - floor;
            report.Append("    ")
                .Append(axis.PadRight(32))
                .Append(floor.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(" B   ceiling ")
                .Append(ceiling.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append("   headroom ")
                .Append(headroom.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                .Append('\n');

            if (floor > ceiling)
            {
                over.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{axis} allocated {floor} B against a ceiling of {ceiling} B"));
            }
        }

        Console.Out.Write(report.ToString());

        // Reported together rather than one assertion per axis: a change that moves allocations
        // usually moves several, and seeing one of them is how the other five get fixed one commit
        // at a time.
        Assert.True(
            over.Count == 0,
            string.Join("\n", over) + "\n" + report);
    }

    /// <summary>
    /// The floor is a steady-state figure, so the paths must not keep growing across runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floor alone cannot tell "this path costs 190 kB every time" from "this path costs 190 kB
    /// once and 400 kB thereafter", and only the first is a bounded open. A cache that never
    /// evicts, or a static list appended to per open, shows up here and nowhere else in the suite.
    /// </para>
    /// <para>
    /// BOTH ENDS ARE FLOORS, for the reason the class comment gives about the other test: a tiered
    /// promotion allocates, and a single measured run is whichever one happened to absorb it.
    /// Comparing two single samples is a latent flake: one promotion landing in the last sample
    /// reads as retention, on whichever axis it falls, whether or not that path changed.
    /// </para>
    /// <para>
    /// Floors at both ends still catch what the assertion is for: a path that retains across opens
    /// raises its floor too, and a floor is the steady-state figure retention actually moves.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLastRunOfAPathCostsWhatTheFirstDid()
    {
        Decoders.EnsureRegistered();

        foreach ((string axis, string id, _, Func<string, ValueTask<long>> path) in Axes)
        {
            string file = Corpus.Path(id);
            for (int i = 0; i < Warmup; i++)
            {
                Complete(path(file));
            }

            long first = long.MaxValue;
            for (int i = 0; i < Samples; i++)
            {
                first = Math.Min(first, Measure(path, file));
            }

            for (int i = 0; i < Runs; i++)
            {
                Complete(path(file));
            }

            long last = long.MaxValue;
            for (int i = 0; i < Samples; i++)
            {
                last = Math.Min(last, Measure(path, file));
            }
            Assert.True(
                last <= first,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{axis} allocated {first} B early and {last} B after {Runs} more opens (floor of {Samples} at each end), so something is retained across opens"));
        }
    }

    private static long Floor(Func<string, ValueTask<long>> path, string file)
    {
        for (int i = 0; i < Warmup; i++)
        {
            Complete(path(file));
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            floor = Math.Min(floor, Measure(path, file));
        }

        return floor;
    }

    /// <summary>What one run of <paramref name="path"/> over <paramref name="file"/> allocates.</summary>
    /// <remarks>
    /// <paramref name="file"/> is the corpus file's absolute path, resolved by the caller before
    /// anything is measured, as the write ratchet resolves its own. The string is as long as the
    /// checkout's location, so a path built inside the measurement would charge the axis two bytes
    /// for every character of the directory the repository is cloned in, and a ceiling met in one
    /// clone would be missed in a deeper one.
    /// </remarks>
    private static long Measure(Func<string, ValueTask<long>> path, string file)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Complete(path(file));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Drives one path to completion, and asserts it never left this thread.
    /// </summary>
    /// <remarks>
    /// This is the precondition of the whole measurement, not a bonus check.
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts THIS thread, so a continuation that
    /// resumed on the pool would have its allocations silently omitted and the ceiling would be
    /// guarding a fraction of the path. Over a memory-mapped file nothing here is truly
    /// asynchronous, so the assertion holds - and if a read ever goes to the pool, this fails
    /// loudly instead of quietly under-counting.
    /// </remarks>
    private static long Complete(ValueTask<long> work)
    {
        Assert.True(
            work.IsCompletedSuccessfully,
            "the path did not complete synchronously, so a per-thread allocation figure would " +
            "count only the part of it that ran on this thread");
        return work.Result;
    }

    private static async ValueTask<long> FooterOnly(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        return file.RowCount;
    }

    private static async ValueTask<long> FirstBatch(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return batch.RowCount;
        }

        return 0;
    }

    private static async ValueTask<long> FullScan(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async ValueTask<long> ProjectedScan(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Project("monotone").ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    private static async ValueTask<long> ScatteredTake(string path)
    {
        // One row from each of the file's 64 splits of 1024, the take axis of `--ratio-check`'s shape.
        long[] indices = new long[64];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (i * 1024L) + 511;
        }

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().Take(indices).ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>A narrow band of the sorted column: ~100 rows of 65 536, pruning's own case.</summary>
    private static async ValueTask<long> PrunedFilter(string path)
    {
        return await Band(path, pruning: true);
    }

    /// <summary>The same band with pruning off: every split decoded, then masked.</summary>
    private static ValueTask<long> UnprunedFilter(string path) => Band(path, pruning: false);

    private static async ValueTask<long> Band(string path, bool pruning)
    {
        VortexExpr filter = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        await using VortexFile opened = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in opened.ScanBuilder()
            .Project("monotone")
            .Where(filter)
            .WithPruning(pruning)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }
}
