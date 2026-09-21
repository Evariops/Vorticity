// How big our output is, next to the reference's, over the whole corpus.
//
// The write target is an output no larger than 105% of Rust's on the same data, edition and
// configuration, with the delta reported per dataset -- not byte-parity: the compressor is a
// sampler, so two honest implementations diverge on borderline data. The ratio is measured here
// rather than remembered: a remembered ratio is one that nothing reproduces.
//
// SO THIS IS A RATCHET, NOT A GATE. Asserting 105% would miss a regression that stays under the
// target, and asserting nothing would let the ratio drift upward unnoticed. The
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
    /// The ceiling sits BELOW the 1.05 target, so this is a guard on a passing number rather
    /// than a ratchet on a failing one: the target itself would not notice a regression.
    /// </summary>
    /// <remarks>
    /// A ceiling that stays put while improvements land stops guarding: the slack it leaves grows
    /// until a regression fits inside it, so the ceiling comes down with every gain.
    /// <para>
    /// Not tighter on purpose: a ceiling closer to the measured ratio would trip on a corpus
    /// addition rather than on a regression. Not looser either: the headroom must stay smaller
    /// than the size regression it is there to turn red.
    /// </para>
    /// <para>
    /// The file's identity in every postscript -- sixteen bytes of value, the entry's key and
    /// segment in the postscript, and the padding they move: 72 to 96 bytes a file -- is growth
    /// this ceiling accepts, because it is what lets an index or a dataset prove which bytes it
    /// describes without reading them.
    /// </para>
    /// </remarks>
    private const double CorpusCeiling = 0.65;

    /// <summary>How many of the worst offenders to name, so the number is actionable.</summary>
    private const int Worst = 12;

    /// <summary>
    /// Rows per chunk for the multi-chunk axis.
    /// </summary>
    /// <remarks>
    /// THE CORPUS SWEEP ABOVE IS BLIND TO EVERYTHING THAT PLAYS BETWEEN THE CHUNKS OF A COLUMN.
    /// <c>VortexWriteOptions.RowBlockSize</c> is 8192 and the corpus's largest file is 8 193 rows,
    /// so **2 files out of 856** write more than one chunk for any field -- eight (file, field)
    /// pairs in the whole corpus. An FSST table shared between chunks, a dictionary carried across
    /// them, a zone statistic amortized over them: none of it can show a gain or trip a regression
    /// there, in either direction. That is not theoretical: a gain measured where one
    /// WriteAsync is one chunk says nothing about a writer that cuts its own blocks.
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
    /// A ZONE IS A BLOCK of <c>RowBlockSize</c> rows counted from row 0, not a chunk. These
    /// four write one chunk (the 1 MiB byte target is never reached at 512 rows a batch) and
    /// carry 17, 17, 8 and 8 zones, which is <c>ceil(rows / 512)</c> -- the granularity the
    /// caller asks for when it sets <c>RowBlockSize = 512</c>. Eight bytes a zone per column for
    /// the two utf8 files (null count only, no bounds on a string column) and seven bytes a zone
    /// for the other two.
    /// <para>
    /// It is a size increase that buys pruning, which is the only kind this table accepts; the
    /// whole-corpus ratio above did not move, because the default 8192-row block leaves every corpus
    /// file at one or two zones.
    /// </para>
    /// </remarks>
    private static readonly (string Id, long Bytes)[] Chunked =
    [
        // Besides the columns, these sizes carry: on a dictionary-encoded column, the dict.probe
        // entry Auto writes by default, free at write time, with the index directory that holds it
        // and its metadata entry in the postscript (138 B; a file where Auto keeps nothing has no
        // directory); the file's identity in every postscript -- 16 bytes of value, the entry's
        // key and segment, and the padding they move (72 or 96 B); and the index directory's
        // XXH3-64 trailer, on the files that carry one (8 B).
        ("types/utf8_nonnull_r8193", 16_902),    // 17 chunks in, 17 zones; including the file statistics segment (96 B), the dictionary probe (138 B), the identity (72 B) and the directory checksum (8 B)
        ("types/utf8_nullable_r8193", 17_382),   // 17 chunks in, 17 zones; including the same four
        // The copy of the carried remainder materializes only the bytes its views name, so the
        // heap written for a chunk never carries the strings of blocks already emitted.
        ("encodings/fsst", 4_004),               // 8 chunks in, 8 zones; including the file statistics segment (96 B) and the identity (96 B)
        ("encodings/dict", 3_134),               // 8 chunks in, 8 zones; including the file statistics segment (96 B), the dictionary probe (138 B), the identity (72 B) and the directory checksum (8 B)
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
    /// This axis exists because the corpus sweep cannot see the multi-chunk path, so a change
    /// confined to it is invisible to the only size oracle this repository has.
    /// Here every column of every file is in eight chunks or more, and the assertion is equality.
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
    /// <c>types/no_dtype_segment</c> is in scope and carries no DType segment. Opening it without a
    /// DType is a <c>VortexFormatException</c> by contract, so the donor is a real corpus file with
    /// the identical schema.
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
