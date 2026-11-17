// A job's writer sends the runs its merge staged through on a lane of their own while the merge stages the next ones: the
// objects a consolidation writes on four lanes are those it writes on one, cut at the same rows, holding the same rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetCompactionLanesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two days of documents in blocks of 1 024 rows, consolidated on one lane and on four. On four, the writers send
    /// staged blocks through on a lane of their own; on one, none. The objects are cut at the same rows either way, the size
    /// a writer reads to cut them taken before a staged block goes through, whichever lane it goes through on; and they hold
    /// the same rows. Their bytes are compared on copies of one store (<see cref="AConsolidationWritesTheSameSegmentsAtAnyDegreeAndOnAnyRun"/>):
    /// here each run appends objects of fresh identities, which order the objects of equal keys in the tree.
    /// </summary>
    [Fact]
    public async Task AConsolidationOnFourLanesCutsItsObjectsAtTheRowsItCutsThemOnOne()
    {
        (List<long> alone, List<Doc> aloneRows, long aloneFlown) = await ConsolidateAsync(lanes: 1);
        (List<long> across, List<Doc> acrossRows, long acrossFlown) = await ConsolidateAsync(lanes: 4);
        Assert.Equal(0, aloneFlown);
        Assert.True(acrossFlown > 0, "no staged block went through on a lane of its own");
        Assert.True(alone.Count >= 2, $"{alone.Count} objects");
        Assert.Equal(alone, across);
        Assert.Equal(aloneRows.Count, acrossRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, acrossRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(acrossRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    /// <summary>
    /// A consolidation on a session of four lanes, three of them held by other work: the job's census, its blocks and its
    /// chunks go through on the one left and on its own thread, and it ends with the objects it writes on one lane.
    /// </summary>
    [Fact]
    public async Task AConsolidationLeftOneLaneOfFourEndsWithTheObjectsOfOne()
    {
        (List<long> alone, List<Doc> aloneRows, _) = await ConsolidateAsync(lanes: 1);
        (List<long> left, List<Doc> leftRows, _) = await ConsolidateAsync(lanes: 4, held: 3);
        Assert.Equal(alone, left);
        Assert.Equal(aloneRows.Count, leftRows.Count);
        for (int i = 0; i < aloneRows.Count; i++)
        {
            Assert.Equal(aloneRows[i].Sequence, leftRows[i].Sequence);
            Assert.True(aloneRows[i].Attributes.Equals(leftRows[i].Attributes), $"row {aloneRows[i].Sequence}");
        }
    }

    /// <summary>
    /// A consolidation's merge keeps each input's batch as its scan decoded it, its keys encoded beside the decode, until
    /// the writers have copied the runs they keep of it: it takes every batch over from its scan, copies none, and encodes
    /// no key on its own thread, at one lane and at four. A consolidation in pieces leaves deletion vectors on its inputs,
    /// which the decode applies.
    /// </summary>
    [Fact]
    public async Task AConsolidationsMergeTakesItsInputsBatchesOverAndCopiesNone()
    {
        foreach (int lanes in new[] { 1, 4 })
        {
            Random random = new Random(31);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, OptionsOf(session), Ct);
            for (int a = 0; a < 6; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
            Assert.True(results.Sum(static result => result.InputWindowsTaken) > 0, $"at {lanes} lanes, no batch was taken over");
            Assert.Equal(0, results.Sum(static result => result.InputWindowsCopied));
            Assert.Equal(0, results.Sum(static result => result.InputWindowsKeyedByMerge));

            // The merge hands its writers a block's map at a time, short as an object ends, and never a run: runs of a few
            // rows, the services of two days interleaved, made thousands of calls.
            long calls = results.Sum(static result => result.WriterBatches);
            long merged = results.Sum(static result => result.Rows);
            Assert.True(merged >= calls * 256, $"at {lanes} lanes, {calls} calls of the writers for {merged} rows");
            Assert.Equal(48_000, await dataset.ScanBuilder().CountAsync(Ct));
        }
    }

    /// <summary>
    /// One store of six appends, copied four times and consolidated on each copy at one lane, one lane again, four lanes
    /// and fourteen: every segment of every object is the same bytes. The rows of one key come in the order of the job's
    /// inputs whatever the batches its inputs are read in, which the memory the job measures decides; an input that ran
    /// through the keys equal to another's wrote them first or not depending on where its batch ended. Cut in slices of
    /// 2 048 rows, the merge is the same at any degree too, its slices merged ahead on the lanes free or by the job's flow.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2_048)]
    public async Task AConsolidationWritesTheSameSegmentsAtAnyDegreeAndOnAnyRun(int sliceRows)
    {
        // Blocks of 256 rows, so that an object is aimed at dozens of them and its writer sends several through at once.
        List<CompactionResult> sliced = await SameSegmentsAtDegreesAsync(
            [1, 1, 4, 14],
            static random => $"svc-{random.Next(0, 60):D2}",
            session =>
            {
                DatasetOptions options = OptionsOf(session) with { Write = new VortexWriteOptions { BlockRows = 256 } };
                return sliceRows > 0 ? options with { SliceRows = sliceRows } : options;
            });

        // At four lanes and fourteen, the writers sent blocks through their line, several at once, and the bytes are those of one.
        Assert.True(sliced.Sum(static job => job.WriterBlocksOnLine) > 0, "no block went through a writer's line");
        if (sliceRows > 0)
        {
            Assert.True(sliced.Max(static job => job.MergeSlices) >= 2, "no merge was cut in slices");
            Assert.True(sliced.Sum(static job => job.MergeSlicesAhead) > 0, "no slice was merged ahead");
        }
    }

    /// <summary>
    /// Services named at a kibibyte, so that a chunk of the file holds four blocks of 256 rows and ends within an object: the
    /// writers' lines meet the end of a chunk, emitted once every block of it landed and before the next block ingests, and
    /// the segments are the same bytes at one lane, four and fourteen.
    /// </summary>
    [Fact]
    public async Task AConsolidationWhoseChunksEndWithinItsObjectsWritesTheSameSegmentsAtAnyDegree()
    {
        string padding = new string('x', 1_000);
        List<CompactionResult> jobs = await SameSegmentsAtDegreesAsync(
            [1, 4, 14],
            random => $"svc-{random.Next(0, 60):D2}-{padding}",
            static session => OptionsOf(session) with { Write = new VortexWriteOptions { BlockRows = 256 } });
        Assert.True(jobs.Sum(static job => job.WriterBlocksOnLine) > 0, "no block went through a writer's line");
        Assert.True(jobs.Sum(static job => job.WriterChunksEndedOnLine) > 0, "no chunk ended on a writer's line");
    }

    /// <summary>
    /// One store of six appends of 8 000 rows, whose services <paramref name="service"/> names, copied once a degree of
    /// <paramref name="degrees"/> and consolidated there with <paramref name="optionsOf"/>: every segment of every object is
    /// the same bytes on every copy. The jobs' results, of every degree.
    /// </summary>
    private static async Task<List<CompactionResult>> SameSegmentsAtDegreesAsync(
        int[] degrees, Func<Random, string> service, Func<VortexSession, DatasetOptions> optionsOf)
    {
        List<List<(string Path, byte[] Bytes)>> runs = [];
        List<CompactionResult> jobs = [];
        await using MemoryObjectStore built = new MemoryObjectStore();
        {
            Random random = new Random(29);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(built, Doc.Schema, OptionsOf(VortexSession.Default), Ct);
            for (int a = 0; a < 6; a++)
            {
                Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), service(random), i, Attributes(random)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Doc>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }
        }

        // A copy keeps the objects' identities, which order the objects of equal keys in the tree, as a maintainer's store does.
        for (int run = 0; run < degrees.Length; run++)
        {
            await using MemoryObjectStore copy = new MemoryObjectStore();
            Vorticity.IO.IObjectStore from = built;
            await foreach (Vorticity.IO.ObjectListing listed in from.ListAsync(string.Empty, null, Ct))
            {
                byte[] bytes = new byte[listed.Head.Length];
                await from.ReadAsync(listed.Key, 0, bytes, null, Ct);
                await ((Vorticity.IO.IObjectStore)copy).PutAsync(listed.Key, new System.Buffers.ReadOnlySequence<byte>(bytes), Vorticity.IO.PutCondition.None, Ct);
            }

            int degree = degrees[run];
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(copy, optionsOf(session), Ct);
            jobs.AddRange(await dataset.ConsolidateAsync(Ct));
            List<(string Path, byte[] Bytes)> segments = [];
            int index = 0;
            await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                byte[] bytes = new byte[positioned.Entry.Bytes];
                await ((Vorticity.IO.IObjectStore)copy).ReadAsync(positioned.Entry.Key, 0, bytes, null, Ct);
                await using VortexFile file = await VortexSession.Default.OpenAsync(new Vorticity.IO.MemorySegmentSource(bytes), cancellationToken: Ct);
                Collect(await file.GetLayoutAsync(Ct), $"object {index}", bytes, file, segments);
                index++;
            }

            runs.Add(segments);
        }

        Assert.True(runs[0].Count >= 8, $"{runs[0].Count} segments");
        System.Text.StringBuilder differ = new System.Text.StringBuilder();
        for (int run = 1; run < degrees.Length; run++)
        {
            Assert.Equal(runs[0].Count, runs[run].Count);
            for (int i = 0; i < runs[0].Count; i++)
            {
                if (runs[0][i].Path != runs[run][i].Path || !runs[0][i].Bytes.AsSpan().SequenceEqual(runs[run][i].Bytes))
                {
                    differ.Append("degree ").Append(degrees[run]).Append(", ").Append(runs[run][i].Path).Append(": ")
                        .Append(runs[0][i].Bytes.Length).Append(" bytes against ").Append(runs[run][i].Bytes.Length).Append('\n');
                }
            }
        }

        Assert.True(differ.Length == 0, "segments differ:\n" + differ);
        return jobs;
    }

    private static async Task<(List<long> Objects, List<Doc> Rows, long Flown)> ConsolidateAsync(int lanes, int held = 0)
    {
        Random random = new Random(29);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
        long[] taken = new long[held];
        for (int i = 0; i < held; i++)
        {
            Assert.True(session.Lanes.TryEnter(out taken[i]));
        }
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Doc.Schema, OptionsOf(session), Ct);
        for (int a = 0; a < 6; a++)
        {
            Doc[] rows = [.. Enumerable.Range(a * 8_000, 8_000).Select(i => new Doc(random.Next(0, 2), $"svc-{random.Next(0, 60):D2}", i, Attributes(random)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Doc>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        IReadOnlyList<CompactionResult> results = await dataset.ConsolidateAsync(Ct);
        Assert.NotEmpty(results);
        List<long> objects = [];
        await foreach (PositionedObject positioned in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            objects.Add(positioned.Entry.Rows);
        }

        List<Doc> read = [.. (await dataset.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence)];
        foreach (long entered in taken)
        {
            session.Lanes.Exit(entered);
        }

        return (objects, read, results.Sum(static result => result.WriterBatchesFlown));
    }

    /// <summary>The dataset of two days of documents, a service's rows together, in objects of 256 KiB and blocks of 1 024 rows.</summary>
    private static DatasetOptions OptionsOf(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Write = new VortexWriteOptions { BlockRows = 1_024 },
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Doc.Day)),
            SortKey = [LayoutValue.Column(nameof(Doc.Service))],
            ObjectBytes = 1 << 18,
            CalmAfter = null,
        },
    };

    private static void Collect(VortexLayout node, string path, byte[] bytes, VortexFile file, List<(string Path, byte[] Bytes)> into)
    {
        for (int s = 0; s < node.Segments.Length; s++)
        {
            VortexSegment segment = file.SegmentMap[node.Segments[s]];
            into.Add(($"{path}/{node.Encoding}/{node.ArrayEncoding}#{s}", bytes.AsSpan((int)segment.Offset, (int)segment.Length).ToArray()));
        }

        for (int c = 0; c < node.Children.Length; c++)
        {
            Collect(node.Children[c], $"{path}.{c}", bytes, file, into);
        }
    }

    /// <summary>Attributes of a service's records: a route, a status, a duration, a user, and now and then a key of its own.</summary>
    private static Variant Attributes(Random random)
    {
        List<(string, Variant)> fields =
        [
            ("route", Variant.From($"/api/r{random.Next(20)}")),
            ("status", Variant.From(200 + (random.Next(4) * 100))),
            ("duration", Variant.From(random.NextDouble() * 100)),
            ("user", Variant.From($"user-{random.Next(500)}")),
        ];
        if (random.Next(5) == 0)
        {
            fields.Add(($"k{random.Next(40)}", Variant.From(random.Next(1_000))));
        }

        return Variant.Object([.. fields]);
    }

    [VortexRecord]
    public partial record struct Doc(int Day, string Service, long Sequence, Variant Attributes);
}
