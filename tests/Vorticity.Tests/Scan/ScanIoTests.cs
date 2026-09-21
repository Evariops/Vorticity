// The two I/O properties the scan promises and no value comparison can show.
//
// F4 - PROJECTION PRUNES I/O. Scanning one column of a wide file must never ask the source for the
// other columns' segments. Without this test "projection" is a claim about the shape of the output,
// not a feature: a reader that decoded all eleven columns and then dropped ten would pass every
// value assertion in the suite.
//
// ONE ReadManyAsync PER BATCH. Register and Execute are separate so that a batch can coalesce its
// whole split into a single read. A layout reader that read a
// segment itself, or registered one during Execute, breaks that SILENTLY - the scan still returns
// the right values and the I/O count doubles. This assertion is the only thing that catches it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanIoTests
{
    [Fact]
    public async Task ProjectingOneColumnNeverRequestsTheOthersSegments()
    {
        Decoders.EnsureRegistered();

        const string Entry = "types/struct_field_names";
        RecordingSegmentSource source = new RecordingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(Entry)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        LayoutTree tree = LayoutTree.Parse(file);
        LayoutNode root = tree.Root;
        Assert.Equal(LayoutEncodingId.Struct, root.Encoding);

        int fieldCount = file.Schema.FieldCount;
        Assert.Equal(12, fieldCount);
        Assert.False(file.Schema.IsNullable, "the fixture's root struct is non-nullable, so field k is child k");

        // Every field's own segments, from the layout tree - not guessed from offsets.
        HashSet<uint>[] perField = new HashSet<uint>[fieldCount];

        // The subset an unfiltered read actually needs: the zones child of a vortex.zoned layout is
        // read only by pruning, which an unfiltered scan does not run. Asserting
        // against the whole subtree would demand I/O the design deliberately skips.
        HashSet<uint>[] read = new HashSet<uint>[fieldCount];
        for (int k = 0; k < fieldCount; k++)
        {
            perField[k] = new HashSet<uint>();
            CollectSegments(root.GetChild(k), perField[k]);
            Assert.NotEmpty(perField[k]);
            read[k] = new HashSet<uint>();
            CollectReadSegments(root.GetChild(k), read[k]);
            Assert.NotEmpty(read[k]);
        }

        const int Projected = 3;
        source.ResetCounters();

        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ProjectFields([Projected]).ExecuteAsync())
        {
            Assert.Equal(1, batch.FieldCount);
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        Assert.True(source.ReadManyCalls > 0, "the scan must have read something");

        HashSet<uint> requested = RequestedIds(file, source);
        Assert.NotEmpty(requested);

        // Everything the projected column needs was asked for...
        foreach (uint id in read[Projected])
        {
            Assert.Contains(id, requested);
        }

        // ...and nothing that belongs only to another column was.
        for (int k = 0; k < fieldCount; k++)
        {
            if (k == Projected)
            {
                continue;
            }

            foreach (uint id in perField[k])
            {
                if (perField[Projected].Contains(id))
                {
                    continue;
                }

                Assert.False(
                    requested.Contains(id),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"segment {id} belongs to unprojected field {k} but was read"));
            }
        }
    }

    [Fact]
    public async Task AWiderProjectionReadsStrictlyMore()
    {
        Decoders.EnsureRegistered();

        const string Entry = "types/struct_field_names";
        HashSet<uint> one = await RequestedFor(Entry, [3]);
        HashSet<uint> two = await RequestedFor(Entry, [3, 7]);
        HashSet<uint> all = await RequestedFor(Entry, null);

        Assert.ProperSubset(two, one);
        Assert.ProperSubset(all, two);
    }

    [Fact]
    public async Task ExactlyOneReadManyPerBatch()
    {
        Decoders.EnsureRegistered();

        RecordingSegmentSource source = new RecordingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path("distributions/high_cardinality_i64_r8193")));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        source.ResetCounters();

        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(1000).ExecuteAsync())
        {
            batches++;
            Assert.Equal(batches, source.ReadManyCalls);
        }

        Assert.True(batches >= 9);
        Assert.Equal(batches, source.ReadManyCalls);

        // The scan uses the batched entry point only: no single reads, no range reads, no length
        // probes after open.
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, source.ReadRangeCalls);
        Assert.Equal(0, source.LengthCalls);
    }

    [Theory]
    [InlineData("containers/uncompressed_canonical")]
    [InlineData("containers/chunked_stream_3")]
    [InlineData("types/date_days_nullable_r8193")]
    [InlineData("distributions/long_runs_i32_r8193")]
    public async Task OneReadManyPerBatchAcrossLayoutShapes(string entry)
    {
        Decoders.EnsureRegistered();

        RecordingSegmentSource source = new RecordingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(entry)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        source.ResetCounters();

        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(512).ExecuteAsync())
        {
            batches++;
        }

        Assert.Equal(batches, source.ReadManyCalls);
    }

    private static async Task<HashSet<uint>> RequestedFor(string entry, int[]? fields)
    {
        RecordingSegmentSource source = new RecordingSegmentSource(
            MemoryMappedSegmentSource.Open(Corpus.Path(entry)));
        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        ScanBuilder builder = file.Scan();
        if (fields is not null)
        {
            builder = builder.ProjectFields(fields);
        }

        source.ResetCounters();
        await foreach (RecordBatch batch in builder.ExecuteAsync())
        {
            Assert.True(batch.RowCount > 0);
        }

        return RequestedIds(file, source);
    }

    private static HashSet<uint> RequestedIds(VortexFile file, RecordingSegmentSource source)
    {
        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        HashSet<uint> ids = new HashSet<uint>();
        for (int i = 0; i < specs.Length; i++)
        {
            if (source.WasRequested(specs[i]))
            {
                ids.Add((uint)i);
            }
        }

        return ids;
    }

    /// <summary>
    /// The segments a Phase 1 read of <paramref name="node"/> touches, following the same child
    /// selection the layout readers make: a zoned or stats layout reads only its data child, and a
    /// dict layout reads both its values and its codes.
    /// </summary>
    private static void CollectReadSegments(in LayoutNode node, HashSet<uint> into)
    {
        ReadOnlySpan<uint> segments = node.Segments;
        for (int i = 0; i < segments.Length; i++)
        {
            into.Add(segments[i]);
        }

        int children = node.ChildCount;
        if ((node.Encoding == LayoutEncodingId.Zoned || node.Encoding == LayoutEncodingId.Stats) &&
            children >= 1)
        {
            children = 1;
        }

        for (int i = 0; i < children; i++)
        {
            LayoutNode child = node.GetChild(i);
            CollectReadSegments(in child, into);
        }
    }

    private static void CollectSegments(in LayoutNode node, HashSet<uint> into)
    {
        ReadOnlySpan<uint> segments = node.Segments;
        for (int i = 0; i < segments.Length; i++)
        {
            into.Add(segments[i]);
        }

        int children = node.ChildCount;
        for (int i = 0; i < children; i++)
        {
            LayoutNode child = node.GetChild(i);
            CollectSegments(in child, into);
        }
    }
}
