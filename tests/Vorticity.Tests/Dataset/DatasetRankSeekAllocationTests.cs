using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A selection on a warm cursor allocates nothing, text keys included, which are compared where a
/// cursor lends them; measured alone, as every allocation ceiling is, since a sorted column's zones
/// decode into buffers of the process-wide pool its neighbours drain.
/// </summary>
[Collection(nameof(AllocationCollection))]
public sealed class DatasetRankSeekAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASelectionOnAWarmCursorAllocatesNothing(bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Utf8(Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await DatasetRankSeekTests.BuildAsync(
            store, types, schema, DatasetRankSeekTests.Shape.Shared, text: true, sortedColumn, ct);
        await using DatasetKeyCursor cursor = await DatasetRankSeekTests.OpenAsync(dataset, sortedColumn, ct);

        // A first pass opens the objects and reads what the selections read; the ones after it
        // allocate nothing. Floored over several passes rather than measured on one: a one-off
        // inside a single pass - a tiered promotion is the usual one - once read as 4 032 bytes
        // over 64 selections. A selection that allocates raises every pass, so the floor still
        // catches it.
        const int Ranks = 64;
        const int Passes = 5;
        for (int rank = 0; rank < Ranks; rank++)
        {
            Assert.True(await cursor.SelectAsync(rank * 31, ct));
        }

        long floor = long.MaxValue;
        for (int pass = 0; pass < Passes; pass++)
        {
            int synchronous = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rank = 0; rank < Ranks; rank++)
            {
                ValueTask<bool> select = cursor.SelectAsync(rank * 31, ct);
                if (select.IsCompletedSuccessfully)
                {
                    synchronous++;
                }

                Assert.True(await select);
            }

            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(Ranks, synchronous);
            floor = Math.Min(floor, delta);
        }

        Assert.True(floor == 0, $"{Ranks} selections allocated {floor} bytes on each of {Passes} passes");
    }

    [Fact]
    public async Task ACursorOpensWithoutReadingAPageOfItsTrees()
    {
        // A walk finds its objects in the levels' trees as it reaches them: opening the cursor reads
        // nothing and holds nothing per object, whatever their number.
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int Objects = 20_000;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        DatasetOptions options = new DatasetOptions { Seed = 0xC0_45E5, ClusteringKey = ["key"] };
        await using (VortexDataset created = await VortexDataset.CreateAsync(store, schema, options, ct))
        {
        }

        DatasetOperation[] adds = new DatasetOperation[Objects];
        for (int i = 0; i < adds.Length; i++)
        {
            UInt128 uid = (UInt128)(ulong)i + 1;
            byte[] key = new byte[24];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, (ulong)(2L * i) ^ (1UL << 63));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(16), (ulong)uid);
            adds[i] = new DatasetOperation.AddObject(key, new ObjectEntry(CommitKey.ForData($"{i:x16}"), uid, 1_000, 1 << 20, uid)) { Level = 1 };
        }

        await DatasetCommitter.CommitAsync(store, adds, new CommitOptions { Seed = options.Seed }, ct);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, ct);
        store.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<DatasetKeyCursor> opening = DatasetKeyCursor.OpenAsync(dataset, ct);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(opening.IsCompletedSuccessfully);
        await using DatasetKeyCursor cursor = await opening;
        Assert.Equal(0, store.Requests);
        Assert.True(allocated < 1_024, $"a cursor opened over {Objects} objects allocated {allocated} bytes");
    }
}
