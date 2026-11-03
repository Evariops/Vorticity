using System;
using System.Collections.Generic;
using System.Threading;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The sparse indexes a dataset handle holds in hand, within a budget in bytes, least recently used first out: ClickHouse's
/// primary index cache. An index is admitted once its pages are read; the oldest let go are forgotten by their objects'
/// tables of contents, so that the next read of one reads it again, and a search that holds one finishes with it.
/// </summary>
/// <remarks>
/// An object's index is the key of its first row of each granule, held as its records with a whole key a run
/// (<see cref="GranuleKeys"/>): some 25 bytes a granule where neighbouring granules share most of their key, 80 KB for an
/// object of a gibibyte. Without a budget a handle kept every index it read as long as the tree pages naming the objects
/// lived, gigabytes over a month of objects. Thread-safe.
/// </remarks>
internal sealed class KeyIndexCache
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<GranuleKeys, LinkedListNode<Held>> _held = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Held> _order = [];
    private long _size;

    /// <summary>A cache of at most <paramref name="capacityBytes"/> bytes of indexes.</summary>
    /// <param name="capacityBytes">The budget; 0 keeps no index past the read that asked for it.</param>
    internal KeyIndexCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        Capacity = capacityBytes;
    }

    /// <summary>The budget in bytes.</summary>
    internal long Capacity { get; }

    /// <summary>The bytes of the indexes it holds now.</summary>
    internal long Size
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    /// <summary>The indexes it holds now.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>The indexes it let go to stay within its budget.</summary>
    internal long Evictions => Interlocked.Read(ref _evictions);

    private long _evictions;

    /// <summary>
    /// Holds <paramref name="keys"/>, every page in hand, as <paramref name="contents"/>' index, the most recently used; the
    /// oldest let go while the indexes held pass the budget, this one too when it passes it alone.
    /// </summary>
    internal void Admit(ObjectContents contents, GranuleKeys keys)
    {
        List<Held>? forgotten = null;
        lock (_gate)
        {
            if (_held.TryGetValue(keys, out LinkedListNode<Held>? known))
            {
                _order.Remove(known);
                _order.AddFirst(known);
                return;
            }

            Held held = new Held(contents, keys, keys.ResidentBytes);
            _held.Add(keys, _order.AddFirst(held));
            _size += held.Bytes;
            while (_size > Capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _held.Remove(oldest.Value.Keys);
                _size -= oldest.Value.Bytes;
                (forgotten ??= []).Add(oldest.Value);
            }
        }

        // Forgotten outside the gate: a search that holds one finishes with it, and the next read reads it again.
        if (forgotten is not null)
        {
            foreach (Held held in forgotten)
            {
                held.Contents.ForgetKeys(held.Keys);
            }

            Interlocked.Add(ref _evictions, forgotten.Count);
        }
    }

    /// <summary>Makes <paramref name="keys"/> the most recently used, when held: a read that found it in hand.</summary>
    internal void Touch(GranuleKeys keys)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(keys, out LinkedListNode<Held>? known) && known != _order.First)
            {
                _order.Remove(known);
                _order.AddFirst(known);
            }
        }
    }

    /// <summary>An index held, the contents that keep it, and the bytes it takes.</summary>
    private readonly record struct Held(ObjectContents Contents, GranuleKeys Keys, long Bytes);
}
