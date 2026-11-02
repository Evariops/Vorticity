using System.Threading;

namespace Vorticity.Scanning;

/// <summary>
/// The splits the lanes of an aggregation's pass read ahead past their own share, given up by the lanes whose ranges are all
/// read: a lane's scan of a long range takes them while it reads, and gives them back as its reads land. Each lane was
/// granted its share of what the pass reserved, two splits at 32 lanes, and kept it to the end: the last long piece of a
/// pass read its splits two at a time while the others' share lay idle, the 99th percentile per service over S1's last
/// quarter of an hour (QP-6) at the S3 profile 570 ms of round trips one after another.
/// </summary>
/// <remarks>
/// A pass's pool has a number in a table of the process's, which the count of splits a scan reads ahead carries above its
/// own splits (<see cref="Carry"/>), from the pass's lanes to the scans of the objects they read: a field for it in the
/// builders and scans every question makes, beside that count, cost each of them its bytes. A pass gives its number back
/// when it ends (<see cref="Dispose"/>); a scan that meets a number given back reads ahead of its own alone.
/// </remarks>
internal sealed class ReadAheadPool : System.IDisposable
{
    /// <summary>The splits a lane's scan reads ahead at most, its own share and those it takes: what eight lanes reading two each keep in flight.</summary>
    internal const int Most = 16;

    // The splits a count carries below the pool's number, and the passes that hold a number at once.
    private const int SplitBits = 16;
    private const int Numbers = 255;

    private static readonly ReadAheadPool?[] s_pools = new ReadAheadPool?[Numbers + 1];

    private readonly int _number;
    private readonly int _lanes;
    private int _free;
    private int _finished;

    private ReadAheadPool(int number, int lanes)
    {
        _number = number;
        _lanes = lanes;
    }

    /// <summary>The splits no lane holds.</summary>
    internal int Free => Volatile.Read(ref _free);

    /// <summary>A pool numbered in the process's table for a pass of <paramref name="lanes"/> lanes; null when every number is held.</summary>
    internal static ReadAheadPool? Lend(int lanes)
    {
        for (int number = 1; number <= Numbers; number++)
        {
            if (Volatile.Read(ref s_pools[number]) is null)
            {
                ReadAheadPool pool = new ReadAheadPool(number, lanes);
                if (Interlocked.CompareExchange(ref s_pools[number], pool, null) is null)
                {
                    return pool;
                }
            }
        }

        return null;
    }

    /// <summary><paramref name="splits"/> read ahead, carrying <paramref name="pool"/>'s number when there is one.</summary>
    internal static int Carry(int splits, ReadAheadPool? pool) => pool is null || splits <= 0 ? splits : splits | (pool._number << SplitBits);

    /// <summary>The splits a count <see cref="Carry"/> made reads ahead of its own.</summary>
    internal static int SplitsOf(int carried) => carried & ((1 << SplitBits) - 1);

    /// <summary>The pool a count <see cref="Carry"/> made carries; null for none, or one whose pass ended.</summary>
    internal static ReadAheadPool? Of(int carried) =>
        (carried >> SplitBits) is int number and > 0 and <= Numbers && Volatile.Read(ref s_pools[number]) is { } pool ? pool : null;

    /// <summary>Gives back a split taken, once its read landed.</summary>
    internal void Give(int splits) => Interlocked.Add(ref _free, splits);

    /// <summary>A lane's ranges are all read: its share of <paramref name="splits"/> goes to the others.</summary>
    internal void Finished(int splits)
    {
        Interlocked.Increment(ref _finished);
        Give(splits);
    }

    /// <summary>
    /// Takes a split to read ahead; false when none is free, or while a quarter of the pass's lanes or more still read. Taken
    /// as lanes finished one by one, a lane's reads ahead filled the reads in flight while lanes still reading waited on
    /// theirs: the routes of one service under 5xx per hour over S1's spans (QP-1) at 32 lanes, 122 → 252 ms at the median.
    /// </summary>
    internal bool TryTake()
    {
        if (Volatile.Read(ref _finished) * 4 < _lanes * 3)
        {
            return false;
        }

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

    /// <summary>The pass ended: its number goes back to the table, and no split is taken from the pool again.</summary>
    public void Dispose()
    {
        Volatile.Write(ref _free, int.MinValue / 2);
        Interlocked.CompareExchange(ref s_pools[_number], null, this);
    }
}
