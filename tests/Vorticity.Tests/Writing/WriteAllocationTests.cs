// What writing a file allocates. Nothing measured this before.
//
// docs/05-benchmarks.md reports the read paths and PathAllocationTests pins six of them. The write
// path had neither: no benchmark axis, no ceiling, no figure anywhere in the repository. The one
// write-side number that exists is WrittenSizeTests' output ratio, which is about the BYTES ON DISK
// and says nothing about what producing them costs in managed memory.
//
// WHAT "AUDITED" MEANS HERE, because a literal zero would be the wrong target and would be learned
// as noise within a week. A writer builds buffers; that is its job. The target is **no
// UNINTENTIONAL allocation**: every remaining one named, justified and bounded by something other
// than the input size where possible. So this file does two things a benchmark cannot:
//
//   * it pins the total against a ceiling, the way WrittenSizeTests pins the size ratio;
//   * it pins the total PER ROW, which is the shape question. A writer that allocates a constant
//     per batch is bounded; one that allocates per row is not, and the two are indistinguishable
//     from a single total. Two files of very different row counts separate them.
//
// The sink discards. The measurement is of the writer, not of a FileStream or of a MemoryStream's
// doubling - both are the caller's choice of destination, and neither is what this audits.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What the write path allocates, held against a ratchet.</summary>
/// <remarks>
/// Shares <see cref="Vorticity.Tests.Scan.AllocationCollection"/> for the reason that class
/// records: <c>ArrayPool&lt;T&gt;.Shared</c> is process-global, so a neighbouring test class
/// draining it turns a rent that would have been free into an allocation charged here.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class WriteAllocationTests
{
    /// <summary>
    /// Runs discarded before measuring: JIT, statics, and the encoding registry.
    /// </summary>
    /// <remarks>
    /// Five rather than three, and the reason is the counter. This measures process-wide allocation
    /// (see <see cref="Rewrite"/>), so tiered JIT promoting a writer method on its call-count
    /// threshold lands INSIDE a measured run rather than beside it. That tripped a ceiling once on
    /// the first suite run after this file was added and never again in seven. Warming longer fixes
    /// the cause; widening the ceiling would only have hidden it.
    /// </remarks>
    private const int Warmup = 5;

    /// <summary>Runs measured, of which the minimum is the answer.</summary>
    private const int Runs = 6;

    /// <summary>
    /// The files written, and what each is here to expose.
    /// </summary>
    /// <remarks>
    /// Chosen to differ in the dimension that matters rather than to be representative: the same
    /// schema at two row counts would answer the per-row question, and different schemas answer
    /// which ENCODER is expensive. Both questions are worth one axis each.
    /// </remarks>
    /// <remarks>
    /// EIGHT OF THESE TEN CAME DOWN WITH W-1, by the exact bytes it saved, so the headroom is the
    /// same and the ratchet is tighter: interning a component id no longer decodes the wire bytes
    /// to a `string` on every node just to look one up. `encodings/fsst` and `encodings/zstd` did
    /// not move at all, which is its own small fact -- their write is dominated by the compressor
    /// rather than by the node count.
    ///
    /// FIVE CAME DOWN AGAIN WITH W-3, the same way: a metadata scalar store is now sized for the two
    /// integers it holds instead of for a file's statistics. The files that moved are the ones whose
    /// columns elect `fastlanes.for` or `vortex.sequence` -- `zoned_many_zones_nulls` -5 712 B,
    /// `map` -2 304, `delta`, `pco` and `zstd` -576 each -- and the five that did not are the ones
    /// that elect neither.
    ///
    /// AND W-4 MOVED ALL TWELVE, which none of the others did: the blob a column's chunk is
    /// serialized into is rented rather than allocated. It was `new byte[total]` once per column per
    /// chunk -- 20 arrays and 305 kio for the 65 536-row rewrite below, 950 and 5,1 Mio for a
    /// million-row `varbinview` -- and none of it outlives the `WriteAsync` that consumes it.
    /// `high_cardinality_i64_r8193` -32,8 %, `alprd` -20,5 %, `map` -14,8 %, `alp` -12,5 %,
    /// `zoned_many_zones_nulls` -12,7 % (-305 560 B), then -0,5 to -2,2 % on the other seven. The
    /// spread is the point's shape: a file moves in proportion to how many chunks x columns it
    /// writes, not to how large each one is -- no blob in any corpus file reaches the 85 kio LOH
    /// threshold the audit expected, and the win is Gen0 volume rather than LOH.
    ///
    /// AND FIVE WITH W-5, which is the largest of the three by an order of magnitude on the file it
    /// touches: a zstd frame is no longer copied out of the buffer it was compressed into.
    /// `zoned_many_zones_nulls` -35 600 B, `fsst` -2 784, `types/utf8_nullable_r1025` -2 224,
    /// `onpair` -272, `zstd` -144.
    ///
    /// FIVE OTHERS WENT UP BY 32 TO 128 BYTES in the same change, and that is not noise: carrying
    /// the rental costs `PendingBuffer` two more fields, and a file that elects zstd nowhere pays
    /// for them without collecting anything. It is written here rather than absorbed silently
    /// because a ratchet whose floors drift upward unremarked is how the next one gets excused.
    ///
    /// AND W-7a TOOK TEN OF THE TWELVE DOWN AGAIN, by more than everything before it put together:
    /// the run scan no longer grows two `List<int>` to one entry per row on every column that has
    /// no runs. `zoned_many_zones_nulls` -4 828 112 B (-67 %), `map` -74 %, `high_cardinality` -57 %,
    /// `alp` -50 %. Only `delta` and `pco` do not move, and they are the two whose columns the
    /// sequence detector claims before the run scan ever runs.
    ///
    /// W-6 TOOK 587 096 BYTES OFF `zoned_many_zones_nulls` ALONE -- 119,4 to 110,4 B/row, the
    /// largest single move any of these has made -- by not encoding an ALP column into a `long[]`
    /// only to copy it into the `byte[]` the plan carries, and by renting the patch buffers instead
    /// of growing two `List`s. It is the only one of the original ten with an f64 column, which is
    /// why `alp` and `alprd` are now axes of their own.
    /// <para>
    /// STAGE 4 OF docs/11-write-strategy.md §8 TOOK FIVE MORE DOWN, on 2026-09-15, by not allocating
    /// what is immediately overwritten and not copying what already exists: a canonical buffer is
    /// recorded as a VIEW instead of a <c>ToArray()</c>, and the packed output, the varbin heap, its
    /// offsets and the three arena index buffers are allocated uninitialized.
    /// `high_cardinality_i64_r8193` **135 104 -&gt; 69 736 B** (-48 %), `alprd` 128 536 -&gt; 95 968
    /// (-25 %), `onpair` 223 880 -&gt; 220 048, `utf8_nullable_r1025` 216 632 -&gt; 214 752,
    /// `zoned_many_zones_nulls` 2 105 848 -&gt; 2 100 400.
    /// </para>
    /// <para>
    /// ONE CEILING WENT UP, and it is the only one in this file's history. `encodings/variant`
    /// 63 600 -&gt; **64 700**, because the column writers became a TREE (§3.0): a variant's canonical
    /// form is a two-field struct, so that file now keeps three summarizing nodes where it kept one,
    /// at **+320 B** of fixed per-column state. What it buys is on the same file: `variant` **9,24
    /// -&gt; 3,58** and `parquet_variant` **2,06 -&gt; 0,94** on the write axis, because the leaves
    /// were the columns and nothing had ever measured them. The cost is per COLUMN and not per row —
    /// 15,4 to 15,6 B/row — which is the distinction this file exists to make.
    /// </para>
    /// <para>
    /// The same change took the others DOWN, by shrinking <c>BlockStats</c> from 88 bytes to 56: its
    /// three bound domains are mutually exclusive, so they share two words. `zoned_many_zones_nulls`
    /// 2 100 400 -&gt; 2 098 360, and every file a little.
    /// </para>
    /// </remarks>
    // 12e (2026-09-16) : `Auto` PAR DÉFAUT (10 §5.5), et les plafonds ci-dessous qui bougent le
    // portent : l'écrivain d'index et ses tableaux par colonne, un constructeur Bloom par colonne
    // qu'il indexe (deux ensembles de hachages dont les tables viennent du pool, une file, deux
    // listes), la raison d'un abandon, les entrées du rapport, et pour un fichier à dictionnaire le
    // répertoire. Par fichier et par colonne, jamais par ligne : le garde-fou par ligne ne bouge
    // pas. Les filtres eux-mêmes ne sont plus construits pour une colonne qu'`Auto` va abandonner --
    // l'abandon se décide au premier bloc, sur les octets bruts -- sans quoi high_cardinality
    // prenait 17 kB.
    private static readonly (string Id, long Ceiling)[] Files =
    [
        ("containers/zoned_many_zones_nulls", 2_130_000),   // 2 098 360 mesurés
        ("distributions/high_cardinality_i64_r8193", 73_700),   // 73 256 mesurés (12e) : +1,3 kB, Auto. Était 72 600 : 71 936 mesurés (11a, 2026-09-16) : +2,2 kB par fichier pour le segment de statistiques de fichier -- un FlatBufferBuilder, un ScalarStore, les bornes en protobuf -- par fichier, pas par ligne. Était 70 800 (69 736 mesurés, -48 %)
        ("encodings/fsst", 235_800),   // 235 320 mesurés (12e) : Auto ; était 235 100
        ("encodings/onpair", 224_400),   // 223 904 mesurés (12e) : Auto ; était 223 400 (220 048 mesurés)
        ("types/utf8_nullable_r1025", 219_200),   // 218 736 mesurés (12e) : Auto ; était 218 000 (214 752 mesurés)

        // THE LATE COMPONENTS, on the write side, for PERF-AUDIT-v2.md F2's reason: `fastlanes.delta`,
        // `vortex.pco`, `vortex.zstd`, `vortex.map` and `vortex.variant` were watched by no
        // allocation ratchet on either side. Note that what is written here is the CANONICAL form
        // of each file -- our compressor picks the encoding, it does not preserve the source's --
        // so these axes measure "what does writing this SHAPE of data cost", which is the question
        // a ratchet can answer. Whether our writer re-elects the same encoding is a different
        // question and `bench/crosscheck.sh` is where it is asked.
        ("encodings/fastlanes_delta", 66_100),   // 65 632 mesurés (12e) : +1,0 kB, Auto. Était 64 700 : 64 584 mesurés (11a) : +1,9 kB par fichier, le segment de statistiques ; était 62 900
        ("encodings/pco", 67_600),   // 67 440 mesurés seul, et dans la suite sous DOTNET_TieredPGO=0 ; 67 512 dans la suite avec la PGO dynamique depuis le tri des runs par entiers (§1.21, 2026-09-17) : la mesure est globale au processus, et l'écart suit l'instrumentation, pas l'écrivain, dont le chemin par défaut n'a pas changé. Était 67 500 : 67 096 mesurés (12e) : Auto. Était 66 200 : 66 048 mesurés (11a) : idem ; était 64 400
        ("encodings/zstd", 225_000),   // 224 592 mesurés (12e) : Auto ; était 223 400
        ("encodings/map", 105_500),   // 104 952 mesurés (28a, 2026-09-17) : +2 984 B, les trois nœuds que l'arbre des colonnes gagne sous une map -- les entrées, la clé, la valeur (11 §3.2.4) -- chacun avec ses listes de blocs, sa ligne précédente, et le curseur de fenêtre de la map ; par colonne, pas par ligne. Ce qu'ils achètent : l'écriture de `map` à 0,893 de HEAD sur l'axe 1M, `list` 0,901, `listview` 0,911, octets identiques. Les autres fichiers prennent +8 B, le compteur du rédacteur. Était 102 500 : 102 008 mesurés (12e) : Auto. Était 101 600 : 101 368 mesurés (11a) : +1,2 kB par fichier, le segment de statistiques ; était 100 200
        ("encodings/variant", 68_200),   // 67 792 mesurés (12e) : Auto. Était 67 300 : 67 128 mesurés (11a, 2026-09-16) : +1,8 kB par fichier, le segment de statistiques de fichier. Était 65 400 : 65 336 mesurés (étape 8d, 2026-09-16) : +32 B pour deux champs de référence par ScanContext -- le masque de blocs vivants et le puits de métriques du contrat de lecture (8b, 8d) -- sur les deux contextes de transit que l'écrivain instancie ; par fichier, pas par ligne, pour un état qu'il n'utilise pas (un contexte réduit à l'arène est le correctif si ça compte un jour). Était 65 300 (65 232 mesurés, R5a : +32 B pour le champ PlanMemory? de trois ColumnWriter), 65 200 (R2 : +436 B pour trois DistinctTable), 64 700 (63 920 : +320 B pour deux ColumnWriter de plus)

        // THE TWO ALP SHAPES, added with W-6 because that point moved them and nothing watched it:
        // `alp` is a column ALP fits, `alprd` is one built to defeat it so that every row becomes a
        // patch. The second is the case that made the patch buffers worth renting, and a ratchet
        // that only held the easy shape would have said nothing about it.
        ("encodings/alp", 121_900),   // 121 480 mesurés (12e) : +1,0 kB, Auto. Était 120 600 : 120 432 mesurés (11a) : +0,9 kB par fichier, le segment de statistiques ; était 119 600
        ("encodings/alprd", 99_900),   // 99 480 mesurés (12e) : Auto. Était 98 300 : 98 120 mesurés (11a) : idem ; était 97 400 (95 968 mesurés, -25 %)
    ];

    // FOUR OF THESE FIVE CAME DOWN AGAIN WHEN FSST STOPPED ALLOCATING WHAT IT THROWS AWAY.
    // Pricing FSST means training a table and compressing the whole column, and on a column it
    // loses -- which is the common case, because it is priced against zstd and against the plain
    // form -- the heap, the row table and the code stream are all garbage the moment it returns
    // null. Rented instead of allocated, with a row as two ints rather than a
    // `ReadOnlyMemory<byte>` in a `List`:
    //
    //     containers/zoned_many_zones_nulls   12 061 328 B -> 7 866 272 B   -35%
    //     encodings/fsst                       1 148 096 B ->   303 624 B   -74%
    //     types/utf8_nullable_r1025              490 944 B ->   298 624 B   -39%
    //     encodings/onpair                       495 408 B ->   365 392 B   -26%
    //
    // The corpus still rewrites to 9 942 348 bytes, unchanged to the byte: the sampler draws the
    // same lines and the trainer reaches the same tables.

    // FOUR OF THESE FIVE WENT UP WHEN REPARTITIONING LANDED, and that is a trade rather than a
    // regression, so it is written down rather than rounded over. A writer that buffers rows needs
    // an arena to buffer them in, and the rows it buffers are materialized into it -- a fixed cost
    // per FILE, plus a second materialization when several batches are concatenated into one chunk.
    // On a file whose rows fit in one block that cost is all there is, and it is worth 3% to 20%:
    //
    //     containers/zoned_many_zones_nulls   32 172 440 B -> 12 059 208 B   -62%
    //     distributions/high_cardinality         381 512 B ->    463 704 B   +22%
    //     encodings/fsst                       1 129 768 B ->  1 148 096 B    +2%
    //     encodings/onpair                       477 008 B ->    495 408 B    +4%
    //     types/utf8_nullable_r1025              472 544 B ->    490 944 B    +4%
    //
    // What it buys, on the file large enough to have chunks to save: 64 chunks become 3, which is
    // 20 MB of write allocation and -76% of the allocation a SCAN of that file costs
    // (RewrittenComparison: 147 510 B -> 35 437 B). Setting `RowBlockSize = null` restores the old
    // figures exactly, for a caller whose batches are already its chunking.

    /// <summary>
    /// A shape guard, in bytes per row, over and above each file's own ceiling.
    /// </summary>
    /// <remarks>
    /// The per-file ceilings catch a regression on these five files. This catches the thing they
    /// cannot: a cost that scales with the DATA rather than the schema would pass every per-file
    /// ceiling the day it was set and fail on the first larger file anyone wrote. Set well above
    /// the worst current figure rather than near it, because it is not the tight bound - it is the
    /// bound that says "still roughly proportional to what it was".
    /// </remarks>
    private const double PerRowCeiling = 600.0;

    [Fact]
    public async Task WritingAllocatesWithinItsPerRowCeiling()
    {
        ReleaseOnlyCeilings.Require();
        Decoders.EnsureRegistered();

        StringBuilder report = new StringBuilder("WRITE ALLOCATIONS: floor of ")
            .Append(Runs.ToString(CultureInfo.InvariantCulture))
            .Append(" runs after ")
            .Append(Warmup.ToString(CultureInfo.InvariantCulture))
            .Append(" warm-ups\n");

        List<string> over = [];
        foreach ((string id, long ceiling) in Files)
        {
            string path = Corpus.Path(id);
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            (long floor, long rows) = await Measure(path);
            double perRow = rows == 0 ? 0 : (double)floor / rows;
            report.Append("    ")
                .Append(id.PadRight(42))
                .Append(rows.ToString(CultureInfo.InvariantCulture).PadLeft(7))
                .Append(" rows  ")
                .Append(floor.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                .Append(" B  ")
                .Append(perRow.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7))
                .Append(" B/row   ceiling ")
                .Append(ceiling.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                .Append('\n');

            if (floor > ceiling)
            {
                over.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{id} allocated {floor} B against a ceiling of {ceiling}"));
            }

            if (perRow > PerRowCeiling)
            {
                over.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{id} allocated {perRow:F1} B/row against the shape guard of {PerRowCeiling:F1}"));
            }
        }

        Console.Out.Write(report.ToString());
        Assert.True(over.Count == 0, string.Join("\n", over) + "\n" + report);
    }

    /// <summary>Rewrites one file through a discarding sink and returns the floor and its rows.</summary>
    private static async Task<(long Floor, long Rows)> Measure(string path)
    {
        long rows = 0;
        for (int i = 0; i < Warmup; i++)
        {
            rows = await Rewrite(path);
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            rows = await Rewrite(path);
            floor = Math.Min(floor, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return (floor, rows);
    }

    /// <summary>
    /// Reads every batch of a file and writes it back out, which is the writer's whole job.
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetTotalAllocatedBytes"/> rather than the per-thread counter
    /// <see cref="Vorticity.Tests.Scan.PathAllocationTests"/> uses: writing goes through
    /// <c>WriteAsync</c>, and a sink is entitled to complete asynchronously on another thread even
    /// when this one does not. Process-wide is the safe direction to be wrong in - it can only
    /// over-count - and the class runs alone, so there is nothing else to over-count.
    ///
    /// The READ half is inside the measurement and cannot be subtracted without a second harness.
    /// PathAllocationTests prices it: 190 672 B for a full scan of the largest file here. The
    /// figures below are therefore an upper bound on what writing costs, which is the honest
    /// direction for a ceiling.
    /// </remarks>
    private static async Task<long> Rewrite(string path)
    {
        long rows = 0;
        await using VortexFile source = await VortexFile.OpenAsync(path, CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(new NullSink(), source.Schema);

        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return rows;
    }

    /// <summary>A sink that counts and keeps nothing, so the writer is what gets measured.</summary>
    private sealed class NullSink : ISegmentSink
    {
        public long Position { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
