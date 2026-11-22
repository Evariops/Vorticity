// The lanes of a compaction input's scan that reach a chunk at once: what a read of the chunk decodes whole, a dictionary's
// values, is decoded once for them all, the split of a lane that meets another's decode run again once it is published.
//
// THE ORACLE IS THE CHILDREN DECODED WHOLE: as many on lanes side by side as on one lane, and the same rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <remarks>In the collection that runs alone, because the count it reads is the process's.</remarks>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class JobInputChunkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The distinct messages the object's rows name, kept in a dictionary a reader decodes whole.</summary>
    private const int Messages = 2_000;

    /// <summary>Messages of some 600 bytes each, of words that repeat: their dictionary's values are compressed, 1.2 MB decoded.</summary>
    private static readonly string[] Texts = [.. Enumerable.Range(0, Messages).Select(static m =>
        string.Join(' ', Enumerable.Range(0, 60).Select(w => $"word{(m * 31 + w * 7) % 97:D2}-{m % 13}")) + $" #{m:D5}")];

    /// <summary>The scans of each kind: enough for lanes to meet at a chunk in some of them.</summary>
    private const int Rounds = 8;

    /// <summary>
    /// An input read as a merge reads it, six batches decoded ahead on lanes of the session's: the lanes reach the chunk's
    /// dictionary at once, and a scan that reserves its memory decodes it once all the same, as many children decoded
    /// whole as on one lane, in every scan. A lane that met another's decode had its split run again once the dictionary
    /// was published, where it decoded a copy of its own, held beside the other's.
    /// </summary>
    [Fact]
    public async Task LanesThatReachAChunkAtOnceDecodeItsDictionaryOnce()
    {
        await using VortexSession session = VortexSession.Create(static o => o.MaxDegreeOfParallelism = 8);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        Random random = new Random(11);
        Line[] lines = [.. Enumerable.Range(0, 120_000).Select(i => new Line(0, $"svc-{random.Next(0, 12):D3}", i, Texts[random.Next(Messages)]))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Line>(lines, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        List<ObjectEntry> entries = [];
        await foreach ((_, ObjectEntry parsed) in dataset.Snapshot.EntriesAsync(0, Ct).ConfigureAwait(false))
        {
            entries.Add(parsed);
        }

        ObjectEntry entry = Assert.Single(entries);

        // On one lane, a child a read decodes whole is decoded once: the count the lanes side by side are held to.
        (long rows, long alone, _) = await ScanAsync(dataset, entry, ahead: 0, reserves: true);
        Assert.Equal(entry.Rows, rows);
        Assert.True(alone >= 1, "the object holds no child a read decodes whole");

        long waits = 0;
        for (int round = 0; round < Rounds; round++)
        {
            (rows, long decoded, long met) = await ScanAsync(dataset, entry, ahead: 6, reserves: true);
            Assert.Equal(entry.Rows, rows);
            Assert.Equal(alone, decoded);
            waits += met;
        }

        // What a scan that reserves nothing does at the same lanes, told and not held: which lanes meet is a race.
        long copies = 0;
        for (int round = 0; round < Rounds; round++)
        {
            (rows, long decoded, _) = await ScanAsync(dataset, entry, ahead: 6, reserves: false);
            Assert.Equal(entry.Rows, rows);
            copies += decoded - alone;
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"CHUNK WAITS: {alone} children decoded whole on one lane; on lanes that reserve, as many in each of {Rounds} scans, {waits} splits run again after another lane's decode; on lanes that reserve nothing, {copies} copies decoded past them in {Rounds} scans");
    }

    /// <summary>
    /// The rows of <paramref name="entry"/>'s object read as a merge reads an input, <paramref name="ahead"/> batches
    /// decoded ahead, its batches handed over; under a reserve when it <paramref name="reserves"/>. How many rows, how many
    /// children a read decodes whole were decoded, and how many splits met a chunk another lane was decoding.
    /// </summary>
    private static async Task<(long Rows, long Decoded, long Met)> ScanAsync(VortexDataset dataset, ObjectEntry entry, int ahead, bool reserves)
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
        MemoryReserve? reserve = reserves ? new MemoryReserve(budget) : null;
        BatchHandover handover = new BatchHandover { Reserve = reserve };
        using WorkAccount work = new WorkAccount(budget, "the scan of one input");
        CountedAccount account = new CountedAccount(work, reserve);
        long before = Interlocked.Read(ref ArrayDecodeContext.SharedChildrenDecoded);
        long rows = 0;
        try
        {
            ObjectLease lease = await dataset.RentAsync(entry, Ct);
            await using (lease.ConfigureAwait(false))
            {
                ScanBuilder scan = lease.File.ScanBuilder().WithAccount(account).WithPrefetch(ahead).WithConsumerLane()
                    .WithMaxBatchRows(1_024).WithWindowRows(1_024).WithHandover(handover);
                await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(Ct).ConfigureAwait(false))
                {
                    rows += batch.RowCount;
                }
            }
        }
        finally
        {
            reserve?.End();
        }

        return (rows, Interlocked.Read(ref ArrayDecodeContext.SharedChildrenDecoded) - before, Interlocked.Read(ref handover.ChunkWaits));
    }

    private static DatasetOptions Options(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Line.Day)),
            SortKey = [LayoutValue.Column(nameof(Line.Service)), LayoutValue.Column(nameof(Line.Sequence))],
            ObjectBytes = 64 << 20,
            MergeFanout = 64,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    };

    [VortexRecord]
    public partial record struct Line(int Day, string Service, long Sequence, string Message);
}
