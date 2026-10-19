using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ObjectBoundTests
{
    /// <summary>The threshold past which an object goes to the store as it is written, low enough for objects of a test.</summary>
    private const long StreamedFrom = 64 << 10;

    [VortexRecord]
    public partial record struct Measured(long Key, long Measure);

    [Fact]
    public async Task ACompactionAnnouncesWhatItReadsToEachObjectItStreams()
    {
        // Past 64 KiB an object goes to the store as it is written, its length known only at its end. An
        // append announces nothing, since nothing bounds what it writes; a compaction announces what its
        // inputs weigh times what a rewrite writes a byte at worst, which the store sizes its parts from,
        // and the object it writes stays within it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using BoundRecordingStore store = new BoundRecordingStore(new MemoryObjectStore());
        await using VortexDataset dataset = await AppendedAsync(store, outPerIn: ObjectBound.OutPerIn, ct);
        Assert.NotEmpty(store.Bounds);
        Assert.All(store.Bounds.Values, bound => Assert.Equal(-1, bound));
        store.Bounds.Clear();

        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(Compaction, ct));
        Assert.Equal(OperationOutcome.Applied, result.Outcome);
        ObjectEntry written = Assert.Single(await EntriesAsync(dataset, level: 1, ct));
        Assert.True(written.Bytes > StreamedFrom, $"an object of {written.Bytes} bytes does not stream");
        long bound = Assert.Single(store.Bounds).Value;
        Assert.Equal(ObjectBound.Of(result.BytesIn, 0), bound);
        Assert.InRange(written.Bytes, 1, bound);
    }

    [Fact]
    public async Task AnObjectPastItsBoundStartsItsCompactionAgainUnderOneTwiceAsLarge()
    {
        // Announced at a quarter of what the job reads, the object runs past its bound, which the sink
        // sees at the write that passes it, here before any put starts: the job gives the object up and
        // starts again under a bound twice as large, until one holds it, and its rows are all there. The
        // store sees one put, under the first bound that holds the object.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using BoundRecordingStore store = new BoundRecordingStore(new MemoryObjectStore());
        await using VortexDataset dataset = await AppendedAsync(store, outPerIn: 0.25, ct);
        store.Bounds.Clear();

        CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(Compaction, ct));
        Assert.Equal(OperationOutcome.Applied, result.Outcome);
        Assert.Equal(Appends * RowsEach, result.Rows);
        ObjectEntry written = Assert.Single(await EntriesAsync(dataset, level: 1, ct));
        int doublings = Doublings(Assert.Single(store.Bounds).Value, result.BytesIn, written.Bytes);
        Assert.True(doublings >= 2, $"the object of {written.Bytes} bytes held after {doublings} doublings of a quarter of {result.BytesIn}");
        Assert.Equal(Appends * RowsEach, await dataset.Scan<Measured>().CountAsync(ct));
    }

    /// <summary>
    /// How many times a rewrite announcing a quarter of <paramref name="bytesIn"/> doubled its bound before
    /// <paramref name="bound"/>, the first that holds an object of <paramref name="written"/> bytes.
    /// </summary>
    private static int Doublings(long bound, long bytesIn, long written)
    {
        for (int doublings = 0; doublings <= ObjectBound.MostDoublings; doublings++)
        {
            if (ObjectBound.Of(bytesIn, doublings, 0.25) == bound)
            {
                Assert.InRange(written, doublings == 0 ? 0 : ObjectBound.Of(bytesIn, doublings - 1, 0.25) + 1, bound);
                return doublings;
            }
        }

        Assert.Fail($"{bound} is no doubling of a quarter of {bytesIn}");
        return -1;
    }

    [Fact]
    public async Task ADeleteThatRewritesAnObjectStartsAgainUnderABoundTwiceAsLargeWithoutAnAttempt()
    {
        // An object under a mebibyte is rewritten by a delete rather than marked: its kept rows announce
        // what it weighs, here a quarter of it, run past it and start again under twice as much. Running
        // past a bound is none of another writer's doing, so it takes no attempt.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        await using BoundRecordingStore store = new BoundRecordingStore(new MemoryObjectStore());
        await using VortexDataset dataset = await AppendedAsync(store, outPerIn: 0.25, ct);
        Assert.IsType<CompactionResult>(await dataset.CompactAsync(Compaction with { }, ct));
        ObjectEntry merged = Assert.Single(await EntriesAsync(dataset, level: 1, ct));
        store.Bounds.Clear();

        RowChangeResult deleted = await dataset.DeleteAsync<Measured>(row => row.Key < 400, ct);
        Assert.Equal(OperationOutcome.Applied, deleted.Outcome);
        Assert.Equal(1, deleted.Attempts);
        Assert.Equal(400, deleted.Rows);
        ObjectEntry kept = Assert.Single(await EntriesAsync(dataset, level: 1, ct));
        int doublings = Doublings(Assert.Single(store.Bounds).Value, merged.Bytes, kept.Bytes);
        Assert.True(doublings >= 2, $"the object of {kept.Bytes} bytes held after {doublings} doublings of a quarter of {merged.Bytes}");
        Assert.Equal((Appends * RowsEach) - 400, await dataset.Scan<Measured>().CountAsync(ct));
    }

    private const int Appends = 4;

    private const int RowsEach = 20_000;

    /// <summary>The objects of level 0 merged into one object of level 1.</summary>
    private static CompactionOptions Compaction { get; } = new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 64L << 20 };

    /// <summary>
    /// A dataset of four appends of interleaved keys, each an object past the threshold, whose rewrites
    /// announce <paramref name="outPerIn"/> bytes a byte they read.
    /// </summary>
    private static async Task<VortexDataset> AppendedAsync(IObjectStore store, double outPerIn, CancellationToken ct)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Measured.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Measured.Key)],
            StreamedObjectBytes = StreamedFrom,
            BoundOutPerIn = outPerIn,
        }, ct);
        Random random = new Random(0xB0_0D);
        for (int append = 0; append < Appends; append++)
        {
            Measured[] rows = [.. Enumerable.Range(0, RowsEach).Select(i => new Measured(((long)i * Appends) + append, random.NextInt64()))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Measured>(rows, ct);
            await dataset.AppendAsync(draft, ct);
        }

        return dataset;
    }

    /// <summary>The entries of one level.</summary>
    private static async Task<List<ObjectEntry>> EntriesAsync(VortexDataset dataset, int level, CancellationToken ct)
    {
        List<ObjectEntry> entries = [];
        await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Snapshot.Pages, ct))
        {
            entries.Add(ObjectEntry.FromBytes(entry.Value));
        }

        return entries;
    }

    /// <summary>A store that records the bound each put of unknown length announced, by key.</summary>
    private sealed class BoundRecordingStore(IObjectStore inner) : ForwardingStore(inner), IObjectStore
    {
        /// <summary>The bound of every put of unknown length, -1 for none.</summary>
        internal ConcurrentDictionary<string, long> Bounds { get; } = new ConcurrentDictionary<string, long>();

        public override ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            if (length < 0)
            {
                Bounds.TryAdd(key, -1);
            }

            return Inner.PutAsync(key, content, length, condition, cancellationToken);
        }

        public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, long maxLength, PutCondition condition, CancellationToken cancellationToken)
        {
            if (length < 0)
            {
                Bounds[key] = maxLength;
            }

            return Inner.PutAsync(key, content, length, condition, cancellationToken);
        }

        public override async ValueTask DisposeAsync() => await Inner.DisposeAsync();
    }
}
