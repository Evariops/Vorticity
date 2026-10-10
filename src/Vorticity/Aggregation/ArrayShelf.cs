using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// The arrays the core's sub-tables leave as they grow and split, and its slabs of batches, taken again
/// by the next that needs one of the same type and length: the core's pool.
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

    /// <summary>The bytes from which an array lies on the large object heap.</summary>
    private const long LargeBytes = 85_000;

    /// <summary>The piles a shelf keeps once emptied: past them, a pile its budget empties goes.</summary>
    private const int MostPiles = 4096;

    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(Type Type, int Length), Pile> _piles = [];
    private readonly ArrayShelf? _parent;

    // What the shelf keeps of large arrays, and apart of small ones: a small array missed is allocated on
    // the small object heap, whose collections stop every lane and copy what lives there; a large one,
    // a page fault. The bytes it holds, and of them the small arrays'.
    private readonly long _budget;
    private readonly long _smallBudget;
    private long _held;
    private long _heldSmall;
    private bool _used;

    // The process's shelf counts its large arrays in the process's budget (Governed). Past its budget, it
    // keeps what the queries running lately need (Expected), up to a quarter of the memory the process
    // may use (Cap), back to its budget once idle: the bytes of large arrays it refused or let go for
    // want of room since a query last ended (Short), whether it was used since the last tick of its
    // timer, and the ticks it has been idle.
    private readonly bool _governed;
    private readonly long _cap;
    private long _expected;
    private long _short;
    private bool _active;
    private int _idle;
    private Timer? _timer;

    // The piles of each kind from the one taken from or given to most recently to the one least
    // recently, linked: past its budget, the shelf lets go of the oldest first.
    private PileOrder _large;
    private PileOrder _small;

    // A shelf under a query's memory; for a lane's, the bytes of the arrays it handed out that its
    // tables hold, and what it reserved ahead of the next ones.
    private readonly QueryMemory? _memory;
    private long _out;
    private long _credit;
    private bool _forgotten;

    /// <summary>A query's shelf, which takes from the process's when it holds nothing of a length, and hands it what it holds at the end.</summary>
    internal ArrayShelf()
        : this(Retained, long.MaxValue, long.MaxValue)
    {
    }

    /// <summary>
    /// A query's shelf under its memory, the core's: an array
    /// enters the query's count when the shelf takes it new or from the process's, and leaves it when it
    /// is let go or handed back to the process's; one on its piles stays counted. Its reservations are
    /// exact, each taken under no lock of its own: the lanes that apply bursts meet on it.
    /// </summary>
    internal ArrayShelf(QueryMemory memory, bool pooled)
        : this(Retained, pooled ? long.MaxValue : 0, pooled ? long.MaxValue : 0) => _memory = memory;

    /// <summary>
    /// A lane's shelf under its query's memory: an array it
    /// hands out is reserved before it is taken, from the process's shelf or new, and given back when
    /// its table lets it go, to the process's shelf, which keeps it for the next table of its length,
    /// this query's or the next one's; it keeps none itself. A table that doubles reserves its new arrays
    /// while it still holds the old ones: what the copy holds, no more. It reserves <see cref="Ahead"/>
    /// at a time, and keeps up to twice that of what its tables give back; one lane uses it at a time,
    /// and it takes no lock but the process's shelf's.
    /// </summary>
    internal ArrayShelf(QueryMemory memory)
        : this(parent: null, budget: 0, smallBudget: 0) => _memory = memory;

    /// <summary>Whether the shelf is a lane's: under a query's memory, with no pile and no process's shelf behind it.</summary>
    private bool Lane => _memory is not null && _parent is null;

    /// <summary>The query's memory the shelf counts its arrays under: its run's; null for a shelf that counts nothing.</summary>
    internal QueryMemory? Memory => _memory;

    private ArrayShelf(ArrayShelf? parent, long budget, long smallBudget, bool governed = false)
    {
        _parent = parent;
        _budget = budget;
        _smallBudget = smallBudget;
        _governed = governed;
        _cap = governed ? Math.Max(budget, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4) : budget;
    }

    /// <summary>
    /// The process's shelf: what queries left, for the next query not to touch fresh memory again: of
    /// large arrays, up to a quarter of a gigabyte or a sixteenth of the memory the process may use,
    /// whichever is less, and while queries run, up to the most one of them held lately
    /// (<see cref="Expect"/>); of small ones, a quarter of the first again, apart, so that a query whose
    /// large arrays its budget does not hold still finds its small ones. Its large arrays count in the
    /// process's budget as the queries' tables do, and go when a query needs their room
    /// (<see cref="Relieve"/>). Back to its budget after two seconds with no query, and swept after
    /// collections: emptied when no query took from it since the last, or when the machine's memory load
    /// is high.
    /// </summary>
    /// <remarks>
    /// A query's working memory is zero allocation only if kept from one query to the next: q10 on 10⁷
    /// rows at fourteen lanes holds more than a gigabyte and a half, which a shelf of a quarter of a
    /// gigabyte made new at every query, in page faults and zeroed pages (×0.88 kept, ×0.90 for the keys
    /// of 10⁷ values on 4·10⁷ rows). Kept only while queries come, counted, and given up for them.
    /// </remarks>
    internal static ArrayShelf Retained { get; } = NewRetained();

    private static ArrayShelf NewRetained()
    {
        long budget = Math.Min(256L << 20, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16);
        ArrayShelf shelf = new ArrayShelf(parent: null, budget, budget / 4, governed: true);
        CollectionSweeper<ArrayShelf>.Register(shelf);
        return shelf;
    }

    /// <summary>How often the process's shelf, past its budget, looks whether queries still come.</summary>
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(1);

    /// <summary>The ticks with no query after which the process's shelf goes back to its budget.</summary>
    private const int IdleTicks = 2;

    /// <summary>
    /// A query that held <paramref name="peak"/> bytes of its budget at once ended: the process's shelf
    /// keeps up to as many large arrays for the next, and more by what it refused or let go for want of
    /// room since a query last ended, which the next of the kind would make again; up to
    /// <see cref="_cap"/>, until two seconds pass with no query. A query's peak alone fell short: what
    /// its lanes gave back early and its core took late is never held at once (s7 on 4·10⁶ rows at
    /// fourteen lanes, 134 MB a query made anew with the shelf at its peak).
    /// </summary>
    internal void Expect(long peak)
    {
        if (!_governed)
        {
            return;
        }

        long shortBy = Interlocked.Exchange(ref _short, 0);
        long want = Math.Min(_cap, Math.Max(peak, shortBy > 0 ? LargeBudget + shortBy : 0));
        if (want <= _budget)
        {
            return;
        }

        long expected = Volatile.Read(ref _expected);
        while (want > expected)
        {
            long seen = Interlocked.CompareExchange(ref _expected, want, expected);
            if (seen == expected)
            {
                break;
            }

            expected = seen;
        }

        Watch();
    }

    /// <summary>
    /// A query needs room the process's budget refused: the shelf's large arrays all go to the collector,
    /// which the budget then asks for. A cache, not a holder: never in a query's way.
    /// </summary>
    internal void Relieve()
    {
        if (!_governed || Volatile.Read(ref _held) == Volatile.Read(ref _heldSmall))
        {
            return;
        }

        lock (_gate)
        {
            Volatile.Write(ref _expected, 0);
            Evict(long.MaxValue, ref _large, kept: null, before: long.MaxValue);
        }
    }

    /// <summary>The timer that brings the process's shelf back to its budget once idle, started when it keeps past it.</summary>
    private void Watch()
    {
        if (Volatile.Read(ref _timer) is null)
        {
            Timer timer = new Timer(static state => ((ArrayShelf)state!).Tick(), this, IdleTick, IdleTick);
            if (Interlocked.CompareExchange(ref _timer, timer, null) is not null)
            {
                timer.Dispose();
            }
        }
    }

    /// <summary>
    /// A tick of the timer: a shelf used since the last waits on; idle for <see cref="IdleTicks"/>, it lets
    /// go of what it keeps past its budget, to the collector, which it asks to run when that is much.
    /// </summary>
    private void Tick()
    {
        long trimmed = 0;
        lock (_gate)
        {
            if (_active)
            {
                _active = false;
                _idle = 0;
                return;
            }

            if (++_idle < IdleTicks)
            {
                return;
            }

            _idle = 0;
            Volatile.Write(ref _expected, 0);
            long over = _held - _heldSmall - _budget;
            if (over > 0)
            {
                long before = _held;
                Evict(over, ref _large, kept: null, before: long.MaxValue);
                trimmed = before - _held;
            }

            _timer?.Dispose();
            _timer = null;
        }

        if (trimmed >= _budget)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
    }

    /// <summary>
    /// An array of <paramref name="length"/> elements at least, its power of two from sixteen, from the
    /// process's shelf, holding whatever it held: the shared array pool's rent, but found again on any
    /// thread, where the pool kept an array for each thread that gave one back and the next query's,
    /// on another thread, made a new one.
    /// </summary>
    internal static T[] Rent<T>(int length) =>
        Retained.Take<T>(length <= 1 << 30 ? (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 16)) : length, zeroed: false);

    /// <summary>An array <see cref="Rent{T}"/> handed out, back to the process's shelf.</summary>
    internal static void Return<T>(T[] array) => Retained.Give(array);

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

    /// <summary>What a lane's shelf holds of its query's memory: the arrays it handed out, and what it reserved ahead of the next.</summary>
    internal long Reserved => _out + _credit;

    /// <summary>The arrays a lane's shelf handed out, every one, as its tables grew: a count of the work a growth costs, which the tests hold.</summary>
    internal long Handed { get; private set; }

    /// <summary>The bytes of those arrays.</summary>
    internal long HandedBytes { get; private set; }

    /// <summary>The bytes a lane's tables copied from an array into the one that replaced it (<see cref="Resize{T}"/>).</summary>
    internal long CopiedBytes { get; private set; }

    /// <summary>The most bytes the shelf keeps on its piles, its large arrays' and its small ones'; past it, what it is given goes.</summary>
    internal long Budget => LargeBudget > long.MaxValue - _smallBudget ? long.MaxValue : LargeBudget + _smallBudget;

    /// <summary>The most bytes of large arrays the shelf keeps: its budget, or for the process's, what a query held lately past it.</summary>
    private long LargeBudget => _governed ? Math.Max(_budget, Volatile.Read(ref _expected)) : _budget;

    /// <summary>
    /// Whether a lane's shelf takes an array its budget refuses all the same, counted past the ceiling:
    /// a lane that can turn to the core, which it does at the next batch.
    /// </summary>
    internal bool Overdraws { get; set; }

    /// <summary>Whether the shelf took an array past its budget, which turns its lane to the core at the next batch.</summary>
    internal bool Overdrawn { get; private set; }

    /// <summary>The lane gave back what it took past the budget, its tables written to the scratch: it may take past it again.</summary>
    internal void Relieved() => Overdrawn = false;

    /// <summary>The lane took past the budget what its tables hold apart from the shelf's arrays: it is overdrawn as if the shelf had.</summary>
    internal void Overdrew() => Overdrawn = true;

    /// <summary>What a lane's shelf reserved ahead of the arrays to come given back: its tables grow no more, but by a merge, which reserves again.</summary>
    internal void GiveBackAhead()
    {
        if (_memory is { } memory && _credit > 0)
        {
            memory.Shrink(_credit);
            _credit = 0;
        }
    }

    /// <summary>
    /// The pressure the core whose shelf this is was made under: while memory is still
    /// to come back, a lane's table or the batches it emptied into, the shelf takes past the budget what
    /// a stack asks more, lanes having met on the room left.
    /// </summary>
    internal CorePressure? Pressure { get; set; }

    /// <summary>
    /// Whether a lane's shelf reserves each array alone, nothing ahead: the cache of a lane turned to the
    /// core under pressure, which a quarter of a megabyte ahead on every lane would
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
        int copied = Math.Min(array.Length, length);
        array.AsSpan(0, copied).CopyTo(grown);
        if (shelf.Lane)
        {
            shelf.CopiedBytes += (long)copied * Unsafe.SizeOf<T>();
        }

        shelf.Give(array);
        array = grown;
    }

    /// <summary>
    /// An array of <paramref name="length"/> elements, from the shelf, the process's, or new; zeroed
    /// when <paramref name="zeroed"/>. Under a query's memory, reserved first, then new; past the budget
    /// when <paramref name="overdraw"/>, for a lane emptying its table into the core, whose table is
    /// given back right after. <paramref name="counted"/>: whether the taker counts it in its query's
    /// memory, so that the process's shelf hands its count over rather than leave it to the rest.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array.</exception>
    internal T[] Take<T>(int length, bool zeroed, bool overdraw = false, bool counted = false)
    {
        if (Lane)
        {
            long bytes = (long)length * Unsafe.SizeOf<T>();
            if (bytes >= LeastCounted)
            {
                Reserve(_memory!, bytes);
            }

            Handed++;
            HandedBytes += bytes;
            return Retained.Take<T>(length, zeroed, counted: bytes >= LeastCounted);
        }

        Array? found = null;
        lock (_gate)
        {
            _used = true;
            _active = true;
            if (_piles.TryGetValue((typeof(T), length), out Pile? pile))
            {
                Touch(pile);
                if (pile.Arrays.Count > 0)
                {
                    found = pile.Arrays.Pop();
                    Hold(pile, -pile.Bytes);
                    if (_governed && !pile.Small)
                    {
                        QueryMemoryBudget.Process.Unkeep(pile.Bytes, counted);
                    }
                }
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
        bool counts = false;
        if (_memory is { } memory && (long)length * Unsafe.SizeOf<T>() is long asked and >= LeastCounted)
        {
            if (!memory.TryGrow(asked))
            {
                if (!overdraw && !Overdraws && Pressure is not { Owed: true })
                {
                    throw memory.Exceeded("group by", -1, asked);
                }

                memory.Force(asked);
            }

            memory.Measure(asked);
            counts = true;
        }

        if (_parent is not null)
        {
            return _parent.Take<T>(length, zeroed, counted: counts);
        }

        // Made new for code that counts nothing: the rest of the process, which the shelf takes it out of
        // when it is given back.
        if (_governed && !counted && (long)length * Unsafe.SizeOf<T>() is long made and >= LargeBytes)
        {
            QueryMemoryBudget.Process.Discard(made);
        }

        return zeroed ? new T[length] : GC.AllocateUninitializedArray<T>(length);
    }

    /// <summary>
    /// Puts an array nothing holds any more back on the shelf; past the shelf's budget, lets it go.
    /// A lane's shelf gives its bytes back to the query's memory and the array to the process's shelf.
    /// </summary>
    internal void Give<T>(T[] array)
    {
        if (Lane)
        {
            // A result delivered offers its arrays: kept in place of what queries before its own left, never
            // of what its own used. A table larger than the process's budget, given back whole at the end,
            // let go the arrays the next query's table takes first as it grows, and that query then let go
            // of these (q10 at one lane, 722 MB a query against 517 when the table went to the collector);
            // kept only where there was room, it found the shelf full of the queries' before it.
            if (_forgotten)
            {
                Retained.Offer(array, _memory!.Started);
                return;
            }

            if ((long)array.Length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
            {
                Unreserve(_memory!, bytes);
            }

            Retained.Give(array);
            return;
        }

        Clean(array);

        // Let go past the shelf's budget, or while the shelf lets go of what it is given: it leaves the query's count.
        if (Drops || !Give(typeof(T), array, Unsafe.SizeOf<T>()))
        {
            Leave(array.Length * (long)Unsafe.SizeOf<T>());
        }
    }

    /// <summary>
    /// An array put on the process's shelf where its budget has room, or in place of arrays of lengths
    /// no one took or gave since <paramref name="since"/>, in <see cref="Stopwatch"/> ticks.
    /// </summary>
    private void Offer<T>(T[] array, long since)
    {
        Clean(array);
        Give(typeof(T), array, Unsafe.SizeOf<T>(), since);
    }

    /// <summary>An array given back made safe to keep: poisoned under the tests, its references cleared.</summary>
    private static void Clean<T>(T[] array)
    {
        // An array of references kept would keep what it points to: the objects of the query that gave it.
        if (PoisonsGiven)
        {
            Poison(array);
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array);
        }
    }

    /// <summary>
    /// Whether an array given back is filled with a pattern no table writes before it is kept: a table
    /// that reads an array after giving it reads garbage at once, rather than another table's groups
    /// once a shelf lends it again. The tests' switch.
    /// </summary>
    internal static bool PoisonsGiven { get; set; }

    private static void Poison<T>(T[] array)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array);
            return;
        }

        MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)), array.Length * Unsafe.SizeOf<T>()).Fill(0xDB);
    }

    /// <summary>
    /// Whether a query's shelf lets go of what it is given rather than keep it for the next to ask:
    /// the core delivering its parts one after the other once it spilled, each given back once
    /// delivered.
    /// </summary>
    internal bool Drops { get; set; }

    /// <summary>
    /// An array this shelf handed out that <paramref name="to"/>'s tables hold from now on, never counted
    /// twice: between two lanes' shelves of one query, its bytes move from one count to the other and the
    /// budget is not asked; else out of a lane's count first, then into the other's
    /// (<see cref="Adopt{T}"/>). Adopted alone, a set a stream took from a range it followed counted twice
    /// until the range was let go, and asked for more ahead while the range kept its own: the stream
    /// failed under three times what one lane holds.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array to <paramref name="to"/>.</exception>
    internal void Hand<T>(T[] array, ArrayShelf? to)
    {
        long bytes = (long)array.Length * Unsafe.SizeOf<T>();
        if (Lane && to is { Lane: true } && to._memory == _memory)
        {
            if (bytes >= LeastCounted)
            {
                _out -= bytes;
                to._out += bytes;
            }

            return;
        }

        if (Lane && bytes >= LeastCounted)
        {
            Unreserve(_memory!, bytes);
        }

        to?.Adopt(array);
    }

    /// <summary>
    /// An array another lane's shelf handed out, which this lane's tables now hold: reserved and
    /// measured here as if handed out here. Nothing for a shelf that is not a lane's.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the array.</exception>
    internal void Adopt<T>(T[] array)
    {
        if (Lane && (long)array.Length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
        {
            Reserve(_memory!, bytes);
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

    /// <summary>
    /// A lane's shelf whose tables are a result delivered, its query's memory given back whole: what
    /// they give back goes to the process's shelf, counted nowhere.
    /// </summary>
    internal void Forget()
    {
        _out = 0;
        _credit = 0;
        _forgotten = true;
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
        // Each leaves the query's count, kept by the process's shelf or let go: handed under this shelf's
        // lock, then the process's, in that order only, with no list of them made first.
        lock (_gate)
        {
            if (_parent is not null)
            {
                foreach (KeyValuePair<(Type Type, int Length), Pile> entry in _piles)
                {
                    Pile pile = entry.Value;
                    while (pile.Arrays.Count > 0)
                    {
                        Array array = pile.Arrays.Pop();
                        long bytes = (long)array.Length * pile.ElementBytes;
                        Leave(bytes);
                        _parent.Give(entry.Key.Type, array, pile.ElementBytes);
                    }
                }
            }

            Empty();
        }
    }

    /// <summary>
    /// An array of elements of <paramref name="type"/>, <paramref name="elementBytes"/> each, on the shelf,
    /// within its budget: whether it kept it. Past the budget, the arrays of the lengths taken or given
    /// least recently go first, those last used before <paramref name="before"/> only: a shelf full of
    /// another query's lengths kept them while every query went on taking from it, and the arrays of the
    /// queries running now could not come in. The process's shelf counts a large one in the process's
    /// budget, and lets it go when the budget has no room for it.
    /// </summary>
    private bool Give(Type type, Array array, int elementBytes, long before = long.MaxValue)
    {
        int length = array.Length;
        if (length == 0)
        {
            return true;
        }

        bool watch = false;
        lock (_gate)
        {
            if (!_piles.TryGetValue((type, length), out Pile? pile))
            {
                pile = new Pile((type, length), elementBytes);
                _piles.Add(pile.Key, pile);
            }

            Touch(pile);
            _active = true;
            long budget = pile.Small ? _smallBudget : LargeBudget;
            long held = pile.Small ? _heldSmall : _held - _heldSmall;
            if (held + pile.Bytes > budget)
            {
                // Short of room: what goes for it, or it, the next query of its kind makes again.
                long heldBefore = _held;
                bool room = pile.Bytes <= budget && Evict(held + pile.Bytes - budget, ref OrderOf(pile), pile, before);
                if (_governed && !pile.Small)
                {
                    Interlocked.Add(ref _short, heldBefore - _held + (room ? 0 : pile.Bytes));
                }

                if (!room)
                {
                    return false;
                }

                held = pile.Small ? _heldSmall : _held - _heldSmall;
            }

            if (_governed && !pile.Small)
            {
                if (!QueryMemoryBudget.Process.TryKeep(pile.Bytes))
                {
                    return false;
                }

                watch = held + pile.Bytes > _budget;
            }

            pile.Arrays.Push(array);
            Hold(pile, pile.Bytes);
        }

        if (watch)
        {
            Watch();
        }

        return true;
    }

    /// <summary>The bytes the shelf holds moved by <paramref name="bytes"/>, an array of <paramref name="pile"/>'s taken or given.</summary>
    private void Hold(Pile pile, long bytes)
    {
        _held += bytes;
        if (pile.Small)
        {
            _heldSmall += bytes;
        }
    }

    /// <summary>
    /// Lets go of <paramref name="bytes"/> at least of the arrays on the piles of <paramref name="order"/>
    /// taken from or given to least recently, never <paramref name="kept"/>'s nor one used since
    /// <paramref name="before"/>, under the lock: whether it could. Past <see cref="MostPiles"/>, a pile
    /// emptied goes, so that the lengths a process once met do not pile up; short of them it stays, its
    /// next array a push. The process's shelf takes a large array it lets go out of the process's budget,
    /// to the collector.
    /// </summary>
    private bool Evict(long bytes, ref PileOrder order, Pile? kept, long before)
    {
        // The piles lie in the order of their last use: the first one used since the bound ends the walk.
        Pile? pile = order.Oldest;
        while (bytes > 0 && pile is not null && pile.Touched < before)
        {
            Pile? newer = pile.Newer;
            if (pile != kept)
            {
                while (bytes > 0 && pile.Arrays.Count > 0)
                {
                    pile.Arrays.Pop();
                    Hold(pile, -pile.Bytes);
                    if (_governed && !pile.Small)
                    {
                        QueryMemoryBudget.Process.Unkeep(pile.Bytes, counted: false);
                    }

                    bytes -= pile.Bytes;
                }

                if (pile.Arrays.Count == 0 && _piles.Count > MostPiles)
                {
                    Unlink(ref order, pile);
                    _piles.Remove(pile.Key);
                }
            }

            pile = newer;
        }

        return bytes <= 0;
    }

    /// <summary>The order of the piles of <paramref name="pile"/>'s kind.</summary>
    private ref PileOrder OrderOf(Pile pile) => ref pile.Small ? ref _small : ref _large;

    /// <summary>Makes <paramref name="pile"/> the one of its kind used most recently, under the lock.</summary>
    private void Touch(Pile pile)
    {
        pile.Touched = Stopwatch.GetTimestamp();
        ref PileOrder order = ref OrderOf(pile);
        if (order.Newest == pile)
        {
            return;
        }

        Unlink(ref order, pile);
        pile.Older = order.Newest;
        if (order.Newest is not null)
        {
            order.Newest.Newer = pile;
        }

        order.Newest = pile;
        order.Oldest ??= pile;
    }

    /// <summary>Takes <paramref name="pile"/> out of <paramref name="order"/>, under the lock; nothing for one not in it.</summary>
    private static void Unlink(ref PileOrder order, Pile pile)
    {
        if (pile.Newer is { } newer)
        {
            newer.Older = pile.Older;
        }
        else if (order.Newest == pile)
        {
            order.Newest = pile.Older;
        }

        if (pile.Older is { } older)
        {
            older.Newer = pile.Newer;
        }
        else if (order.Oldest == pile)
        {
            order.Oldest = pile.Newer;
        }

        pile.Newer = null;
        pile.Older = null;
    }

    /// <summary>Every pile gone, under the lock.</summary>
    private void Empty()
    {
        if (_governed && _held > _heldSmall)
        {
            QueryMemoryBudget.Process.Unkeep(_held - _heldSmall, counted: false);
            Volatile.Write(ref _expected, 0);
        }

        _piles.Clear();
        _large = default;
        _small = default;
        _held = 0;
        _heldSmall = 0;
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
                Empty();
            }

            _used = false;
        }
    }

    /// <summary>The newest and the oldest pile of a kind, linked through them.</summary>
    private struct PileOrder
    {
        internal Pile? Newest;
        internal Pile? Oldest;
    }

    /// <summary>The arrays of one type and length, and the bytes of an element.</summary>
    private sealed class Pile((Type Type, int Length) key, int elementBytes)
    {
        internal (Type Type, int Length) Key { get; } = key;

        internal Stack<Array> Arrays { get; } = new Stack<Array>();

        internal int ElementBytes { get; } = elementBytes;

        /// <summary>The bytes of each of its arrays.</summary>
        internal long Bytes { get; } = (long)key.Length * elementBytes;

        /// <summary>Whether its arrays lie on the small object heap.</summary>
        internal bool Small => Bytes < LargeBytes;

        /// <summary>When it was last taken from or given to, in <see cref="Stopwatch"/> ticks.</summary>
        internal long Touched { get; set; }

        /// <summary>The pile used next more recently, null for the newest.</summary>
        internal Pile? Newer { get; set; }

        /// <summary>The pile used next less recently, null for the oldest.</summary>
        internal Pile? Older { get; set; }
    }
}
