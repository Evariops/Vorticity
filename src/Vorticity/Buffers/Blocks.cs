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
/// The pool frees nothing: it lets go, and the collector takes. After a collection of the oldest
/// generation, a class no rent has asked for over a minute lets go of its blocks, and every class does
/// when the machine's memory load is high. A reservation the process's budget refuses lets go of
/// everything (<see cref="Trim"/>).
/// </para>
/// </remarks>
internal static class Blocks
{
    /// <summary>The smallest block: 16 bytes.</summary>
    private const int MinShift = 4;

    /// <summary>The largest block kept: a gigabyte. A longer one is allocated at its length and let go when given back.</summary>
    private const int MaxShift = 30;

    /// <summary>How long a class goes without a rent before a sweep lets go of its blocks: the minute <c>ArrayPool&lt;T&gt;.Shared</c> gives its own.</summary>
    private const long IdleMilliseconds = 60_000;

    /// <summary>The least block a query's memory counts, a page: smaller ones go uncounted both ways.</summary>
    internal const long LeastCounted = 4096;

    private static readonly Stack[] s_classes = NewClasses();

    /// <summary>What the collector's sweeps and the memory report see of the pool's typed face, held here for as long as the process runs.</summary>
    internal static IHeldMemory Holder { get; } = Sweeper.Start();

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
        where T : unmanaged
    {
        if (block.Bytes is not { } bytes)
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

    /// <summary>Lets go of every block the pool keeps: a reservation needs their room.</summary>
    internal static void Trim() => Sweep(Environment.TickCount64, everything: true);

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

    /// <summary>A sweep: the classes no rent asked for over <see cref="IdleMilliseconds"/> let go of their blocks, or every class when <paramref name="everything"/>.</summary>
    private static void Sweep(long now, bool everything)
    {
        foreach (Stack stack in s_classes)
        {
            stack.Sweep(now, everything);
        }
    }

    private static Stack[] NewClasses()
    {
        Stack[] classes = new Stack[MaxShift - MinShift + 1];
        for (int i = 0; i < classes.Length; i++)
        {
            classes[i] = new Stack(1 << (i + MinShift));
        }

        return classes;
    }

    /// <summary>One class: a stack of its blocks, under its own lock.</summary>
    private sealed class Stack(int size)
    {
        private readonly Lock _gate = new Lock();
        private byte[]?[] _items = [];
        private int _count;

        /// <summary>Whether a rent has asked for the class since the last sweep.</summary>
        private bool _rented;

        /// <summary>When the sweep that last found the class asked for ran.</summary>
        private long _idleSince;

        /// <summary>The bytes the class keeps, read without its lock: a count, for the memory report.</summary>
        internal long Bytes => (long)Volatile.Read(ref _count) * size;

        internal byte[]? Take()
        {
            lock (_gate)
            {
                // Written once per sweep, so that renters do not keep writing the line.
                if (!_rented)
                {
                    _rented = true;
                }

                if (_count == 0)
                {
                    return null;
                }

                byte[]? bytes = _items[--_count];
                _items[_count] = null;
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

        /// <summary>Lets go of the class's blocks once no rent asked for it over the idle time, or at once when <paramref name="everything"/>.</summary>
        internal void Sweep(long now, bool everything)
        {
            lock (_gate)
            {
                if (!everything)
                {
                    if (_rented)
                    {
                        _rented = false;
                        _idleSince = now;
                        return;
                    }

                    if (now - _idleSince < IdleMilliseconds)
                    {
                        return;
                    }
                }

                Array.Clear(_items, 0, _count);
                _count = 0;

                // An array grown for a burst goes with its blocks.
                if (_items.Length > 64)
                {
                    _items = [];
                }
            }
        }
    }

    /// <summary>What the collector's sweeps and the memory report see of the typed face.</summary>
    private sealed class Sweeper : ISweptAfterCollections, IHeldMemory
    {
        internal static Sweeper Start()
        {
            Sweeper sweeper = new Sweeper();
            CollectionSweeper<Sweeper>.Register(sweeper);
            HeldMemory.Register(sweeper);
            return sweeper;
        }

        public string Holder => "pool";

        public long HeldBytes => Blocks.HeldBytes;

        public void Sweep()
        {
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            Blocks.Sweep(Environment.TickCount64, memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes);
        }
    }
}
