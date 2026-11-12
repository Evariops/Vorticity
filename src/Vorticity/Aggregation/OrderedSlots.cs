using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Aggregating;

/// <summary>
/// The values of a fixed-width number column as <typeparamref name="TWide"/>, read once a batch: a view of the batch's
/// values where they are stored as <typeparamref name="TWide"/> is, a copy widened otherwise. An order of unsigned 64-bit
/// integers is moved by 2^63 into a <see cref="long"/>, which keeps its order.
/// </summary>
internal struct WideColumn<TWide>
    where TWide : unmanaged
{
    private TWide[]? _copy;
    private VortexBuffer _view;
    private long _batch;
    private int _node;
    private int _canonical;
    private int _length;
    private bool _copied;

    /// <summary>The values of <paramref name="node"/>, stored as <paramref name="type"/>, and their validity.</summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="batch">The batch's number, from 1; 0 reads the node again.</param>
    /// <param name="node">The node.</param>
    /// <param name="type">The type the column stores.</param>
    /// <param name="validity">The validity words, empty when every value is valid.</param>
    internal ReadOnlySpan<TWide> Of(CanonicalArena arena, long batch, int node, PType type, out ReadOnlySpan<ulong> validity)
    {
        if (batch != 0 && batch == _batch && node == _node)
        {
            validity = ArenaWords.Validity(arena, _canonical);
            return _copied ? _copy.AsSpan(0, _length) : MemoryMarshal.Cast<byte, TWide>(_view.Span)[.._length];
        }

        int canonical = EncodedForms.Canonical(arena, node);
        validity = ArenaWords.Validity(arena, canonical);
        ref readonly CanonicalRecord record = ref arena.RecordRef(canonical);
        int length = record.Length;
        _batch = batch;
        _node = node;
        _canonical = canonical;
        _length = length;
        if (Native(type))
        {
            _view = record.BufferA;
            _copied = false;
            return MemoryMarshal.Cast<byte, TWide>(record.BufferA.Span)[..length];
        }

        _copy ??= [];
        Scratch.Grow(ref _copy, length);
        Span<TWide> into = _copy.AsSpan(0, length);
        Widen(record.BufferA.Span, type, into);
        _view = default;
        _copied = true;
        return into;
    }

    /// <summary>Gives the copy back to the process's shelf.</summary>
    internal void Release()
    {
        if (_copy is { Length: > 0 } copy)
        {
            ArrayShelf.Retained.Give(copy);
        }

        this = default;
    }

    /// <summary>Whether a column of <paramref name="type"/> is read as it lies.</summary>
    private static bool Native(PType type) =>
        (typeof(TWide) == typeof(long) && type == PType.I64)
        || (typeof(TWide) == typeof(ulong) && type == PType.U64)
        || (typeof(TWide) == typeof(double) && type == PType.F64);

    private static void Widen(ReadOnlySpan<byte> bytes, PType type, Span<TWide> into)
    {
        if (typeof(TWide) == typeof(double))
        {
            Span<double> doubles = MemoryMarshal.Cast<TWide, double>(into);
            switch (type)
            {
                case PType.F32:
                    Widen(MemoryMarshal.Cast<byte, float>(bytes), doubles);
                    return;
                case PType.F16:
                    Widen(MemoryMarshal.Cast<byte, Half>(bytes), doubles);
                    return;
            }
        }
        else if (typeof(TWide) == typeof(ulong))
        {
            Span<ulong> unsigned = MemoryMarshal.Cast<TWide, ulong>(into);
            switch (type)
            {
                case PType.U8:
                    Widen(bytes, unsigned);
                    return;
                case PType.U16:
                    Widen(MemoryMarshal.Cast<byte, ushort>(bytes), unsigned);
                    return;
                case PType.U32:
                    Widen(MemoryMarshal.Cast<byte, uint>(bytes), unsigned);
                    return;
            }
        }
        else if (typeof(TWide) == typeof(long))
        {
            Span<long> signed = MemoryMarshal.Cast<TWide, long>(into);
            switch (type)
            {
                case PType.I8:
                    Widen(MemoryMarshal.Cast<byte, sbyte>(bytes), signed);
                    return;
                case PType.I16:
                    Widen(MemoryMarshal.Cast<byte, short>(bytes), signed);
                    return;
                case PType.I32:
                    Widen(MemoryMarshal.Cast<byte, int>(bytes), signed);
                    return;
                case PType.U8:
                    Widen(bytes, signed);
                    return;
                case PType.U16:
                    Widen(MemoryMarshal.Cast<byte, ushort>(bytes), signed);
                    return;
                case PType.U32:
                    Widen(MemoryMarshal.Cast<byte, uint>(bytes), signed);
                    return;
                case PType.U64:
                {
                    // Moved by 2^63: the unsigned order kept among longs.
                    ReadOnlySpan<ulong> from = MemoryMarshal.Cast<byte, ulong>(bytes);
                    for (int i = 0; i < signed.Length; i++)
                    {
                        signed[i] = (long)(from[i] ^ 0x8000_0000_0000_0000UL);
                    }

                    return;
                }
            }
        }

        throw new InvalidOperationException($"A column of {type} is not read as {typeof(TWide).Name}.");
    }

    private static void Widen<TFrom, TTo>(ReadOnlySpan<TFrom> from, Span<TTo> into)
        where TFrom : INumberBase<TFrom>
        where TTo : INumberBase<TTo>
    {
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = TTo.CreateTruncating(from[i]);
        }
    }
}

/// <summary>
/// The rows of a batch an aggregate over an order folds: those its scan and its filter keep, whose order and value both
/// hold one; worked out once a batch, whatever ranges or windows it is folded in.
/// </summary>
internal struct PointMask
{
    private ulong[]? _words;
    private ulong[]? _scratch;
    private long _batch;
    private int _node;
    private int _orderNode;
    private int _length;
    private byte _source;

    internal ReadOnlySpan<ulong> Of(in BatchInput input, ReadOnlySpan<ulong> values, ReadOnlySpan<ulong> orders)
    {
        if (input.Batch == 0 || _batch != input.Batch || _node != input.Node || _orderNode != input.OrderNode)
        {
            _batch = input.Batch;
            _node = input.Node;
            _orderNode = input.OrderNode;
            _scratch ??= [];
            _words ??= [];
            ReadOnlySpan<ulong> valid = RowMasks.And(values, orders, input.Rows, ref _scratch);
            ReadOnlySpan<ulong> kept = RowMasks.And(input.Selection, valid, input.Rows, ref _words);
            if (kept.IsEmpty)
            {
                _source = 0;
            }
            else if (kept == input.Selection)
            {
                _source = 1;
            }
            else
            {
                // Copied where the conjunction is not already in the words: one of the masks as it was.
                if (!Unsafe.AreSame(ref MemoryMarshal.GetReference(kept), ref MemoryMarshal.GetArrayDataReference(_words)))
                {
                    Scratch.Grow(ref _words, kept.Length);
                    kept.CopyTo(_words);
                }

                _length = kept.Length;
                _source = 2;
            }
        }

        return _source switch
        {
            0 => default,
            1 => input.Selection,
            _ => _words.AsSpan(0, _length),
        };
    }

    internal void Release()
    {
        if (_words is { Length: > 0 } words)
        {
            ArrayShelf.Retained.Give(words);
        }

        if (_scratch is { Length: > 0 } scratch)
        {
            ArrayShelf.Retained.Give(scratch);
        }

        this = default;
    }
}

/// <summary>
/// The arithmetic of a growth over values widened to <typeparamref name="TWide"/>: which values are points, and the
/// exact sum of the values that come before a fall, which the growth adds to the last point less the first.
/// </summary>
internal interface IGrowth<TWide, TSum>
    where TWide : unmanaged, INumber<TWide>
    where TSum : unmanaged
{
    /// <summary>Whether a value makes a point: a float's NaN or infinity does not.</summary>
    static abstract bool Takes(TWide value);

    /// <summary>The value as a point keeps it: a float's −0 as +0, so that the two zeros are one value.</summary>
    static abstract TWide Kept(TWide value);

    /// <summary>Adds the value a fall came from.</summary>
    static abstract void Add(ref TSum sum, TWide value);

    static abstract void Merge(ref TSum into, in TSum other);

    /// <summary>The growth, exactly: <paramref name="last"/> − <paramref name="first"/> + <paramref name="sum"/>.</summary>
    static abstract TSum Growth(in TSum sum, TWide first, TWide last);
}

/// <summary>The growth of signed integers, in 128 bits.</summary>
internal readonly struct SignedGrowth : IGrowth<long, Int128>
{
    public static bool Takes(long value) => true;

    public static long Kept(long value) => value;

    public static void Add(ref Int128 sum, long value) => sum += value;

    public static void Merge(ref Int128 into, in Int128 other) => into += other;

    public static Int128 Growth(in Int128 sum, long first, long last) => sum + ((Int128)last - first);
}

/// <summary>The growth of unsigned integers, in 128 bits.</summary>
internal readonly struct UnsignedGrowth : IGrowth<ulong, Int128>
{
    public static bool Takes(ulong value) => true;

    public static ulong Kept(ulong value) => value;

    public static void Add(ref Int128 sum, ulong value) => sum += value;

    public static void Merge(ref Int128 into, in Int128 other) => into += other;

    public static Int128 Growth(in Int128 sum, ulong first, ulong last) => sum + ((Int128)last - first);
}

/// <summary>The growth of floats: an exact sum (<see cref="WideIndexedSum"/>), rounded once.</summary>
internal readonly struct FloatGrowth : IGrowth<double, WideIndexedSum>
{
    public static bool Takes(double value) => double.IsFinite(value);

    public static double Kept(double value) => value + 0.0;

    public static void Add(ref WideIndexedSum sum, double value) => sum.Add(value);

    public static void Merge(ref WideIndexedSum into, in WideIndexedSum other) => into.Merge(in other);

    public static WideIndexedSum Growth(in WideIndexedSum sum, double first, double last)
    {
        WideIndexedSum growth = sum;
        growth.Add(last);
        growth.Add(-first);
        return growth;
    }
}

/// <summary>A run of points: the first and the last, in the order (order, value), and the rows of the source they lie between.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct PointRun<TWide>
    where TWide : unmanaged
{
    internal long FirstOrder;
    internal TWide FirstValue;
    internal long LastOrder;
    internal TWide LastValue;
    internal long FirstRow;
    internal long LastRow;

    internal static PointRun<TWide> Of(long order, TWide value, long row) => new PointRun<TWide>
    {
        FirstOrder = order,
        FirstValue = value,
        LastOrder = order,
        LastValue = value,
        FirstRow = row,
        LastRow = row,
    };
}

/// <summary>
/// A group's growth so far: the sum of the values its falls came from, the run of points open, the runs closed before it,
/// kept apart in the slot's arrays, and whether its points have to be read again once the pass is done.
/// </summary>
/// <remarks>
/// What a point's step reads and writes comes first, its run's last point in the first 64 bytes; the sum, which only a fall
/// adds to, comes last. Measured on twenty thousand groups whose points come interleaved, a group a row
/// (<c>vortex-queries --micro growth</c>).
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct GrowthState<TWide, TSum>
    where TWide : unmanaged
    where TSum : unmanaged
{
    /// <summary><see cref="GrowthSlots.Has"/>, <see cref="GrowthSlots.Spread"/>, <see cref="GrowthSlots.Preceded"/>.</summary>
    internal int Flags;

    /// <summary>The piece the open run was read in; <see cref="GrowthSlots.NoPiece"/> once it came from a merge.</summary>
    internal int Piece;

    internal PointRun<TWide> Open;

    /// <summary>
    /// The value the open run held before its last order, its largest at the order before (<see cref="GrowthSlots.Preceded"/>):
    /// where the run fell from, if it fell, as the values of the last order come in no order of their own.
    /// </summary>
    internal TWide Before;

    /// <summary>The smallest value of the open run's last order, which the run went down or up to from <see cref="Before"/>.</summary>
    internal TWide Lowest;

    /// <summary>The first closed run, its index plus one; 0 for none.</summary>
    internal int Chain;

    /// <summary>The closed runs of the chain.</summary>
    internal int Closed;

    internal TSum Sum;
}

/// <summary>What the slots of a growth share.</summary>
internal static class GrowthSlots
{
    /// <summary>The group holds a point.</summary>
    internal const int Has = 1;

    /// <summary>
    /// The group's points are read again once the pass is done: its runs were too many to keep, and the open run then
    /// spans the rows of all of them.
    /// </summary>
    internal const int Spread = 2;

    /// <summary>The open run reached an order past its first: <see cref="GrowthState{TWide, TSum}.Before"/> holds a value.</summary>
    internal const int Preceded = 4;

    /// <summary>The piece of a run that came from a merge: no point read next follows it.</summary>
    internal const int NoPiece = -1;

    /// <summary>
    /// The closed runs a group keeps before its points are read again after the pass. A group meets a run per piece of
    /// the source it lies in, a few a lane, and one more for each point that comes before the last one read; a column of
    /// points in no order meets one nearly every point, which its rows read again hold in fewer bytes.
    /// </summary>
    internal const int MostRuns = 256;

    /// <summary>Whether (o1, v1) comes before (o2, v2): by the order, then by the value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Before<TWide>(long o1, TWide v1, long o2, TWide v2)
        where TWide : INumber<TWide> => o1 < o2 || (o1 == o2 && v1 < v2);
}

/// <summary>What the settling after the pass asks of a growth's slot, whatever its types.</summary>
internal interface IGrowthRuns
{
    /// <summary>
    /// Folds a group's runs into one when each ends at or before the next one starts, the points then all known in
    /// order: true. False when two runs cross, or when the group's runs were too many to keep: its points are read again.
    /// </summary>
    bool TrySettle(int group);

    /// <summary>The rows of the source each run of <paramref name="group"/> lies between, from its first to its last.</summary>
    void Spans(int group, List<(long First, long Last)> into);

    /// <summary>What gathers the points of groups read again, and settles each from them.</summary>
    GrowthPoints Points(QueryMemory? memory);
}

/// <summary>
/// A growth over a column of points in the order of another: each group keeps the exact sum of the values its falls came
/// from, its runs of points, a run the points of a piece read in order, and the rows each lies between. A run ends where a
/// point comes before the last one, or where the lane takes a piece that does not follow the last. The runs of a group
/// fold into one after the pass when each ends at or before the next starts (<see cref="TrySettle"/>); when two cross, the
/// group's points are read again from those rows (<see cref="GrowthReads"/>).
/// </summary>
/// <remarks>
/// Folding two runs that follow each other is exact whatever was read between them: a point that came between them would
/// lie in another run, which would cross the two folded, and send the group to be read again. The pieces only keep apart
/// runs that a point read on another lane may fall between.
/// </remarks>
internal sealed class GrowthSlot<TWide, TSum, TGrowth, TResult>(PType values, PType orders, Func<TSum, TResult> finish)
    : AggregateSlot<TResult>, IGrowthRuns
    where TWide : unmanaged, INumber<TWide>
    where TSum : unmanaged
    where TGrowth : IGrowth<TWide, TSum>
{
    private GrowthState<TWide, TSum>[] _states = [];
    private int _groups;

    // The closed runs, each with the next of its chain (its index plus one; 0 at the end), the runs taken from the arrays'
    // start, the first one let go (its index plus one; 0 for none), and those the chains hold.
    private PointRun<TWide>[] _runs = [];
    private int[] _next = [];
    private int _used;
    private int _free;
    private int _held;

    private ArrayShelf? _shelf;
    private WideColumn<TWide> _values;
    private WideColumn<long> _orders;
    private PointMask _mask;

    private static int StateSize => Unsafe.SizeOf<GrowthState<TWide, TSum>>();

    private static int RunSize => Unsafe.SizeOf<PointRun<TWide>>() + sizeof(int);

    internal override void Govern(ArrayShelf shelf) => _shelf = shelf;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _states.Length)
        {
            ArrayShelf.Resize(_shelf, ref _states, Scratch.Capacity(groups, _states.Length));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TWide> points = _values.Of(input.Arena, input.Batch, input.Node, values, out ReadOnlySpan<ulong> valueValid);
        ReadOnlySpan<long> order = _orders.Of(input.Arena, input.Batch, input.OrderNode, orders, out ReadOnlySpan<ulong> orderValid);
        if (_mask.Of(input, valueValid, orderValid).IsEmpty)
        {
            FoldRange(ref _states[group], order, points, start, end, input.StartRow, input.Piece);
            return;
        }

        Fold(input, start, end, default, group);
    }

    /// <summary>The ranges of a batch, its columns read once: those whose every row is a candidate a stretch at a time.</summary>
    internal override void StepRanges(in BatchInput input, GroupRanges ranges)
    {
        ReadOnlySpan<TWide> points = _values.Of(input.Arena, input.Batch, input.Node, values, out ReadOnlySpan<ulong> valueValid);
        ReadOnlySpan<long> order = _orders.Of(input.Arena, input.Batch, input.OrderNode, orders, out ReadOnlySpan<ulong> orderValid);
        if (!_mask.Of(input, valueValid, orderValid).IsEmpty)
        {
            for (int r = 0; r < ranges.Count; r++)
            {
                Fold(input, ranges.StartAt(r), ranges.EndAt(r), default, ranges.GroupAt(r));
            }

            return;
        }

        Span<GrowthState<TWide, TSum>> states = _states;
        for (int r = 0; r < ranges.Count; r++)
        {
            FoldRange(ref states[ranges.GroupAt(r)], order, points, ranges.StartAt(r), ranges.EndAt(r), input.StartRow, input.Piece);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups) => Fold(input, input.Start, input.End, groups, 0);

    /// <summary>
    /// Folds rows [<paramref name="start"/>, <paramref name="end"/>) of one group, every one of which holds an order and a
    /// value: a point a step, then the stretch after it whose points each come at or after the one before them, order and
    /// value both, which only moves the run's last point (<see cref="GrowthKernels.Rising{TWide}"/>).
    /// </summary>
    private void FoldRange(ref GrowthState<TWide, TSum> state, ReadOnlySpan<long> order, ReadOnlySpan<TWide> points, int start, int end, long first, int piece)
    {
        int row = start;
        while (row < end)
        {
            TWide value = points[row];
            row++;
            if (!TGrowth.Takes(value))
            {
                continue;
            }

            value = TGrowth.Kept(value);
            long at = order[row - 1];
            Step(ref state, at, value, first + row - 1, piece);
            if ((state.Flags & GrowthSlots.Spread) != 0)
            {
                // The rows the group's points lie between, every point of the range in them.
                state.Open.LastRow = Math.Max(state.Open.LastRow, first + end - 1);
                return;
            }

            // The point just stepped is the run's last: the stretch after it rises from it.
            if (row == end || state.Open.LastOrder != at || state.Open.LastValue != value)
            {
                continue;
            }

            int stretch = GrowthKernels.Rising(order, points, row, end, typeof(TWide) == typeof(double));
            if (stretch > row)
            {
                Extend(ref state, order, points, row, stretch, first, piece);
                row = stretch;
            }
        }
    }

    /// <summary>
    /// Moves the open run's last point to the end of a stretch [<paramref name="from"/>, <paramref name="to"/>) whose points
    /// each come at or after the one before them, order and value both, from the run's last: no fall, so the sum stays; the
    /// value the last order came from and its smallest are those of the stretch's last order.
    /// </summary>
    private static void Extend(ref GrowthState<TWide, TSum> state, ReadOnlySpan<long> order, ReadOnlySpan<TWide> points, int from, int to, long first, int piece)
    {
        int last = to - 1;
        long at = order[last];

        // The first row of the last order: where the stretch reached it, unless the run held it before the stretch.
        int k = last;
        while (k > from && order[k - 1] == at)
        {
            k--;
        }

        if (order[k - 1] != at)
        {
            state.Before = TGrowth.Kept(points[k - 1]);
            state.Lowest = TGrowth.Kept(points[k]);
            state.Flags |= GrowthSlots.Preceded;
        }

        state.Open.LastOrder = at;
        state.Open.LastValue = TGrowth.Kept(points[last]);
        state.Open.LastRow = first + last;
        state.Piece = piece;
    }

    private void Fold(in BatchInput input, int start, int end, ReadOnlySpan<int> groups, int group)
    {
        ReadOnlySpan<TWide> points = _values.Of(input.Arena, input.Batch, input.Node, values, out ReadOnlySpan<ulong> valueValid);
        ReadOnlySpan<long> order = _orders.Of(input.Arena, input.Batch, input.OrderNode, orders, out ReadOnlySpan<ulong> orderValid);
        ReadOnlySpan<ulong> mask = _mask.Of(input, valueValid, orderValid);
        Span<GrowthState<TWide, TSum>> states = _states;
        long first = input.StartRow;
        int piece = input.Piece;
        RowCursor cursor = new RowCursor(mask, start, end);
        while (cursor.Next(out int row))
        {
            TWide value = points[row];
            if (!TGrowth.Takes(value))
            {
                continue;
            }

            Step(ref states[groups.IsEmpty ? group : groups[row]], order[row], TGrowth.Kept(value), first + row, piece);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Step(ref GrowthState<TWide, TSum> state, long order, TWide value, long row, int piece)
    {
        if ((state.Flags & GrowthSlots.Has) == 0)
        {
            state.Flags = GrowthSlots.Has;
            state.Open = PointRun<TWide>.Of(order, value, row);
            state.Piece = piece;
            return;
        }

        if ((state.Flags & GrowthSlots.Spread) != 0)
        {
            state.Open.FirstRow = Math.Min(state.Open.FirstRow, row);
            state.Open.LastRow = Math.Max(state.Open.LastRow, row);
            return;
        }

        ref PointRun<TWide> open = ref state.Open;
        if (order >= open.LastOrder && (piece == state.Piece || (state.Piece >= 0 && piece == state.Piece + 1)))
        {
            if (order > open.LastOrder)
            {
                // A later order: the run falls to the value, or rises.
                if (value < open.LastValue)
                {
                    TGrowth.Add(ref state.Sum, open.LastValue);
                }

                state.Before = open.LastValue;
                state.Lowest = value;
                state.Flags |= GrowthSlots.Preceded;
                open.LastOrder = order;
            }
            else if (!(value < open.LastValue))
            {
                // The same order, a value as large or larger: the values of one order rise.
            }
            else if ((state.Flags & GrowthSlots.Preceded) == 0)
            {
                // The run's first order, a value below its largest: the first point is its smallest.
                if (value < open.FirstValue)
                {
                    open.FirstValue = value;
                }

                open.LastRow = row;
                state.Piece = piece;
                return;
            }
            else
            {
                // A value below the last order's largest: the run reached that order through its smallest, which fell
                // from the order before once it is below it.
                if (value < state.Lowest)
                {
                    if (!(state.Lowest < state.Before) && value < state.Before)
                    {
                        TGrowth.Add(ref state.Sum, state.Before);
                    }

                    state.Lowest = value;
                }

                open.LastRow = row;
                state.Piece = piece;
                return;
            }

            open.LastValue = value;
            open.LastRow = row;
            state.Piece = piece;
            return;
        }

        Close(ref state);
        if ((state.Flags & GrowthSlots.Spread) != 0)
        {
            // The runs grew too many: the open run now spans the rows of all of them, this point's too.
            state.Open.FirstRow = Math.Min(state.Open.FirstRow, row);
            state.Open.LastRow = Math.Max(state.Open.LastRow, row);
            return;
        }

        // A new run, at its first order.
        state.Open = PointRun<TWide>.Of(order, value, row);
        state.Flags &= ~GrowthSlots.Preceded;
        state.Piece = piece;
    }

    /// <summary>Closes the open run into the group's chain; past <see cref="GrowthSlots.MostRuns"/>, spreads the group.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Close(ref GrowthState<TWide, TSum> state)
    {
        Push(ref state, in state.Open);
        if (state.Closed > GrowthSlots.MostRuns)
        {
            SpreadOut(ref state);
        }
    }

    /// <summary>Adds a run to the chain of a group.</summary>
    private void Push(ref GrowthState<TWide, TSum> state, in PointRun<TWide> run)
    {
        int at = Take();
        _runs[at] = run;
        _next[at] = state.Chain;
        state.Chain = at + 1;
        state.Closed++;
    }

    /// <summary>A run of the arrays, one let go or the next past those taken.</summary>
    private int Take()
    {
        _held++;
        if (_free != 0)
        {
            int at = _free - 1;
            _free = _next[at];
            return at;
        }

        if (_used == _runs.Length)
        {
            int capacity = Scratch.Capacity(_used + 1, _runs.Length);
            ArrayShelf.Resize(_shelf, ref _runs, capacity);
            ArrayShelf.Resize(_shelf, ref _next, capacity);
        }

        return _used++;
    }

    /// <summary>Lets a group's chain go.</summary>
    private void Free(ref GrowthState<TWide, TSum> state)
    {
        int link = state.Chain;
        while (link != 0)
        {
            int at = link - 1;
            link = _next[at];
            _next[at] = _free;
            _free = at + 1;
            _held--;
        }

        state.Chain = 0;
        state.Closed = 0;
    }

    /// <summary>The group's points to be read again: its open run spans the rows of every run, which are let go.</summary>
    private void SpreadOut(ref GrowthState<TWide, TSum> state)
    {
        (long first, long last) = Rows(in state, this);
        Free(ref state);
        state.Flags |= GrowthSlots.Spread;
        state.Open.FirstRow = first;
        state.Open.LastRow = last;
    }

    /// <summary>The rows from the first of a group's runs to the last of them, its chain in <paramref name="slot"/>'s arrays.</summary>
    private static (long First, long Last) Rows(in GrowthState<TWide, TSum> state, GrowthSlot<TWide, TSum, TGrowth, TResult> slot)
    {
        long first = state.Open.FirstRow;
        long last = state.Open.LastRow;
        for (int link = state.Chain; link != 0; link = slot._next[link - 1])
        {
            first = Math.Min(first, slot._runs[link - 1].FirstRow);
            last = Math.Max(last, slot._runs[link - 1].LastRow);
        }

        return (first, last);
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        GrowthSlot<TWide, TSum, TGrowth, TResult> theirs = (GrowthSlot<TWide, TSum, TGrowth, TResult>)other;
        for (int i = 0; i < from.Length; i++)
        {
            ref GrowthState<TWide, TSum> state = ref theirs._states[from[i]];
            if ((state.Flags & GrowthSlots.Has) != 0)
            {
                Absorb(ref _states[into[i]], in state, theirs);
            }
        }
    }

    /// <summary>
    /// Folds another lane's state of the same group into <paramref name="mine"/>: the sums added, and its runs, the open
    /// one too, closed into the chain, since no point read here follows them.
    /// </summary>
    private void Absorb(ref GrowthState<TWide, TSum> mine, in GrowthState<TWide, TSum> theirs, GrowthSlot<TWide, TSum, TGrowth, TResult> slot)
    {
        if ((mine.Flags & GrowthSlots.Has) == 0)
        {
            mine = theirs;
            mine.Chain = 0;
            mine.Closed = 0;
            mine.Piece = GrowthSlots.NoPiece;
            for (int link = theirs.Chain; link != 0; link = slot._next[link - 1])
            {
                Push(ref mine, in slot._runs[link - 1]);
            }

            return;
        }

        TGrowth.Merge(ref mine.Sum, in theirs.Sum);
        if (((mine.Flags | theirs.Flags) & GrowthSlots.Spread) != 0)
        {
            (long first, long last) = Rows(in theirs, slot);
            SpreadOut(ref mine);
            mine.Open.FirstRow = Math.Min(mine.Open.FirstRow, first);
            mine.Open.LastRow = Math.Max(mine.Open.LastRow, last);
            return;
        }

        Push(ref mine, in theirs.Open);
        for (int link = theirs.Chain; link != 0; link = slot._next[link - 1])
        {
            Push(ref mine, in slot._runs[link - 1]);
        }

        if (mine.Closed > GrowthSlots.MostRuns)
        {
            SpreadOut(ref mine);
        }
    }

    public bool TrySettle(int group)
    {
        ref GrowthState<TWide, TSum> state = ref _states[group];
        if ((state.Flags & GrowthSlots.Has) == 0 || state.Chain == 0)
        {
            return (state.Flags & GrowthSlots.Spread) == 0;
        }

        if ((state.Flags & GrowthSlots.Spread) != 0)
        {
            return false;
        }

        // The runs in the order of their first points, then of their last: each must end where the next starts or before.
        int count = state.Closed + 1;
        PointRun<TWide>[] runs = ArrayShelf.Rent<PointRun<TWide>>(count);
        try
        {
            runs[0] = state.Open;
            int at = 1;
            for (int link = state.Chain; link != 0; link = _next[link - 1])
            {
                runs[at++] = _runs[link - 1];
            }

            Span<PointRun<TWide>> held = runs.AsSpan(0, count);
            held.Sort(static (a, b) =>
                GrowthSlots.Before(a.FirstOrder, a.FirstValue, b.FirstOrder, b.FirstValue) ? -1
                : GrowthSlots.Before(b.FirstOrder, b.FirstValue, a.FirstOrder, a.FirstValue) ? 1
                : GrowthSlots.Before(a.LastOrder, a.LastValue, b.LastOrder, b.LastValue) ? -1
                : GrowthSlots.Before(b.LastOrder, b.LastValue, a.LastOrder, a.LastValue) ? 1
                : 0);
            TSum sum = state.Sum;
            long firstRow = long.MaxValue;
            long lastRow = long.MinValue;
            for (int i = 0; i < held.Length; i++)
            {
                firstRow = Math.Min(firstRow, held[i].FirstRow);
                lastRow = Math.Max(lastRow, held[i].LastRow);
                if (i == 0)
                {
                    continue;
                }

                ref PointRun<TWide> before = ref held[i - 1];
                ref PointRun<TWide> next = ref held[i];
                if (GrowthSlots.Before(next.FirstOrder, next.FirstValue, before.LastOrder, before.LastValue))
                {
                    return false;
                }

                if (next.FirstValue < before.LastValue)
                {
                    TGrowth.Add(ref sum, before.LastValue);
                }
            }

            Free(ref state);
            state.Sum = sum;
            state.Open = new PointRun<TWide>
            {
                FirstOrder = held[0].FirstOrder,
                FirstValue = held[0].FirstValue,
                LastOrder = held[^1].LastOrder,
                LastValue = held[^1].LastValue,
                FirstRow = firstRow,
                LastRow = lastRow,
            };
            state.Flags &= ~GrowthSlots.Preceded;
            state.Piece = GrowthSlots.NoPiece;
            return true;
        }
        finally
        {
            ArrayShelf.Return(runs);
        }
    }

    public void Spans(int group, List<(long First, long Last)> into)
    {
        ref GrowthState<TWide, TSum> state = ref _states[group];
        into.Add((state.Open.FirstRow, state.Open.LastRow));
        for (int link = state.Chain; link != 0; link = _next[link - 1])
        {
            into.Add((_runs[link - 1].FirstRow, _runs[link - 1].LastRow));
        }
    }

    public GrowthPoints Points(QueryMemory? memory) => new GrowthPoints<TWide, TSum, TGrowth, TResult>(values, orders, memory);

    /// <summary>A group's points read again, in order: the group's state made of them, its runs let go.</summary>
    internal void Settle(int group, ReadOnlySpan<long> order, ReadOnlySpan<TWide> value)
    {
        ref GrowthState<TWide, TSum> state = ref _states[group];
        Free(ref state);
        TSum sum = default;
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] < value[i - 1])
            {
                TGrowth.Add(ref sum, value[i - 1]);
            }
        }

        long firstRow = state.Open.FirstRow;
        long lastRow = state.Open.LastRow;
        state.Sum = sum;
        state.Open = new PointRun<TWide>
        {
            FirstOrder = order[0],
            FirstValue = value[0],
            LastOrder = order[^1],
            LastValue = value[^1],
            FirstRow = firstRow,
            LastRow = lastRow,
        };
        state.Flags = GrowthSlots.Has;
        state.Piece = GrowthSlots.NoPiece;
    }

    internal override TResult Result(int group)
    {
        ref GrowthState<TWide, TSum> state = ref _states[group];
        if ((state.Flags & GrowthSlots.Has) == 0)
        {
            return default!;
        }

        if ((state.Flags & GrowthSlots.Spread) != 0 || state.Chain != 0)
        {
            throw new InvalidOperationException(
                $"The growth of group {group}, whose runs of points were not settled after the pass, was read: flags {state.Flags}, {state.Closed} runs closed, rows {state.Open.FirstRow} to {state.Open.LastRow}.");
        }

        return finish(TGrowth.Growth(in state.Sum, state.Open.FirstValue, state.Open.LastValue));
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The chains of the groups left out let go first, the kept states then moved to the front, in order.
        int kept = 0;
        for (int group = 0; group < _groups; group++)
        {
            if (kept < groups.Length && groups[kept] == group)
            {
                kept++;
                continue;
            }

            Free(ref _states[group]);
        }

        for (int i = 0; i < groups.Length; i++)
        {
            _states[i] = _states[groups[i]];
        }

        _states.AsSpan(groups.Length, _groups - groups.Length).Clear();
        _groups = groups.Length;
    }

    internal override void Clear()
    {
        _states.AsSpan(0, _groups).Clear();
        _groups = 0;
        _used = 0;
        _free = 0;
        _held = 0;
    }

    internal override void Release()
    {
        if (_states.Length > 0)
        {
            _shelf?.Give(_states);
            _states = [];
        }

        if (_runs.Length > 0)
        {
            _shelf?.Give(_runs);
            _shelf?.Give(_next);
            _runs = [];
            _next = [];
        }

        _groups = 0;
        _used = 0;
        _free = 0;
        _held = 0;
        _values.Release();
        _orders.Release();
        _mask.Release();
    }

    internal override long Footprint => ((long)_states.Length * StateSize) + ((long)_runs.Length * RunSize);

    /// <summary>A state a new group, and a run a row at most: a run closes where a row starts another.</summary>
    internal override long GrowthFor(int more) => StatesFor(more) + RunsFor(more);

    /// <summary>A state a new group, and the runs of the other's groups, each open run closed here.</summary>
    internal override long GrowthFor(AggregateSlot from, int groups)
    {
        GrowthSlot<TWide, TSum, TGrowth, TResult> theirs = (GrowthSlot<TWide, TSum, TGrowth, TResult>)from;
        return StatesFor(groups) + RunsFor(theirs._held + theirs._groups);
    }

    private long StatesFor(int more) =>
        _groups + (long)more > _states.Length ? (long)Scratch.Capacity((int)Math.Min(int.MaxValue, _groups + (long)more), _states.Length) * StateSize : 0;

    private long RunsFor(long more) =>
        _free == 0 && _used + more > _runs.Length ? (long)Scratch.Capacity((int)Math.Min(int.MaxValue, _used + more), _runs.Length) * RunSize : 0;

    internal override bool SpillsStates => true;

    internal override void WriteStates(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            ref GrowthState<TWide, TSum> state = ref _states[group];
            buffer.Write(state);
            for (int link = state.Chain; link != 0; link = _next[link - 1])
            {
                buffer.Write(_runs[link - 1]);
            }
        }
    }

    internal override void ReadStates(ref SpillReader reader, int count)
    {
        EnsureGroups(count);
        for (int group = 0; group < count; group++)
        {
            GrowthState<TWide, TSum> state = reader.Read<GrowthState<TWide, TSum>>();
            int closed = state.Closed;
            state.Chain = 0;
            state.Closed = 0;
            state.Piece = GrowthSlots.NoPiece;
            for (int r = 0; r < closed; r++)
            {
                Push(ref state, reader.Read<PointRun<TWide>>());
            }

            _states[group] = state;
        }
    }
}

/// <summary>The points of the groups a growth reads again after the pass, gathered from the batches of that read.</summary>
internal abstract class GrowthPoints
{
    /// <summary>
    /// The rows of a batch that are points: those <paramref name="selection"/> keeps, whose order and value both hold one,
    /// and whose value makes a point. The batch's columns are read once, for <see cref="Add"/> too.
    /// </summary>
    internal abstract ReadOnlySpan<ulong> Rows(CanonicalArena arena, long batch, int valueNode, int orderNode, int rows, ReadOnlySpan<ulong> selection);

    /// <summary>
    /// Adds the points of rows <paramref name="rows"/> of the batch <see cref="Rows"/> read, row <c>rows[i]</c> to target
    /// <c>targets[i]</c>: false, the points let go, when the query's memory does not hold them.
    /// </summary>
    internal abstract bool Add(CanonicalArena arena, long batch, int valueNode, int orderNode, ReadOnlySpan<int> rows, ReadOnlySpan<int> targets);

    /// <summary>Sorts the points by target, then in order, and settles each target's group from its own.</summary>
    internal abstract bool Settle(ReadOnlySpan<(AggregateSlot Slot, int Group)> targets);

    /// <summary>Gives back what the points held.</summary>
    internal abstract void Release();
}

/// <summary>The points of groups read again, as (target, order, value), held under the query's memory.</summary>
internal sealed class GrowthPoints<TWide, TSum, TGrowth, TResult>(PType values, PType orders, QueryMemory? memory) : GrowthPoints
    where TWide : unmanaged, INumber<TWide>
    where TSum : unmanaged
    where TGrowth : IGrowth<TWide, TSum>
{

    private WideColumn<TWide> _values;
    private WideColumn<long> _orders;

    private GrowthPoint<TWide>[] _points = [];
    private int _count;
    private ulong[] _rows = [];

    internal override ReadOnlySpan<ulong> Rows(CanonicalArena arena, long batch, int valueNode, int orderNode, int rows, ReadOnlySpan<ulong> selection)
    {
        ReadOnlySpan<TWide> value = _values.Of(arena, batch, valueNode, values, out ReadOnlySpan<ulong> valueValid);
        _ = _orders.Of(arena, batch, orderNode, orders, out ReadOnlySpan<ulong> orderValid);

        // Every row, then those each mask keeps, then, for a float, those whose value makes a point.
        int words = (rows + 63) >> 6;
        Scratch.Grow(ref _rows, words);
        Span<ulong> into = _rows.AsSpan(0, words);
        into.Fill(ulong.MaxValue);
        if ((rows & 63) != 0)
        {
            into[^1] = (1UL << (rows & 63)) - 1;
        }

        Keep(into, selection);
        Keep(into, valueValid);
        Keep(into, orderValid);
        if (typeof(TWide) == typeof(double))
        {
            RowCursor cursor = new RowCursor(into, 0, rows);
            while (cursor.Next(out int row))
            {
                if (!TGrowth.Takes(value[row]))
                {
                    into[row >> 6] &= ~(1UL << (row & 63));
                }
            }
        }

        return into;
    }

    /// <summary>The rows <paramref name="mask"/> keeps, every one when it is empty.</summary>
    private static void Keep(Span<ulong> into, ReadOnlySpan<ulong> mask)
    {
        if (mask.IsEmpty)
        {
            return;
        }

        for (int w = 0; w < into.Length; w++)
        {
            into[w] &= w < mask.Length ? mask[w] : 0;
        }
    }

    internal override bool Add(CanonicalArena arena, long batch, int valueNode, int orderNode, ReadOnlySpan<int> rows, ReadOnlySpan<int> targets)
    {
        ReadOnlySpan<TWide> value = _values.Of(arena, batch, valueNode, values, out _);
        ReadOnlySpan<long> order = _orders.Of(arena, batch, orderNode, orders, out _);
        if (_count + rows.Length > _points.Length && !TryGrow(_count + rows.Length))
        {
            return false;
        }

        Span<GrowthPoint<TWide>> into = _points.AsSpan(_count, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            into[i] = new GrowthPoint<TWide>(targets[i], order[row], TGrowth.Kept(value[row]));
        }

        _count += rows.Length;
        return true;
    }

    /// <summary>Room for <paramref name="length"/> points under the query's memory; false, the points let go, when it refuses it.</summary>
    private bool TryGrow(int length)
    {
        if (!QueryArrays.TryRent(memory, Scratch.Capacity(length, _points.Length), out GrowthPoint<TWide>[] grown))
        {
            Release();
            return false;
        }

        _points.AsSpan(0, _count).CopyTo(grown);
        Give();
        _points = grown;
        return true;
    }

    internal override bool Settle(ReadOnlySpan<(AggregateSlot Slot, int Group)> targets)
    {
        // Sorted in place by target, then in order: each target's points then lie side by side.
        Span<GrowthPoint<TWide>> points = _points.AsSpan(0, _count);
        points.Sort(default(GrowthPointOrder<TWide>));
        long[] order = [];
        TWide[] value = [];
        try
        {
            int start = 0;
            while (start < points.Length)
            {
                int target = points[start].Target;
                int end = start + 1;
                while (end < points.Length && points[end].Target == target)
                {
                    end++;
                }

                Scratch.Grow(ref order, end - start);
                Scratch.Grow(ref value, end - start);
                for (int i = start; i < end; i++)
                {
                    order[i - start] = points[i].Order;
                    value[i - start] = points[i].Value;
                }

                (AggregateSlot slot, int group) = targets[target];
                ((GrowthSlot<TWide, TSum, TGrowth, TResult>)slot).Settle(group, order.AsSpan(0, end - start), value.AsSpan(0, end - start));
                start = end;
            }

            return true;
        }
        finally
        {
            Scratch.Return(ref order);
            Scratch.Return(ref value);
        }
    }

    internal override void Release()
    {
        Give();
        _points = [];
        _count = 0;
        _values.Release();
        _orders.Release();
        Scratch.Return(ref _rows);
    }

    private void Give()
    {
        if (_points.Length > 0)
        {
            QueryArrays.Return(memory, _points);
        }
    }
}

/// <summary>A point read again, and the group it goes to among those read.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct GrowthPoint<TWide>(int target, long order, TWide value)
    where TWide : unmanaged
{
    internal readonly int Target = target;
    internal readonly long Order = order;
    internal readonly TWide Value = value;
}

/// <summary>Points by group, then in order: by the order, then by the value.</summary>
internal readonly struct GrowthPointOrder<TWide> : IComparer<GrowthPoint<TWide>>
    where TWide : unmanaged, INumber<TWide>
{
    public int Compare(GrowthPoint<TWide> x, GrowthPoint<TWide> y) =>
        x.Target != y.Target ? x.Target.CompareTo(y.Target)
        : x.Order != y.Order ? x.Order.CompareTo(y.Order)
        : x.Value < y.Value ? -1 : y.Value < x.Value ? 1 : 0;
}

/// <summary>
/// The value of a group's first or last point in the order of a column: the point that comes first, or last, by the
/// order, then by the value. Its state is the point, which merges by the same comparison: the same bits at every degree.
/// </summary>
internal sealed class EdgeSlot<TWide, TSum, TGrowth, TResult>(bool last, PType values, PType orders, Func<TWide, TResult> finish)
    : RecordSlot<EdgeState<TWide>, TResult>
    where TWide : unmanaged, INumber<TWide>
    where TSum : unmanaged
    where TGrowth : IGrowth<TWide, TSum>
{
    private WideColumn<TWide> _values;
    private WideColumn<long> _orders;
    private PointMask _mask;

    internal override EdgeState<TWide> Seed => default;

    private protected override void ReleaseScratch()
    {
        _values.Release();
        _orders.Release();
        _mask.Release();
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group) => Fold(input, start, end, default, group);

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups) => Fold(input, input.Start, input.End, groups, 0);

    private void Fold(in BatchInput input, int start, int end, ReadOnlySpan<int> groups, int group)
    {
        ReadOnlySpan<TWide> points = _values.Of(input.Arena, input.Batch, input.Node, values, out ReadOnlySpan<ulong> valueValid);
        ReadOnlySpan<long> order = _orders.Of(input.Arena, input.Batch, input.OrderNode, orders, out ReadOnlySpan<ulong> orderValid);
        ReadOnlySpan<ulong> mask = _mask.Of(input, valueValid, orderValid);
        StateView<EdgeState<TWide>> states = States;
        RowCursor cursor = new RowCursor(mask, start, end);
        while (cursor.Next(out int row))
        {
            TWide value = points[row];
            if (TGrowth.Takes(value))
            {
                Consider(ref states[groups.IsEmpty ? group : groups[row]], order[row], TGrowth.Kept(value));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consider(ref EdgeState<TWide> state, long order, TWide value)
    {
        if (state.Has == 0
            || (last ? GrowthSlots.Before(state.Order, state.Value, order, value) : GrowthSlots.Before(order, value, state.Order, state.Value)))
        {
            state.Order = order;
            state.Value = value;
            state.Has = 1;
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        StateView<EdgeState<TWide>> states = States;
        StateView<EdgeState<TWide>> others = StatesOf(other);
        for (int i = 0; i < from.Length; i++)
        {
            ref EdgeState<TWide> theirs = ref others[from[i]];
            if (theirs.Has != 0)
            {
                Consider(ref states[into[i]], theirs.Order, theirs.Value);
            }
        }
    }

    internal override TResult Result(int group)
    {
        EdgeState<TWide> state = State(group);
        return state.Has == 0 ? default! : finish(state.Value);
    }
}

/// <summary>A group's first or last point so far.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct EdgeState<TWide>
    where TWide : unmanaged
{
    internal long Order;
    internal TWide Value;
    internal long Has;
}

/// <summary>The kernels of a growth's step: the stretch of points that only rise, found four rows at a time.</summary>
internal static class GrowthKernels
{
    /// <summary>
    /// The first row of [<paramref name="from"/>, <paramref name="to"/>) whose point does not come at or after the one
    /// before it, by its order and by its value both, or, for <paramref name="floats"/>, whose value is a NaN or an
    /// infinity; <paramref name="to"/> when every one does. Row <paramref name="from"/> − 1 is the first compared against.
    /// </summary>
    /// <remarks>
    /// Four rows a vector, each against the row before it, both read unaligned; the scalar loop finishes, and runs alone
    /// where vectors of 256 bits are not accelerated.
    /// </remarks>
    internal static int Rising<TWide>(ReadOnlySpan<long> order, ReadOnlySpan<TWide> values, int from, int to, bool floats)
        where TWide : unmanaged, INumber<TWide>
    {
        int row = from;
        if (Vector256.IsHardwareAccelerated && Vector256<TWide>.IsSupported && Vector256<TWide>.Count == Vector256<long>.Count)
        {
            row = RisingVectors(order, values, from, to, floats);
            if (row < to - Vector256<long>.Count + 1)
            {
                return row;
            }
        }

        return RisingScalar(order, values, row, to, floats);
    }

    /// <summary>The scalar twin of <see cref="Rising{TWide}"/>, from <paramref name="from"/>.</summary>
    internal static int RisingScalar<TWide>(ReadOnlySpan<long> order, ReadOnlySpan<TWide> values, int from, int to, bool floats)
        where TWide : unmanaged, INumber<TWide>
    {
        for (int row = from; row < to; row++)
        {
            TWide value = values[row];
            if (order[row] < order[row - 1] || !(value >= values[row - 1]) || (floats && !(value - value == TWide.Zero)))
            {
                return row;
            }
        }

        return to;
    }

    /// <summary>The rows of [<paramref name="from"/>, <paramref name="to"/>) four at a time: the first that breaks, or the first past the whole vectors.</summary>
    private static int RisingVectors<TWide>(ReadOnlySpan<long> order, ReadOnlySpan<TWide> values, int from, int to, bool floats)
        where TWide : unmanaged, INumber<TWide>
    {
        int width = Vector256<long>.Count;
        ref long orders = ref MemoryMarshal.GetReference(order);
        ref TWide points = ref MemoryMarshal.GetReference(values);
        int row = from;
        for (; row + width <= to; row += width)
        {
            Vector256<long> at = Vector256.LoadUnsafe(ref orders, (nuint)row);
            Vector256<long> before = Vector256.LoadUnsafe(ref orders, (nuint)(row - 1));
            Vector256<TWide> value = Vector256.LoadUnsafe(ref points, (nuint)row);
            Vector256<TWide> previous = Vector256.LoadUnsafe(ref points, (nuint)(row - 1));
            Vector256<long> rises = Vector256.GreaterThanOrEqual(at, before) & Vector256.GreaterThanOrEqual(value, previous).As<TWide, long>();
            if (floats)
            {
                rises &= Vector256.Equals(value - value, Vector256<TWide>.Zero).As<TWide, long>();
            }

            uint broken = ~rises.ExtractMostSignificantBits() & ((1u << width) - 1);
            if (broken != 0)
            {
                return row + BitOperations.TrailingZeroCount(broken);
            }
        }

        return row;
    }
}
