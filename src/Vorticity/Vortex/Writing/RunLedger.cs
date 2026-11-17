using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// The runs a merge's writers keep where they lie in the merge's input batches, numbered in the order the merge hands them
/// over, and how far every one of them is copied out: what tells the merge which of its inputs' batches no writer reads
/// any more, so that it gives them back by rank rather than have the writers copy them before an input moves on.
/// </summary>
/// <remarks>
/// <para>
/// A writer's staging opens in the ledger with its first such run and leaves it once its runs are copied, as its block is
/// assembled, on whichever lane assembles it. Everything numbered below the first run of the oldest staging still open is
/// copied (<see cref="Copied"/>). An input batch the merge moved past at <see cref="Noted"/> runs is read by no writer once
/// <see cref="Copied"/> reaches that number.
/// </para>
/// <para>
/// The merge's thread numbers the runs, one count a run and no lock but at a staging's first run; a staging leaves under the
/// lock, once a block: one lock a block, never one a run.
/// </para>
/// </remarks>
internal sealed class RunLedger
{
    private readonly Lock _gate = new Lock();

    // The stagings holding runs not copied yet, by their first run, oldest first.
    private readonly List<Opened> _open = [];
    private long _noted;
    private long _copied;

    /// <summary>The runs noted so far, on the merge's thread.</summary>
    internal long Noted => Volatile.Read(ref _noted);

    /// <summary>Every run numbered below is copied out of the inputs' batches.</summary>
    internal long Copied => Volatile.Read(ref _copied);

    /// <summary>Notes one run that <paramref name="holder"/>, a writer's staging, keeps where it lies; on the merge's thread.</summary>
    internal void Note(object holder, ref long first)
    {
        if (first < 0)
        {
            lock (_gate)
            {
                first = _noted;
                _open.Add(new Opened(holder, first));
                Volatile.Write(ref _noted, _noted + 1);
            }

            return;
        }

        Volatile.Write(ref _noted, _noted + 1);
    }

    /// <summary>
    /// Says that every run <paramref name="holder"/> kept is copied, or given up with its writer: it leaves the ledger, and
    /// may be noted again from <paramref name="first"/> reset.
    /// </summary>
    internal void Release(object holder, ref long first)
    {
        if (first < 0)
        {
            return;
        }

        lock (_gate)
        {
            for (int i = 0; i < _open.Count; i++)
            {
                if (ReferenceEquals(_open[i].Holder, holder))
                {
                    _open.RemoveAt(i);
                    break;
                }
            }

            first = -1;
            Volatile.Write(ref _copied, _open.Count > 0 ? _open[0].First : Volatile.Read(ref _noted));
        }
    }

    private readonly record struct Opened(object Holder, long First);
}
