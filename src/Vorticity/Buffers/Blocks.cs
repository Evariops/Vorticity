using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Aggregating;

namespace Vorticity.Buffers;

/// <summary>
/// The pool's typed face: every array of elements that hold no reference the library rents, a table's,
/// a decoder's, a writer's scratch, as a <see cref="Block{T}"/> over a block of bytes of a power-of-two
/// size, from 16 bytes to a gigabyte. The bytes are the unit it keeps and counts, so a block given back
/// by one type serves the next of another, and what it keeps is one number.
/// </summary>
/// <remarks>
/// <para>
/// The blocks are ordinary arrays, never pinned: a pinned small array goes through the lock the runtime
/// takes for the pinned heap, which lanes renting at once wait on. What needs an address that does not
/// move, a segment, is a <see cref="BlockOwner"/> of <see cref="BlockPool"/>.
/// </para>
/// <para>
/// Each class is one stack under one lock. A rent is met once per operation, not once per row: code that
/// needs a block at every batch takes it from what owns its work, a lane, a scan's context, a writer's
/// workspace, which keeps it from one batch to the next and gives it back when the work ends. Nothing is
/// cleared: a block holds whatever its last renter left.
/// </para>
/// <para>
/// The pool frees nothing: it lets go, and the collector takes. It keeps every block given back, and the registry of
/// kept memory (<see cref="HeldMemory"/>) takes back what it keeps above its floors: after each collection of the
/// oldest generation and at each turn of the registry's slow timer, the blocks of each class that sat kept since the
/// time before; everything above the floors when a reservation needs the room or the machine's memory load is high.
/// </para>
/// </remarks>
internal static class Blocks
{
    /// <summary>The smallest block: 16 bytes.</summary>
    private const int MinShift = 4;

    /// <summary>The largest block kept: a gigabyte. A longer one is allocated at its length and let go when given back.</summary>
    private const int MaxShift = 30;

    /// <summary>
    /// The bytes each class keeps whatever the registry asks: 4 096 blocks of 16 bytes, one of 64 KiB, none larger.
    /// 448 rents of 16 bytes to 64 KiB took 9 to 10 µs from blocks kept, 42 to 46 µs allocated anew
    /// (<c>--micro poolrent</c>, Apple M4 Pro, 2026-11-16).
    /// </summary>
    internal const int FloorBytesPerClass = 64 * 1024;

    /// <summary>The least block a query's memory counts, a page: smaller ones go uncounted both ways.</summary>
    internal const long LeastCounted = 4096;

    private static readonly Stack[] s_classes = NewClasses();

    /// <summary>What the registry of kept memory sees of the pool's typed face, held here for as long as the process runs.</summary>
    internal static IHeldMemory Holder { get; } = Kept.Register();

    /// <summary>Whether a block given back is filled with garbage first, in tests: a reader that kept a view of it reads garbage, and fails.</summary>
    internal static bool PoisonsGiven { get; set; }

    /// <summary>A block of <paramref name="length"/> elements, holding whatever its last renter left.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, or its bytes would pass the longest array.</exception>
    internal static Block<T> Rent<T>(int length)
        where T : unmanaged
    {
        if (length <= 0)
        {
            return length == 0 ? default : ThrowNegative<T>();
        }

        long bytes = (long)length * Unsafe.SizeOf<T>();
        if (bytes > 1L << MaxShift)
        {
            return new Block<T>(Outsized(bytes), length);
        }

        int shift = ShiftOf((int)bytes);
        return new Block<T>(s_classes[shift - MinShift].Take() ?? GC.AllocateUninitializedArray<byte>(1 << shift), length);
    }

    /// <summary><see cref="Rent{T}(int)"/>, every element zeroed.</summary>
    internal static Block<T> RentCleared<T>(int length)
        where T : unmanaged
    {
        Block<T> block = Rent<T>(length);
        block.Clear();
        return block;
    }

    /// <summary>
    /// A block of <paramref name="length"/> elements that no pool lent, zeroed, at its exact size: for a
    /// result a caller keeps, held where rented blocks are. Given back, it is let go.
    /// </summary>
    internal static Block<T> Allocate<T>(int length)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length == 0 ? default : new Block<T>(new byte[checked(length * Unsafe.SizeOf<T>())], length);
    }

    /// <summary>
    /// Gives <paramref name="block"/> back: neither it nor any copy of it may be read after. A block no
    /// pool lent, or the default, is let go.
    /// </summary>
    internal static void Return<T>(Block<T> block)
        where T : unmanaged => ReturnBytes(block.Bytes);

    /// <summary>
    /// Gives back the bytes behind a block, for a holder that kept them alone, its own offset and length beside: the
    /// buffers a blob queues, which are many and small. Null, or bytes no pool lent, are let go.
    /// </summary>
    internal static void ReturnBytes(byte[]? bytes)
    {
        if (bytes is null)
        {
            return;
        }

        int length = bytes.Length;
        if (length < 1 << MinShift || length > 1 << MaxShift || !BitOperations.IsPow2(length))
        {
            return;
        }

        if (PoisonsGiven)
        {
            bytes.AsSpan().Fill(0xDB);
        }

        s_classes[BitOperations.Log2((uint)length) - MinShift].Give(bytes);
    }

    /// <summary>
    /// <paramref name="block"/> grown or shrunk to <paramref name="length"/> elements, those it held kept
    /// and the new ones zeroed, as <see cref="Array.Resize{T}"/> does: within its bytes when they have
    /// room, otherwise into a block rented for it, the old one given back.
    /// </summary>
    internal static void Resize<T>(ref Block<T> block, int length)
        where T : unmanaged
    {
        int kept = block.Length;
        Grow(ref block, length);
        if (length > kept)
        {
            block.AsSpan(kept).Clear();
        }
    }

    /// <summary>
    /// The length a block of <paramref name="held"/> elements grows to for <paramref name="needed"/>: twice it at least,
    /// <paramref name="least"/> at the fewest, and a power of two, so that the bytes are a class's whole and the next
    /// growth as far finds them again.
    /// </summary>
    internal static int Grown(int needed, int held, int least = 1_024) =>
        (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, Math.Max(least, 2 * held)));

    /// <summary>
    /// <paramref name="block"/> made <paramref name="length"/> elements long at least, what it held let go: within its
    /// bytes when they have room, as long as they go, otherwise a block rented for it, the old one given back.
    /// </summary>
    internal static void Ensure<T>(ref Block<T> block, int length)
        where T : unmanaged
    {
        if (length <= block.Length)
        {
            return;
        }

        if (length <= block.Capacity)
        {
            block = block.WithLength(block.Capacity);
            return;
        }

        Return(block);
        block = Rent<T>(length);
    }

    /// <summary>
    /// <paramref name="block"/> grown or shrunk to <paramref name="length"/> elements, those it held kept
    /// and the new ones whatever the bytes held: within its bytes when they have room, otherwise into a
    /// block rented for it, the old one given back.
    /// </summary>
    internal static void Grow<T>(ref Block<T> block, int length)
        where T : unmanaged
    {
        if (length <= block.Capacity)
        {
            block = block.WithLength(length);
            return;
        }

        Block<T> grown = Rent<T>(length);
        block.AsSpan(0, Math.Min(block.Length, length)).CopyTo(grown.AsSpan());
        Return(block);
        block = grown;
    }

    /// <summary>
    /// A block of <paramref name="length"/> elements for <paramref name="what"/>, reserved first under
    /// <paramref name="memory"/> at the bytes it holds, when they reach a page.
    /// </summary>
    /// <exception cref="VortexMemoryException">The query's budget does not grant the block.</exception>
    internal static Block<T> Rent<T>(QueryMemory? memory, int length, string what)
        where T : unmanaged =>
        TryRent(memory, length, out Block<T> block) ? block : throw memory!.Exceeded(what, -1, BytesFor<T>(length));

    /// <summary>
    /// <see cref="Rent{T}(QueryMemory?, int, string)"/> for a caller that has a way of its own when the
    /// budget does not grant the block: false then, nothing reserved and <paramref name="block"/> empty.
    /// </summary>
    internal static bool TryRent<T>(QueryMemory? memory, int length, out Block<T> block)
        where T : unmanaged
    {
        if (memory is not null && BytesFor<T>(length) is long bytes and >= LeastCounted)
        {
            if (!memory.TryGrow(bytes))
            {
                block = default;
                return false;
            }

            memory.Measure(bytes);
        }

        block = Rent<T>(length);
        return true;
    }

    /// <summary>A block <see cref="Rent{T}(QueryMemory?, int, string)"/> handed out, given back, its bytes given back to <paramref name="memory"/>.</summary>
    internal static void Return<T>(QueryMemory? memory, Block<T> block)
        where T : unmanaged
    {
        if (memory is not null && block.Bytes is { Length: >= (int)LeastCounted } bytes)
        {
            memory.Shrink(bytes.Length);
            memory.Measure(-bytes.Length);
        }

        Return(block);
    }

    /// <summary>
    /// A block of <paramref name="length"/> elements counted in <paramref name="account"/> at the bytes it holds, when they
    /// reach a page, until <see cref="Return{T}(MemoryAccount, Block{T})"/> gives it back: what a writer's builders grow
    /// their records in, refused as the account refuses rather than failing the heap. Counted nowhere, the entries of a
    /// column's counts filled a heap held to 1.5 GiB, and the job died of an <see cref="OutOfMemoryException"/> rather
    /// than giving way (2026-11-16).
    /// </summary>
    /// <exception cref="VortexMemoryException">The account refuses the block.</exception>
    internal static Block<T> Rent<T>(MemoryAccount account, int length)
        where T : unmanaged
    {
        long bytes = BytesFor<T>(length);
        if (bytes >= LeastCounted && !account.TryTake(bytes))
        {
            throw account.Refused(bytes);
        }

        return Rent<T>(length);
    }

    /// <summary>A block <see cref="Rent{T}(MemoryAccount, int)"/> handed out, given back, its bytes given back to <paramref name="account"/>.</summary>
    internal static void Return<T>(MemoryAccount account, Block<T> block)
        where T : unmanaged
    {
        if (block.Bytes is { Length: >= (int)LeastCounted } bytes)
        {
            account.Give(bytes.Length);
        }

        Return(block);
    }

    /// <summary><see cref="Grow{T}(ref Block{T}, int)"/> for a block counted in <paramref name="account"/>.</summary>
    /// <exception cref="VortexMemoryException">The account refuses the larger block: the block stays as it was.</exception>
    internal static void Grow<T>(MemoryAccount account, ref Block<T> block, int length)
        where T : unmanaged
    {
        if (length <= block.Capacity)
        {
            block = block.WithLength(length);
            return;
        }

        Block<T> grown = Rent<T>(account, length);
        block.AsSpan(0, Math.Min(block.Length, length)).CopyTo(grown.AsSpan());
        Return(account, block);
        block = grown;
    }

    /// <summary><see cref="Resize{T}(ref Block{T}, int)"/> for a block counted in <paramref name="account"/>.</summary>
    /// <exception cref="VortexMemoryException">The account refuses the larger block: the block stays as it was.</exception>
    internal static void Resize<T>(MemoryAccount account, ref Block<T> block, int length)
        where T : unmanaged
    {
        int kept = block.Length;
        Grow(account, ref block, length);
        if (length > kept)
        {
            block.AsSpan(kept).Clear();
        }
    }

    /// <summary><see cref="Ensure{T}(ref Block{T}, int)"/> for a block counted in <paramref name="account"/>.</summary>
    /// <exception cref="VortexMemoryException">The account refuses the larger block: the block is empty then.</exception>
    internal static void Ensure<T>(MemoryAccount account, ref Block<T> block, int length)
        where T : unmanaged
    {
        if (length <= block.Length)
        {
            return;
        }

        if (length <= block.Capacity)
        {
            block = block.WithLength(block.Capacity);
            return;
        }

        Return(account, block);
        block = default;
        block = Rent<T>(account, length);
    }

    /// <summary>The bytes a block of <paramref name="length"/> elements holds: its class's size, or past the largest, its own.</summary>
    internal static long BytesFor<T>(int length)
        where T : unmanaged
    {
        long bytes = (long)Math.Max(length, 0) * Unsafe.SizeOf<T>();
        return bytes == 0 ? 0 : bytes <= 1L << MaxShift ? 1L << ShiftOf((int)bytes) : bytes;
    }

    /// <summary>The bytes the pool keeps for the next rents, each class read as it stands.</summary>
    internal static long HeldBytes
    {
        get
        {
            long held = 0;
            foreach (Stack stack in s_classes)
            {
                held += stack.Bytes;
            }

            return held;
        }
    }

    /// <summary>Lets go of every block the pool keeps, floors included: for the tests and the benchmarks, which start from nothing.</summary>
    internal static void Trim()
    {
        foreach (Stack stack in s_classes)
        {
            stack.LetGo(int.MaxValue, aboveFloor: false);
        }
    }

    /// <summary>Lets go of up to <paramref name="bytes"/> of the blocks kept above the floors, the largest classes first: the bytes let go.</summary>
    internal static long GiveBack(long bytes)
    {
        long given = 0;
        for (int i = s_classes.Length - 1; i >= 0 && given < bytes; i--)
        {
            long size = 1L << (i + MinShift);
            given += s_classes[i].LetGo(BlockPool.BlocksFor(bytes - given, size), aboveFloor: true) * size;
        }

        return given;
    }

    /// <summary>Lets go, in each class, of the blocks that sat kept since the last time, above the floors.</summary>
    internal static void LetGoUnused()
    {
        foreach (Stack stack in s_classes)
        {
            stack.LetGo(stack.Unused(), aboveFloor: true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShiftOf(int bytes) => bytes <= 1 << MinShift ? MinShift : BitOperations.Log2((uint)bytes - 1) + 1;

    private static byte[] Outsized(long bytes) =>
        bytes <= Array.MaxLength
            ? GC.AllocateUninitializedArray<byte>((int)bytes)
            : throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "A block holds at most the bytes of the longest array.");

    [DoesNotReturn]
    private static Block<T> ThrowNegative<T>()
        where T : unmanaged =>
        throw new ArgumentOutOfRangeException("length", "A block's length is not negative.");

    private static Stack[] NewClasses()
    {
        Stack[] classes = new Stack[MaxShift - MinShift + 1];
        for (int i = 0; i < classes.Length; i++)
        {
            int size = 1 << (i + MinShift);
            classes[i] = new Stack(size, FloorBytesPerClass / size);
        }

        return classes;
    }

    /// <summary>One class: a stack of its blocks, under its own lock, above a floor the registry leaves it.</summary>
    private sealed class Stack(int size, int floor)
    {
        private readonly Lock _gate = new Lock();
        private byte[]?[] _items = [];
        private int _count;

        /// <summary>The fewest blocks the class kept since it was last asked what it did not use: none of them was taken.</summary>
        private int _fewest;

        /// <summary>The bytes the class keeps, read without its lock: a count, for the memory report.</summary>
        internal long Bytes => (long)Volatile.Read(ref _count) * size;

        internal byte[]? Take()
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return null;
                }

                byte[]? bytes = _items[--_count];
                _items[_count] = null;
                if (_count < _fewest)
                {
                    _fewest = _count;
                }

                return bytes;
            }
        }

        internal void Give(byte[] bytes)
        {
            lock (_gate)
            {
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, Math.Max(4, _items.Length * 2));
                }

                _items[_count++] = bytes;
            }
        }

        /// <summary>
        /// How many blocks sat kept since the class was last asked, the fewest it kept in between, and from now on counted
        /// again from what it keeps.
        /// </summary>
        internal int Unused()
        {
            lock (_gate)
            {
                int unused = _fewest;
                _fewest = _count;
                return unused;
            }
        }

        /// <summary>Lets go of up to <paramref name="count"/> blocks, above the floor or all of them: how many.</summary>
        internal int LetGo(int count, bool aboveFloor)
        {
            lock (_gate)
            {
                int gone = Math.Min(count, aboveFloor ? Math.Max(0, _count - floor) : _count);
                if (gone == 0)
                {
                    return 0;
                }

                Array.Clear(_items, _count - gone, gone);
                _count -= gone;
                _fewest = Math.Min(_fewest, _count);

                // An array grown for a burst goes with its blocks.
                if (_count == 0 && _items.Length > 64)
                {
                    _items = [];
                }

                return gone;
            }
        }
    }

    /// <summary>What the registry of kept memory and the memory report see of the typed face.</summary>
    private sealed class Kept : IHeldMemory
    {
        internal static Kept Register()
        {
            Kept kept = new Kept();
            HeldMemory.Register(kept);
            return kept;
        }

        public string Holder => "pool";

        public long HeldBytes => Blocks.HeldBytes;

        public long GiveBack(KeptKind kind, long bytes) => kind == KeptKind.Blocks && bytes > 0 ? Blocks.GiveBack(bytes) : 0;

        public void LetGoUnused(bool collection) => Blocks.LetGoUnused();
    }
}
