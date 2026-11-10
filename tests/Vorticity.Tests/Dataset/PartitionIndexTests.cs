// The index of a consolidated partition (D13; spec A, 12): a consolidation of a dataset with an identifier writes, a day, an
// index object whose coverage is the day's level 1 and whose postings place every covered row's identifier in its object
// and unit, while its objects keep no postings of their own; late rows consolidated keep the objects they leave covered;
// a purge takes the objects it rewrites out of the coverage; a vacuum keeps the index the version names.
//
// THE ORACLE IS THE ROWS: each covered row's identifier, hashed as the postings key it, found at its object's rank and its
// unit; a lookup of an identifier through the dataset returns LINQ's row, or none; the store holds what the version names.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class PartitionIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six appends of 15 000 rows over two days, partitioned by day, keyed by a service and a status, the sequence an
    /// identifier, merged and consolidated: an index a day, which covers the day's level 1, places every covered row, and
    /// rules covered objects out of a lookup unread. Late rows of the first day consolidated keep it so; a purge of part
    /// of the second day leaves the objects it rewrites outside its index; a vacuum past the retention keeps the indexes
    /// the version names and takes the first day's first. Sealed, the same: an index is read by its plaintext's length, which
    /// the store's is not, by a lookup and by the consolidation that merges it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConsolidatedDaysIndexPlacesItsRowsAndKeepsItsCoverage(bool encrypted)
    {
        Random random = new Random(71);
        await using MemoryObjectStore store = new MemoryObjectStore();
        using VortexKeyring? keyring = encrypted ? Sealing.SealedObjects.Keyring() : null;
        await using VortexSession? session = keyring is null ? null : VortexSession.Create(options => options.Keyring = keyring);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Session = session ?? VortexSession.Default,
            Encrypted = encrypted,
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            Identifiers = [nameof(Row.Sequence)],

            // Inside the hour the vacuum's clock runs ahead, and past twice any job's time, half of which a commit allows.
            RetentionWindow = TimeSpan.FromMinutes(10),
        }, Ct);
        List<Row> all = [];
        await AppendAsync(dataset, random, all, appends: 6, rows: 15_000, days: 2);
        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);
        await IndexesAsync(dataset, all, expectDays: 2, purged: false);
        string firstDayZero = (await EntriesAsync(dataset))[0].ObjectKey;

        // Late rows of day 0, consolidated with the objects they meet: the others stay covered through the new index.
        await AppendAsync(dataset, random, all, appends: 2, rows: 3_000, days: 1);
        await dataset.ConsolidateAsync(Ct);
        await IndexesAsync(dataset, all, expectDays: 2, purged: false);

        // A purge of a quarter of day 1, which rewrites the objects it falls in: they leave the coverage.
        await dataset.DeleteAsync<Row>(static row => row.Day == 1 & row.Status < 25, Ct);
        all.RemoveAll(static row => row.Day == 1 && row.Status < 25);
        await IndexesAsync(dataset, all, expectDays: 1, purged: true);

        // The vacuum keeps the indexes the latest version names, and takes the first index of day 0.
        await dataset.VacuumAsync(new VacuumOptions { TimeProvider = new ManualClock(DateTimeOffset.UtcNow.AddHours(1)) }, Ct);
        foreach (PartitionIndexEntry entry in await EntriesAsync(dataset))
        {
            Assert.NotNull(await store.HeadAsync(entry.ObjectKey, Ct));
        }

        Assert.Null(await store.HeadAsync(firstDayZero, Ct));
    }

    private static async Task AppendAsync(VortexDataset dataset, Random random, List<Row> all, int appends, int rows, int days)
    {
        for (int a = 0; a < appends; a++)
        {
            Row[] batch = [.. Enumerable.Range(0, rows).Select(i => new Row(random.Next(0, days), $"svc-{random.Next(0, 400):D3}", all.Count + i, random.Next(0, 100), random.NextDouble()))];
            all.AddRange(batch);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(batch, Ct);
            await dataset.AppendAsync(draft, Ct);
        }
    }

    private static async Task<List<PartitionIndexEntry>> EntriesAsync(VortexDataset dataset)
    {
        DatasetSnapshot version = dataset.Snapshot;
        List<PartitionIndexEntry> entries = [];
        if (!version.Levels.PartitionIndexes.IsEmpty)
        {
            await foreach (TreeEntry entry in version.Levels.PartitionIndexes.EnumerateAsync(version.Pages, Ct))
            {
                entries.Add(PartitionIndexEntry.FromBytes(entry.Key.Span, entry.Value.Span));
            }
        }

        return entries;
    }

    /// <summary>
    /// The version's partition indexes, <paramref name="expectDays"/> of them: each covers its day's level 1 objects that keep
    /// no postings of their own (after a purge, the rewritten ones stand outside), places every hundredth covered row's
    /// identifier at its object's rank and unit; forty lookups of an identifier, absent ones among them, return LINQ's rows.
    /// </summary>
    private static async Task IndexesAsync(VortexDataset dataset, List<Row> all, int expectDays, bool purged)
    {
        List<PartitionIndexEntry> entries = await EntriesAsync(dataset);
        Assert.Equal(expectDays, entries.Count);
        Dictionary<UInt128, (PositionedObject Held, List<Row> Rows)> levelOne = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Level == 1)
            {
                await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
                levelOne[held.Entry.Uid] = (held, await lease.File.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct));
            }
        }

        foreach (PartitionIndexEntry entry in entries)
        {
            await using ISegmentReader reader = await SealedObjectStore.OpenDataAsync(dataset.Store, entry.ObjectKey, entry.Uid, entry.Bytes, Ct);
            PartitionIndex index = await PartitionPostings.ReadAsync(reader, Ct);
            PartitionIndex.Entry sequence = index.Of(nameof(Row.Sequence), PartitionIndex.EntryKind.Postings) ?? throw new InvalidOperationException("no postings of Sequence");
            int day = levelOne.Values.Where(held => entry.Covers(held.Held.Entry.Uid)).Select(held => held.Rows[0].Day).FirstOrDefault(-1);
            Assert.True(day >= 0, $"'{entry.ObjectKey}' covers no level 1 object");

            // Coverage: the day's level 1 objects, each with no postings of its own; a purge's objects stand outside it.
            foreach ((UInt128 uid, (PositionedObject held, List<Row> rows)) in levelOne)
            {
                if (rows.Count > 0 && rows[0].Day == day)
                {
                    bool own = held.Entry.Contents?.Locating?.Any(static locating => locating.Kind == Vorticity.Indexes.IndexKinds.PostingsHashed) == true;
                    Assert.True(entry.Covers(uid) != own || (purged && !entry.Covers(uid)), $"day {day}'s object covered {entry.Covers(uid)} with postings of its own {own}");
                }
            }

            // Every hundredth covered row's identifier placed at its object's rank and unit.
            for (int rank = 0; rank < index.Objects.Count; rank++)
            {
                PartitionIndex.Covered covered = index.Objects[rank];
                if (!entry.Covers(covered.Uid))
                {
                    continue;
                }

                Assert.True(levelOne.TryGetValue(covered.Uid, out (PositionedObject Held, List<Row> Rows) held) && sequence.Covers(rank), $"day {day}'s index covers rank {rank}, which no level 1 object holds");
                for (int row = 0; row < held.Rows.Count; row += 97)
                {
                    long value = held.Rows[row].Sequence;
                    byte[] bytes = new byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                    ulong hash = XxHash3.HashToUInt64(bytes);
                    int page = sequence.PageOf(hash);
                    List<(int Rank, uint Block)> found = [];
                    if (page >= 0)
                    {
                        PartitionIndex.Page at = sequence.Pages[page];
                        using Vorticity.Buffers.SegmentOwner owner = await reader.ReadAsync(
                            new Vorticity.Serialization.Schemas.SegmentSpec((ulong)at.Offset, (uint)at.Length, 0, 0, 0), Ct);
                        sequence.Find(owner.Buffer.Span, at, hash, found);
                    }

                    long place = row / ((long)covered.BlockRows * sequence.UnitBlocks);
                    Assert.True(found.Contains((rank, (uint)place)), $"Sequence {value} of day {day} is not placed at rank {rank}, place {place}");
                }
            }
        }

        Assert.True(!purged || levelOne.Keys.Any(uid => !entries.Any(entry => entry.Covers(uid))), "no object left the coverage");

        // Lookups of an identifier through the dataset: the rows LINQ finds.
        Random random = new Random(67);
        HashSet<long> live = [.. all.Select(static row => row.Sequence)];
        long passed = 0;
        for (int probe = 0; probe < 40; probe++)
        {
            long value = probe % 5 == 4 ? -1 - probe : all[random.Next(all.Count)].Sequence;
            DatasetScanCounters counters = new DatasetScanCounters();
            long found = 0;
            VortexExpr equal = Expr.Eq(Expr.Field(nameof(Row.Sequence)), Expr.Literal(FilterLiteral.From(value)));
            await foreach (RecordBatch batch in dataset.ScanBuilder().Where(equal).WithMetrics(counters).ExecuteAsync(Ct))
            {
                found += batch.SelectedRows;
            }

            passed += counters.ObjectsPassedByPartitionIndex;
            Assert.Equal(live.Contains(value) ? 1 : 0, found);
        }

        Assert.True(passed > 0, "no covered object was passed over by its partition's index");
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Value);
}
