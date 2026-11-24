// Row keys encoded for a piece of work, a part of a dataset sorted before it is written, weigh as much as the rows they order:
// their blocks and their sort's scratch are counted in the work's account.
using System;
using Vorticity.Buffers;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowKeysAccountTests
{
    /// <summary>
    /// The keys' blocks are held in the account while the keys live, a sort by radix gives back its scratch when it ends, and
    /// the keys' disposal gives back the rest: the account holds what it held before.
    /// </summary>
    [Fact]
    public void KeysEncodedForAWorkAreCountedInItsAccountUntilTheyGo()
    {
        using RowFixture fixture = new RowFixture();
        const int Rows = 100_000;
        long[] values = new long[Rows];
        Random random = new Random(7);
        for (int i = 0; i < Rows; i++)
        {
            values[i] = random.NextInt64();
        }

        int column = fixture.Primitive<long>(PType.I64, values);
        using WorkAccount account = new WorkAccount(QueryMemoryBudget.Process, "row keys test");
        long before = account.Held;
        RowKeys keys = RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending], account);
        long encoded = account.Held - before;

        // Nine bytes a key at the least, and a size and an offset a row.
        Assert.True(encoded >= (long)Rows * (9 + 4 + 4), $"{encoded} bytes counted for {Rows} keys");

        int[] order = new int[Rows];
        for (int i = 0; i < Rows; i++)
        {
            order[i] = i;
        }

        keys.SortStable(order);
        Assert.Equal(encoded, account.Held - before);
        for (int i = 1; i < Rows; i++)
        {
            Assert.True(values[order[i - 1]] <= values[order[i]], $"rows {order[i - 1]} and {order[i]} out of order");
        }

        keys.Dispose();
        Assert.Equal(before, account.Held);
    }
}
