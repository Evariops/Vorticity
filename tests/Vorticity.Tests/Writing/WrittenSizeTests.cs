// How big our output is, next to the reference's, over the whole corpus.
//
// docs/05-benchmarks.md §3 states the write target as "output size <= 105% of Rust's on the same
// data, edition and configuration, with the delta reported per dataset", and explains why it is not
// byte-parity: the compressor is a sampler, so two honest implementations diverge on borderline
// data. We are nowhere near 105% yet, which is exactly why this needs to be a measurement rather
// than a remembered figure - docs/90-registry.md quotes ratios that nothing reproduces.
//
// SO THIS IS A RATCHET, NOT A GATE. Asserting 105% today would be a permanently red test that
// everyone learns to ignore; asserting nothing would let the ratio drift upward unnoticed. The
// ceiling is set just above the measured value, so the test fails on a regression and passes on
// every improvement - and the ceiling is lowered by hand when the improvement lands, which is the
// only part a human should have to do.
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

public sealed class WrittenSizeTests
{
    /// <summary>
    /// The whole-corpus ceiling. Lower it when a compression improvement lands; never raise it
    /// without saying in the commit message what got bigger and why that is acceptable.
    ///
    /// Now BELOW the 1.05 target rather than above it, so this has stopped being a ratchet on a
    /// failing number and become a guard on a passing one: the target itself would no longer
    /// notice a regression.
    /// </summary>
    /// <remarks>
    /// 0.67 -> 0.65 on 2026-09-15 (BENCH-AUDIT.md B18). Measured that day: 856 files, 10 254 248
    /// bytes against the reference's 15 984 453, ratio **0.641514**. At 0.67 that was **4,44 % of
    /// slack** -- the improvements had landed and the hand had not followed, which is the same
    /// species as B13 and B14: a reference that does not come down stops guarding.
    /// <para>
    /// 0.65 leaves **1,32 %**, so a 1,5 % size regression is red. Not tighter on purpose: 0.645
    /// would leave 0,54 % and would trip on a corpus addition rather than on a regression, and
    /// 0.651 would sit exactly on the criterion with nothing in hand.
    /// </para>
    /// <para>
    /// 0.65 -> **0.64 on 2026-09-15**, during stage 2b of docs/11-write-strategy.md §8. Measured that
    /// day: 856 files, **10 084 008** bytes against 15 984 453, ratio **0,631**. The gain is not
    /// stage 2b's — it was already there when the stage started, earned by W-31, W-33 and W-35 — and
    /// that is exactly the complaint B18 makes: a reference that does not come down stops guarding.
    /// 0.65 had grown back to **2,9 %** of slack. 0.64 leaves **1,43 %**, the same margin the
    /// paragraph above argues for.
    /// </para>
    /// </remarks>
    private const double CorpusCeiling = 0.64;

    /// <summary>How many of the worst offenders to name, so the number is actionable.</summary>
    private const int Worst = 12;

    /// <summary>
    /// Rows per chunk for the multi-chunk axis. BENCH-AUDIT.md B17.
    /// </summary>
    /// <remarks>
    /// THE CORPUS SWEEP ABOVE IS BLIND TO EVERYTHING THAT PLAYS BETWEEN THE CHUNKS OF A COLUMN.
    /// <c>VortexWriteOptions.RowBlockSize</c> is 8192 and the corpus's largest file is 8 193 rows,
    /// so **2 files out of 856** write more than one chunk for any field -- eight (file, field)
    /// pairs in the whole corpus. An FSST table shared between chunks, a dictionary carried across
    /// them, a zone statistic amortized over them: none of it can show a gain or trip a regression
    /// there, in either direction. That is not theoretical -- it is what made W-21 fall, its +4,3 %
    /// having been measured in <c>aed1901</c>, before RowBlockSize existed and when one WriteAsync
    /// was one chunk.
    /// <para>
    /// 512 rows is set by the SMALLEST file on this axis, not by the largest: `encodings/fsst` and
    /// `encodings/dict` have 4 096 rows, so 1024 gave them four chunks and the axis would have been
    /// measuring the single-chunk path on half its entries. At 512 they write eight, and the
    /// 8 193-row text files write seventeen.
    /// </para>
    /// </remarks>
    private const int ChunkedRowBlock = 512;

    /// <summary>
    /// The multi-chunk axis, byte-exact. LOWER A NUMBER BY HAND when a change earns it, in the
    /// same commit, and never raise one without saying what grew and why.
    /// </summary>
    /// <remarks>
    /// Exact rather than a ceiling, unlike <see cref="CorpusCeiling"/>: written size is
    /// deterministic for a given build, and the whole point of this axis is that a change confined
    /// to the multi-chunk path MOVES A NUMBER. A ceiling with slack would reintroduce the blindness
    /// it exists to remove.
    /// </remarks>
    /// <remarks>
    /// THE FOUR NUMBERS ROSE ON 2026-09-15, by 128, 128, 56 and 56 bytes, and the cause is the one
    /// thing on this axis that is supposed to grow: docs/11-write-strategy.md §8 stage 1 made a zone
    /// a BLOCK of <c>RowBlockSize</c> rows counted from row 0 instead of a chunk. These four write
    /// one chunk (the 1 MiB byte target is never reached at 512 rows a batch) and therefore carried
    /// ONE zone for the whole file; they now carry 17, 17, 8 and 8, which is <c>ceil(rows / 512)</c>
    /// -- the granularity the caller asked for when it set <c>RowBlockSize = 512</c> and did not get.
    /// Eight bytes a zone per column for the two utf8 files (null count only, no bounds on a string
    /// column) and seven bytes a zone for the other two.
    /// <para>
    /// It is a size increase that buys pruning, which is the only kind this table accepts; the
    /// whole-corpus ratio above did not move, because the default 8192-row block leaves every corpus
    /// file at one or two zones.
    /// </para>
    /// </remarks>
    private static readonly (string Id, long Bytes)[] Chunked =
    [
        // +138 B (12e) : Auto par défaut, et la colonne est encodée en dictionnaire -- son entrée
        // dict.probe (10 §5.3), gratuite à l'écriture, et le répertoire d'index qui la porte avec
        // son entrée de métadonnées au postscript. Un fichier dont Auto ne garde rien n'a pas de
        // répertoire et ne bouge pas.
        ("types/utf8_nonnull_r8193", 16_822),    // 17 chunks in, 17 zones; +96 B since the file statistics segment (11a); +138 B, the dictionary probe (12e)
        ("types/utf8_nullable_r8193", 17_302),   // 17 chunks in, 17 zones; +96 B, same; +138 B, same
        // 4 700 -> 3 756 (-20,1 %) le 2026-09-15, WRITE-AUDIT.md W-31 : la copie du reste reporte
        // ne materialise plus que les octets que les vues nomment, donc le tas ecrit ne porte plus
        // les chaines des blocs deja emis. Verifie par bench/crosscheck.sh : 854 fichiers relus par
        // Vortex Rust, scalaire par scalaire. C'est le seul des quatre qui bouge -- les trois autres
        // n'ont pas de VarBinView dans leur chemin d'ecriture.
        ("encodings/fsst", 3_908),               // 8 chunks in, 8 zones; +96 B, same
        ("encodings/dict", 3_054),               // 8 chunks in, 8 zones; +96 B, same; +138 B, the dictionary probe (12e)
    ];

    [Fact]
    public async Task TheCorpusRewritesWithinTheSizeRatchet()
    {
        Decoders.EnsureRegistered();

        long ours = 0;
        long theirs = 0;
        int files = 0;
        List<(string Id, double Ratio, long Ours, long Theirs)> entries = [];

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            long reference = new FileInfo(entry.Path).Length;
            long written = await Rewrite(entry);
            ours += written;
            theirs += reference;
            files++;
            if (reference > 0)
            {
                entries.Add((entry.Id, (double)written / reference, written, reference));
            }
        }

        entries.Sort((a, b) => b.Ours - b.Theirs == a.Ours - a.Theirs
            ? b.Ratio.CompareTo(a.Ratio)
            : (b.Ours - b.Theirs).CompareTo(a.Ours - a.Theirs));

        StringBuilder report = new StringBuilder();
        double ratio = (double)ours / theirs;
        report.Append("WRITTEN SIZE: ")
            .Append(files.ToString(CultureInfo.InvariantCulture))
            .Append(" files, ")
            .Append(ours.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes against the reference's ")
            .Append(theirs.ToString(CultureInfo.InvariantCulture))
            .Append(" -- ratio ")
            .Append(ratio.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" (target 1.05, ceiling ")
            .Append(CorpusCeiling.ToString("F2", CultureInfo.InvariantCulture))
            .Append(")\n");

        // Ranked by BYTES LOST, not by ratio: a 40x ratio on a 300-byte file is a rounding error,
        // and optimizing for it would be the most expensive way to move the total by nothing.
        report.Append("  worst by bytes lost:\n");
        for (int i = 0; i < Math.Min(Worst, entries.Count); i++)
        {
            (string id, double each, long mine, long reference) = entries[i];
            report.Append("    ")
                .Append(id.PadRight(46))
                .Append((mine - reference).ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(" bytes  ")
                .Append(each.ToString("F2", CultureInfo.InvariantCulture))
                .Append("x  (")
                .Append(mine.ToString(CultureInfo.InvariantCulture))
                .Append(" vs ")
                .Append(reference.ToString(CultureInfo.InvariantCulture))
                .Append(")\n");
        }

        Console.Out.Write(report.ToString());

        Assert.True(files > 700, $"only {files} files were measured");
        Assert.True(
            ratio <= CorpusCeiling,
            $"the corpus rewrites at {ratio:F3}x, above the {CorpusCeiling:F2}x ratchet.\n{report}");
    }

    /// <summary>
    /// Four files rewritten at <see cref="ChunkedRowBlock"/> rows per chunk, byte-exact.
    /// </summary>
    /// <remarks>
    /// This is the axis BENCH-AUDIT.md B17 opened: the corpus sweep cannot see the multi-chunk
    /// path, so a change confined to it is invisible to the only size oracle this repository has.
    /// Here every column of every file is in nine chunks or more, and the assertion is equality.
    /// </remarks>
    [Fact]
    public async Task TheMultiChunkPathWritesTheSameBytes()
    {
        Decoders.EnsureRegistered();

        StringBuilder report = new StringBuilder("MULTI-CHUNK SIZE: ")
            .Append(ChunkedRowBlock.ToString(CultureInfo.InvariantCulture))
            .Append(" rows per chunk\n");
        List<string> moved = [];

        foreach ((string id, long expected) in Chunked)
        {
            CorpusEntry entry = CorpusManifest.Get(id);
            (long written, int chunks) = await RewriteChunked(entry);
            report.Append("    ")
                .Append(id.PadRight(34))
                .Append(written.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(" bytes  expected ")
                .Append(expected.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append("  chunks ")
                .Append(chunks.ToString(CultureInfo.InvariantCulture))
                .Append('\n');

            if (written != expected)
            {
                moved.Add($"{id}: {written} bytes, expected {expected} ({written - expected:+#;-#;0})");
            }

            // Eight or more is what makes this axis different from the sweep; below that it would
            // be measuring the same single-chunk path twice.
            Assert.True(chunks >= 8, $"{id} wrote {chunks} chunks; this axis needs 8 or more");
        }

        Console.Out.Write(report.ToString());
        Assert.True(
            moved.Count == 0,
            $"the multi-chunk path no longer writes the same bytes:\n  {string.Join("\n  ", moved)}\n{report}");
    }

    /// <summary>Rewrites one file in small chunks; returns its size and how many batches went in.</summary>
    private static async Task<(long Bytes, int Chunks)> RewriteChunked(CorpusEntry entry)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-chunked-{Guid.NewGuid():N}.vortex");
        VortexWriteOptions options = new VortexWriteOptions { RowBlockSize = ChunkedRowBlock };
        try
        {
            int chunks = 0;
            await using (VortexFile source = await VortexFile.OpenAsync(
                entry.Path, OpenOptionsFor(entry), CancellationToken.None))
            await using (VortexFileWriter writer =
                VortexFileWriter.Create(written, source.Schema, options))
            {
                await foreach (RecordBatch batch in source.Scan()
                    .WithMaxBatchRows(ChunkedRowBlock).ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    chunks++;
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return (new FileInfo(written).Length, chunks);
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    /// <summary>Writes one corpus file out and returns how many bytes it took.</summary>
    private static async Task<long> Rewrite(CorpusEntry entry)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-size-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(
                entry.Path, OpenOptionsFor(entry), CancellationToken.None))
            await using (VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema))
            {
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return new FileInfo(written).Length;
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    /// <summary>Open options for one entry: the schema out of band when the file has none.</summary>
    /// <param name="entry">The corpus entry about to be opened.</param>
    /// <remarks>
    /// <c>types/no_dtype_segment</c> reached this sweep only when <c>vortex.map</c> gained a decoder
    /// and the file became in-scope. Opening it without a DType is a <c>VortexFormatException</c> by
    /// contract §7.4, so the donor is a real corpus file with the identical schema.
    /// </remarks>
    private static VortexOpenOptions OpenOptionsFor(CorpusEntry entry) =>
        entry.HasDTypeSegment
            ? VortexOpenOptions.Default
            : new VortexOpenOptions { DType = OutOfBandSchema.Value };

    private static readonly Lazy<Vorticity.Types.DType> OutOfBandSchema =
        new Lazy<Vorticity.Types.DType>(static () =>
        {
            VortexFile donor = VortexFile
                .OpenAsync(
                    CorpusManifest.Get("types/user_metadata_segments").Path,
                    VortexOpenOptions.Default,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return donor.Schema;
        });
}
