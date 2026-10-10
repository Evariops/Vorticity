using System.Threading;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// The lanes of the pool a scan lends its column readers to decompress pages ahead of those they read:
/// as many decompressions at once as the scan's degree, shared by every column, so that a wide file's
/// columns and a narrow one's next pages alike keep them busy.
/// </summary>
/// <param name="lanes">The decompressions that may run at once.</param>
internal sealed class PageLanes(int lanes)
{
    /// <summary>The data pages a column reader keeps decompressed or decompressing ahead of the one it reads.</summary>
    internal const int Depth = 2;

    private int _free = lanes;

    /// <summary>Takes a lane; false when every one is busy, and the page is decompressed when it is read.</summary>
    internal bool TryTake()
    {
        int free = Volatile.Read(ref _free);
        while (free > 0)
        {
            int seen = Interlocked.CompareExchange(ref _free, free - 1, free);
            if (seen == free)
            {
                return true;
            }

            free = seen;
        }

        return false;
    }

    /// <summary>Gives a lane back.</summary>
    internal void Give() => Interlocked.Increment(ref _free);
}
