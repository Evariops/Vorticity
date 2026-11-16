using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Dataset;

/// <summary>
/// Values that never change, kept by key within a budget in bytes and read without a lock: a hit marks its slot used, and
/// past the budget the oldest kept goes first unless it was used since an eviction last passed it, which puts it back at
/// the newest end once (a clock, or second chance).
/// </summary>
/// <remarks>
/// <para>
/// What a dataset handle keeps of pages and synopses is read by every lane of every question and written once a value, the
/// first time a question reads it: a lookup that took a lock, as these did, made every lane wait on every other; one that
/// relinked its entry in an order of use wrote to its neighbours under it; and one kept first in first out let a value
/// asked at every question go as soon as the budget's worth of newer values came in, to be read and parsed again.
/// </para>
/// <para>
/// A value an eviction takes out while a lookup holds it stays valid, since nothing changes it: the lookup gets what the
/// value was and always will be. The lock is taken to keep a value, never to find one.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What a value is kept under.</typeparam>
/// <typeparam name="TValue">The values.</typeparam>
internal sealed class ClockMemo<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Slot> _slots;
    private readonly Queue<Slot> _order = new Queue<Slot>();
    private readonly Lock _gate = new Lock();
    private long _held;

    // The turn of the registry's slow timer under way: a value read or kept in it carries it.
    private int _turn;

    /// <summary>A memo of at most <paramref name="budget"/> bytes.</summary>
    /// <param name="budget">The bytes it holds at most; a value larger than it is never kept.</param>
    /// <param name="comparer">How keys compare; the default comparer for null.</param>
    internal ClockMemo(long budget, IEqualityComparer<TKey>? comparer = null)
        : this(budget, 0, comparer)
    {
    }

    private ClockMemo(long budget, long floor, IEqualityComparer<TKey>? comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentOutOfRangeException.ThrowIfNegative(floor);
        Budget = budget;
        Floor = floor;
        _slots = new ConcurrentDictionary<TKey, Slot>(comparer);
    }

    /// <summary>
    /// A memo with no budget of its own, as a dataset handle keeps them: it holds what questions read, and the registry of
    /// kept memory takes back what it holds above <paramref name="floor"/> (<see cref="LetGo"/>).
    /// </summary>
    /// <param name="floor">The bytes it keeps whatever the registry asks.</param>
    /// <param name="comparer">How keys compare; the default comparer for null.</param>
    internal static ClockMemo<TKey, TValue> Kept(long floor, IEqualityComparer<TKey>? comparer = null) =>
        new ClockMemo<TKey, TValue>(long.MaxValue, floor, comparer);

    /// <summary>The bytes it holds at most.</summary>
    internal long Budget { get; }

    /// <summary>The bytes it keeps whatever the registry of kept memory asks.</summary>
    internal long Floor { get; }

    /// <summary>The bytes it holds now.</summary>
    internal long Held => Interlocked.Read(ref _held);

    /// <summary>The values it holds now.</summary>
    internal int Count => _slots.Count;

    /// <summary>The value kept under <paramref name="key"/>, when one is; marks it used.</summary>
    internal bool TryGet(TKey key, out TValue value)
    {
        if (_slots.TryGetValue(key, out Slot? slot))
        {
            // Plain writes: a lookup that loses the mark to a concurrent eviction only gives the value one chance fewer.
            slot.Used = true;
            slot.Turn = _turn;
            value = slot.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Keeps <paramref name="value"/>, which holds <paramref name="bytes"/> bytes, under <paramref name="key"/>, unless a
    /// value is kept there already: the value the key holds after, this one or the first kept; false when the budget cannot
    /// hold it, <paramref name="kept"/> then being <paramref name="value"/>.
    /// </summary>
    internal bool Keep(TKey key, TValue value, long bytes, out TValue kept)
    {
        kept = value;
        if (bytes > Budget)
        {
            return false;
        }

        lock (_gate)
        {
            if (_slots.TryGetValue(key, out Slot? known))
            {
                kept = known.Value;
                return true;
            }

            Slot slot = new Slot(key, value, bytes) { Turn = _turn };
            _slots[key] = slot;
            _order.Enqueue(slot);
            long held = _held + bytes;

            // Every slot passed is unmarked; the slot just kept is passed over, so that a value is never let go by its own
            // keeping, and the walk ends before it is alone, since it fits the budget. Lookups beside the walk may mark the
            // slots again, which takes no lock: past one turn of the queue's marks, a mark is no longer heeded, and the walk
            // ends within two turns whatever the lookups do. Alone, it never heeds more than a turn's marks.
            int chances = _order.Count;
            while (held > Budget && _order.TryDequeue(out Slot? oldest))
            {
                if (ReferenceEquals(oldest, slot) || (oldest.Used && chances-- > 0))
                {
                    oldest.Used = false;
                    _order.Enqueue(oldest);
                    continue;
                }

                _slots.TryRemove(new KeyValuePair<TKey, Slot>(oldest.Key, oldest));
                held -= oldest.Bytes;
            }

            Interlocked.Exchange(ref _held, held);
            return true;
        }
    }

    /// <summary>
    /// Lets go of up to <paramref name="bytes"/> of the values held above <see cref="Floor"/>, the oldest kept first unless
    /// a lookup used it since an eviction last passed it, as an eviction does; only those no lookup read since the
    /// timer's turn before when <paramref name="stale"/>, which then starts a turn: the bytes let go. A lookup that holds
    /// a value let go keeps it valid, as an eviction leaves it.
    /// </summary>
    internal long LetGo(long bytes, bool stale)
    {
        long given = 0;
        lock (_gate)
        {
            long held = _held;
            int chances = _order.Count;
            for (int left = 2 * _order.Count; left > 0 && given < bytes && held > Floor && _order.TryDequeue(out Slot? oldest); left--)
            {
                if (stale ? oldest.Turn == _turn : oldest.Used && chances-- > 0)
                {
                    oldest.Used &= stale;
                    _order.Enqueue(oldest);
                    continue;
                }

                _slots.TryRemove(new KeyValuePair<TKey, Slot>(oldest.Key, oldest));
                held -= oldest.Bytes;
                given += oldest.Bytes;
            }

            Interlocked.Exchange(ref _held, held);
            if (stale)
            {
                _turn++;
            }
        }

        return given;
    }

    /// <summary>One value kept: its key, its bytes, and whether a lookup used it since an eviction last passed it.</summary>
    private sealed class Slot(TKey key, TValue value, long bytes)
    {
        internal TKey Key { get; } = key;

        internal TValue Value { get; } = value;

        internal long Bytes { get; } = bytes;

        internal bool Used { get; set; }

        /// <summary>The timer's turn the value was last kept or read in.</summary>
        internal int Turn { get; set; }
    }
}
