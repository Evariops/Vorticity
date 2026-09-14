// All 819 golden files, opened. That is 819 real postscripts and footers written by Vortex 0.86.1
// and it is the only oracle in Phase 1 that can tell the open path it is wrong about a real file.
//
// The I/O-count assertion in OpensEveryCorpusFile is the headline promise of PHASE1-CONTRACTS.md
// §7.1 made measurable: one length probe plus one tail read, for every one of them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileCorpusTests
{
    [Fact]
    public async Task OpensEveryCorpusFileAndAgreesWithTheManifest()
    {
        CorpusEntry[] entries = CorpusManifest.Entries;
        Assert.Equal(851, entries.Length);

        List<string> failures = new List<string>();
        HashSet<long> rowCounts = new HashSet<long>();
        int filesWithStatistics = 0;
        int filesWithMetadata = 0;
        int filesWithoutDTypeSegment = 0;

        foreach (CorpusEntry entry in entries)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(CorpusManifest.FullPath(entry));
            Assert.Equal(entry.SizeBytes, bytes.LongLength);

            TestSegmentSource source = new TestSegmentSource(bytes);
            VortexOpenOptions options = OptionsFor(entry);
            rowCounts.Add(entry.RowCount);

            if (!entry.HasDTypeSegment)
            {
                // has_dtype_segment is not a property of the open file; it is observable as the
                // one behaviour that depends on it (PHASE1-CONTRACTS.md §7.4 row 4).
                filesWithoutDTypeSegment++;
                await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(
                    new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None));
            }
            else
            {
                await using VortexFile embedded = await VortexFile.OpenAsync(
                    new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None);
                Check(failures, entry, "embedded dtype", entry.DType, ManifestDTypeFormatter.Format(embedded.Schema));
            }

            try
            {
                VortexFile file = await VortexFile.OpenAsync(source, options, CancellationToken.None);
                await using (file)
                {
                    Check(failures, entry, "row_count", entry.RowCount, file.RowCount);
                    Check(failures, entry, "format_version", 1, file.FormatVersion);
                    Check(failures, entry, "file_length", entry.SizeBytes, file.FileLength);
                    Check(
                        failures,
                        entry,
                        "has_file_statistics",
                        entry.HasFileStatistics,
                        file.HasFileStatistics);
                    Check(
                        failures,
                        entry,
                        "metadata_count",
                        entry.MetadataKeys.Length,
                        file.MetadataCount);

                    string rendered = ManifestDTypeFormatter.Format(file.Schema);
                    if (!string.Equals(rendered, entry.DType, StringComparison.Ordinal))
                    {
                        failures.Add($"{entry.Id}: dtype\n  expected {entry.DType}\n  actual   {rendered}");
                    }

                    // The postscript length is not exposed on the file, so read it back off the
                    // wire: it is the u16 at [len-6, len-4) and the manifest records it.
                    int postscript = bytes[^6] | (bytes[^5] << 8);
                    Check(failures, entry, "postscript_bytes", entry.PostscriptBytes, postscript);

                    for (int i = 0; i < entry.MetadataKeys.Length; i++)
                    {
                        Check(
                            failures,
                            entry,
                            $"metadata[{i}].key",
                            entry.MetadataKeys[i],
                            file.GetMetadataKey(i));
                        Check(
                            failures,
                            entry,
                            $"metadata[{i}].len",
                            (long)entry.MetadataLengths[i],
                            (long)file.GetMetadataSegment(i).Length);
                    }

                    // Every segment in the map lies inside the file and the map is ordered: the
                    // open path validated it, this asserts the validation actually ran.
                    ulong previous = 0;
                    ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
                    for (int i = 0; i < specs.Length; i++)
                    {
                        Assert.True(specs[i].Offset >= previous, $"{entry.Id}: segment {i} out of order");
                        Assert.True(specs[i].End <= (ulong)entry.SizeBytes, $"{entry.Id}: segment {i} past EOF");
                        previous = specs[i].Offset;
                    }

                    Assert.False(file.RootLayoutBytes.IsEmpty, $"{entry.Id}: empty root layout");

                    if (file.HasFileStatistics)
                    {
                        filesWithStatistics++;
                        int expected = file.Schema.Kind == DTypeKind.Struct ? file.Schema.FieldCount : 1;
                        Check(failures, entry, "statistics.field_count", expected, file.Statistics.FieldCount);
                    }

                    if (file.MetadataCount > 0)
                    {
                        filesWithMetadata++;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Id}: threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // PHASE1-CONTRACTS.md §7.1: one length probe plus one read. Not a single golden file
            // places a footer segment before the 65535-byte tail window, so not one of them costs
            // the second read.
            if (source.LengthProbes != 1 || source.TotalReads != 1)
            {
                failures.Add(
                    $"{entry.Id}: expected 1 length probe and 1 read, got " +
                    $"{source.LengthProbes} probes, {source.RangeReads} range reads, " +
                    $"{source.SegmentReads} segment reads, {source.BatchReads} batch reads");
            }
        }

        Assert.Equal(575, filesWithStatistics);
        Assert.Equal(2, filesWithMetadata);
        Assert.Equal(1, filesWithoutDTypeSegment);

        // PHASE1-CONTRACTS.md §1.8: 1024 is the FastLanes block and 8192 the default row block,
        // and that is where the bugs are. The corpus covers every one of the eight.
        foreach (long boundary in new long[] { 0, 1, 1023, 1024, 1025, 8191, 8192, 8193 })
        {
            Assert.Contains(boundary, rowCounts);
        }

        AssertNoFailures(failures);
    }

    [Fact]
    public async Task ASuppliedDTypeRemovesTheLengthProbeAndTheDTypeSegmentRead()
    {
        CorpusEntry entry = CorpusManifest.Find("types/user_metadata_segments");
        byte[] bytes = CorpusManifest.Bytes(entry.Id);

        TestSegmentSource probing = new TestSegmentSource(bytes);
        await using (await VortexFile.OpenAsync(probing, VortexOpenOptions.Default, CancellationToken.None))
        {
        }

        Assert.Equal(1, probing.LengthProbes);
        Assert.Equal(1, probing.TotalReads);

        TestSegmentSource informed = new TestSegmentSource(bytes);
        VortexOpenOptions options = new VortexOpenOptions { FileLength = bytes.Length };
        await using (await VortexFile.OpenAsync(informed, options, CancellationToken.None))
        {
        }

        Assert.Equal(0, informed.LengthProbes);
        Assert.Equal(1, informed.TotalReads);
    }

    [Fact]
    public async Task NoDTypeSegmentFailsWithoutASuppliedDTypeAndSucceedsWithOne()
    {
        CorpusEntry entry = CorpusManifest.Find("types/no_dtype_segment");
        Assert.False(entry.HasDTypeSegment);
        byte[] bytes = CorpusManifest.Bytes(entry.Id);

        VortexFormatException error = await Assert.ThrowsAsync<VortexFormatException>(
            async () => await VortexFile.OpenAsync(
                new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None));
        Assert.Contains("doesn't embed a DType", error.Message, StringComparison.Ordinal);

        // types/user_metadata_segments carries the identical schema and does embed it, so the
        // out-of-band DType comes from a real file rather than from 33 hand-written arena calls.
        DType schema;
        await using (VortexFile donor = await VortexFile.OpenAsync(
            new TestSegmentSource(CorpusManifest.Bytes("types/user_metadata_segments")),
            VortexOpenOptions.Default,
            CancellationToken.None))
        {
            schema = donor.Schema;
        }

        Assert.Equal(entry.DType, ManifestDTypeFormatter.Format(schema));

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions { DType = schema }, CancellationToken.None);

        Assert.Equal(entry.RowCount, file.RowCount);
        Assert.Equal(entry.DType, ManifestDTypeFormatter.Format(file.Schema));
        Assert.True(file.IsTabular);
        Assert.Same(schema.Arena, file.Types);
        Assert.Equal(1, source.TotalReads);
    }

    [Fact]
    public async Task PostscriptMaxMetadataYieldsSixteenSixtyFourByteKeysInStoredOrder()
    {
        CorpusEntry entry = CorpusManifest.Find("containers/postscript_max_metadata");
        Assert.Equal(16, entry.MetadataKeys.Length);

        byte[] bytes = CorpusManifest.Bytes(entry.Id);
        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        Assert.Equal(16, file.MetadataCount);
        string previous = string.Empty;
        for (int i = 0; i < 16; i++)
        {
            string key = file.GetMetadataKey(i);
            Assert.Equal(entry.MetadataKeys[i], key);
            Assert.Equal(64, Encoding.UTF8.GetByteCount(key));
            Assert.True(string.CompareOrdinal(previous, key) < 0, "keys are stored sorted");
            previous = key;

            Assert.True(file.TryGetMetadataIndex(Encoding.UTF8.GetBytes(key), out int found));
            Assert.Equal(i, found);

            Assert.Equal((uint)entry.MetadataLengths[i], file.GetMetadataSegment(i).Length);
        }

        Assert.False(file.TryGetMetadataIndex("nope"u8, out int missing));
        Assert.Equal(-1, missing);

        // Every metadata segment of this file sits inside the initial tail window, so reading a
        // value costs nothing beyond the open. Metadata values are lazy but already-covered ones
        // are free (docs/02-format.md §2).
        int before = source.TotalReads;
        for (int i = 0; i < 16; i++)
        {
            global::Vorticity.Buffers.SegmentOwner owner = await file.ReadMetadataAsync(i, CancellationToken.None);
            try
            {
                Assert.Equal(entry.MetadataLengths[i], owner.Length);
            }
            finally
            {
                owner.Release();
            }
        }

        Assert.Equal(before, source.TotalReads);
    }

    [Fact]
    public async Task UserMetadataSegmentsCarryTheRecordedPayloadLengths()
    {
        CorpusEntry entry = CorpusManifest.Find("types/user_metadata_segments");
        Assert.Equal(3, entry.MetadataKeys.Length);

        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(CorpusManifest.Bytes(entry.Id)),
            VortexOpenOptions.Default,
            CancellationToken.None);

        for (int i = 0; i < entry.MetadataKeys.Length; i++)
        {
            Assert.Equal(entry.MetadataKeys[i], file.GetMetadataKey(i));
            global::Vorticity.Buffers.SegmentOwner owner = await file.ReadMetadataAsync(i, CancellationToken.None);
            try
            {
                Assert.Equal(entry.MetadataLengths[i], owner.Length);
            }
            finally
            {
                owner.Release();
            }
        }

        // A zero-length metadata value is legal and must come back as an empty buffer, not a throw.
        Assert.Contains(0, entry.MetadataLengths);
    }

    [Fact]
    public async Task OpensFromAPathThroughTheMemoryMappedSource()
    {
        // The in-memory source above never exercises MemoryMappedSegmentSource; a handful of real
        // files through the real default path do.
        string[] ids =
        [
            "containers/all_null_i64_explicit_validity_r1025",
            "containers/postscript_max_metadata",
            "types/user_metadata_segments",
            "distributions/huge_string_r16",
        ];

        foreach (string id in ids)
        {
            CorpusEntry entry = CorpusManifest.Find(id);
            await using VortexFile file = await VortexFile.OpenAsync(CorpusManifest.FullPath(entry));
            Assert.Equal(entry.RowCount, file.RowCount);
            Assert.Equal(entry.DType, ManifestDTypeFormatter.Format(file.Schema));
            Assert.Equal(entry.SizeBytes, file.FileLength);
        }
    }

    [Fact]
    public async Task AnEmptyOrTruncatedFileOnDiskIsRejectedThroughTheMemoryMappedPath()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vorticity-file-open-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string empty = System.IO.Path.Combine(directory, "empty.vortex");
            await System.IO.File.WriteAllBytesAsync(empty, []);
            await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(empty));

            string tiny = System.IO.Path.Combine(directory, "tiny.vortex");
            await System.IO.File.WriteAllBytesAsync(tiny, [0x56, 0x54, 0x58, 0x46]);
            await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(tiny));

            string truncated = System.IO.Path.Combine(directory, "truncated.vortex");
            byte[] bytes = CorpusManifest.Bytes("containers/all_null_i64_explicit_validity_r1025");
            await System.IO.File.WriteAllBytesAsync(truncated, bytes.AsSpan(0, bytes.Length / 2).ToArray());
            await Assert.ThrowsAsync<VortexFormatException>(async () => await VortexFile.OpenAsync(truncated));
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownEncodingIdsResolveToUnknownWithoutFailingTheOpen()
    {
        // §2.3: an id we do not implement maps to Unknown and that is NOT an error at open. The
        // writer pre-populates array_specs with every id its editions permit, so an unused and
        // unresolvable entry is the normal case, not the exceptional one.
        //
        // EVERY DECLARED ID NOW RESOLVES, so this test has no unresolvable one to point at. It
        // used to name encodings/fastlanes_delta, then encodings/map, then encodings/pco, then
        // encodings/variant -- each replaced when the id gained a decoder, and the variants were
        // the last. What it asserts now is the half that is still checkable HERE: the open reads
        // the whole table and resolves each entry, and the resolution is addressable by the wire
        // index. The tolerance for an id from a future edition is tested in `ScanContextTests`,
        // over a forged id, which is the only way left to have one.
        CorpusEntry entry = CorpusManifest.Find("encodings/variant");
        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(CorpusManifest.Bytes(entry.Id)),
            VortexOpenOptions.Default,
            CancellationToken.None);

        bool sawVariant = false;
        bool sawKnown = false;
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            string id = file.GetArrayEncodingId(i);
            global::Vorticity.Arrays.ArrayEncodingId resolved = file.GetArrayEncoding(i);
            if (string.Equals(id, "vortex.variant", StringComparison.Ordinal))
            {
                sawVariant = true;
                Assert.Equal(global::Vorticity.Arrays.ArrayEncodingId.Variant, resolved);
            }

            if (string.Equals(id, "vortex.primitive", StringComparison.Ordinal))
            {
                sawKnown = true;
                Assert.Equal(global::Vorticity.Arrays.ArrayEncodingId.Primitive, resolved);
            }
        }

        Assert.True(sawVariant, "the variant file must declare vortex.variant");
        Assert.True(sawKnown, "every corpus file declares vortex.primitive");

        Assert.True(file.LayoutEncodingCount > 0);
        Assert.Throws<VortexFormatException>(() => file.GetArrayEncoding(file.ArrayEncodingCount));
        Assert.Throws<VortexFormatException>(() => file.GetArrayEncodingId(-1));
        Assert.Throws<VortexFormatException>(() => file.GetLayoutEncoding(file.LayoutEncodingCount));
        Assert.Throws<VortexFormatException>(() => file.GetLayoutEncodingId(-1));
    }

    private static VortexOpenOptions OptionsFor(CorpusEntry entry)
    {
        if (entry.HasDTypeSegment)
        {
            return VortexOpenOptions.Default;
        }

        // types/no_dtype_segment is the only such file; its schema is byte-identical to the one
        // types/user_metadata_segments embeds.
        return new VortexOpenOptions { DType = SuppliedSchema.Value };
    }

    private static readonly Lazy<DType> SuppliedSchema = new Lazy<DType>(() =>
    {
        VortexFile donor = VortexFile
            .OpenAsync(
                new TestSegmentSource(CorpusManifest.Bytes("types/user_metadata_segments")),
                VortexOpenOptions.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        DType schema = donor.Schema;
        donor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return schema;
    });

    private static void Check<T>(List<string> failures, CorpusEntry entry, string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Id}: {what} expected {expected} but was {actual}"));
        }
    }

    private static void AssertNoFailures(List<string> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        StringBuilder message = new StringBuilder();
        message.Append(failures.Count).Append(" corpus mismatches:\n");
        int shown = Math.Min(failures.Count, 25);
        for (int i = 0; i < shown; i++)
        {
            message.Append("  ").Append(failures[i]).Append('\n');
        }

        if (shown < failures.Count)
        {
            message.Append("  ... and ").Append(failures.Count - shown).Append(" more");
        }

        Assert.Fail(message.ToString());
    }
}
