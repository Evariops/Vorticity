using System.Threading;

namespace Vorticity.Scanning;

/// <summary>
/// The locks the lanes of a scan meet in, kept by the process and lent to one scan at a time.
/// </summary>
/// <remarks>
/// A lock allocates on its first contention: a monitor makes a lock object the first time two
/// threads meet in it, and either kind makes a wait event the first time a thread blocks on it,
/// 40 bytes and 120 measured. A lock made per scan pays that in every scan, at whatever batch two of
/// its lanes first meet, and one blocked lane is enough. A lock the process keeps pays it once. The
/// slots are taken and given back with an exchange each, so lending allocates nothing, and past the
/// slots a lock is made, and goes to the collector when it comes back.
/// </remarks>
internal static class LaneGates
{
    /// <summary>The locks of each kind kept: two a scan of several lanes, for this many scans at once.</summary>
    private const int Kept = 64;

    private static readonly Lock?[] s_locks = new Lock?[Kept];
    private static readonly object?[] s_monitors = new object?[Kept];

    /// <summary>A lock for a scan's lanes, one the process has used before when it keeps one.</summary>
    internal static Lock RentLock() => Take(s_locks) ?? new Lock();

    /// <summary>Gives back a lock <see cref="RentLock"/> lent, once no thread holds or waits for it.</summary>
    internal static void Return(Lock gate) => Put(s_locks, gate);

    /// <summary>An object to wait and pulse on as a monitor, one the process has used before when it keeps one.</summary>
    internal static object RentMonitor() => Take(s_monitors) ?? new object();

    /// <summary>Gives back a monitor <see cref="RentMonitor"/> lent, once no thread holds or waits for it.</summary>
    internal static void ReturnMonitor(object gate) => Put(s_monitors, gate);

    private static T? Take<T>(T?[] slots)
        where T : class
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (Volatile.Read(ref slots[i]) is not null && Interlocked.Exchange(ref slots[i], null) is { } taken)
            {
                return taken;
            }
        }

        return null;
    }

    private static void Put<T>(T?[] slots, T gate)
        where T : class
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (Volatile.Read(ref slots[i]) is null && Interlocked.CompareExchange(ref slots[i], gate, null) is null)
            {
                return;
            }
        }
    }
}
