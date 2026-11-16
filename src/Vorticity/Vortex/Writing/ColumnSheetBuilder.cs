using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// Builds one column's sheet (<see cref="ColumnSheet"/>) as the column is written: each valid row's value hashed as a
/// Bloom filter hashes it, the hash counted while the column's distinct values number at most <see cref="ExactLimit"/>,
/// with the bytes of each value first met while they fit in <see cref="ValueBytesLimit"/>, and the register of each
/// distinct hash raised. Past the limit, or once nine rows in ten of the column are values it had not met, the counts
/// go and the registers alone go on.
/// </summary>
/// <remarks>
/// <para>
/// It rides with the file's indexes, fed as they are, but no budget weighs it and no policy names it: a sheet is what a
/// dataset's object says of every column it holds.
/// </para>
/// <para>
/// A text column's rows are looked up in a small table of the views met last first: a value of twelve bytes or fewer
/// lies in its view, so a view met again is the same value, counted without a hash or a probe of the counts.
/// Fixed-width values are hashed a batch at a time. Measured on the design's requests (<c>vortex-queries --micro
/// sheets</c>), eleven columns, one thread.
/// </para>
/// </remarks>
internal sealed class ColumnSheetBuilder : IndexBuilder
{
    /// <summary>The distinct values counted exactly, and kept for a filter, before the registers alone estimate them.</summary>
    internal const int ExactLimit = 1 << 20;

    /// <summary>The bytes of the values first met a builder keeps, for the frequent ones the sheet names.</summary>
    internal const int ValueBytesLimit = 16 << 20;

    /// <summary>The share of the valid rows the distinct values may reach for the sheet to keep frequent values and a filter: a quarter.</summary>
    private const int RepeatShare = 4;

    /// <summary>
    /// The valid rows at which a column first shows whether its values repeat: past it, at every doubling, a column with a
    /// new value nine rows in ten drops its counts: an identifier, an instant, a measure, whose sheet keeps no filter.
    /// </summary>
    private const long FirstCheck = 1 << 16;

    private const int FirstSlots = 1 << 10;

    /// <summary>The views a text column's rows are looked up in first: a power of two, 24 KiB.</summary>
    private const int Views = 1_024;

    /// <summary>The values hashed together, a fixed-width column's.</summary>
    private const int HashBatch = 64;

    // The slots past which the table outgrows the second cache (512 KiB of slots): a batch's slots are fetched ahead.
    private const int PrefetchSlots = 1 << 15;

    /// <summary>
    /// The bits of a slot's word its rows take, some 10^12 of them; the number of its value, in the order the values were
    /// met, lies above them.
    /// </summary>
    private const int IdShift = 40;

    private const long RowsMask = (1L << IdShift) - 1;

    // The builder's working blocks come from the pool and go back to it as they grow and when the builder is done
    // (Dispose): made new, an object's sheets took a third of a megabyte each, some 4.6 bytes a row of a consolidation of
    // S1's logs, a sixth of what it allocated. What a sheet keeps once finished is its own.
    private readonly Block<byte> _ranks = Blocks.RentCleared<byte>(ColumnSheet.Registers);
    private long _rows;
    private long _nulls;
    private long _check = FirstCheck;

    // The distinct hashes, by open addressing on their mix, each beside its rows and its value's number, one line of the
    // cache a probe, and apart where its value's bytes lie in the heap, -1 when they did not fit; 0 is no hash, and the
    // hash 0 is counted apart. Empty once the counts go.
    private Block<Slot> _slots = Blocks.RentCleared<Slot>(FirstSlots);
    private Block<int> _offsets = Blocks.RentCleared<int>(FirstSlots);
    private Block<int> _lengths = Blocks.RentCleared<int>(FirstSlots);
    private int _distinct;
    private int _ids;
    private long _zeroRows;
    private int _zeroId = -1;
    private int _zeroOffset = -1;
    private int _zeroLength;
    private Block<byte> _heap;
    private int _heapUsed;

    // Each number's slot, ZeroSlot for the value of hash 0, Unkept for one whose bytes did not fit, built once the rows
    // are in for the builders that read the values where they lie (KeptEveryValue); the rows it was built at.
    private Block<int> _slotOf;
    private long _slotOfRows = -1;
    private bool _keptEvery;

    // The views met last of a text column, three words an entry: the view's two halves and its slot plus one, or 1
    // once the counts go, 0 for none.
    private Block<ulong> _views;

    // The number of each row's value the last rows taken hold, -1 for a null, when the column's counts ask for them; and
    // whether those rows' numbers are all there.
    private Block<int> _rowIds;
    private bool _rowIdsFilled;

    // The hash of each row of the batch being fed, by its row in the batch, 0 for a null, when another column's counts ask
    // for them; and whether every row fed of the batch has its hash.
    private Block<ulong> _rowHashes;
    private bool _rowHashesWhole;

    // Whether the working blocks went back to the pool (Dispose), the ranks among them, which a second giving would lend twice.
    private bool _disposed;

    /// <summary>Whether the builder can describe a column of <paramref name="dtype"/>: an integer, a float, a string or a binary, through any extension.</summary>
    internal static bool Supports(DType dtype) => KeyLayout.TryOf(dtype, out _);

    /// <summary>
    /// Whether the builder still counts the column's values, its distinct values within <see cref="ExactLimit"/> and not
    /// new nine rows in ten; a column's counts number its rows' values while it does.
    /// </summary>
    internal bool Counting => !_slots.IsEmpty;

    /// <summary>The rows taken, nulls included.</summary>
    internal long Rows => _rows;

    /// <summary>The nulls among the rows taken.</summary>
    internal long Nulls => _nulls;

    /// <summary>The distinct values counted, while <see cref="Counting"/>.</summary>
    internal int Distinct => _distinct + (_zeroRows > 0 ? 1 : 0);

    /// <summary>The values numbered: each distinct value counted has a number below it, in the order they were met.</summary>
    internal int Numbered => _ids;

    /// <summary>
    /// The number of the value of each row the last <see cref="Accumulate"/> took, from its first, -1 for a null; empty
    /// when those rows were not numbered, the counts gone or the column of a kind whose values no index keys. Asked for
    /// with <see cref="NumberRows"/>.
    /// </summary>
    internal ReadOnlySpan<int> RowIds => _rowIdsFilled ? _rowIds : default;

    /// <summary>Asks the builder to number each row's value as it takes it (<see cref="RowIds"/>), for the column's counts.</summary>
    internal void NumberRows()
    {
        if (_rowIds.IsEmpty)
        {
            _rowIds = Blocks.Rent<int>(1_024);
        }
    }

    /// <summary>Asks the builder to keep the hash of each row of a batch (<see cref="RowHashes"/>), for the distinct values another column's counts keep of it.</summary>
    internal void HashRows()
    {
        if (_rowHashes.IsEmpty)
        {
            _rowHashes = Blocks.RentCleared<ulong>(1_024);
        }
    }

    /// <summary>Stops keeping the hash of each row: no column's counts asks for them any more.</summary>
    internal void StopHashing()
    {
        Blocks.Return(_rowHashes);
        _rowHashes = default;
    }

    /// <summary>Every working block back to the pool, once: the builder is not used again.</summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Blocks.Return(_slots);
        Blocks.Return(_offsets);
        Blocks.Return(_lengths);
        Blocks.Return(_heap);
        Blocks.Return(_views);
        Blocks.Return(_rowIds);
        Blocks.Return(_rowHashes);
        Blocks.Return(_ranks);
        Blocks.Return(_slotOf);
        _slotOf = default;
        _slots = default;
        _offsets = default;
        _lengths = default;
        _heap = default;
        _views = default;
        _rowIds = default;
        _rowHashes = default;
        base.Dispose();
    }

    /// <summary>
    /// The hash of each row of the batch fed last, by its row in the batch, 0 for a null; empty when a row of it has none,
    /// the counts gone. Asked for with <see cref="HashRows"/>.
    /// </summary>
    internal ReadOnlySpan<ulong> RowHashes => _rowHashesWhole && !_slots.IsEmpty ? _rowHashes : default;

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        _rowIdsFilled = false;
        if (count <= 0)
        {
            return;
        }

        if (!_rowIds.IsEmpty && _rowIds.Length < count)
        {
            // At a power of two, as the pool lends: a batch's count asked as it came would find a class's whole only by
            // chance.
            Blocks.Ensure(ref _rowIds, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, _rowIds.Length * 2)));
        }

        // A batch's first rows start its hashes; its next pieces keep those before them.
        if (!_rowHashes.IsEmpty)
        {
            _rowHashesWhole = start == 0 || _rowHashesWhole;
            if (_rowHashes.Length < start + count)
            {
                Block<ulong> grown = Blocks.RentCleared<ulong>((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(start + count, _rowHashes.Length * 2)));
                _rowHashes.AsSpan(0, Math.Min(start, _rowHashes.Length)).CopyTo(grown);
                Blocks.Return(_rowHashes);
                _rowHashes = grown;
            }
        }

        _rows += count;
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            _nulls += count;
            if (!_rowIds.IsEmpty)
            {
                _rowIds.AsSpan(0, count).Fill(-1);
            }

            if (!_rowHashes.IsEmpty)
            {
                _rowHashes.AsSpan(start, count).Clear();
            }

            _rowIdsFilled = !_rowIds.IsEmpty;
            return;
        }

        bool allValid = own.AllValid && wrapper.AllValid;
        switch (node.Kind)
        {
            case CanonicalKind.Primitive:
                Fixed(node.Values.Span, node.PType.ByteWidth(), start, count, allValid, own, wrapper);
                break;

            case CanonicalKind.Constant:
            {
                long valid = 0;
                for (int row = start; row < start + count; row++)
                {
                    valid += allValid || (own.IsValid(row) && wrapper.IsValid(row)) ? 1 : 0;
                }

                _nulls += count - valid;
                int id = -1;
                if (valid > 0)
                {
                    ReadOnlySpan<byte> value = node.ConstantElement;
                    int slot = Take(SplitBlockBloom.Hash(value, BloomHash.XxHash3), value, valid);
                    id = slot >= 0 ? IdAt(slot) : _zeroId;
                }

                if (!_rowIds.IsEmpty)
                {
                    Span<int> ids = _rowIds;
                    for (int row = start; row < start + count; row++)
                    {
                        ids[row - start] = allValid || (own.IsValid(row) && wrapper.IsValid(row)) ? id : -1;
                    }
                }

                if (!_rowHashes.IsEmpty)
                {
                    Span<ulong> rowHashes = _rowHashes;
                    ulong hash = valid > 0 ? SplitBlockBloom.Hash(node.ConstantElement, BloomHash.XxHash3) : 0;
                    for (int row = start; row < start + count; row++)
                    {
                        rowHashes[row] = allValid || (own.IsValid(row) && wrapper.IsValid(row)) ? hash : 0;
                    }
                }

                break;
            }

            case CanonicalKind.VarBinView:
                Text(node.Views.Span, node.DataBuffers, start, count, allValid, own, wrapper);
                break;

            default:
                // A kind no index keys: the rows are counted, their values not, nor hashed.
                _rowHashesWhole = false;
                return;
        }

        _rowIdsFilled = !_rowIds.IsEmpty && !_slots.IsEmpty;
        Check();
    }

    private const int ZeroSlot = -2;
    private const int Unkept = -1;

    /// <summary>
    /// Whether the sheet kept the bytes of every value it numbered (<see cref="Numbered"/>), which <see cref="ValueAt"/>
    /// then reads where they lie; while <see cref="Counting"/>, once every row is in. The builders that list the values
    /// read them so: copied out, a list for each of three of them, an object's sheets made a copy of every distinct value
    /// three times over.
    /// </summary>
    internal bool KeptEveryValue
    {
        get
        {
            SlotsOf();
            return _keptEvery;
        }
    }

    /// <summary>
    /// A copy of the bytes of each value counted by its number (<see cref="Numbered"/>), null for one whose bytes were not
    /// kept; while <see cref="Counting"/>: for the counts, whose groups each hold their value's.
    /// </summary>
    internal byte[]?[] ValuesByNumber()
    {
        Block<int> slotOf = SlotsOf();
        byte[]?[] values = new byte[]?[_ids];
        for (int id = 0; id < values.Length; id++)
        {
            values[id] = slotOf[id] == Unkept ? null : ValueAt(id).ToArray();
        }

        return values;
    }

    /// <summary>The bytes of value number <paramref name="id"/>, where the sheet keeps them: every value kept (<see cref="KeptEveryValue"/>).</summary>
    internal ReadOnlySpan<byte> ValueAt(int id) => _slotOf[id] is int slot and >= 0
        ? _heap.AsSpan(_offsets[slot], _lengths[slot])
        : _heap.AsSpan(_zeroOffset, _zeroLength);

    /// <summary>Each number's slot, built once for the rows fed so far.</summary>
    private Block<int> SlotsOf()
    {
        if (!_slotOf.IsEmpty && _slotOfRows == _rows)
        {
            return _slotOf;
        }

        if (_slotOf.Length < Math.Max(_ids, 1))
        {
            Blocks.Ensure(ref _slotOf, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(_ids, 1)));
        }

        Block<int> slotOf = _slotOf;
        slotOf.AsSpan(0, _ids).Fill(Unkept);
        Span<Slot> slots = _slots;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Key != 0 && _offsets[i] >= 0)
            {
                slotOf[(int)(slots[i].Count >> IdShift)] = i;
            }
        }

        if (_zeroId >= 0 && _zeroOffset >= 0)
        {
            slotOf[_zeroId] = ZeroSlot;
        }

        _keptEvery = !slotOf.AsSpan(0, _ids).Contains(Unkept);
        _slotOfRows = _rows;
        return slotOf;
    }

    /// <summary>Numbers in the order of their values' bytes, every value kept.</summary>
    internal readonly struct ByValue(ColumnSheetBuilder sheet) : IComparer<int>
    {
        public int Compare(int x, int y) => sheet.ValueAt(x).SequenceCompareTo(sheet.ValueAt(y));
    }

    /// <summary>
    /// The first <paramref name="count"/> values of <paramref name="sheet"/>, every value kept, read where they lie: by
    /// their numbers, or in the numbers' order <paramref name="order"/> gives when it is not empty.
    /// </summary>
    internal readonly struct Values(ColumnSheetBuilder sheet, Block<int> order, int count) : IValuesByNumber
    {
        public int Count => count;

        public ReadOnlySpan<byte> this[int index] => sheet.ValueAt(order.IsEmpty ? index : order[index]);
    }

    /// <summary>The number of the value slot <paramref name="slot"/> holds.</summary>
    private int IdAt(int slot) => (int)(_slots[slot].Count >> IdShift);

    internal override void CloseBlock()
    {
    }

    /// <summary>The sheet's bytes, once every row is in; null for a column of no row.</summary>
    internal byte[]? Finish()
    {
        if (_rows == 0)
        {
            return null;
        }

        long valid = _rows - _nulls;
        bool exact = !_slots.IsEmpty;
        long distinct = exact ? _distinct + (_zeroRows > 0 ? 1 : 0) : 0;
        byte[] ranks = _ranks.AsSpan().ToArray();
        if (!exact)
        {
            distinct = Math.Min(new ColumnSheet(_rows, _nulls, 0, exact: false, ranks, [], null).Estimate(), valid);
        }

        bool repeats = exact && distinct > 0 && distinct * RepeatShare <= valid;
        IReadOnlyList<SheetValue> top = repeats ? Frequent() : [];
        BinaryFuse8? filter = repeats ? BinaryFuse8.Build(Hashes()) : null;
        return new ColumnSheet(_rows, _nulls, distinct, exact, ranks, top, filter).ToBytes();
    }

    /// <summary>
    /// A fixed-width column's rows <c>[start, start + count)</c>, hashed a batch at a time; a row whose hash is the row
    /// before's counted in that row's slot, with no probe.
    /// </summary>
    private void Fixed(ReadOnlySpan<byte> values, int width, int start, int count, bool allValid, ValidityMask own, ValidityMask wrapper)
    {
        Span<ulong> hashes = stackalloc ulong[HashBatch];
        Span<int> ids = _rowIds.IsEmpty ? default : _rowIds.AsSpan(0, count);
        bool numbering = !ids.IsEmpty;
        ulong last = 0;
        int lastSlot = -1;
        int lastId = -1;
        for (int from = start; from < start + count; from += HashBatch)
        {
            int stop = Math.Min(from + HashBatch, start + count);
            XxHash3Fixed.HashRows(values, width, from, stop, hashes);
            if (!_rowHashes.IsEmpty)
            {
                Span<ulong> rowHashes = _rowHashes;
                for (int row = from; row < stop; row++)
                {
                    rowHashes[row] = allValid || (own.IsValid(row) && wrapper.IsValid(row)) ? hashes[row - from] : 0;
                }
            }

            // A table past the second cache: the batch's slots fetched ahead of their probes, whose misses then overlap
            // rather than wait one after the other.
            if (_slots.Length >= PrefetchSlots)
            {
                Prefetch(_slots, hashes[..(stop - from)]);
            }

            if (_slots.IsEmpty && allValid)
            {
                // The counts gone, every hash raises its register and nothing more.
                foreach (ulong hash in hashes[..(stop - from)])
                {
                    Raise(hash);
                }

                continue;
            }

            for (int row = from; row < stop; row++)
            {
                if (!allValid && !(own.IsValid(row) && wrapper.IsValid(row)))
                {
                    _nulls++;
                    if (numbering)
                    {
                        ids[row - start] = -1;
                    }

                    continue;
                }

                ulong hash = hashes[row - from];
                if (hash == last && lastSlot >= 0 && !_slots.IsEmpty)
                {
                    _slots[lastSlot].Count++;
                    if (numbering)
                    {
                        ids[row - start] = lastId;
                    }

                    continue;
                }

                int slot = Take(hash, values.Slice(row * width, width), 1);
                (last, lastSlot) = (hash, slot);
                if (numbering)
                {
                    lastId = slot >= 0 ? IdAt(slot) : _zeroId;
                    ids[row - start] = lastId;
                }
            }
        }
    }

    /// <summary>A text column's rows <c>[start, start + count)</c>, each looked up in the views met last first.</summary>
    private void Text(
        ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> buffers, int start, int count, bool allValid, ValidityMask own, ValidityMask wrapper)
    {
        if (_slots.IsEmpty && allValid)
        {
            // The counts gone, a view of a value met raises nothing new, and any other its value's register.
            Dropped(views, buffers, start, count);
            return;
        }

        Span<ulong> met = MetViews();
        ref byte origin = ref MemoryMarshal.GetReference(views);
        Span<int> ids = _rowIds.IsEmpty ? default : _rowIds.AsSpan(0, count);
        bool numbering = !ids.IsEmpty;
        Span<ulong> rowHashes = _rowHashes;
        bool hashing = !rowHashes.IsEmpty;

        // The longer view taken last in this call, its slot, -1 for none or for a value counted apart, and its hash: a view
        // that names the same bytes of the same batch is the same value, which a shredded key's rows repeat row after row,
        // each viewing the first's bytes (VariantFieldExpr.Output.Repeat), and each was hashed again.
        bool repeats = false;
        ulong lastLow = 0;
        ulong lastHigh = 0;
        int lastAt = -1;
        ulong lastHash = 0;
        for (int row = start; row < start + count; row++)
        {
            if (!allValid && !(own.IsValid(row) && wrapper.IsValid(row)))
            {
                _nulls++;
                if (numbering)
                {
                    ids[row - start] = -1;
                }

                if (hashing)
                {
                    rowHashes[row] = 0;
                }

                continue;
            }

            ulong low = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref origin, row * 16));
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref origin, (row * 16) + 8));

            // A view of twelve bytes or fewer is its value, which an equal view names again; a longer one names its bytes'
            // place, which another copy of them does not share, and is looked up by its hash alone.
            bool inline = (uint)low <= 12;
            int entry = inline ? 3 * (int)(((low ^ high) * 0x9E37_79B9_7F4A_7C15UL) >> (64 - 10)) : -1;
            if (entry >= 0 && met[entry + 2] != 0 && met[entry] == low && met[entry + 1] == high)
            {
                ulong slot = met[entry + 2];
                if (slot > 1)
                {
                    ref Slot held = ref _slots[(int)(slot - 2)];
                    held.Count++;
                    if (numbering)
                    {
                        ids[row - start] = (int)(held.Count >> IdShift);
                    }

                    if (hashing)
                    {
                        rowHashes[row] = held.Key;
                    }
                }
                else if (!_slots.IsEmpty)
                {
                    _zeroRows++;
                    if (numbering)
                    {
                        ids[row - start] = _zeroId;
                    }

                    if (hashing)
                    {
                        rowHashes[row] = 0;
                    }
                }

                continue;
            }

            // The rows a longer view repeats are counted as Take counts a value it holds: its slot's count, or the rows of
            // the value counted apart; past the counts, its register was raised already.
            if (!inline && repeats && low == lastLow && high == lastHigh)
            {
                if (lastAt >= 0)
                {
                    _slots[lastAt].Count++;
                }
                else if (!_slots.IsEmpty)
                {
                    _zeroRows++;
                }

                if (numbering)
                {
                    ids[row - start] = lastAt >= 0 ? IdAt(lastAt) : _zeroId;
                }

                if (hashing)
                {
                    rowHashes[row] = lastHash;
                }

                continue;
            }

            ReadOnlySpan<byte> value = BloomBuilder.ValueAt(views, buffers, row);
            ulong valueHash = SplitBlockBloom.Hash(value, BloomHash.XxHash3);
            if (hashing)
            {
                rowHashes[row] = valueHash;
            }

            int at = Take(valueHash, value, 1);
            if (numbering)
            {
                ids[row - start] = at >= 0 ? IdAt(at) : _zeroId;
            }

            // A value taken may have grown the counts, which moves every slot: the longer view taken last is forgotten.
            if (entry >= 0)
            {
                met[entry] = low;
                met[entry + 1] = high;
                met[entry + 2] = at < 0 ? 1 : (ulong)at + 2;
                repeats = false;
            }
            else
            {
                (repeats, lastLow, lastHigh, lastAt, lastHash) = (true, low, high, at, valueHash);
            }
        }
    }

    /// <summary>A text column's valid rows <c>[start, start + count)</c> once the counts are gone: each value's register raised.</summary>
    private void Dropped(ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> buffers, int start, int count)
    {
        Span<ulong> met = MetViews();
        ref byte origin = ref MemoryMarshal.GetReference(views);
        for (int row = start; row < start + count; row++)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref origin, row * 16));
            if ((uint)low <= 12)
            {
                ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref origin, (row * 16) + 8));
                int entry = 3 * (int)(((low ^ high) * 0x9E37_79B9_7F4A_7C15UL) >> (64 - 10));
                if (met[entry + 2] != 0 && met[entry] == low && met[entry + 1] == high)
                {
                    continue;
                }

                met[entry] = low;
                met[entry + 1] = high;
                met[entry + 2] = 1;
            }

            Raise(SplitBlockBloom.Hash(BloomBuilder.ValueAt(views, buffers, row), BloomHash.XxHash3));
        }
    }

    /// <summary>The views met last, rented zeroed the first time a text column asks.</summary>
    private Span<ulong> MetViews()
    {
        if (_views.IsEmpty)
        {
            _views = Blocks.RentCleared<ulong>(Views * 3);
        }

        return _views;
    }

    /// <summary>
    /// Counts <paramref name="rows"/> rows of the value <paramref name="value"/>, whose hash is <paramref name="hash"/>: its
    /// slot in the counts, or -1 for the hash 0, counted apart, and once the counts are gone.
    /// </summary>
    private int Take(ulong hash, ReadOnlySpan<byte> value, long rows)
    {
        if (_slots.IsEmpty)
        {
            Raise(hash);
            return -1;
        }

        Span<Slot> slots = _slots;

        if (hash == 0)
        {
            if (_zeroRows == 0)
            {
                Raise(hash);
                (_zeroOffset, _zeroLength) = Keep(value);
                _zeroId = _ids++;
            }

            _zeroRows += rows;
            return -1;
        }

        int mask = slots.Length - 1;
        int slot = (int)(KeyHash.Mix(hash) & (ulong)mask);
        while (true)
        {
            ref Slot held = ref slots[slot];
            if (held.Key == hash)
            {
                held.Count += rows;
                return slot;
            }

            if (held.Key == 0)
            {
                break;
            }

            slot = (slot + 1) & mask;
        }

        Raise(hash);
        if (_distinct + 1 + (_zeroRows > 0 ? 1 : 0) > ExactLimit)
        {
            Drop();
            return -1;
        }

        slots[slot] = new Slot { Key = hash, Count = rows | ((long)_ids++ << IdShift) };
        (_offsets[slot], _lengths[slot]) = Keep(value);
        if (++_distinct * 2 > slots.Length)
        {
            Grow();
            return Find(hash);
        }

        return slot;
    }

    /// <summary>Asks the caches for the slot each of <paramref name="hashes"/> probes first.</summary>
    private static unsafe void Prefetch(Block<Slot> table, ReadOnlySpan<ulong> hashes)
    {
        if (!Sse.IsSupported)
        {
            return;
        }

        ulong mask = (ulong)table.Length - 1;
        fixed (Slot* slots = table)
        {
            foreach (ulong hash in hashes)
            {
                Sse.Prefetch0(slots + (KeyHash.Mix(hash) & mask));
            }
        }
    }

    /// <summary>The slot of <paramref name="hash"/>, which the counts hold.</summary>
    private int Find(ulong hash)
    {
        Span<Slot> slots = _slots;
        int mask = slots.Length - 1;
        int slot = (int)(KeyHash.Mix(hash) & (ulong)mask);
        while (slots[slot].Key != hash)
        {
            slot = (slot + 1) & mask;
        }

        return slot;
    }

    /// <summary>Once the valid rows reach the next check, drops the counts of a column that met a new value nine rows in ten.</summary>
    private void Check()
    {
        long valid = _rows - _nulls;
        if (_slots.IsEmpty || valid < _check)
        {
            return;
        }

        _check *= 2;
        if ((_distinct + (_zeroRows > 0 ? 1 : 0)) * 10L > valid * 9)
        {
            Drop();
        }
    }

    /// <summary>Drops the counts, the values and the filter's keys: the registers go on alone, and the views met say only that they were.</summary>
    private void Drop()
    {
        Blocks.Return(_slots);
        Blocks.Return(_offsets);
        Blocks.Return(_lengths);
        Blocks.Return(_heap);
        _slots = default;
        _offsets = default;
        _lengths = default;
        _heap = default;
        Span<ulong> views = _views;
        for (int entry = 2; entry < views.Length; entry += 3)
        {
            views[entry] = views[entry] == 0 ? 0UL : 1UL;
        }
    }

    /// <summary>Raises the register <paramref name="hash"/> falls in.</summary>
    private void Raise(ulong hash)
    {
        ref byte rank = ref _ranks[HyperLogLog.Register(hash, ColumnSheet.Precision)];
        rank = Math.Max(rank, HyperLogLog.Rank(hash, ColumnSheet.Precision));
    }

    /// <summary>Where <paramref name="value"/>'s bytes are kept in the heap, -1 when they do not fit.</summary>
    private (int Offset, int Length) Keep(ReadOnlySpan<byte> value)
    {
        if (_heapUsed + value.Length > ValueBytesLimit)
        {
            return (-1, 0);
        }

        if (_heapUsed + value.Length > _heap.Length)
        {
            Block<byte> heap = _heap.WithLength(_heapUsed);
            Blocks.Grow(ref heap, Math.Min(ValueBytesLimit, Math.Max(_heap.Length * 2, Math.Max(4_096, _heapUsed + value.Length))));
            _heap = heap;
        }

        value.CopyTo(_heap.AsSpan(_heapUsed));
        int at = _heapUsed;
        _heapUsed += value.Length;
        return (at, value.Length);
    }

    /// <summary>Doubles the table, every hash moved to its slot in the larger one, and forgets the views met, whose slots moved.</summary>
    private void Grow()
    {
        Block<Slot> slots = _slots;
        Block<int> offsets = _offsets;
        Block<int> lengths = _lengths;
        int size = slots.Length * 2;
        _slots = Blocks.RentCleared<Slot>(size);
        _offsets = Blocks.Rent<int>(size);
        _lengths = Blocks.Rent<int>(size);
        Span<Slot> grown = _slots;
        Span<int> grownOffsets = _offsets;
        Span<int> grownLengths = _lengths;
        int mask = size - 1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Key == 0)
            {
                continue;
            }

            int slot = (int)(KeyHash.Mix(slots[i].Key) & (ulong)mask);
            while (grown[slot].Key != 0)
            {
                slot = (slot + 1) & mask;
            }

            grown[slot] = slots[i];
            grownOffsets[slot] = offsets[i];
            grownLengths[slot] = lengths[i];
        }

        Blocks.Return(slots);
        Blocks.Return(offsets);
        Blocks.Return(lengths);
        _views.Clear();
    }

    /// <summary>Every distinct hash, the hash 0 included.</summary>
    private ulong[] Hashes()
    {
        ulong[] hashes = new ulong[_distinct + (_zeroRows > 0 ? 1 : 0)];
        int next = 0;
        foreach (Slot slot in _slots)
        {
            if (slot.Key != 0)
            {
                hashes[next++] = slot.Key;
            }
        }

        if (_zeroRows > 0)
        {
            hashes[next] = 0;
        }

        return hashes;
    }

    /// <summary>
    /// The most frequent values whose bytes were kept, the most first, ties by their bytes: the values kept so far in a heap
    /// whose top is the least of them, which a value passes or not by its rows alone, mostly, where every distinct value
    /// was listed and sorted to keep the first 128.
    /// </summary>
    private SheetValue[] Frequent()
    {
        Block<byte> heap = _heap;
        Ranked rank = new Ranked(heap);
        PriorityQueue<(long Rows, int Offset, int Length), (long Rows, int Offset, int Length)> kept =
            new PriorityQueue<(long, int, int), (long, int, int)>(ColumnSheet.TopValues + 1, rank);
        Span<Slot> slots = _slots;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Key != 0 && _offsets[i] >= 0)
            {
                Keep(kept, (slots[i].Count & RowsMask, _offsets[i], _lengths[i]), rank);
            }
        }

        if (_zeroRows > 0 && _zeroOffset >= 0)
        {
            Keep(kept, (_zeroRows, _zeroOffset, _zeroLength), rank);
        }

        // The least kept leave first: the array fills from its end, the most first.
        SheetValue[] top = new SheetValue[kept.Count];
        for (int i = top.Length - 1; i >= 0; i--)
        {
            (long rows, int offset, int length) = kept.Dequeue();
            top[i] = new SheetValue(heap.AsSpan(offset, length).ToArray(), rows, 0);
        }

        return top;

        static void Keep(
            PriorityQueue<(long Rows, int Offset, int Length), (long Rows, int Offset, int Length)> kept,
            (long Rows, int Offset, int Length) value, Ranked rank)
        {
            if (kept.Count < ColumnSheet.TopValues)
            {
                kept.Enqueue(value, value);
            }
            else if (rank.Compare(value, kept.Peek()) > 0)
            {
                kept.DequeueEnqueue(value, value);
            }
        }
    }

    /// <summary>
    /// Ranks two kept values, the less frequent below, and of as many rows the greater bytes below: the order a sheet
    /// lists its frequent values in, reversed, so that a heap's top is the one to leave first.
    /// </summary>
    private sealed class Ranked(Block<byte> heap) : IComparer<(long Rows, int Offset, int Length)>
    {
        public int Compare((long Rows, int Offset, int Length) x, (long Rows, int Offset, int Length) y) =>
            x.Rows != y.Rows
                ? x.Rows.CompareTo(y.Rows)
                : heap.AsSpan(y.Offset, y.Length).SequenceCompareTo(heap.AsSpan(x.Offset, x.Length));
    }
    /// <summary>A distinct hash, and its rows with its value's number above them (<see cref="IdShift"/>), side by side in one entry.</summary>
    private struct Slot
    {
        internal ulong Key;
        internal long Count;
    }
}
