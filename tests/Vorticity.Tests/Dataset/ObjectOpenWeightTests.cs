// What an object's entry says a reader holds of it open, and what its rows weigh decoded: counted by its writer as it
// encodes each chunk, kept in two tagged fields of the entry, and read by a merge that counts the object as an input.
//
// THE ORACLE IS A SCAN UNDER AN ACCOUNT OF ITS OWN: an object read in its rows' order at the shortest batches holds, past
// its batches, no more than its entry says, and the entry says no more than a measured multiple of it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ObjectOpenWeightTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The distinct messages a column of the rows holds, long enough that their dictionary is what an open object weighs.</summary>
    private const int Messages = 2_000;

    /// <summary>
    /// Messages of some 600 bytes each, of words that repeat, so that their dictionary's values are compressed and a
    /// reader decodes them whole: 1.2 MB of values for 40 000 rows that each name one.
    /// </summary>
    private static readonly string[] Texts = [.. Enumerable.Range(0, Messages).Select(static m =>
        string.Join(' ', Enumerable.Range(0, 60).Select(w => $"word{(m * 31 + w * 7) % 97:D2}-{m % 13}")) + $" #{m:D5}")];

    /// <summary>
    /// An object whose rows name a few thousand long messages keeps them in a dictionary, which a reader of any of its
    /// rows decodes whole: its entry says so, in what it holds open, and says what its rows weigh decoded. A scan of the
    /// object at batches of 1 024 rows, under an account of its own, holds no more than the entry says past its batches.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-11-21: an object of 210 KB, its messages' column in two chunks, each with a dictionary of 1.1 MB
    /// decoded in a block of 2 MiB. Its entry says 4 608 000 bytes open and 24.2 MB decoded; a scan held 4 284 416 at its
    /// least, 2 154 496 within its first chunk, and 4 759 552 at batches of 1 024 rows with one decoded ahead.
    /// </remarks>
    [Fact]
    public async Task AnEntrySaysWhatAReaderHoldsOfItsObjectOpen()
    {
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        Random random = new Random(11);
        Line[] rows = [.. Enumerable.Range(0, 40_000).Select(i => new Line(0, $"svc-{random.Next(0, 12):D2}", i, Texts[random.Next(Messages)]))];
        await AppendAsync(dataset, rows);

        ObjectEntry entry = Assert.Single(await EntriesAsync(dataset, 0));
        long values = Texts.Sum(static text => (long)text.Length);
        long named = rows.Sum(static row => (long)row.Message.Length + row.Service.Length);
        long peak = await ScanPeakAsync(dataset, entry, 1_024, prefetch: 1);
        long least = await ScanPeakAsync(dataset, entry, 256, prefetch: 0);
        long within = await ScanPeakAsync(dataset, entry, 256, prefetch: 0, only: 4_096);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"OPEN: entry {entry.Bytes:N0} B, open {entry.OpenBytes:N0}, decoded {entry.DecodedBytes:N0}, dictionary values {values:N0}, rows name {named:N0}, " +
            $"scan peak {peak:N0}, at its least {least:N0}, within its first chunk {within:N0}");

        // The dictionary's values, decoded, are in what the entry says the object holds open, twice: the column's two chunks
        // each keep one, and a scan that crosses from the first to the second holds both.
        Assert.True(entry.OpenBytes >= 2 * values, $"the entry says {entry.OpenBytes:N0} open, under its two dictionaries of {values:N0}");

        // What the rows weigh decoded: the bytes their texts name at least, their views and their numbers past them.
        Assert.InRange(entry.DecodedBytes, named, named + (rows.Length * 64L));

        // A scan at its shortest batches, none decoded ahead, holds no more than the entry says; and the entry says no more
        // than half as much again. Read within its first chunk, it holds one dictionary, not two.
        Assert.True(least <= entry.OpenBytes, $"a scan held {least:N0}, past the {entry.OpenBytes:N0} its entry says");
        Assert.True(entry.OpenBytes <= least + (least / 2), $"the entry says {entry.OpenBytes:N0}, half as much again as the {least:N0} a scan held");
        Assert.True(within <= (entry.OpenBytes * 3) / 5, $"a scan of the first chunk's rows held {within:N0}, more than a chunk of the {entry.OpenBytes:N0} its entry says");

        // With batches of 1 024 rows, one decoded ahead, it holds them past what the entry says: three, a row some 600 bytes.
        long batches = 3 * 1_024 * (entry.DecodedBytes / rows.Length);
        Assert.True(peak <= entry.OpenBytes + batches, $"a scan held {peak:N0}, past the {entry.OpenBytes:N0} its entry says and {batches:N0} of batches");
    }

    /// <summary>
    /// The entry of an object a merge writes says what a reader holds of it open as an append's does, and a merge of
    /// objects that say it writes an object that says it: the weights go on through the jobs.
    /// </summary>
    [Fact]
    public async Task AMergedObjectsEntrySaysItToo()
    {
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        Random random = new Random(5);
        long first = 0;
        for (int append = 0; append < 4; append++)
        {
            Line[] rows = [.. Enumerable.Range(0, 5_000).Select(i => new Line(0, $"svc-{random.Next(0, 12):D2}", first + i, Texts[random.Next(Messages)]))];
            await AppendAsync(dataset, rows);
            first += rows.Length;
        }

        List<ObjectEntry> appended = await EntriesAsync(dataset, 0);
        Assert.Equal(4, appended.Count);
        Assert.All(appended, static entry => Assert.True(entry is { OpenBytes: > 0, DecodedBytes: > 0 }));

        CompactionResult merged = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(4, merged.ObjectsIn);
        ObjectEntry written = Assert.Single(await EntriesAsync(dataset, 0));

        // The merged object's rows weigh what its inputs' did, about: counted by runs of its inputs' batches, at what a
        // row of a batch names on average.
        long before = appended.Sum(static entry => entry.DecodedBytes);
        Assert.InRange(written.DecodedBytes, before - (before / 8), before + (before / 8));
        Assert.True(written.OpenBytes > 0);
    }

    /// <summary>
    /// The weights are tagged fields of the entry: an entry written with them reads back with them, an entry without
    /// them says zero, and they stay through the entry's other changes.
    /// </summary>
    [Fact]
    public void TheWeightsAreFieldsOfTheEntryThatItsOtherChangesKeep()
    {
        ObjectEntry bare = new ObjectEntry("data/a.vortex", new UInt128(1, 2), 100, 4_096, new UInt128(3, 4));
        Assert.Equal(0, bare.OpenBytes);
        Assert.Equal(0, bare.DecodedBytes);

        ObjectEntry weighed = bare.WithWeights(123_456, 7_890_123);
        Assert.Equal(123_456, weighed.OpenBytes);
        Assert.Equal(7_890_123, weighed.DecodedBytes);
        Assert.Same(weighed, weighed.WithWeights(123_456, 7_890_123));

        ObjectEntry changed = weighed.WithArrival(1_800_000_000).WithLastKey(new byte[] { 1, 2, 3 }).WithFooterBytes(1 << 20);
        Assert.Equal(123_456, changed.OpenBytes);
        Assert.Equal(7_890_123, changed.DecodedBytes);
        Assert.Equal(1_800_000_000, changed.Arrival);

        byte[] bytes = changed.ToBytes();
        ObjectEntry read = ObjectEntry.FromBytes(bytes);
        Assert.Equal(changed, read);
        Assert.Equal(bytes, read.ToBytes());
        Assert.Equal(123_456, read.OpenBytes);
        Assert.Equal(7_890_123, read.DecodedBytes);
    }

    /// <summary>
    /// What a scan of <paramref name="entry"/>'s object holds at most, read in its rows' order in batches of
    /// <paramref name="batchRows"/> rows, <paramref name="prefetch"/> of them decoded ahead, as a merge reads an input,
    /// under an account of its own; with <paramref name="only"/>, of its first rows alone, that many.
    /// </summary>
    private static async Task<long> ScanPeakAsync(VortexDataset dataset, ObjectEntry entry, int batchRows, int prefetch, long only = 0)
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
        using WorkAccount account = new WorkAccount(budget, "the scan of one object");
        ObjectLease lease = await dataset.RentAsync(entry, Ct);
        await using (lease.ConfigureAwait(false))
        {
            long rows = 0;
            ScanBuilder scan = lease.File.ScanBuilder().WithAccount(account).WithPrefetch(prefetch).WithMaxBatchRows(batchRows).WithWindowRows(batchRows);
            await foreach (RecordBatch batch in (only > 0 ? scan.Rows(new RowRange(0, only)) : scan).ExecuteAsync().WithCancellation(Ct).ConfigureAwait(false))
            {
                rows += batch.RowCount;
            }

            Assert.Equal(only > 0 ? only : entry.Rows, rows);
        }

        return budget.PeakBytes;
    }

    /// <summary>The entries of level <paramref name="level"/>, in key order.</summary>
    private static async Task<List<ObjectEntry>> EntriesAsync(VortexDataset dataset, int level)
    {
        List<ObjectEntry> entries = [];
        await foreach ((_, ObjectEntry parsed) in dataset.Snapshot.EntriesAsync(level, Ct).ConfigureAwait(false))
        {
            entries.Add(parsed);
        }

        return entries;
    }

    private static DatasetOptions Options(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Line.Day)),
            SortKey = [LayoutValue.Column(nameof(Line.Service)), LayoutValue.Column(nameof(Line.Sequence))],
            ObjectBytes = 64 << 20,
            MergeFanout = 4,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    };

    private static async Task AppendAsync(VortexDataset dataset, Line[] rows)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Line>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Line(int Day, string Service, long Sequence, string Message);
}
