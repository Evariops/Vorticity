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

    /// <summary>
    /// What a lane's shelf reserves ahead of its tables: a quarter of a megabyte, sixteen pages of a key
    /// numbered by value, so that its pages meet the budget's count once in sixteen; a megabyte held
    /// ahead on every lane outweighed the small tables of a lane turned to the core.
    /// </summary>
    private const long Ahead = 256 * 1024;

    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(Type Type, int Length), Pile> _piles = [];
    private readonly ArrayShelf? _parent;
    private readonly long _budget;
    private long _held;
    private bool _used;

    // A shelf under a query's memory; for a lane's, the bytes of the arrays it handed out that its
    // tables hold, and what it reserved ahead of the next ones.
    private readonly QueryMemory? _memory;
    private long _out;
    private long _credit;

    /// <summary>A query's shelf, which takes from the process's when it holds nothing of a length, and hands it what it holds at the end.</summary>
    internal ArrayShelf()
        : this(Retained, long.MaxValue)
    {
    }

    /// <summary>
    /// A query's shelf under its memory, the core's (PLAN-HIGH-CARDINALITY, H4, milestone 2): an array
    /// enters the query's count when the shelf takes it new or from the process's, and leaves it when it
    /// is let go or handed back to the process's; one on its piles stays counted. Its reservations are
    /// exact, each taken under no lock of its own: the lanes that apply bursts meet on it.
    /// </summary>
    internal ArrayShelf(QueryMemory memory, bool pooled)
        : this(Retained, pooled ? long.MaxValue : 0) => _memory = memory;

    /// <summary>
    /// A lane's shelf under its query's memory (PLAN-HIGH-CARDINALITY, H2, decision 13): an array it
    /// hands out is reserved before it is allocated, and given back when its table lets it go, which
    /// leaves it to the next collection; it keeps none. A table that doubles reserves its new arrays
    /// while it still holds the old ones: what the copy holds, no more. It reserves <see cref="Ahead"/>
    /// at a time, and keeps up to twice that of what its tables give back; one lane uses it at a time,
    /// and it takes no lock.
    /// </summary>
    internal ArrayShelf(QueryMemory memory)
        : this(parent: null, budget: 0) => _memory = memory;

    /// <summary>Whether the shelf is a lane's: under a query's memory, with no pile and no process's shelf behind it.</summary>
    private bool Lane => _memory is not null && _parent is null;

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

    /// <summary>The most bytes the shelf keeps on its piles; past it, what it is given goes.</summary>
    internal long Budget => _budget;

    /// <summary>
    /// Whether a lane's shelf takes an array its budget refuses all the same, counted past the ceiling:
    /// a lane that can turn to the core, which it does at the next batch (H4, milestone 2).
    /// </summary>
    internal bool Overdraws { get; set; }

    /// <summary>Whether the shelf took an array past its budget, which turns its lane to the core at the next batch.</summary>
    internal bool Overdrawn { get; private set; }

    /// <summary>
    /// The pressure the core whose shelf this is was made under (H4, milestone 2): while memory is still
    /// to come back, a lane's table or the batches it emptied into, the shelf takes past the budget what
    /// a stack asks more, lanes having met on the room left.
    /// </summary>
    internal CorePressure? Pressure { get; set; }

    /// <summary>
    /// Whether a lane's shelf reserves each array alone, nothing ahead: the cache of a lane turned to the
    /// core under pressure (H4, milestone 2), which a quarter of a megabyte ahead on every lane would
    /// outweigh.
    /// </summary>
    internal bool Exact { get; set; }

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
    /// when <paramref name="zeroed"/>. Under a query's memory, reserved first, then new; past the budget
    /// when <paramref name="overdraw"/>, for a lane emptying its table into the core, whose table is
    /// given back right after (H4, milestone 2).
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array.</exception>
    internal T[] Take<T>(int length, bool zeroed, bool overdraw = false)
    {
        if (Lane)
        {
            if ((long)length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
            {
                Reserve(_memory!, bytes);
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

        // An array the query did not hold enters its count, exactly, before it comes.
        if (_memory is { } memory && (long)length * Unsafe.SizeOf<T>() is long counted and >= LeastCounted)
        {
            if (!memory.TryGrow(counted))
            {
                if (!overdraw && Pressure is not { Owed: true })
                {
                    throw memory.Exceeded("group by", -1, counted);
                }

                memory.Force(counted);
            }

            memory.Measure(counted);
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
        if (Lane)
        {
            if ((long)array.Length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
            {
                Unreserve(_memory!, bytes);
            }

            return;
        }

        // Let go past the shelf's budget: it leaves the query's count.
        if (!Give(typeof(T), array, Unsafe.SizeOf<T>()))
        {
            Leave(array.Length * (long)Unsafe.SizeOf<T>());
        }
    }

    /// <summary>
    /// An array the shelf handed out that nothing will take again, of a length no other asks: it leaves
    /// the query's count, to the next collection, rather than wait on a pile.
    /// </summary>
    internal void Drop<T>(T[] array) => Leave(array.Length * (long)Unsafe.SizeOf<T>());

    /// <summary>The bytes of an array that leaves a query's shelf under its memory, let go or handed to the process's: given back, left to the next collection.</summary>
    private void Leave(long bytes)
    {
        if (_memory is { } memory && bytes >= LeastCounted)
        {
            memory.Shrink(bytes);
            memory.Measure(-bytes);
        }
    }

    /// <summary>
    /// What a lane's shelf holds of its query's memory given back, its tables let go with their
    /// partition: the arrays they hold, left to the next collection, and what it reserved ahead.
    /// </summary>
    internal void LetGo()
    {
        if (_memory is { } memory)
        {
            memory.Shrink(_out + _credit);
            memory.Measure(-_out);
            _out = 0;
            _credit = 0;
        }
    }

    /// <summary>The bytes of an array a lane's shelf hands out, reserved from what it holds ahead, or else <see cref="Ahead"/> more.</summary>
    private void Reserve(QueryMemory memory, long bytes)
    {
        if (_credit < bytes)
        {
            long more = Math.Max(bytes - _credit, Exact ? 0 : Ahead);
            if (!memory.TryGrow(more))
            {
                // What it reserves ahead may be what does not fit: the array alone, before failing, or
                // before taking it past the budget when the lane turns to the core at the next batch.
                more = bytes - _credit;
                if (!memory.TryGrow(more))
                {
                    if (!Overdraws)
                    {
                        throw memory.Exceeded("group by", -1, bytes);
                    }

                    memory.Force(more);
                    Overdrawn = true;
                }
            }

            _credit += more;
        }

        _credit -= bytes;
        _out += bytes;
        memory.Measure(bytes);
    }

    /// <summary>The bytes of an array a lane's table let go: kept ahead up to twice <see cref="Ahead"/>, the rest given back; the array, left to the next collection.</summary>
    private void Unreserve(QueryMemory memory, long bytes)
    {
        _out -= bytes;
        _credit += bytes;
        memory.Measure(-bytes);
        long kept = Exact ? 0 : Ahead;
        if (_credit > 2 * kept)
        {
            memory.Shrink(_credit - kept);
            _credit = kept;
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

        // Each leaves the query's count, kept by the process's shelf or let go.
        foreach ((Type type, Pile pile) in piles)
        {
            foreach (Array array in pile.Arrays)
            {
                _parent.Give(type, array, pile.ElementBytes);
                Leave((long)array.Length * pile.ElementBytes);
            }
        }
    }

    /// <summary>An array of elements of <paramref name="type"/>, <paramref name="elementBytes"/> each, on the shelf, within its budget: whether it kept it.</summary>
    private bool Give(Type type, Array array, int elementBytes)
    {
        int length = array.Length;
        if (length == 0)
        {
            return true;
        }

        long bytes = (long)length * elementBytes;
        lock (_gate)
        {
            if (_held + bytes > _budget)
            {
                return false;
            }

            if (!_piles.TryGetValue((type, length), out Pile? pile))
            {
                pile = new Pile(elementBytes);
                _piles.Add((type, length), pile);
            }

            pile.Arrays.Push(array);
            _held += bytes;
            return true;
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
