using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A rank seek on a dataset's key cursor lands on the entry a walk from the first reaches after as
/// many steps, whether it walks there or selects it over the objects, and the merge goes on from
/// it in order: keys shared by several objects, objects that overlap, and levels a compaction
/// made; integer and text keys, served by the objects' sorted runs or by their sorted columns.
/// </summary>
public sealed class DatasetRankSeekTests
{
    public enum Shape
    {
        /// <summary>Four appended objects drawing from the same few keys, each key in several of them.</summary>
        Shared,

        /// <summary>Four appended objects, each a run of keys of its own.</summary>
        Disjoint,

        /// <summary>Objects compacted into levels, then two appended over them.</summary>
        Compacted,
    }

    public static TheoryData<Shape, bool, bool> Cases
    {
        get
        {
            TheoryData<Shape, bool, bool> cases = [];
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                foreach (bool text in (bool[])[false, true])
                {
                    foreach (bool sortedColumn in (bool[])[false, true])
                    {
                        cases.Add(shape, text, sortedColumn);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRankLandsWhereTheWalkDoesAndTheMergeGoesOnFromIt(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType keyType = text ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(
            ["key", "measure"],
            [keyType, types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await BuildAsync(store, types, schema, shape, text, sortedColumn, ct);
        if (shape == Shape.Compacted)
        {
            Assert.True(dataset.Levels.Count > 1 && dataset.Levels[1].Entries > 0 && dataset.Levels[0].Entries > 0);
        }

        // The walk from the first is the merge's order, ties between objects included.
        List<(FilterLiteral Key, long Row, string Object)> walked = [];
        await using (DatasetKeyCursor walker = await OpenAsync(dataset, sortedColumn, ct))
        {
            for (bool ok = await walker.SeekFirstAsync(ct); ok; ok = await walker.NextAsync(ct))
            {
                walked.Add((walker.Key, walker.Row, walker.Object.Key));
            }
        }

        await using DatasetKeyCursor cursor = await OpenAsync(dataset, sortedColumn, ct);
        for (int rank = 0; rank < walked.Count; rank++)
        {
            Assert.True(await cursor.SelectAsync(rank, ct), $"rank {rank}");
            AssertAt(cursor, walked, rank);
            if (rank % 17 != 0)
            {
                continue;
            }

            // Every object is left where the merge would have it, the ones past the key included, and
            // the walk from there crosses every boundary between objects, the ones still waiting too.
            FilterLiteral key = walked[rank].Key;
            Assert.Equal(walked.FindAll(entry => entry.Key == key).Count, await cursor.KeyCountAsync(ct));
            for (int at = rank + 1; at < Math.Min(rank + 18, walked.Count); at++)
            {
                Assert.True(await cursor.NextAsync(ct));
                AssertAt(cursor, walked, at);
            }
        }

        // The public seek, which walks to the ranks below the number of objects and selects the others.
        for (int rank = 0; rank < walked.Count; rank += rank < 16 ? 1 : 97)
        {
            Assert.True(await cursor.SeekRankAsync(rank, ct));
            AssertAt(cursor, walked, rank);
        }

        Assert.False(await cursor.SelectAsync(walked.Count, ct));
        Assert.False(await cursor.SeekRankAsync(walked.Count, ct));
        Assert.False(await cursor.SeekRankAsync(-1, ct));
    }

    internal static ValueTask<DatasetKeyCursor> OpenAsync(VortexDataset dataset, bool sortedColumn, CancellationToken ct) =>
        DatasetKeyCursor.OpenAsync(dataset, dataset.Snapshot, "key", distinct: false, indexes: !sortedColumn, ct);

    private static void AssertAt(DatasetKeyCursor cursor, List<(FilterLiteral Key, long Row, string Object)> walked, int rank)
    {
        Assert.Equal(walked[rank].Key, cursor.Key);
        Assert.Equal(walked[rank].Row, cursor.Row);
        Assert.Equal(walked[rank].Object, cursor.Object.Key);
    }

    internal static async Task<VortexDataset> BuildAsync(
        MemoryObjectStore store, DTypeArena types, DType schema, Shape shape, bool text, bool sortedColumn, CancellationToken ct)
    {
        DatasetOptions options = new DatasetOptions
        {
            Seed = 0x5EE4_4A4E,
            Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
            ClusteringKey = ["key"],
        };

        VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        Random random = new Random(27);
        for (int part = 0; part < 4; part++)
        {
            int count = 300 + (part * 170);
            long[] keys = new long[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = shape == Shape.Disjoint ? (part * 10_000L) + i : random.Next(400);
            }

            await dataset.AppendAsync(Batches(types, schema, Ordered(keys, text, sortedColumn), text), ct);
        }

        if (shape != Shape.Compacted)
        {
            return dataset;
        }

        CompactionOptions compaction = new CompactionOptions
        {
            LevelZeroCeiling = 1,
            TargetBytesAtLevelOne = 4 << 10,
            MaxObjectBytes = 1L << 30,
        };
        while (await dataset.CompactAsync(compaction, ct) is not null)
        {
        }

        for (int part = 0; part < 2; part++)
        {
            long[] keys = new long[250];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = random.Next(400);
            }

            await dataset.AppendAsync(Batches(types, schema, Ordered(keys, text, sortedColumn), text), ct);
        }

        return dataset;
    }

    /// <summary>
    /// The keys as appended: in their own order for the sorted runs, which order them; sorted as the
    /// keys compare for a sorted column, which must be written in order to be one.
    /// </summary>
    private static long[] Ordered(long[] keys, bool text, bool sortedColumn)
    {
        if (sortedColumn)
        {
            Array.Sort(keys, (left, right) => text
                ? Text(left).AsSpan().SequenceCompareTo(Text(right))
                : left.CompareTo(right));
        }

        return keys;
    }

    /// <summary>A key's text, of a length that follows its digits, so that texts compare other than their numbers.</summary>
    private static byte[] Text(long key) => Encoding.ASCII.GetBytes($"k{key}");

    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, long[] keys, bool text)
    {
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        int key = text ? Texts(arena, types, keys) : Integers(arena, types, keys);
        VortexBuffer measureBuffer = arena.Allocate(keys.Length * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<double> measureValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < keys.Length; i++)
        {
            measureValues[i] = i;
        }

        int measure = arena.AddPrimitive(f64, keys.Length, Validity.NonNullable, PType.F64, measureBuffer);
        int root = arena.AddStruct(schema, keys.Length, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask;
    }

    private static int Integers(CanonicalArena arena, DTypeArena types, long[] keys)
    {
        VortexBuffer buffer = arena.Allocate(keys.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        keys.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(bytes));
        return arena.AddPrimitive(types.Primitive(PType.I64, Nullability.NonNullable), keys.Length, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>Texts of at most six bytes, each held inline in its view.</summary>
    private static int Texts(CanonicalArena arena, DTypeArena types, long[] keys)
    {
        VortexBuffer views = arena.Allocate(keys.Length * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            byte[] bytes = Text(keys[i]);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, bytes.Length);
            bytes.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(types.Utf8(Nullability.NonNullable), keys.Length, Validity.NonNullable, views, []);
    }
}
