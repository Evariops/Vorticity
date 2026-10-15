// Source offsets: any number of writers may take the same entries of a source, and the commit
// counts each of them once. A commit that declares the ranges it consumed applies only where each
// range starts at its source's offset in the version it lands on, adds nothing where each is counted
// already, and throws otherwise, with the offsets recorded.
//
// THE ROWS OF A RANGE ARE ITS ENTRIES: entry n of the source is the row of key n, so a row counted
// twice shows as a key twice, and the dataset's keys are the union of the ranges applied.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Protobuf;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetSourceOffsetTests
{
    private const string Spans = "raw/spans/17";

    [Fact]
    public async Task TwoWritersOfOneRangeCountItOnceAndTheLoserAddsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset a = await CreateAsync(store, ct);
        Assert.Equal(OperationOutcome.Applied, (await AppendAsync(a, Spans, 0, 41_200, ct)).Outcome);

        // Both read 41 200. A pauses, B takes (41 200, 42 300] and commits first.
        await using VortexDataset b = await VortexDataset.OpenAsync(store, Options(), ct);
        ulong from = a.SourceOffset(Spans);
        Assert.Equal((41_200UL, 41_200UL), (from, b.SourceOffset(Spans)));
        SourceCommit first = await AppendAsync(b, Spans, from, 42_300, ct);
        Assert.Equal(OperationOutcome.Applied, first.Outcome);
        Assert.Equal(42_300UL, b.SourceOffset(Spans));

        // A comes back with (41 200, 41 950]: its rebase finds the range counted, and adds nothing.
        SourceCommit late = await AppendAsync(a, Spans, from, 41_950, ct);
        Assert.Equal((OperationOutcome.AlreadyThere, first.Version), (late.Outcome, late.Version));
        Assert.Equal((first.Version, 42_300UL), (a.Version, a.SourceOffset(Spans)));

        // Every entry once.
        List<long> keys = await KeysAsync(a, ct);
        Assert.Equal(42_300, keys.Count);
        Assert.Equal(Enumerable.Range(0, 42_300).Select(i => (long)i), keys);
    }

    [Fact]
    public async Task AnOverlapAndAGapThrowWithTheOffsetsRecorded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset a = await CreateAsync(store, ct);
        await AppendAsync(a, Spans, 0, 41_200, ct);
        await using VortexDataset b = await VortexDataset.OpenAsync(store, Options(), ct);
        await AppendAsync(b, Spans, 41_200, 42_300, ct);
        ulong version = b.Version;

        // A, still on 41 200, overlaps without being included: it learns the offset, and its handle
        // reads the version that recorded it.
        SourceOffsetConflictException overlap = await Assert.ThrowsAsync<SourceOffsetConflictException>(
            async () => await AppendAsync(a, Spans, 41_200, 42_500, ct));
        Assert.Equal(42_300UL, overlap.Recorded[Spans]);
        Assert.Equal(version, overlap.Version);
        Assert.Equal([new SourceRange(Spans, 41_200, 42_500)], overlap.Declared);
        Assert.Contains("'raw/spans/17' (41200, 42500] where version", overlap.Message, StringComparison.Ordinal);
        Assert.Equal((version, 42_300UL), (a.Version, a.SourceOffset(Spans)));

        // A gap: entries 42 301 to 42 500 would never be counted.
        SourceOffsetConflictException gap = await Assert.ThrowsAsync<SourceOffsetConflictException>(
            async () => await AppendAsync(a, Spans, 42_500, 43_000, ct));
        Assert.Equal(42_300UL, gap.Recorded[Spans]);

        // Taking up from the offset recorded applies, and nothing was committed in between.
        Assert.Equal(version, a.Version);
        Assert.Equal(OperationOutcome.Applied, (await AppendAsync(a, Spans, 42_300, 42_500, ct)).Outcome);
        Assert.Equal(42_500, (await KeysAsync(a, ct)).Count);
    }

    [Fact]
    public async Task ACommitReplayedAfterALostAnswerIsAlreadyThere()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        SourceCommit applied = await AppendAsync(dataset, Spans, 0, 500, ct);

        // A writer that restarts without knowing it committed replays the same range, from a handle
        // opened before the commit.
        await using VortexDataset restarted = await VortexDataset.OpenAsync(store, dataset.Version - 1, Options(), ct);
        SourceCommit replayed = await AppendAsync(restarted, Spans, 0, 500, ct);
        Assert.Equal((OperationOutcome.AlreadyThere, applied.Version), (replayed.Outcome, replayed.Version));
        Assert.Equal(500, (await KeysAsync(restarted, ct)).Count);
    }

    [Fact]
    public async Task TheRangesOfOneCommitApplyTogetherOrNotAtAll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        Assert.Equal(OperationOutcome.Applied, (await AppendAsync(dataset, [new SourceRange("a", 0, 10), new SourceRange("b", 0, 20)], 0, ct)).Outcome);
        Assert.Equal((10UL, 20UL, 0UL), (dataset.SourceOffset("a"), dataset.SourceOffset("b"), dataset.SourceOffset("c")));

        // One counted and one at its offset: the rows mix them, so the commit cannot apply in part.
        await Assert.ThrowsAsync<SourceOffsetConflictException>(
            async () => await AppendAsync(dataset, [new SourceRange("a", 0, 10), new SourceRange("b", 20, 30)], 100, ct));

        // Both counted: nothing added. Both at their offsets: applied, both move.
        Assert.Equal(OperationOutcome.AlreadyThere, (await AppendAsync(dataset, [new SourceRange("a", 0, 10), new SourceRange("b", 10, 20)], 100, ct)).Outcome);
        Assert.Equal(OperationOutcome.Applied, (await AppendAsync(dataset, [new SourceRange("a", 10, 15), new SourceRange("c", 0, 5)], 100, ct)).Outcome);
        Assert.Equal((15UL, 20UL, 5UL), (dataset.SourceOffset("a"), dataset.SourceOffset("b"), dataset.SourceOffset("c")));

        // What a commit cannot declare.
        await Assert.ThrowsAsync<ArgumentException>(async () => await AppendAsync(dataset, [], 0, ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await AppendAsync(dataset, [new SourceRange("a", 15, 16), new SourceRange("a", 16, 17)], 0, ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRange("a", 5, 5));
        Assert.Throws<ArgumentException>(() => new SourceRange("", 0, 1));
    }

    [Fact]
    public async Task ARangeWithoutRowsMovesTheOffsetAndChangesNoRow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        await AppendAsync(dataset, Spans, 0, 100, ct);
        ulong changed = dataset.LastDataChange;

        // Entries the writer filtered out entirely still have to be counted, or it would take them
        // again forever.
        await using ObjectDraft draft = dataset.StartObject();
        SourceCommit empty = await dataset.AppendAsync(draft, [new SourceRange(Spans, 100, 250)], ct);
        Assert.Equal(OperationOutcome.Applied, empty.Outcome);
        Assert.Equal((250UL, changed), (dataset.SourceOffset(Spans), dataset.LastDataChange));
        Assert.True(empty.Version > changed);
        Assert.Equal(100, (await KeysAsync(dataset, ct)).Count);
    }

    [Fact]
    public async Task EveryOtherCommitCarriesTheOffsetsOnAndForgettingDropsOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        await AppendAsync(dataset, Spans, 0, 100, ct);
        await AppendAsync(dataset, "raw/spans/18", 0, 100, ct);
        await AppendPlainAsync(dataset, 500, 50, ct);

        // A compaction, an indexing and a delete declare no source: each carries the offsets on.
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, ct));
        await dataset.IndexAsync(IndexPolicy.None.Bloom("City"), cancellationToken: ct);
        await dataset.DeleteAsync<ChangeRow>(r => r.Key == 3, ct);
        Assert.Equal((100UL, 100UL), (dataset.SourceOffset(Spans), dataset.SourceOffset("raw/spans/18")));

        ulong changed = dataset.LastDataChange;
        await dataset.ForgetSourcesAsync([Spans], ct);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, Options(), ct);
        Assert.Equal((0UL, 100UL), (reader.SourceOffset(Spans), reader.SourceOffset("raw/spans/18")));
        Assert.Equal(changed, reader.LastDataChange);
    }

    [Fact]
    public async Task ADatasetThatCountsSourcesIsWrittenInFormatThree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        await AppendPlainAsync(dataset, 0, 10, ct);
        Assert.Equal(CommitFormat.Version, await FormatAsync(store, dataset.Version, ct));

        // From the first offset on, and for as long as one is counted, a commit of any kind.
        await AppendAsync(dataset, Spans, 0, 10, ct);
        Assert.Equal(CommitFormat.WithSources, await FormatAsync(store, dataset.Version, ct));
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, ct));
        Assert.Equal(CommitFormat.WithSources, await FormatAsync(store, dataset.Version, ct));
        await dataset.ForgetSourcesAsync([Spans], ct);
        Assert.Equal(CommitFormat.Version, await FormatAsync(store, dataset.Version, ct));
    }

    [Fact]
    public void AHeaderThatCountsTwoHundredAndFiftySixSourcesStaysSmall()
    {
        // Every header carries the whole table, out of the 192 KiB it also keeps for the tree's
        // pages: a few hundred sources, one per shard of raw data, must stay a small part of it.
        SourceOffset[] sources = [.. Enumerable.Range(0, 256).Select(i => new SourceOffset(
            $"raw/spans/{i:D3}", 1_000_000_000UL + ((ulong)i * 7_919), 1_760_000_000_000 + i))];
        ProtoWriter writer = new ProtoWriter();
        try
        {
            new CommitHeader { Version = 1, Sources = sources }.Write(ref writer);
            int bare = Measure(new CommitHeader { Version = 1 });

            // 256 entries of 30 bytes: a tag and a length, a name of 13 bytes, an offset of 5 bytes
            // and a time of 6, each behind its tag and the name behind its length too. 7.5 KiB out
            // of 192. Raised only with a reason, here.
            Assert.Equal(7_680, writer.WrittenSpan.Length - bare);
            Assert.Equal(sources, CommitHeader.Read(writer.WrittenSpan).Sources);
        }
        finally
        {
            writer.Dispose();
        }

        static int Measure(CommitHeader header)
        {
            ProtoWriter measured = new ProtoWriter();
            try
            {
                header.Write(ref measured);
                return measured.WrittenSpan.Length;
            }
            finally
            {
                measured.Dispose();
            }
        }
    }

    private static DatasetOptions Options() => new DatasetOptions { Seed = 0x50_04CE, ClusteringKey = ["Key"] };

    private static async Task<VortexDataset> CreateAsync(IObjectStore store, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        return await VortexDataset.CreateAsync(store, ChangeRow.Schema, Options(), ct);
    }

    /// <summary>The rows of the entries past <paramref name="from"/> up to <paramref name="to"/>, as one commit that counts them.</summary>
    private static Task<SourceCommit> AppendAsync(VortexDataset dataset, string source, ulong from, ulong to, CancellationToken ct) =>
        AppendAsync(dataset, [new SourceRange(source, from, to)], (long)from, (int)(to - from), ct);

    private static Task<SourceCommit> AppendAsync(VortexDataset dataset, IReadOnlyList<SourceRange> consumed, long first, CancellationToken ct) =>
        AppendAsync(dataset, consumed, first, 10, ct);

    private static async Task<SourceCommit> AppendAsync(
        VortexDataset dataset, IReadOnlyList<SourceRange> consumed, long first, int count, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(Rows(first, count), ct);
        return await dataset.AppendAsync(draft, consumed, ct);
    }

    private static ChangeRow[] Rows(long first, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ChangeRow(first + i, i * 0.5, i % 2 == 0 ? "Paris" : "Lyon"))];

    /// <summary>Rows appended without a range.</summary>
    private static async Task AppendPlainAsync(VortexDataset dataset, long first, int count, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(Rows(first, count), ct);
        await dataset.AppendAsync(draft, ct);
    }

    private static async Task<List<long>> KeysAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<long> keys = [.. (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key)];
        keys.Sort();
        return keys;
    }

    private static async Task<uint> FormatAsync(MemoryObjectStore store, ulong version, CancellationToken ct)
    {
        byte[] preamble = new byte[16];
        await store.ReadAsync(CommitKey.For(version), 0, preamble, null, ct);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(preamble.AsSpan(8));
    }
}
