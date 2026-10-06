using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The states of fixed size of a partition's groups, a record a group at a fixed stride: every slot
/// whose state holds no reference finds its own at an offset of its group's record. A row folded
/// touches one line of states whatever the aggregates, where each slot's array was a line of its
/// own; the records grow, are seeded and kept once for every slot.
/// </summary>
/// <remarks>
/// Words of eight bytes, so that a record starts at the alignment of the widest state a slot has. The
/// records start at a line of cache as the array lies when it is made: a record of 16, 32 or 64 bytes
/// then never straddles two lines. An array of the large object heap stays where it is; a small one
/// the collector moves may lose the alignment, which costs time, never an answer. A slot finds its
/// state through a <see cref="StateView{TState}"/>, read once a batch: the records move when they
/// grow, and only between batches.
/// </remarks>
internal sealed class GroupRecords
{
    /// <summary>The line of cache the records start at: 64 bytes, a half of the 128 of some cores, which a record within it does not straddle either.</summary>
    internal const int Line = 64;

    private readonly RecordLayout _layout;
    private ulong[] _words = [];

    // The words before the first record, which starts at a line; the records the array holds past
    // them, which the words left at its end do not count.
    private int _base;
    private int _capacity;
    private int _groups;

    // The query's shelf a sub-table of the core takes its words from and gives them back to.
    private readonly ArrayShelf? _shelf;

    internal GroupRecords(RecordLayout layout, ArrayShelf? shelf = null)
    {
        _layout = layout;
        _shelf = shelf;
    }

    internal RecordLayout Layout => _layout;

    /// <summary>The groups whose records are made and seeded.</summary>
    internal int Groups => _groups;

    /// <summary>The bytes the records hold, made or not: what they cost.</summary>
    internal long Footprint => (long)_words.Length * sizeof(ulong);

    /// <summary>Makes the records of groups up to <paramref name="groups"/>, each a copy of the seeds.</summary>
    internal void EnsureGroups(int groups)
    {
        if (groups <= _groups)
        {
            return;
        }

        int stride = _layout.Stride;
        if (groups > _capacity)
        {
            // Doubled as the records were, the words past them aside: counted, they would round the
            // next capacity up to four times this one.
            Reserve(Scratch.Capacity(groups, _capacity));
        }

        ReadOnlySpan<ulong> seed = _layout.Seed;
        Span<ulong> words = _words.AsSpan(_base);
        for (int g = _groups; g < groups; g++)
        {
            seed.CopyTo(words.Slice(g * stride, stride));
        }

        _groups = groups;
    }

    /// <summary>Room for <paramref name="capacity"/> records, those made kept: a sub-table of the core made at its bound, which then never grows.</summary>
    internal void Reserve(int capacity)
    {
        if (capacity <= _capacity)
        {
            return;
        }

        int stride = _layout.Stride;
        int words = checked((capacity * stride) + (Line / sizeof(ulong)) - 1);
        ulong[] grown = _shelf is null ? GC.AllocateUninitializedArray<ulong>(words) : _shelf.Take<ulong>(words, zeroed: false);
        int start = LineStart(grown);
        _words.AsSpan(_base, _groups * stride).CopyTo(grown.AsSpan(start));
        _shelf?.Give(_words);
        _words = grown;
        _base = start;
        _capacity = capacity;
    }

    /// <summary>Gives the words back to the shelf: a sub-table split, whose groups another holds.</summary>
    internal void Release()
    {
        _shelf?.Give(_words);
        _words = [];
        _base = 0;
        _capacity = 0;
        _groups = 0;
    }

    /// <summary>Keeps the records of <paramref name="groups"/> alone, ascending, record <c>groups[i]</c> becoming <c>i</c>.</summary>
    internal void Keep(ReadOnlySpan<int> groups)
    {
        int stride = _layout.Stride;
        Span<ulong> words = _words.AsSpan(_base);
        for (int i = 0; i < groups.Length; i++)
        {
            if (groups[i] != i)
            {
                words.Slice(groups[i] * stride, stride).CopyTo(words.Slice(i * stride, stride));
            }
        }

        // The groups past them are seeded again when they are made.
        _groups = groups.Length;
    }

    /// <summary>The states at byte <paramref name="offset"/> of the records, until they next grow.</summary>
    internal StateView<TState> View<TState>(int offset) => new StateView<TState>(_words.AsSpan(_base), _layout.Stride, offset);

    /// <summary>The words of the records made, the first group's first: what a lane's cache copies into its batches (PLAN-HIGH-CARDINALITY, H4).</summary>
    internal ReadOnlySpan<ulong> Made => _words.AsSpan(_base, _groups * _layout.Stride);

    /// <summary>
    /// Reads the first <paramref name="groups"/> records of <paramref name="words"/>, from its first
    /// word, at the layout's stride: the entries of a part's batch, a record each, which the slots
    /// that merge them view this way (PLAN-HIGH-CARDINALITY, H4). Records read so are never grown,
    /// seeded nor kept.
    /// </summary>
    internal void Over(ulong[] words, int groups)
    {
        _words = words;
        _base = 0;
        _capacity = groups;
        _groups = groups;
    }

    /// <summary>The words of <paramref name="words"/> before the first that starts a line, where the array lies now.</summary>
    private static unsafe int LineStart(ulong[] words)
    {
        nint address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(words));
        return (int)((-address & (Line - 1)) / sizeof(ulong));
    }
}

/// <summary>Where each slot's state lies in a record: its offset, the record's stride, and a record of seeds.</summary>
internal sealed class RecordLayout
{
    private RecordLayout(int stride, ulong[] seed, int[] offsets, int width)
    {
        Stride = stride;
        Seed = seed;
        Offsets = offsets;
        Width = width;
    }

    /// <summary>The words of a record.</summary>
    internal int Stride { get; }

    /// <summary>The bytes the states take from a record's first: past them, its words are padding.</summary>
    internal int Width { get; }

    /// <summary>The same states at a stride of <paramref name="words"/>, at least the record's: the entries of a part's batch, a record and a key each (PLAN-HIGH-CARDINALITY, H4).</summary>
    internal RecordLayout Widened(int words)
    {
        ulong[] seed = new ulong[words];
        Seed.CopyTo(seed, 0);
        return new RecordLayout(words, seed, Offsets, Width);
    }

    /// <summary>A record each of whose states is its slot's seed: what a group starts as.</summary>
    internal ulong[] Seed { get; }

    /// <summary>The byte at which each slot's state lies, in the order of the slots; -1 for a slot that keeps its states apart.</summary>
    internal int[] Offsets { get; }

    /// <summary>
    /// The records of <paramref name="slots"/>: the states that hold no reference, the widest first,
    /// each at its own alignment up to eight bytes, the record rounded to a power of two when that
    /// adds at most a third (16, 32, 64 bytes), so that a record starting at a line's alignment does
    /// not straddle two. Null when no slot keeps its states in records.
    /// </summary>
    internal static RecordLayout? Of(ReadOnlySpan<AggregateSlot> slots)
    {
        int[] offsets = new int[slots.Length];
        int[] order = new int[slots.Length];
        int count = 0;
        for (int s = 0; s < slots.Length; s++)
        {
            offsets[s] = -1;
            if (slots[s].StateBytes > 0)
            {
                order[count++] = s;
            }
        }

        if (count == 0)
        {
            return null;
        }

        // The widest first: a state then starts at its alignment with no padding before it.
        Span<int> placed = order.AsSpan(0, count);
        for (int i = 1; i < placed.Length; i++)
        {
            int slot = placed[i];
            int j = i - 1;
            while (j >= 0 && slots[placed[j]].StateBytes < slots[slot].StateBytes)
            {
                placed[j + 1] = placed[j];
                j--;
            }

            placed[j + 1] = slot;
        }

        int at = 0;
        foreach (int s in placed)
        {
            int bytes = slots[s].StateBytes;
            int alignment = Math.Min(8, 1 << BitOperations.TrailingZeroCount(bytes));
            at = (at + alignment - 1) & -alignment;
            offsets[s] = at;
            at += bytes;
        }

        int width = (at + 7) & -8;
        int power = (int)BitOperations.RoundUpToPowerOf2((uint)width);
        int stride = power <= 64 && 3 * power <= 4 * width ? power : width;
        ulong[] seed = new ulong[stride / sizeof(ulong)];
        Span<byte> record = MemoryMarshal.AsBytes(seed.AsSpan());
        foreach (int s in placed)
        {
            slots[s].WriteSeed(record.Slice(offsets[s], slots[s].StateBytes));
        }

        return new RecordLayout(stride / sizeof(ulong), seed, offsets, at);
    }
}

/// <summary>
/// The states of one slot, a group at a time: at the slot's offset of each record, or in an array of
/// its own when the state holds references, which a record of bytes cannot.
/// </summary>
internal readonly ref struct StateView<TState>
{
    private readonly Span<ulong> _words;
    private readonly int _stride;
    private readonly int _offset;
    private readonly Span<TState> _array;

    internal StateView(Span<ulong> words, int stride, int offset)
    {
        _words = words;
        _stride = stride;
        _offset = offset;
    }

    internal StateView(Span<TState> array) => _array = array;

    /// <summary>The state of <paramref name="group"/>; the record's start checked against the records' end.</summary>
    internal ref TState this[int group]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
            {
                return ref _array[group];
            }

            return ref Unsafe.As<byte, TState>(ref Unsafe.AddByteOffset(ref Unsafe.As<ulong, byte>(ref _words[group * _stride]), (nint)_offset));
        }
    }
}

/// <summary>
/// A slot whose state of a group is a value of a fixed size without references, kept in the group's
/// record (<see cref="GroupRecords"/>), bound by the partition that makes the slot; a slot made alone
/// keeps records of its own. A state that holds references, a caller's aggregator's, keeps an array.
/// </summary>
/// <typeparam name="TState">The state of one group.</typeparam>
/// <typeparam name="TResult">The answer of one group.</typeparam>
internal abstract class RecordSlot<TState, TResult> : AggregateSlot<TResult>
{
    private GroupRecords? _records;
    private int _offset;
    private bool _alone;

    // A state with references: kept in an array, seeded and kept here.
    private TState[] _array = [];
    private int _groups;

    /// <summary>The state a group starts from.</summary>
    internal abstract TState Seed { get; }

    internal sealed override int StateBytes => RuntimeHelpers.IsReferenceOrContainsReferences<TState>() ? 0 : Unsafe.SizeOf<TState>();

    internal sealed override void WriteSeed(Span<byte> state)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
        {
            TState seed = Seed;
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(state), seed);
        }
    }

    internal sealed override void Bind(GroupRecords records, int offset)
    {
        _records = records;
        _offset = offset;
    }

    internal sealed override GroupRecords? Bound => _alone ? null : _records;

    /// <summary>
    /// The array of a state with references, at its size without what they reach, which escapes the
    /// count; the records of a slot made alone; nothing for records a partition shares.
    /// </summary>
    internal sealed override long Footprint =>
        RuntimeHelpers.IsReferenceOrContainsReferences<TState>() ? (long)_array.Length * Unsafe.SizeOf<TState>()
        : _alone ? _records!.Footprint
        : 0;

    internal sealed override void EnsureGroups(int groups)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
        {
            Records.EnsureGroups(groups);
            return;
        }

        if (groups > _array.Length)
        {
            Array.Resize(ref _array, Scratch.Capacity(groups, _array.Length));
        }

        for (int g = _groups; g < groups; g++)
        {
            _array[g] = Seed;
        }

        _groups = Math.Max(_groups, groups);
    }

    /// <summary>Keeps the states of <paramref name="groups"/>: records the slot shares are kept by their partition, once for every slot.</summary>
    internal sealed override void Keep(ReadOnlySpan<int> groups)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
        {
            if (_alone)
            {
                Records.Keep(groups);
            }

            return;
        }

        for (int i = 0; i < groups.Length; i++)
        {
            _array[i] = _array[groups[i]];
        }

        // The groups past them are seeded again when they are made.
        _groups = groups.Length;
    }

    /// <summary>The slot's states, until they next grow: read once a batch, then a group at a time.</summary>
    private protected StateView<TState> States =>
        RuntimeHelpers.IsReferenceOrContainsReferences<TState>() ? new StateView<TState>(_array) : Records.View<TState>(_offset);

    /// <summary>The state of <paramref name="group"/>.</summary>
    private protected ref TState State(int group) => ref States[group];

    /// <summary>The states of the same aggregate of another partition, for a merge.</summary>
    private protected static StateView<TState> StatesOf(AggregateSlot other) => ((RecordSlot<TState, TResult>)other).States;

    private GroupRecords Records => _records ?? Alone();

    /// <summary>Records of the slot's own, for a slot no partition bound.</summary>
    private GroupRecords Alone()
    {
        RecordLayout layout = RecordLayout.Of([this])!;
        _records = new GroupRecords(layout);
        _offset = layout.Offsets[0];
        _alone = true;
        return _records;
    }
}
