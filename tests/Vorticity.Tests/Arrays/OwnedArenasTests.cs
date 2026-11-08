using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Arrays;

/// <summary>
/// The arenas the batches a caller owns copy their rows into come back with the batch disposed, and the next copy takes one:
/// each copy reads the rows it was made of, whatever the copy before left in the arena.
/// </summary>
public sealed class OwnedArenasTests
{
    [Fact]
    public async Task EachCopyReadsItsOwnRowsInTheArenasCopiesGaveBack()
    {
        await using VortexFile file = await VortexFile.OpenAsync(await ContractFile.PathAsync(), TestContext.Current.CancellationToken);
        long row = 0;
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync(TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                Columns<Reading> columns = batch.As<Reading>();
                int[] days = new int[batch.RowCount];
                columns.Column<int>(0).CopyTo(days);
                for (int i = 0; i < days.Length; i++)
                {
                    Assert.Equal(ContractFile.Row((int)(row + i)).Day, days[i]);
                }

                row += days.Length;
                batches++;
            }

            // The arena of the copy disposed is kept for the next.
            Assert.True(OwnedArenas.Count > 0);
        }

        Assert.True(batches > 1);
        Assert.Equal(ContractFile.Rows, row);
    }
}
