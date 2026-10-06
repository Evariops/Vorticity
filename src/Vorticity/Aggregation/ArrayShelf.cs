using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// The arrays the core's sub-tables leave as they grow and split, and its slabs of batches, taken again
/// by the next that needs one of the same type and length (PLAN-HIGH-CARDINALITY, H4): the core's pool.
/// A doubling and a split make the same few lengths over and over, so that past its first sub-tables a
/// query allocates little more than the memory it holds. A query's shelf hands what it holds at the end
/// to the process's (<see cref="Retained"/>), which keeps it under a budget for the next query and lets
/// it go after a collection that finds it idle.
/// </summary>
/// <remarks>
/// One lock for every type and length: a sub-table takes and gives arrays when it grows or splits, a
/// few times for thousands of entries, and the lanes that hold parts rarely meet on it. The arrays are
/// ordinary ones: pinned, each small one went through the lock the runtime takes for the pinned heap,
/// which fourteen lanes splitting at once waited on more than they worked.
/// </remarks>
internal sealed class ArrayShelf : ISweptAfterCollections
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(Type Type, int Length), Pile> _piles = [];
    private readonly ArrayShelf? _parent;
    private readonly long _budget;
    private long _held;
    private bool _used;

    /// <summary>A query's shelf, which takes from the process's when it holds nothing of a length, and hands it what it holds at the end.</summary>
    internal ArrayShelf()
        : this(Retained, long.MaxValue)
    {
    }

    private ArrayShelf(ArrayShelf? parent, long budget)
    {
        _parent = parent;
        _budget = budget;
    }

    /// <summary>
    /// The process's shelf: what queries left, up to a quarter of a gigabyte or a sixteenth of the
    /// memory the process may use, whichever is less, for the next query not to touch fresh memory
    /// again. Swept after collections: emptied when no query took from it since the last, or when the
    /// machine's memory load is high.
    /// </summary>
    internal static ArrayShelf Retained { get; } = NewRetained();

    private static ArrayShelf NewRetained()
    {
        ArrayShelf shelf = new ArrayShelf(parent: null, Math.Min(256L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16));
        CollectionSweeper<ArrayShelf>.Register(shelf);
        return shelf;
    }

    /// <summary>The bytes the shelf holds.</summary>
    internal long Held
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>An array of <paramref name="length"/> elements, from the shelf, the process's, or new; zeroed when <paramref name="zeroed"/>.</summary>
    internal T[] Take<T>(int length, bool zeroed)
    {
        Array? found = null;
        lock (_gate)
        {
            _used = true;
            if (_piles.TryGetValue((typeof(T), length), out Pile? pile) && pile.Arrays.Count > 0)
            {
                found = pile.Arrays.Pop();
                _held -= (long)length * pile.ElementBytes;
            }
        }

        if (found is T[] array)
        {
            if (zeroed)
            {
                Array.Clear(array);
            }

            return array;
        }

        return _parent is not null ? _parent.Take<T>(length, zeroed)
            : zeroed ? new T[length]
            : GC.AllocateUninitializedArray<T>(length);
    }

    /// <summary>Puts an array nothing holds any more back on the shelf; past the shelf's budget, lets it go.</summary>
    internal void Give<T>(T[] array) => Give(typeof(T), array, Unsafe.SizeOf<T>());

    /// <summary>
    /// Hands every array the shelf holds to the process's, as far as its budget takes them: the pass is
    /// over, and its sub-tables, which keep this shelf, are the result.
    /// </summary>
    internal void Clear()
    {
        List<(Type Type, Pile Pile)> piles = [];
        lock (_gate)
        {
            foreach (((Type type, int _), Pile pile) in _piles)
            {
                piles.Add((type, pile));
            }

            _piles.Clear();
            _held = 0;
        }

        if (_parent is null)
        {
            return;
        }

        foreach ((Type type, Pile pile) in piles)
        {
            foreach (Array array in pile.Arrays)
            {
                _parent.Give(type, array, pile.ElementBytes);
            }
        }
    }

    /// <summary>An array of elements of <paramref name="type"/>, <paramref name="elementBytes"/> each, on the shelf, within its budget.</summary>
    private void Give(Type type, Array array, int elementBytes)
    {
        int length = array.Length;
        if (length == 0)
        {
            return;
        }

        long bytes = (long)length * elementBytes;
        lock (_gate)
        {
            if (_held + bytes > _budget)
            {
                return;
            }

            if (!_piles.TryGetValue((type, length), out Pile? pile))
            {
                pile = new Pile(elementBytes);
                _piles.Add((type, length), pile);
            }

            pile.Arrays.Push(array);
            _held += bytes;
        }
    }

    /// <summary>After a collection: the process's shelf emptied when no query took from it since the last sweep, or under a high memory load.</summary>
    void ISweptAfterCollections.Sweep()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        bool pressed = memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes;
        lock (_gate)
        {
            if (!_used || pressed)
            {
                _piles.Clear();
                _held = 0;
            }

            _used = false;
        }
    }

    /// <summary>The arrays of one type and length, and the bytes of an element.</summary>
    private sealed class Pile(int elementBytes)
    {
        internal Stack<Array> Arrays { get; } = new Stack<Array>();

        internal int ElementBytes { get; } = elementBytes;
    }
}
