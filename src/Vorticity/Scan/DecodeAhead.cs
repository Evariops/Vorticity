using System;
using System.Threading;

namespace Vorticity.Scanning;

/// <summary>
/// The windows the inputs of one merge decode ahead past their own, shared by them all: an input takes one once the merge
/// reads it, from its second batch on, so that the input the merge drains has several windows decoding on lanes of the
/// session's while those the merge waits on hold their own two. As many at once as <see cref="PerInput"/>, each admitted by
/// the job's memory as it is taken, and given back as the merge passes the window.
/// </summary>
/// <remarks>
/// The merge takes its inputs in the order of their keys, so the inputs that read from the pool are those whose next windows
/// hold the keys it needs soonest: the windows start in the order of their first keys, whatever input they belong to, as far
/// as the pool admits. An input the merge does not read yet takes none, and the memory an input's price counts, two
/// batches, stays what it holds.
/// </remarks>
/// <param name="most">The windows the pool holds, and the most one input may take: the session's lanes but the merge's own.</param>
/// <param name="admits">Whether the job's memory admits one more window of the given decoded bytes.</param>
internal sealed class DecodeAhead(int most, Func<long, bool> admits)
{
    private int _free = most;
    private long _taken;

    /// <summary>The windows one input may take past its own, the most the pool holds.</summary>
    internal int PerInput { get; } = most;

    /// <summary>The windows taken since the pool was made, for a job's measures and the tests.</summary>
    internal long Taken => Interlocked.Read(ref _taken);

    /// <summary>The windows no input holds now.</summary>
    internal int Free => Volatile.Read(ref _free);

    /// <summary>Takes a window for a split of about <paramref name="bytes"/> decoded: false when none is free, or the job's memory would not hold it.</summary>
    internal bool TryTake(long bytes)
    {
        int free = Volatile.Read(ref _free);
        while (true)
        {
            if (free <= 0)
            {
                return false;
            }

            int seen = Interlocked.CompareExchange(ref _free, free - 1, free);
            if (seen == free)
            {
                break;
            }

            free = seen;
        }

        if (!admits(bytes))
        {
            Interlocked.Increment(ref _free);
            return false;
        }

        Interlocked.Increment(ref _taken);
        return true;
    }

    /// <summary>Gives back <paramref name="windows"/> windows the merge has passed.</summary>
    internal void Give(int windows) => Interlocked.Add(ref _free, windows);
}
