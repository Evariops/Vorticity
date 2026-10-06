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
    /// <summary>
    /// The least array a lane's shelf counts in its query's memory, a page: a table's first arrays, which
    /// every table makes outside a shelf and gives to it when it grows, are smaller, and go uncounted
    /// both ways.
    /// </summary>
    private const long LeastCounted = 4096;

    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(Type Type, int Length), Pile> _piles = [];
    private readonly ArrayShelf? _parent;
    private readonly long _budget;
    private long _held;
    private bool _used;

    // A lane's shelf: its query's memory; the bytes of the arrays it handed out that its tables hold,
    // and what it reserved ahead of the next ones.
    private readonly QueryMemory? _memory;
    private long _out;
    private long _credit;

    /// <summary>A query's shelf, which takes from the process's when it holds nothing of a length, and hands it what it holds at the end.</summary>
    internal ArrayShelf()
        : this(Retained, long.MaxValue)
    {
    }

    /// <summary>
    /// A lane's shelf under its query's memory (PLAN-HIGH-CARDINALITY, H2, decision 13): an array it
    /// hands out is reserved before it is allocated, and given back when its table lets it go, which
    /// leaves it to the next collection; it keeps none. A table that doubles reserves its new arrays
    /// while it still holds the old ones: what the copy holds, no more. It reserves a megabyte ahead,
    /// and keeps up to two of what its tables give back, so that the pages of a key numbered by value,
    /// sixteen kilobytes each, meet the budget's count once a megabyte; one lane uses it at a time,
    /// and it takes no lock.
    /// </summary>
    internal ArrayShelf(QueryMemory memory)
        : this(parent: null, budget: 0) => _memory = memory;

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

    /// <summary>The bytes of the arrays a lane's shelf handed out that its tables hold: what its query's memory counts of them.</summary>
    internal long Out => _out;

    /// <summary>
    /// <paramref name="array"/> grown to <paramref name="length"/>, its elements copied and the new
    /// ones zeroed, as <see cref="Array.Resize{T}"/> does: its new array from <paramref name="shelf"/>
    /// and its old one given back to it, when there is one.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the new array.</exception>
    internal static void Resize<T>(ArrayShelf? shelf, ref T[] array, int length)
    {
        if (shelf is null)
        {
            Array.Resize(ref array, length);
            return;
        }

        T[] grown = shelf.Take<T>(length, zeroed: true);
        array.AsSpan(0, Math.Min(array.Length, length)).CopyTo(grown);
        shelf.Give(array);
        array = grown;
    }

    /// <summary>
    /// An array of <paramref name="length"/> elements, from the shelf, the process's, or new; zeroed
    /// when <paramref name="zeroed"/>. Under a query's memory, reserved first, then new.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array.</exception>
    internal T[] Take<T>(int length, bool zeroed)
    {
        if (_memory is { } memory)
        {
            if ((long)length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
            {
                Reserve(memory, bytes);
            }

            return zeroed ? new T[length] : GC.AllocateUninitializedArray<T>(length);
        }

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

    /// <summary>
    /// Puts an array nothing holds any more back on the shelf; past the shelf's budget, lets it go.
    /// Under a query's memory, its bytes given back, and left to the next collection.
    /// </summary>
    internal void Give<T>(T[] array)
    {
        if (_memory is { } memory)
        {
            if ((long)array.Length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
            {
                Unreserve(memory, bytes);
            }

            return;
        }

        Give(typeof(T), array, Unsafe.SizeOf<T>());
    }

    /// <summary>The bytes of an array a lane's shelf hands out, reserved from what it holds ahead, or else a megabyte more.</summary>
    private void Reserve(QueryMemory memory, long bytes)
    {
        if (_credit < bytes)
        {
            long more = Math.Max(bytes - _credit, QueryMemory.Chunk);
            if (!memory.TryGrow(more))
            {
                // The megabyte ahead may be what does not fit: the array alone, before failing.
                more = bytes - _credit;
                if (!memory.TryGrow(more))
                {
                    throw memory.Exceeded("group by", -1, bytes);
                }
            }

            _credit += more;
        }

        _credit -= bytes;
        _out += bytes;
        memory.Measure(bytes);
    }

    /// <summary>The bytes of an array a lane's table let go: kept ahead up to two megabytes, the rest given back; the array, left to the next collection.</summary>
    private void Unreserve(QueryMemory memory, long bytes)
    {
        _out -= bytes;
        _credit += bytes;
        memory.Measure(-bytes);
        if (_credit > 2 * QueryMemory.Chunk)
        {
            memory.Shrink(_credit - QueryMemory.Chunk);
            _credit = QueryMemory.Chunk;
        }
    }

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
