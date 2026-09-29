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

        // A first pass opens the objects and reads what the selections read; the second allocates nothing.
        const int Ranks = 64;
        for (int rank = 0; rank < Ranks; rank++)
        {
            Assert.True(await cursor.SelectAsync(rank * 31, ct));
        }

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
        Assert.True(delta == 0, $"{Ranks} selections allocated {delta} bytes");
    }
}
