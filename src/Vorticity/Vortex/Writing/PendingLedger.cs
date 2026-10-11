using System.Collections.Generic;

namespace Vorticity.Writing;

/// <summary>
/// Which of a writer's index builders hold a queued payload, and how many payloads are queued: the
/// next payload is the first of the lowest-ranked builder that holds one, found without walking the
/// builders that hold none.
/// </summary>
/// <remarks>
/// A builder enters the heap when its queue fills from empty and leaves it when a take empties it.
/// An abandoned builder's queue is emptied in place, and its entry is dropped when it reaches the
/// top, as is the stale one a builder refilled since leaves behind.
/// </remarks>
internal sealed class PendingLedger
{
    private readonly List<IndexBuilder> _heap = [];

    /// <summary>The payloads queued across every builder.</summary>
    internal int Payloads { get; private set; }

    /// <summary>A builder queued a payload.</summary>
    internal void Queued(IndexBuilder builder)
    {
        Payloads++;
        if (builder.Pending.Count == 1)
        {
            Push(builder);
        }
    }

    /// <summary>A builder dropped its queued payloads.</summary>
    internal void Dropped(int payloads) => Payloads -= payloads;

    /// <summary>The next payload: the first of the lowest-ranked builder that holds one.</summary>
    internal bool TryTake(out PendingPayload? payload)
    {
        while (_heap.Count > 0)
        {
            IndexBuilder top = _heap[0];
            if (top.Pending.TryDequeue(out payload))
            {
                Payloads--;
                if (top.Pending.Count == 0)
                {
                    Pop();
                }

                return true;
            }

            Pop();
        }

        payload = null;
        return false;
    }

    private void Push(IndexBuilder builder)
    {
        _heap.Add(builder);
        int at = _heap.Count - 1;
        while (at > 0)
        {
            int parent = (at - 1) >> 1;
            if (_heap[parent].Order <= builder.Order)
            {
                break;
            }

            _heap[at] = _heap[parent];
            at = parent;
        }

        _heap[at] = builder;
    }

    private void Pop()
    {
        int last = _heap.Count - 1;
        IndexBuilder moved = _heap[last];
        _heap.RemoveAt(last);
        if (last == 0)
        {
            return;
        }

        int at = 0;
        while (true)
        {
            int child = (2 * at) + 1;
            if (child >= last)
            {
                break;
            }

            if (child + 1 < last && _heap[child + 1].Order < _heap[child].Order)
            {
                child++;
            }

            if (_heap[child].Order >= moved.Order)
            {
                break;
            }

            _heap[at] = _heap[child];
            at = child;
        }

        _heap[at] = moved;
    }
}
