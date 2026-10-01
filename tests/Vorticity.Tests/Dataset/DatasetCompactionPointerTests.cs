// The compaction pointer: the tree key a level's last job stopped at, which a round robin starts the
// level's next job past.
//
// A POINTER IS ONLY AS GOOD AS ITS MOVES: a replacement that applies moves its level's, one abandoned
// leaves it, every other commit carries it, and a handle opened afresh reads it back. The plan that
// takes it is held elsewhere, against a full read; here a dataset whose jobs run is held to taking a
// full level's objects in key order, which only a compactor that records where it stopped does.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetCompactionPointerTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public async Task AReplacementMovesItsLevelsPointerAndEveryCommitAfterItCarriesIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        for (int append = 0; append < 4; append++)
        {
            await AppendAsync(dataset, append, ct);
        }

        Assert.True(dataset.Levels.PointerOf(0).IsEmpty);
        byte[] key = [0x42, 0x17];
        await CommitAsync(store, new DatasetOperation.ReplaceObjects([], []) { Pointer = (0, key) }, ct);
        await dataset.RefreshAsync(ct);
        Assert.Equal(key, dataset.Levels.PointerOf(0).ToArray());

        // Carried by a commit that does not touch it, and read back by a handle opened afresh.
        await AppendAsync(dataset, 4, ct);
        Assert.Equal(key, dataset.Levels.PointerOf(0).ToArray());
        await using (VortexDataset fresh = await VortexDataset.OpenAsync(store, Options(), ct))
        {
            Assert.Equal(key, fresh.Levels.PointerOf(0).ToArray());
            Assert.Equal(key, Assert.Single(fresh.Snapshot.Header.Levels).Pointer.ToArray());
        }

        // A replacement whose input is gone is abandoned, and its pointer with it, though the batch it
        // came in commits.
        CommitResult mixed = await DatasetCommitter.CommitAsync(
            store,
            [
                new DatasetOperation.ReplaceObjects([(0, new byte[] { 0x01 })], []) { Pointer = (0, new byte[] { 0x99 }) },
                new DatasetOperation.ReplaceObjects([], []),
            ],
            new CommitOptions { Seed = Options().Seed },
            ct);
        Assert.Equal([OperationOutcome.Abandoned, OperationOutcome.Applied], mixed.Outcomes);
        Assert.Equal(mixed.Version, await dataset.RefreshAsync(ct));
        Assert.Equal(key, dataset.Levels.PointerOf(0).ToArray());
    }

    [Fact]
    public async Task ALevelThatEmptiesForgetsItsPointer()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        await AppendAsync(dataset, 0, ct);
        await CommitAsync(store, new DatasetOperation.ReplaceObjects([], []) { Pointer = (0, new byte[] { 0x42 }) }, ct);

        // Level 0 compacted whole: nothing is left for a pointer to sit among.
        await dataset.RefreshAsync(ct);
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, ct));
        Assert.True(dataset.Levels[0].IsEmpty);
        Assert.True(dataset.Levels.PointerOf(0).IsEmpty);
        await AppendAsync(dataset, 1, ct);
        Assert.True(dataset.Levels.PointerOf(0).IsEmpty);
    }

    [Fact]
    public async Task ARoundRobinMovesAFullLevelUpInKeyOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        for (int append = 0; append < 4; append++)
        {
            await AppendAsync(dataset, append, ct, rows: 400);
        }

        // Level 0 into level 1, in objects small enough that level 1 holds several.
        Assert.NotNull(await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0, MaxObjectBytes = 2 << 10 }, ct));
        int objects = (int)dataset.Levels[1].Entries;
        Assert.True(objects >= 4, $"level 1 holds {objects} objects");

        // Then a level 1 of a few kilobytes at most, far over its size: each job takes the object
        // after the one the last took, and records it.
        CompactionOptions options = new CompactionOptions
        {
            TargetBytesAtLevelOne = 256,
            Fanout = 2,
            MaxObjectBytes = 2 << 10,
            Pick = CompactionPick.RoundRobin,
        };
        List<ReadOnlyMemory<byte>> taken = [];
        while ((await dataset.PlanCompactionAsync(options, ct)).Job is { Trigger: CompactionTrigger.LevelSize, FromLevel: 1 } job)
        {
            ReadOnlyMemory<byte> source = job.Inputs[0].Key;
            CompactionResult result = Assert.IsType<CompactionResult>(await dataset.CompactAsync(options, ct));
            Assert.Equal(OperationOutcome.Applied, result.Outcome);
            Assert.Equal(Convert.ToHexString(source.Span), Convert.ToHexString(dataset.Levels.PointerOf(1).Span));
            taken.Add(source);
        }

        Assert.True(taken.Count >= 2, $"{taken.Count} job(s) took from level 1");
        for (int i = 1; i < taken.Count; i++)
        {
            Assert.True(taken[i].Span.SequenceCompareTo(taken[i - 1].Span) > 0, $"job {i} went back in key order");
        }

        Assert.Equal(1_600, await RowsAsync(dataset, ct));
        Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
    }

    [Fact]
    public void AHeaderCarriesALevelsPointerAndAHeaderWithoutOneReadsAsEmpty()
    {
        CommitHeader header = new CommitHeader
        {
            Version = 7,
            Levels =
            [
                new CommitLevel(0, 1, new PageReference(7, 0, 10, 1)) { Depth = 1, Rows = 5 },
                new CommitLevel(2, 3, new PageReference(6, 0, 10, 2)) { Depth = 1, Rows = 9, Pointer = new byte[] { 0x10, 0x20, 0x30 } },
            ],
        };

        CommitHeader read = CommitHeader.Read(Bytes(header));
        Assert.True(read.Levels[0].Pointer.IsEmpty);
        Assert.Equal([0x10, 0x20, 0x30], read.Levels[1].Pointer.ToArray());
        DatasetLevels levels = DatasetLevels.Of(read);
        Assert.Equal([0x10, 0x20, 0x30], levels.PointerOf(2).ToArray());
        Assert.True(levels.PointerOf(0).IsEmpty);
        Assert.True(levels.PointerOf(1).IsEmpty);
        Assert.True(levels.PointerOf(5).IsEmpty);
    }

    private static byte[] Bytes(CommitHeader header)
    {
        Vorticity.Serialization.Protobuf.ProtoWriter writer = new Vorticity.Serialization.Protobuf.ProtoWriter();
        try
        {
            header.Write(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static async Task<CommitResult> CommitAsync(IObjectStore store, DatasetOperation operation, CancellationToken ct) =>
        await DatasetCommitter.CommitAsync(store, [operation], new CommitOptions { Seed = Options().Seed }, ct);

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x9_014_7E2,
        ClusteringKey = ["Key"],
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
    };

    private static async Task<VortexDataset> CreateAsync(IObjectStore store, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        return await VortexDataset.CreateAsync(store, ChangeRow.Schema, Options(), ct);
    }

    /// <summary>Appends <paramref name="rows"/> rows, the <paramref name="append"/>-th thousand of keys.</summary>
    private static async Task AppendAsync(VortexDataset dataset, int append, CancellationToken ct, int rows = 50)
    {
        ChangeRow[] written = [.. Enumerable.Range(0, rows).Select(i => new ChangeRow((append * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(written, ct);
        await dataset.AppendAsync(draft, ct);
    }

    private static async Task<long> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Count;
}
