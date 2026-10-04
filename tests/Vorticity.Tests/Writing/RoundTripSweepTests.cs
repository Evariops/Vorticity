// The round trip over the WHOLE in-scope corpus, not nine hand-picked files.
//
// Nine files test the shapes whoever wrote the list thought of. The corpus has every dtype crossed
// with nullability crossed with the row counts where block boundaries live, plus the container
// shapes no type matrix reaches -- and a writer fails on exactly the shape nobody listed. So this
// sweeps all of them and reports every failure at once rather than stopping at the first, because
// "the writer is broken" is useless next to "the writer is broken on lists and on nothing else".
//
// A shape the writer cannot serialize is a FAILURE here, not a skip. The point of a sweep is that
// its coverage is the corpus's rather than the author's, and a silent skip list would put the
// author back in charge of it.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RoundTripSweepTests
{
    [Fact]
    public async Task EveryInScopeCorpusFileSurvivesAWriteAndAReadBack()
    {
        Decoders.EnsureRegistered();

        StringBuilder failures = new StringBuilder();
        int checkedFiles = 0;
        int rows = 0;

        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            try
            {
                rows += await RoundTrip(entry);
                checkedFiles++;
            }
            catch (Exception error) when (failures.Length < 12_000)
            {
                failures.Append(entry.Id).Append(": ")
                    .Append(error.GetType().Name).Append(" - ")
                    .Append(error.Message).Append('\n');
            }
        }

        Console.Out.Write(
            "WRITER SWEEP: " + checkedFiles.ToString(CultureInfo.InvariantCulture) + " corpus files " +
            "written and read back, " + rows.ToString(CultureInfo.InvariantCulture) +
            " rows compared value for value.\n");

        // On the console as well as in the assertion: xunit truncates a long string comparison at
        // fifty characters, which on a sweep over 813 files is one file's name and the first word
        // of its exception.
        if (failures.Length > 0)
        {
            Console.Out.Write(failures.ToString());
        }

        Assert.Equal(string.Empty, failures.ToString());
        Assert.True(checkedFiles > 700, $"only {checkedFiles} files round-tripped");
    }

    [Fact]
    public async Task WritesTheCorpusOutForTheRustCrossCheck()
    {
        // The .NET half of Rust reading what we write: produce the files, and let
        // `cargo run --example verify_written` decide whether the reference agrees with them. It is
        // env-gated because it costs a full corpus write and only the cross-check consumes the
        // output -- and it SKIPS rather than passes when the variable is absent, so a CI job that
        // forgets to set it does not look like a green cross-check.
        string? root = Environment.GetEnvironmentVariable("VORTICITY_WRITE_CORPUS");
        Assert.SkipWhen(
            string.IsNullOrEmpty(root),
            "Set VORTICITY_WRITE_CORPUS to a directory to produce files for the Rust cross-check.");

        Decoders.EnsureRegistered();
        Directory.CreateDirectory(root!);

        int written = 0;
        int indexed = 0;
        int appended = 0;
        int afterTheFact = 0;
        int stringBounded = 0;
        int tables = 0;
        int compacted = 0;
        int apart = 0;
        Dictionary<string, int> built = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            // NOT WRITTEN FOR THE CROSS-CHECK, and the reason is the verifier's, not ours. Our
            // output for types/no_dtype_segment is fine - RoundTrip reads it back with default
            // options - but `verify_written` opens the REFERENCE file to compare against, and that
            // one has no dtype segment by construction, so the Rust side fails at open. Supplying a
            // schema is a capability the example does not have; teaching it one is the better fix.
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            string destination = Path.Combine(root!, entry.Id.Replace('/', Path.DirectorySeparatorChar) + ".vortex");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using VortexFile source = await VortexFile.OpenAsync(
                entry.Path, await CorpusSweep.OpenOptionsForAsync(entry.Path), CancellationToken.None);
            // WITH INDEXES, so that the whole corpus crosses the rule that a file with an index
            // opens and scans in a strict Rust 0.86.1 reader, without error and without
            // configuration. The files take four policies in turn, budget lifted: a Bloom
            // filter at all three resolutions on every column, postings on every column, sorted
            // runs cut into small segments on every column, and `Auto` with a Bloom on every other
            // field -- which puts payload regions BETWEEN the data chunks, where no layout
            // references them. The data the verifier compares is the default write's.
            WritePolicy policy = (written % 6) switch
            {
                0 => WritePolicy.None.WithDefault(IndexSpec.Bloom(resolutions: 3)),
                1 => WritePolicy.None.WithDefault(IndexSpec.Postings),
                2 => WritePolicy.None.WithDefault(IndexSpec.SortedRuns.WithSegmentEntries(500)),
                4 => WritePolicy.None.WithDefault(IndexSpec.NgramBloom(resolutions: 3)),
                5 => WritePolicy.None.WithDefault(IndexSpec.NgramPostings(caseInsensitive: true).WithSegmentEntries(500)),
                _ => WritePolicy.Auto,
            };
            if (written % 6 == 3 && source.DType.Kind == DTypeKind.Struct)
            {
                for (int field = 1; field < source.DType.FieldCount; field += 2)
                {
                    policy = policy.For(source.DType.GetFieldName(field), IndexSpec.Bloom(resolutions: 3));
                }
            }

            // ONE TABLE IN THREE IS APPENDED, AND ONE IN THREE INDEXED AFTER THE FACT, so that
            // Rust reads appended files back too: the first half written, closed, and the
            // rest appended -- inside a block, so the last chunk is re-opened -- or the whole file
            // written without indexes and the runs appended with a new directory and footer.
            bool tabular = source.DType.Kind == DTypeKind.Struct && source.DType.FieldCount > 0 && source.RowCount > 1;
            int mode = tabular ? (tables++ % 3) switch { 1 => 1, 2 => 3, _ => 0 } : 0;
            // STRING ZONE BOUNDS ON HALF THE FILES: the reference's 64 bytes on one in
            // four, and 5 on another, so that values are cut, characters straddle the cut and some
            // maxima have no bound. The verifier prunes with them.
            int stringBounds = (written % 4) switch { 0 => 64, 2 => 5, _ => 0 };
            VortexWriteOptions options = new VortexWriteOptions
            {
                WritePolicy = mode == 3 ? WritePolicy.None : policy,
                IndexBudgetPerMille = 1_000_000,
                StringBoundBytes = stringBounds,
            };
            if (stringBounds > 0 && HasStringField(source.DType))
            {
                stringBounded++;
            }
            // EVERY PLAIN TABLE OF THE WRITE-ONCE THIRD IS A COMPACTED DATASET OBJECT, so that
            // the cross-check reads compacted files, which the corpus holds few of: the rows
            // appended as three objects of an unclustered dataset, which compacts tiered -- a
            // concatenation, so the rows keep the order the verifier compares them in -- and the one
            // object the compaction wrote is what Rust reads.
            if (mode == 0 && source.RowCount >= 3 && PlainColumns(source.DType))
            {
                await System.IO.File.WriteAllBytesAsync(
                    destination, await CompactedAsync(source, options), TestContext.Current.CancellationToken);
                compacted++;
                indexed++;
                written++;
                continue;
            }

            // THE OTHER TABLES OF THE WRITE-ONCE THIRD CHUNK EVERY OTHER COLUMN APART, so that Rust
            // reads columns whose chunks end at different rows: chunks of the file a few blocks
            // long, and every other column gathering many of them into chunks of its own.
            if (mode == 0 && source.DType.FieldCount > 1)
            {
                System.Collections.Immutable.ImmutableDictionary<string, int> targets =
                    System.Collections.Immutable.ImmutableDictionary<string, int>.Empty;
                for (int field = 1; field < source.DType.FieldCount; field += 2)
                {
                    targets = targets.Add(source.DType.GetFieldName(field), 64 << 10);
                }

                options = options with { BlockRows = 256, ChunkTargetBytes = 4 << 10, ColumnChunkTargetBytes = targets };
                apart++;
            }

            IReadOnlyList<IndexWriteReport> indexes;
            int columns;
            long split = mode == 1 ? (source.RowCount / 2) + 1 : long.MaxValue;
            VortexFileWriter writer = VortexFileWriter.Create(destination, source.DType, options);
            try
            {
                long rows = 0;
                await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    if (rows >= split && split != long.MaxValue)
                    {
                        // The first piece is complete: close it and continue by an append.
                        await writer.CompleteAsync(CancellationToken.None);
                        await writer.DisposeAsync();
                        writer = await VortexFileWriter.AppendAsync(destination, options, CancellationToken.None);
                        split = long.MaxValue;
                    }

                    await writer.WriteAsync(batch, CancellationToken.None);
                    rows += batch.RowCount;
                }

                WriteReport report = await writer.CompleteAsync(CancellationToken.None);
                indexes = report.Indexes;
                columns = report.Columns.Length;
            }
            finally
            {
                await writer.DisposeAsync();
            }

            if (mode == 3)
            {
                indexes = await VortexFileIndexer.AppendIndexesAsync(
                    destination, policy, new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 }, CancellationToken.None);
            }

            appended += mode == 1 ? 1 : 0;
            afterTheFact += mode == 3 ? 1 : 0;

            // A struct with no field has no column to index, and so no directory.
            if (indexes.Count > 0 || columns == 0)
            {
                indexed++;
            }

            HashSet<string> kinds = [];
            foreach (IndexWriteReport index in indexes)
            {
                if (index.Outcome == IndexOutcome.Built && kinds.Add(index.Kind))
                {
                    built[index.Kind] = built.GetValueOrDefault(index.Kind) + 1;
                }
            }

            written++;
        }

        StringBuilder line = new StringBuilder("WROTE ")
            .Append(written.ToString(CultureInfo.InvariantCulture)).Append(" files to ").Append(root)
            .Append(", ").Append(indexed.ToString(CultureInfo.InvariantCulture)).Append(" of them with an index directory, ")
            .Append(appended.ToString(CultureInfo.InvariantCulture)).Append(" appended, ")
            .Append(afterTheFact.ToString(CultureInfo.InvariantCulture)).Append(" indexed after the fact, ")
            .Append(stringBounded.ToString(CultureInfo.InvariantCulture)).Append(" with string zone bounds, ")
            .Append(compacted.ToString(CultureInfo.InvariantCulture)).Append(" compacted from three dataset objects, ")
            .Append(apart.ToString(CultureInfo.InvariantCulture)).Append(" with columns chunked apart;");
        foreach ((string kind, int files) in built)
        {
            line.Append(' ').Append(kind).Append(": ").Append(files.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        Console.Out.Write(line.Append('\n').ToString());
        Assert.True(written > 700);
        Assert.Equal(written, indexed);
        Assert.True(appended > 15 && afterTheFact > 15, $"{appended} appended, {afterTheFact} indexed after the fact");
        Assert.True(stringBounded > 50, $"only {stringBounded} files carry string zone bounds");
        Assert.True(compacted > 10, $"only {compacted} files are compacted dataset objects");
        Assert.True(apart > 10, $"only {apart} files chunk their columns apart");

        // Enough files with payload regions between their chunks that the rule is tested, not assumed.
        foreach (string kind in new[] { IndexKinds.BloomSbbf, IndexKinds.PostingsBlocks, IndexKinds.SortedRuns })
        {
            int files = built.GetValueOrDefault(kind);
            Assert.True(files > 60, $"only {files} files carry {kind}");
        }

        // The text kinds find text in fewer files, and enough of them.
        foreach (string kind in new[] { IndexKinds.BloomNgram3, IndexKinds.PostingsNgram3 })
        {
            int files = built.GetValueOrDefault(kind);
            Assert.True(files > 10, $"only {files} files carry {kind}");
        }
    }

    /// <summary>
    /// Writes one corpus file out and reads it back, comparing every value: by digest against the
    /// plain scan of the original, and by line where a row differs. The file written is the one the
    /// size ratchet weighs, written once for both.
    /// </summary>
    /// <returns>How many rows were compared.</returns>
    private static async Task<int> RoundTrip(CorpusEntry entry)
    {
        string written = await CorpusSweep.RewriteAsync(entry.Path);
        await using (VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None))
        {
            if (target.RowCount != entry.RowCount)
            {
                throw new InvalidOperationException(
                    $"wrote {entry.RowCount} rows and read back {target.RowCount}");
            }
        }

        List<UInt128> original = await CorpusSweep.PlainAsync(entry.Path);
        List<UInt128> readBack = await CorpusSweep.DigestAsync(written);
        if (original.Count != readBack.Count)
        {
            throw new InvalidOperationException(
                $"wrote {original.Count} values and read back {readBack.Count}");
        }

        int differs = CollectionsMarshal.AsSpan(original).CommonPrefixLength(CollectionsMarshal.AsSpan(readBack));
        if (differs < original.Count)
        {
            List<string> lines = await CorpusSweep.DescribeAsync(entry.Path);
            List<string> read = await CorpusSweep.DescribeAsync(written);
            throw new InvalidOperationException(
                $"value {differs} was '{lines[differs]}' and read back '{read[differs]}'");
        }

        return (int)entry.RowCount;
    }

    /// <summary>A struct of booleans, primitives, strings and bytes: what a leaf entry summarises plainly.</summary>
    private static bool PlainColumns(DType schema)
    {
        if (schema.Kind != DTypeKind.Struct || schema.FieldCount == 0)
        {
            return false;
        }

        for (int field = 0; field < schema.FieldCount; field++)
        {
            if (schema.GetField(field).Kind is not (DTypeKind.Bool or DTypeKind.Primitive or DTypeKind.Utf8 or DTypeKind.Binary))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The file's rows appended in thirds to an unclustered dataset, compacted into one object, and
    /// that object's bytes.
    /// </summary>
    private static async Task<byte[]> CompactedAsync(VortexFile source, VortexWriteOptions write)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, source.DType, new DatasetOptions { Seed = 0xC0_55C4EC, Write = write });
        long third = source.RowCount / 3;
        foreach ((long from, long to) in ((long, long)[])[(0, third), (third, 2 * third), (2 * third, source.RowCount)])
        {
            await dataset.AppendAsync(source.ScanBuilder().Rows(new RowRange(from, to)).ExecuteAsync());
        }

        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(
            new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 32 }));
        Assert.Equal((3L, 1L, CompactionStyle.Tiered), (result.ObjectsIn, result.ObjectsOut, result.Style));

        string key = string.Empty;
        await foreach (DataObject held in dataset.ObjectsAsync())
        {
            key = held.Key;
        }

        ObjectHead head = (await store.HeadAsync(key, CancellationToken.None))!.Value;
        using ObjectRange range = await store.GetRangeAsync(key, 0, (int)head.Length, CancellationToken.None);
        return range.Bytes.ToArray();
    }

    /// <summary>Whether a zone map of this schema can carry string bounds: a top-level utf8 or binary.</summary>
    private static bool HasStringField(DType schema)
    {
        if (schema.Kind is DTypeKind.Utf8 or DTypeKind.Binary)
        {
            return true;
        }

        if (schema.Kind != DTypeKind.Struct)
        {
            return false;
        }

        for (int field = 0; field < schema.FieldCount; field++)
        {
            if (schema.GetField(field).Kind is DTypeKind.Utf8 or DTypeKind.Binary)
            {
                return true;
            }
        }

        return false;
    }
}
