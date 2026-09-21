// The dataset's levels, and the lag they must report.
//
// WHAT A LEVEL IS FOR, in one sentence: without a merge policy, the number of objects a
// lookup touches is the number of appends. Levels are how that stops being true. What this file
// holds is the MACHINERY, apart from the policy (`DatasetCompactionTests`): a tree per level, a
// commit that writes several of them, a walk that merges them into one key order, and a lag that
// is reported rather than refused.
//
// THE ACCEPTANCE IS A SINGLE FILE'S ANSWER. Moving an object from level 0 to level 1 is what a
// compaction does; the rows it holds must not change because of where its entry sits. So the
// tests below move objects between levels by hand, through the same `ReplaceObjects` the
// compactor uses, and compare the answers against the same rows in one file.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetLevelTests
{
    private const ulong Seed = 0x1E7E15_5EED;

    private static CommitOptions Options() => new CommitOptions
    {
        Seed = Seed,
        Rule = new ProllyBoundaryRule(0x1E7E15_5EED, minBytes: 256, targetBytes: 512, maxBytes: 1_024),
        Template = new CommitHeader
        {
            Version = 1,
            ClusteringKey = ["k"],
            Chunker = new ChunkerSettings(256, 512, 1_024),
        },
    };

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");

    private static ObjectEntry Object(int i, int version = 0) => new ObjectEntry(
        CommitKey.ForData($"{i:x8}{version:x8}"),
        ((UInt128)(uint)i << 32) | (uint)version,
        i + 1,
        4_096,
        (UInt128)(uint)i);

    private static DatasetOperation Add(int i, int level = 0) =>
        new DatasetOperation.AddObject(Key(i), Object(i)) { Level = level };

    [Fact]
    public async Task ACommitWritesATreePerLevelAndAReaderFindsThemAll()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        CommitResult result = await DatasetCommitter.CommitAsync(
            store,
            [Add(1), Add(2), Add(10, level: 1), Add(11, level: 1), Add(100, level: 3)],
            Options(),
            default);

        Assert.Equal(2, result.Levels[0].Entries);
        Assert.Equal(2, result.Levels[1].Entries);
        Assert.Equal(0, result.Levels[2].Entries);
        Assert.Equal(1, result.Levels[3].Entries);
        Assert.Equal(5, result.Levels.Entries);

        // A level nobody filled is an empty tree, not a missing one: its NUMBER is its meaning.
        Assert.Equal(4, result.Levels.Count);
        Assert.True(result.Levels[2].IsEmpty);

        // And the header carries them, so another reader finds the same shape.
        (ulong version, CommitObject? commit) = await DatasetCommitter.LatestAsync(store, default);
        DatasetLevels read = DatasetLevels.Of(Assert.IsType<CommitObject>(commit).Header);
        Assert.Equal(result.Levels.Entries, read.Entries);
        Assert.Equal(result.Levels.Rows, read.Rows);
        Assert.Equal(1UL, version);
        for (int level = 0; level < 4; level++)
        {
            Assert.Equal(result.Levels[level].Entries, read[level].Entries);
            Assert.Equal(result.Levels[level].Root, read[level].Root);
        }
    }

    [Fact]
    public async Task ACompactionMovesObjectsBetweenLevelsInOneOperation()
    {
        // A leveled compaction reads level 0 and the overlapping objects of level 1, and writes
        // level 1. One operation, because a compaction that lost its input to a race must abandon
        // all of it at once.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1), Add(2), Add(3)], Options(), default);
        await DatasetCommitter.CommitAsync(store, [Add(50, level: 1)], Options(), default);

        DatasetOperation compaction = new DatasetOperation.ReplaceObjects(
            [(0, Key(1)), (0, Key(2)), (1, Key(50))],
            [(1, Key(1), Object(1, version: 7))]);
        CommitResult after = await DatasetCommitter.CommitAsync(store, [compaction], Options(), default);

        Assert.Equal([OperationOutcome.Applied], after.Outcomes);
        Assert.Equal(1, after.Levels[0].Entries);
        Assert.Equal(1, after.Levels[1].Entries);
        Assert.NotNull(await after.Levels[0].FindAsync(Key(3), after.Pages, default));
        Assert.NotNull(await after.Levels[1].FindAsync(Key(1), after.Pages, default));

        // The same key at two levels is two objects, so the input at level 0 is gone and the
        // output at level 1 is there.
        Assert.Null(await after.Levels[0].FindAsync(Key(1), after.Pages, default));
    }

    [Fact]
    public async Task ACompactionWhoseInputMovedLevelAbandons()
    {
        // Across levels too, a compaction whose input is gone abandons: a compactor that planned
        // against level 0 and lost the race to one that already moved the object finds its input
        // missing, and its outputs are garbage.
        await using MemoryObjectStore store = new MemoryObjectStore();
        await DatasetCommitter.CommitAsync(store, [Add(1), Add(2)], Options(), default);

        DatasetOperation first = new DatasetOperation.ReplaceObjects(
            [(0, Key(1))], [(1, Key(1), Object(1, version: 3))]);
        Assert.Equal([OperationOutcome.Applied],
            (await DatasetCommitter.CommitAsync(store, [first], Options(), default)).Outcomes);

        DatasetOperation second = new DatasetOperation.ReplaceObjects(
            [(0, Key(1)), (0, Key(2))], [(1, Key(2), Object(2, version: 4))]);
        CommitResult loser = await DatasetCommitter.CommitAsync(store, [second], Options(), default);

        Assert.Equal([OperationOutcome.Abandoned], loser.Outcomes);
        Assert.Equal(1, loser.Levels[0].Entries);
        Assert.Equal(1, loser.Levels[1].Entries);
    }

    [Fact]
    public async Task AScanOverSeveralLevelsAnswersAsOneFile()
    {
        // The answers equal one file's with the objects spread over levels: where an entry SITS
        // changes nothing about the rows it holds.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0x1E7E15_5EED,
            Write = new VortexWriteOptions { RowBlockSize = 128, DataBlockTargetBytes = 8 << 10 },
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options);

        const int objects = 6;
        for (int i = 0; i < objects; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * 200, 200));
        }

        List<long> before = await KeysAsync(dataset.Scan());
        Assert.Equal(objects * 200, before.Count);

        // Move every other object down a level, by hand, the way a compaction would.
        List<(int Level, ReadOnlyMemory<byte> Key)> inputs = [];
        List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> outputs = [];
        int at = 0;
        await foreach (PositionedObject held in dataset.Scan().ObjectsAsync())
        {
            if (at++ % 2 == 0)
            {
                ReadOnlyMemory<byte> key = await KeyOfAsync(dataset, held.Entry);
                inputs.Add((0, key));
                outputs.Add((1, key, held.Entry));
            }
        }

        Assert.Equal(3, inputs.Count);
        await dataset.ApplyAsync([new DatasetOperation.ReplaceObjects(inputs, outputs)]);

        Assert.Equal(3, dataset.Levels[0].Entries);
        Assert.Equal(3, dataset.Levels[1].Entries);
        Assert.Equal(objects, dataset.ObjectCount);
        Assert.Equal(objects * 200, dataset.RowCount);

        // The same rows, in the same order, and the same as one file's.
        Assert.Equal(before, await KeysAsync(dataset.Scan()));

        byte[] single = await OneFileAsync(types, schema, objects * 200, options.Write);
        await using MemorySegmentSource source = new MemorySegmentSource(single);
        await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions(), default);
        Assert.Equal(await KeysAsync(file.ScanBuilder()), await KeysAsync(dataset.Scan()));

        // And `Rows(a, b)` still addresses the dataset's order across the levels it now spans.
        Assert.Equal(
            await KeysAsync(file.ScanBuilder().Rows(new RowRange(150, 450))),
            await KeysAsync(dataset.Rows(150, 450)));
    }

    [Fact]
    public async Task ExplainReportsTheLagAndRefusesNothing()
    {
        // `Explain` reports every violation of the level-0 ceiling as a lag, with the count: a
        // level-0 count above 8 degrades the read bound and is reported, never refused.
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0x1E7E15_5EED,
            Write = new VortexWriteOptions { RowBlockSize = 128, DataBlockTargetBytes = 8 << 10 },
        };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options);

        for (int i = 0; i < 8; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * 100, 100));
        }

        DatasetPlan holding = await dataset.Scan().ExplainAsync();
        Assert.Equal(0, holding.Lag);
        Assert.Equal(8, holding.Objects);
        Assert.Equal([8L], holding.ObjectsByLevel);
        Assert.False(holding.IsClustered);

        // Three more, and the invariant is violated on purpose. Nothing is refused.
        for (int i = 8; i < 11; i++)
        {
            await dataset.AppendAsync(Batches(types, schema, i * 100, 100));
        }

        DatasetPlan lagging = await dataset.Scan().ExplainAsync();
        Assert.Equal(3, lagging.Lag);
        Assert.Equal(11, lagging.Objects);
        Assert.Equal(1_100, lagging.Rows);
        Assert.Equal(1_100, await dataset.Scan().CountAsync());
        Assert.Equal(3, dataset.Lag);

        Console.Out.Write(FormattableString.Invariant(
            $"DATASET LAG: level 0 holds {lagging.ObjectsByLevel[0]} objects against a ceiling of 8, so Explain reports a lag of {lagging.Lag}; the scan still answered {lagging.Rows} rows.\n"));
    }

    /// <summary>The tree key an entry sits at, found by walking — the test has no other way in.</summary>
    private static async Task<ReadOnlyMemory<byte>> KeyOfAsync(VortexDataset dataset, ObjectEntry entry)
    {
        await foreach (TreeEntry held in dataset.Levels[0].EnumerateAsync(dataset.Pages, default))
        {
            if (ObjectEntry.FromBytes(held.Value.Span).Uid == entry.Uid)
            {
                return held.Key;
            }
        }

        throw new InvalidOperationException($"'{entry.Key}' is not in level 0.");
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static async Task<List<long>> KeysAsync(ScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async Task<byte[]> OneFileAsync(
        DTypeArena types, DType schema, int rows, VortexWriteOptions options)
    {
        System.IO.MemoryStream stream = new System.IO.MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(
            new StreamSegmentSink(stream), schema, options))
        {
            await foreach (RecordBatch batch in Batches(types, schema, 0, rows))
            {
                await writer.WriteAsync(batch);
            }

            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }

    private static async IAsyncEnumerable<RecordBatch> Batches(
        DTypeArena types, DType schema, long from, int rows)
    {
        const int size = 100;
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        for (int start = 0; start < rows; start += size)
        {
            int count = Math.Min(size, rows - start);
            CanonicalArena arena = new CanonicalArena();
            VortexBuffer keys = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
            VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
            Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
            Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
            for (int row = 0; row < count; row++)
            {
                keyValues[row] = from + start + row;
                measureValues[row] = (from + start + row) / 4.0;
            }

            int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keys);
            int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            yield return batch;
            await Task.CompletedTask;
        }
    }
}
