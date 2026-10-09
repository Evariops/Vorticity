using System;
using System.Threading;

namespace Vorticity;

/// <summary>
/// The bytes the queries of the sessions that share it may hold in their scratch at once: the parts a
/// group by spills past its memory budget, the runs of a sort its memory does not hold. A host makes
/// one and gives it to each session it shares through <see cref="VortexSessionOptions.ScratchBudget"/>.
/// </summary>
/// <remarks>
/// <para>
/// The free space a file system reports does not show every limit on it. In a Kubernetes pod, the
/// writable layer and an <c>emptyDir</c> count against the pod's <c>ephemeral-storage</c>, which the
/// kubelet enforces by evicting the pod, while the disk under them still shows free space. A budget at
/// that limit, less what the pod writes besides, keeps the queries' scratch under it.
/// </para>
/// <para>
/// A query that needs more scratch than the budget grants fails with a
/// <see cref="VortexMemoryException"/>, its files gone and everything it held given back. Without a
/// budget, the spills of a process keep a tenth of their directory's free space, as they do with one.
/// </para>
/// </remarks>
public sealed class ScratchBudget
{
    private readonly long _ceiling;
    private long _reserved;

    /// <summary>A budget under which the queries of its sessions hold <paramref name="ceilingBytes"/> of scratch at most, together.</summary>
    /// <param name="ceilingBytes">The bytes they may hold at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ceiling is not positive.</exception>
    public ScratchBudget(long ceilingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceilingBytes);
        _ceiling = ceilingBytes;
    }

    /// <summary>The bytes of scratch the queries under the budget may hold at once.</summary>
    public long CeilingBytes => _ceiling;

    /// <summary>The bytes of scratch they hold now.</summary>
    public long ReservedBytes => Volatile.Read(ref _reserved);

    /// <summary>Reserves <paramref name="bytes"/>; false, and nothing reserved, past the ceiling.</summary>
    internal bool TryReserve(long bytes)
    {
        long reserved = Volatile.Read(ref _reserved);
        while (true)
        {
            if (reserved + bytes > _ceiling)
            {
                return false;
            }

            long seen = Interlocked.CompareExchange(ref _reserved, reserved + bytes, reserved);
            if (seen == reserved)
            {
                return true;
            }

            reserved = seen;
        }
    }

    /// <summary>Gives back <paramref name="bytes"/> the queries held: their scratch is gone.</summary>
    internal void Release(long bytes)
    {
        if (bytes != 0)
        {
            Interlocked.Add(ref _reserved, -bytes);
        }
    }
}
