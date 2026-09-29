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
}
