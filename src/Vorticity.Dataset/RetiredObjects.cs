using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// The data objects a handle's compactions replaced, which it deletes itself once no retained version reads them: the
/// replacement committed, and the version before it past the retention, as a vacuum would judge it. Kept in the handle,
/// never in the store: what a handle closed or a process ended leaves noted is the vacuum's to take.
/// </summary>
/// <remarks>
/// <para>
/// A version stays retained while the commit that superseded it is younger than the window, or while it is among the
/// versions the dataset keeps whatever their age (<see cref="DatasetVacuum"/>). An object a commit of version
/// <c>R</c> took out is read by version <c>R - 1</c> last, so it goes once that version is no longer retained. The
/// commit's time is when the handle knew it to be the latest, a little after the store dated it: the deletion comes a
/// little later than a vacuum's would, never sooner.
/// </para>
/// <para>
/// A delete that fails leaves the objects noted, for the next turn or the vacuum. A store under a retention lock deletes
/// nothing before its lock lets go, which the vacuum knows how to wait for: a handle on a locked store notes nothing.
/// </para>
/// </remarks>
internal sealed class RetiredObjects
{
    /// <summary>The most keys a delete asks the store for at once.</summary>
    private const int DeleteBatch = 1_000;

    private readonly Lock _gate = new Lock();
    private readonly Queue<(ulong Version, DateTimeOffset At, string[] Keys)> _noted = new();
    private int _deleting;

    /// <summary>The objects noted and not deleted yet.</summary>
    internal int Pending
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach ((_, _, string[] keys) in _noted)
                {
                    count += keys.Length;
                }

                return count;
            }
        }
    }

    /// <summary>The objects deleted since the handle was opened.</summary>
    internal long Deleted => Interlocked.Read(ref _deleted);

    private long _deleted;

    /// <summary>The last failure of a delete, which left its objects noted; null when none failed.</summary>
    internal Exception? LastFailure { get; private set; }

    /// <summary>Notes <paramref name="keys"/>, which the commit of <paramref name="version"/>, known at <paramref name="at"/>, took out of the dataset.</summary>
    internal void Note(ulong version, DateTimeOffset at, string[] keys)
    {
        if (keys.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _noted.Enqueue((version, at, keys));
        }
    }

    /// <summary>
    /// Deletes the objects noted that no retained version reads any more, at <paramref name="now"/>, the latest version
    /// being <paramref name="latest"/> under <paramref name="retention"/>: the bytes the store gives back.
    /// </summary>
    /// <returns>How many objects were deleted.</returns>
    internal async ValueTask<int> DeleteDueAsync(
        IObjectStore store, ulong latest, RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _deleting, 1) != 0)
        {
            return 0;
        }

        try
        {
            TimeSpan window = DatasetVacuum.WindowOf(retention);
            List<string> due = [];
            lock (_gate)
            {
                // In the order the commits came: the first not due stops the turn, the later ones being younger.
                while (_noted.TryPeek(out (ulong Version, DateTimeOffset At, string[] Keys) noted)
                    && now - noted.At >= window
                    && latest >= noted.Version
                    && latest - noted.Version + 1 > (ulong)Math.Max(0, retention.Versions))
                {
                    _noted.Dequeue();
                    due.AddRange(noted.Keys);
                }
            }

            int deleted = 0;
            for (int from = 0; from < due.Count; from += DeleteBatch)
            {
                List<string> batch = due.GetRange(from, Math.Min(DeleteBatch, due.Count - from));
                try
                {
                    await store.DeleteAsync(batch, cancellationToken).ConfigureAwait(false);
                    deleted += batch.Count;
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // Left noted for the next turn; the vacuum takes them otherwise.
                    LastFailure = failure;
                    lock (_gate)
                    {
                        _noted.Enqueue((1, DateTimeOffset.MinValue, [.. due.GetRange(from, due.Count - from)]));
                    }

                    break;
                }
            }

            Interlocked.Add(ref _deleted, deleted);
            return deleted;
        }
        finally
        {
            Volatile.Write(ref _deleting, 0);
        }
    }
}
