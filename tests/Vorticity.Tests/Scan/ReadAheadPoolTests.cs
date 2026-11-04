// The splits the lanes of a pass give up to those still reading: lent only at the pass's end, carried in a scan's count of
// splits, and gone with the pass.
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ReadAheadPoolTests
{
    /// <summary>
    /// A pass of four lanes, each reading two splits ahead: while two have finished, their four splits stay in the pool;
    /// once three have, a scan takes them, and gives them back. The count of a scan's splits carries the pool's number and
    /// its own two; once the pass ends, the number names no pool.
    /// </summary>
    [Fact]
    public void APoolLendsTheSplitsOfFinishedLanesOnlyOnceThreeQuartersOfThemFinished()
    {
        int carried;
        using (ReadAheadPool pool = ReadAheadPool.Lend(lanes: 4)!)
        {
            carried = ReadAheadPool.Carry(2, pool);
            Assert.Equal(2, ReadAheadPool.SplitsOf(carried));
            Assert.Same(pool, ReadAheadPool.Of(carried));

            pool.Finished(2);
            pool.Finished(2);
            Assert.Equal(4, pool.Free);
            Assert.False(pool.TryTake());

            pool.Finished(2);
            Assert.True(pool.TryTake());
            Assert.Equal(5, pool.Free);
            pool.Give(1);
            Assert.Equal(6, pool.Free);
        }

        Assert.Null(ReadAheadPool.Of(carried));
        Assert.Equal(2, ReadAheadPool.SplitsOf(carried));
    }
}
