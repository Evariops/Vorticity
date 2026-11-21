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

        // What a batch of the rows weighs decoded: two views and two numbers a row, 44 bytes. The texts they name are the
        // dictionary's, counted in what the object holds open, and in no batch.
        Assert.InRange(entry.DecodedBytes, rows.Length * 44L, rows.Length * 64L);
        Assert.True(entry.DecodedBytes < named / 8, $"the entry says {entry.DecodedBytes:N0} decoded, of rows that name {named:N0}");

        // A scan at its shortest batches, none decoded ahead, holds no more than the entry says; and the entry says no more
        // than half as much again. Read within its first chunk, it holds one dictionary, not two.
        Assert.True(least <= entry.OpenBytes, $"a scan held {least:N0}, past the {entry.OpenBytes:N0} its entry says");
        Assert.True(entry.OpenBytes <= least + (least / 2), $"the entry says {entry.OpenBytes:N0}, half as much again as the {least:N0} a scan held");
        Assert.True(within <= (entry.OpenBytes * 3) / 5, $"a scan of the first chunk's rows held {within:N0}, more than a chunk of the {entry.OpenBytes:N0} its entry says");

        // At its first scan, with batches of 1 024 rows, one decoded ahead, it holds past what the entry says its reader's
        // own and its batches, three of them, a row 44 bytes: the window a merge counts it at.
        long window = DatasetCompactor.Window(dataset, dataset.Snapshot.Schema, entry, 1_024);
        Assert.True(peak <= window, $"a scan held {peak:N0}, past the window of {window:N0} a merge counts it at, of which {entry.OpenBytes:N0} its entry says");
        Assert.True(window <= peak + (peak / 8), $"a merge counts it at {window:N0}, an eighth past the {peak:N0} a scan held");
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

        // The merged object says what its own rows' batches weigh, counted by runs of its inputs' batches: less than its
        // inputs' together, whose 5 000 rows each kept their messages as they came where its 20 000 keep them in a
        // dictionary, which it holds open instead.
        long before = appended.Sum(static entry => entry.DecodedBytes);
        Assert.InRange(written.DecodedBytes, written.Rows * 36L, before);
        Assert.True(written.OpenBytes > 0);
    }

    /// <summary>
    /// A merge of objects whose dictionaries weigh several times their batches counts each input at its window, what its
    /// entry says it holds open and its batches: on one lane, where no input decodes ahead of its own, each input's scan
    /// holds no more than its window, and the inputs together no more than they were counted to hold at once.
    /// </summary>
    [Fact]
    public async Task AMergesInputsEachHoldNoMoreThanTheirWindow()
    {
        await using VortexSession session = VortexSession.Create(static o => o.MaxDegreeOfParallelism = 1);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        Random random = new Random(19);
        long first = 0;
        for (int append = 0; append < 4; append++)
        {
            Line[] rows = [.. Enumerable.Range(0, 20_000).Select(i => new Line(0, $"svc-{random.Next(0, 12):D2}", first + i, Texts[random.Next(Messages)]))];
            await AppendAsync(dataset, rows);
            first += rows.Length;
        }

        CompactionResult merged = Assert.IsType<CompactionResult>(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(4, merged.ObjectsIn);
        string held = string.Join("; ", merged.InputWindows.Select(static input => $"window {input.Window:N0} (open {input.Open:N0}), held {input.Held:N0}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"WINDOWS: planned {merged.InputsPlanned:N0}, inputs held {merged.MemoryReservedBy.Inputs:N0}; {held}");
        Assert.Equal(4, merged.InputWindows.Count);
        Assert.All(merged.InputWindows, input => Assert.True(input.Held <= input.Window, held));
        Assert.True(merged.MemoryReservedBy.Inputs <= merged.InputsPlanned, $"the inputs held {merged.MemoryReservedBy.Inputs:N0}, counted {merged.InputsPlanned:N0}; {held}");

        // Most of what an input holds is what its entry says it holds open, its dictionaries: its batches are the rest.
        Assert.All(merged.InputWindows, input => Assert.True(input.Held >= input.Open / 2 && input.Open >= 4 * (input.Held - input.Open), held));
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
