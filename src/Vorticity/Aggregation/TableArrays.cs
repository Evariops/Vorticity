using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Aggregating;

/// <summary>
/// What a lane's tables, or the core's, hold of their query's memory, and the blocks of the pool they hold it in: an
/// account, not a pool. A block a table rents comes from <see cref="Blocks"/> and goes back there; the account counts its
/// bytes under the query's memory as they come and go, as many as its elements take.
/// </summary>
/// <remarks>
/// <para>
/// A lane's account, which one lane uses at a time, reserves <see cref="Ahead"/> at a time, keeps up to twice that of what
/// its tables give back, and keeps a few small blocks for its tables' next ones, taken back with no lock: a quantile's
/// stores take and give blocks of a few lengths as they grow, and at 32 clients they met every other lane on the pool's
/// lock. A table that doubles reserves its new block while it still holds the old one: what the copy holds, no more.
/// </para>
/// <para>
/// The core's account, which the lanes that apply bursts share, reserves each block exactly, under its lock, and keeps
/// reserved what its sub-tables give back, which a split's next sub-tables take again, until the pass is over
/// (<see cref="Clear"/>). An account with no memory counts nothing: the tables of a step outside a query's memory.
/// </para>
/// <para>
/// An array that holds references, which no block can, is the table's own: allocated, and counted as a block of its bytes
/// would be (<see cref="Own{T}"/>).
/// </para>
/// </remarks>
internal sealed class TableArrays
{
    /// <summary>
    /// The least block an account counts in its query's memory, a page: a table's first blocks are smaller, and go
    /// uncounted both ways.
    /// </summary>
    internal const long LeastCounted = 4096;

    /// <summary>
    /// What a lane's account reserves ahead of its tables: a quarter of a megabyte, sixteen pages of a key numbered by
    /// value, so that its pages meet the budget's count once in sixteen; a megabyte held ahead on every lane outweighed the
    /// small tables of a lane turned to the core.
    /// </summary>
    private const long Ahead = 256 * 1024;

    /// <summary>The small blocks a lane keeps for its tables' next ones, and their bytes together at most.</summary>
    private const int Spares = 8;

    private const long SpareBytes = 64 * 1024;

    private readonly QueryMemory? _memory;
    private readonly bool _lane;

    // The core's account is shared by the lanes that apply bursts: its credit moves under this lock.
    private readonly Lock? _gate;

    // The bytes of the blocks handed out that the tables hold, what was reserved ahead of the next ones, and whether all of
    // it is kept until GiveBackAhead, reserved for a merge (ReserveAhead).
    private long _out;
    private long _credit;
    private bool _keepsAhead;
    private bool _forgotten;

    // A lane's small blocks its tables gave back, which no query's memory counts.
    private byte[]?[]? _spares;
    private long _spareBytes;

    /// <summary>A lane's account under its query's <paramref name="memory"/>.</summary>
    internal TableArrays(QueryMemory memory)
        : this(memory, lane: true)
    {
    }

    private TableArrays(QueryMemory? memory, bool lane)
    {
        _memory = memory;
        _lane = lane && memory is not null;
        _gate = !lane && memory is not null ? new Lock() : null;
    }

    /// <summary>The account of tables no query's memory counts.</summary>
    internal static TableArrays Uncounted { get; } = new TableArrays(memory: null, lane: false);

    /// <summary>The core's account under its query's <paramref name="memory"/>; uncounted when there is none.</summary>
    internal static TableArrays OfCore(QueryMemory? memory) => memory is null ? Uncounted : new TableArrays(memory, lane: false);

    /// <summary>
    /// The account the tables of a merge's parts grow under, side by side: a core's, each block reserved exactly under
    /// <paramref name="memory"/> as it is taken and given back as it is let go, none kept for the next; uncounted with none.
    /// </summary>
    internal static TableArrays OfParts(QueryMemory? memory) => memory is null ? Uncounted : new TableArrays(memory, lane: false) { Drops = true };

    /// <summary>The query's memory the account counts its blocks under; null for one that counts nothing.</summary>
    internal QueryMemory? Memory => _memory;

    /// <summary>Whether the account is a lane's.</summary>
    internal bool Lane => _lane;

    /// <summary>The bytes of the blocks a lane's account handed out that its tables hold: what its query's memory counts of them.</summary>
    internal long Out => _out;

    /// <summary>What a lane's account holds of its query's memory: the blocks it handed out, and what it reserved ahead of the next.</summary>
    internal long Reserved => _out + _credit;

    /// <summary>The blocks a lane's account handed out, every one, as its tables grew: a count of the work a growth costs, which the tests hold.</summary>
    internal long Handed { get; private set; }

    /// <summary>The bytes of those blocks.</summary>
    internal long HandedBytes { get; private set; }

    /// <summary>The bytes a lane's tables copied from a block into the one that replaced it (<see cref="Resize{T}"/>).</summary>
    internal long CopiedBytes { get; private set; }

    /// <summary>
    /// Whether a lane's account reserves each block alone, nothing ahead: the cache of a lane turned to the core under
    /// pressure, which a quarter of a megabyte ahead on every lane would outweigh.
    /// </summary>
    internal bool Exact { get; set; }

    /// <summary>
    /// Whether the account takes a block its budget refuses all the same, counted past the ceiling: a lane that can turn to
    /// the core, which it does at the next batch.
    /// </summary>
    internal bool Overdraws { get; set; }

    /// <summary>Whether the account took a block past its budget, which turns its lane to the core at the next batch.</summary>
    internal bool Overdrawn { get; private set; }

    /// <summary>The lane gave back what it took past the budget, its tables written to the scratch: it may take past it again.</summary>
    internal void Relieved() => Overdrawn = false;

    /// <summary>The lane took past the budget what its tables hold apart from the account's blocks: it is overdrawn as if the account had.</summary>
    internal void Overdrew() => Overdrawn = true;

    /// <summary>
    /// The pressure the core whose account this is was made under: while memory is still to come back, a lane's table or
    /// the batches it emptied into, the account takes past the budget what a stack asks more, lanes having met on the room
    /// left.
    /// </summary>
    internal CorePressure? Pressure { get; set; }

    /// <summary>
    /// Whether the core's account gives back to the query what its sub-tables give back, rather than keep it reserved for
    /// the next to ask: the core delivering its parts one after the other once it spilled, each given back once delivered.
    /// </summary>
    internal bool Drops { get; set; }

    /// <summary>
    /// A block of <paramref name="length"/> elements, zeroed when <paramref name="zeroed"/>, its bytes reserved first under
    /// the query's memory; past the budget when <paramref name="overdraw"/>, for a lane emptying its table into the core,
    /// whose table is given back right after.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the block.</exception>
    internal Block<T> Rent<T>(int length, bool zeroed, bool overdraw = false)
        where T : unmanaged
    {
        long bytes = (long)length * Unsafe.SizeOf<T>();
        Count(bytes, overdraw);
        Block<T> block = _lane && bytes > 0 && bytes < LeastCounted && TakeSpare(Blocks.BytesFor<T>(length)) is { } spare
            ? new Block<T>(spare, length)
            : Blocks.Rent<T>(length);
        if (zeroed)
        {
            block.Clear();
        }

        return block;
    }

    /// <summary>A block <see cref="Rent{T}"/> handed out, given back to the pool, and its bytes to the query's memory.</summary>
    internal void Return<T>(Block<T> block)
        where T : unmanaged
    {
        if (block.Bytes is not { } bytes)
        {
            return;
        }

        long counted = (long)block.Length * Unsafe.SizeOf<T>();
        Uncount(counted);
        if (_lane && !_forgotten && counted < LeastCounted && KeepSpare(bytes))
        {
            return;
        }

        Blocks.ReturnBytes(bytes);
    }

    /// <summary>
    /// <paramref name="block"/> grown or shrunk to <paramref name="length"/>, its elements copied and the new ones zeroed, as
    /// <see cref="Array.Resize{T}"/> does: its new block rented from <paramref name="arrays"/> and its old one given back.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the new block.</exception>
    internal static void Resize<T>(TableArrays arrays, ref Block<T> block, int length)
        where T : unmanaged
    {
        Block<T> grown = arrays.Rent<T>(length, zeroed: true);
        int copied = Math.Min(block.Length, length);
        block.AsSpan(0, copied).CopyTo(grown.AsSpan());
        if (arrays._lane)
        {
            arrays.CopiedBytes += (long)copied * Unsafe.SizeOf<T>();
        }

        arrays.Return(block);
        block = grown;
    }

    /// <summary>
    /// An array of <paramref name="length"/> elements that hold references, which no block can: the table's own, zeroed,
    /// its bytes counted as a block's would be.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant its bytes.</exception>
    internal T[] Own<T>(int length)
    {
        Count((long)length * Unsafe.SizeOf<T>(), overdraw: false);
        return length == 0 ? [] : new T[length];
    }

    /// <summary>An array <see cref="Own{T}"/> made, let go to the collector, its bytes given back to the query's memory.</summary>
    internal void Disown<T>(T[] array) => Uncount((long)array.Length * Unsafe.SizeOf<T>());

    /// <summary>
    /// <paramref name="array"/>, an array of references the table owns, grown or shrunk to <paramref name="length"/>, its
    /// elements copied, through <paramref name="arrays"/>'s count.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the new array.</exception>
    internal static void ResizeOwned<T>(TableArrays arrays, ref T[] array, int length)
    {
        T[] grown = arrays.Own<T>(length);
        int copied = Math.Min(array.Length, length);
        array.AsSpan(0, copied).CopyTo(grown);
        if (arrays._lane)
        {
            arrays.CopiedBytes += (long)copied * Unsafe.SizeOf<T>();
        }

        arrays.Disown(array);
        array = grown;
    }

    /// <summary>
    /// A block this account handed out that <paramref name="to"/>'s tables hold from now on, never counted twice: between
    /// two lanes' accounts of one query, its bytes move from one count to the other and the budget is not asked; else out
    /// of a lane's count first, then into the other's (<see cref="Adopt{T}"/>). Adopted alone, a set a stream took from a
    /// range it followed counted twice until the range was let go, and asked for more ahead while the range kept its own:
    /// the stream failed under three times what one lane holds.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the block to <paramref name="to"/>.</exception>
    internal void Hand<T>(Block<T> block, TableArrays to)
        where T : unmanaged
    {
        long bytes = (long)block.Length * Unsafe.SizeOf<T>();
        if (_lane && to._lane && to._memory == _memory)
        {
            if (bytes >= LeastCounted)
            {
                _out -= bytes;
                to._out += bytes;
            }

            return;
        }

        if (_lane && bytes >= LeastCounted)
        {
            Unreserve(_memory!, bytes);
        }

        to.Adopt(block);
    }

    /// <summary>
    /// A block another lane's account handed out, which this lane's tables now hold: reserved and measured here as if
    /// handed out here. Nothing for an account that is not a lane's.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the block.</exception>
    internal void Adopt<T>(Block<T> block)
        where T : unmanaged
    {
        if (_lane && (long)block.Length * Unsafe.SizeOf<T>() is long bytes and >= LeastCounted)
        {
            Reserve(_memory!, bytes);
        }
    }

    /// <summary>
    /// A block the account handed out that nothing will take again, of a length no other asks: it leaves the query's count
    /// at once, given back to the pool, rather than stay reserved for the next sub-table.
    /// </summary>
    internal void Drop<T>(Block<T> block)
        where T : unmanaged
    {
        // Out of the count as it came in: a block under a page, or one handed out once the account forgot, never counted.
        // Given back all the same, the small batches a core applied gave back hundreds of bytes each that the query never
        // reserved, and a core under pressure gave back half a megabyte more than it held.
        long bytes = (long)block.Length * Unsafe.SizeOf<T>();
        if (!_forgotten && bytes >= LeastCounted)
        {
            Leave(bytes);
        }

        Blocks.Return(block);
    }

    /// <summary>
    /// What a lane's account holds of its query's memory given back, its tables let go with their partition: the blocks
    /// they hold, and what it reserved ahead.
    /// </summary>
    internal void LetGo()
    {
        GiveSpares();
        if (_memory is { } memory && _lane)
        {
            memory.Shrink(_out + _credit);
            memory.Measure(-_out);
            _out = 0;
            _credit = 0;
            _keepsAhead = false;
        }
    }

    /// <summary>
    /// An account whose tables are a result delivered, its query's memory given back whole: what they give back from now
    /// on goes to the pool, counted nowhere.
    /// </summary>
    internal void Forget()
    {
        _out = 0;
        _credit = 0;
        _keepsAhead = false;
        _forgotten = true;
        GiveSpares();
    }

    /// <summary>
    /// What the core's account keeps reserved of what its sub-tables gave back, given back to the query: the pass is over,
    /// and its sub-tables, which keep this account, are the result.
    /// </summary>
    internal void Clear()
    {
        if (_gate is null)
        {
            return;
        }

        long credit;
        lock (_gate)
        {
            credit = _credit;
            _credit = 0;
        }

        Leave(credit);
    }

    /// <summary>What a lane's account reserved ahead of the blocks to come given back: its tables grow no more, but by a merge, which reserves again.</summary>
    internal void GiveBackAhead()
    {
        _keepsAhead = false;
        if (_lane && _credit > 0)
        {
            _memory!.Shrink(_credit);
            _credit = 0;
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> ahead of a merge into the account's tables, what the merge is expected to grow
    /// them by, and keeps it with what they give back meanwhile until <see cref="GiveBackAhead"/>: the merge's blocks come
    /// out of it. A merge that only checked the room left it to the lanes that kept folding while it ran, and its doubling
    /// then went past the budget, 8 % past it at 4 lanes under a tenth of what one lane holds alone. False, and nothing
    /// reserved, when the budget would not grant them now.
    /// </summary>
    internal bool ReserveAhead(long bytes)
    {
        if (!_lane || !_memory!.CanGrow(bytes) || !_memory.TryGrow(bytes))
        {
            return false;
        }

        _credit += bytes;
        _keepsAhead = true;
        return true;
    }

    /// <summary>The bytes of a block about to be handed out, counted under the query's memory: a lane's from what it reserved ahead, the core's exactly.</summary>
    private void Count(long bytes, bool overdraw)
    {
        if (_lane)
        {
            Handed++;
            HandedBytes += bytes;
            if (!_forgotten && bytes >= LeastCounted)
            {
                Reserve(_memory!, bytes);
            }

            return;
        }

        if (_memory is not { } memory || _forgotten || bytes < LeastCounted)
        {
            return;
        }

        // What the core's sub-tables gave back stays reserved for the next: a split's halves take the lengths its table gave.
        lock (_gate!)
        {
            if (_credit >= bytes)
            {
                _credit -= bytes;
                return;
            }
        }

        if (!memory.TryGrow(bytes))
        {
            if (!overdraw && !Overdraws && Pressure is not { Owed: true })
            {
                throw memory.Exceeded("group by", -1, bytes);
            }

            memory.Force(bytes);
        }

        memory.Measure(bytes);
    }

    /// <summary>The bytes of a block given back, out of the query's count: a lane's kept ahead, the core's kept reserved unless it drops them.</summary>
    private void Uncount(long bytes)
    {
        if (_memory is not { } memory || _forgotten || bytes < LeastCounted)
        {
            return;
        }

        if (_lane)
        {
            Unreserve(memory, bytes);
            return;
        }

        if (Drops)
        {
            Leave(bytes);
            return;
        }

        lock (_gate!)
        {
            _credit += bytes;
        }
    }

    /// <summary>The bytes of a block a lane's account hands out, reserved from what it holds ahead, or else <see cref="Ahead"/> more.</summary>
    private void Reserve(QueryMemory memory, long bytes)
    {
        if (_credit < bytes)
        {
            long more = Math.Max(bytes - _credit, Exact ? 0 : Ahead);
            if (!memory.TryGrow(more))
            {
                // What it reserves ahead may be what does not fit: the block alone, before failing, or before taking it past
                // the budget when the lane turns to the core at the next batch.
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

    /// <summary>
    /// The bytes of a block a lane's table let go: kept ahead up to twice <see cref="Ahead"/>, the rest given back, all of
    /// them while a merge runs on what it reserved (<see cref="ReserveAhead"/>).
    /// </summary>
    private void Unreserve(QueryMemory memory, long bytes)
    {
        _out -= bytes;
        _credit += bytes;
        memory.Measure(-bytes);
        long kept = Exact ? 0 : Ahead;
        if (!_keepsAhead && _credit > 2 * kept)
        {
            memory.Shrink(_credit - kept);
            _credit = kept;
        }
    }

    /// <summary>The bytes of blocks that leave a query's count, given back to it.</summary>
    private void Leave(long bytes)
    {
        if (_memory is { } memory && bytes > 0)
        {
            memory.Shrink(bytes);
            memory.Measure(-bytes);
        }
    }

    /// <summary>A small block of <paramref name="bytes"/> bytes a lane's tables gave back, kept on the lane; null for none.</summary>
    private byte[]? TakeSpare(long bytes)
    {
        if (_spares is not { } spares)
        {
            return null;
        }

        for (int i = 0; i < spares.Length; i++)
        {
            if (spares[i] is { } spare && spare.Length == bytes)
            {
                spares[i] = null;
                _spareBytes -= spare.Length;
                return spare;
            }
        }

        return null;
    }

    /// <summary>Keeps <paramref name="bytes"/>, a small block a lane's table gave back, on the lane when there is room; whether it did.</summary>
    private bool KeepSpare(byte[] bytes)
    {
        if (_spareBytes + bytes.Length > SpareBytes)
        {
            return false;
        }

        byte[]?[] spares = _spares ??= new byte[Spares][];
        for (int i = 0; i < spares.Length; i++)
        {
            if (spares[i] is null)
            {
                if (Blocks.PoisonsGiven)
                {
                    bytes.AsSpan().Fill(0xDB);
                }

                spares[i] = bytes;
                _spareBytes += bytes.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>The small blocks kept on a lane given back to the pool: its tables are let go or delivered.</summary>
    private void GiveSpares()
    {
        if (_spares is not { } spares)
        {
            return;
        }

        for (int i = 0; i < spares.Length; i++)
        {
            Blocks.ReturnBytes(spares[i]);
            spares[i] = null;
        }

        _spareBytes = 0;
    }
}
