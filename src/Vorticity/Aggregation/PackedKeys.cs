using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>What the statistics say of each column of a key: whether it is sorted, and what bounds an integer.</summary>
/// <param name="Sorted">Whether each column is sorted, in the key's order.</param>
/// <param name="Bounds">The values each integer column holds, when the statistics hold them exactly.</param>
/// <param name="Rows">The rows of the source, which bound what a table of groups by value may span; -1 when unknown.</param>
internal readonly record struct KeyFacts(bool[] Sorted, KeyBounds?[] Bounds, long Rows = -1);

/// <summary>
/// A key of two to four columns as the numbers its parts have in indexes of their own, packed into
/// one word: each column is grouped by the index of a key of one column, with that index's paths by
/// run, by code and by value, its nulls in its null group, and the tuple of the parts' groups is the
/// key. No row is encoded into bytes; a row whose parts are the row before's is a comparison of
/// words.
/// </summary>
/// <remarks>
/// <para>
/// When every part hands its rows as ranges, constant, run-end or sorted, the key's ranges are the
/// parts' cut at the union of their boundaries, one lookup a range.
/// </para>
/// <para>
/// The parts' numbers are dense, from 0 in the order their values are met: while the product of
/// their counts, each rounded up to a power of two, stays under 2^16, a table indexed by the
/// numbers side by side holds the groups in front of the hash, and grows with the parts. Past that,
/// the hash alone.
/// </para>
/// <para>
/// Two numbers of 32 bits make a word of 64, multiplied by an odd number drawn once a process: the
/// product spreads the tuples of small numbers over the whole word, and is undone by the number's
/// inverse. The groups are found by open addressing, a slot from the top bits of the word, in a
/// table of group numbers at most half full; the words are kept once, by group. A word of 128 bits
/// is mixed by the same number before its top bits are read. Drawn once a process, the number cannot
/// be aimed at: no data is built to crowd the slots.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The word: <see cref="ulong"/> for two parts, <see cref="UInt128"/> for three or four.</typeparam>
internal sealed class PackedKeys<TKey> : GroupKeys
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>The bits the table of groups is indexed by at most: 2^16 slots, a quarter of a megabyte.</summary>
    private const int TableBits = 16;

    /// <summary>An odd multiplier drawn once a process: its product spreads the bits of a tuple of small numbers.</summary>
    private static readonly ulong Odd = ((ulong)Random.Shared.NextInt64() << 1) | 1;

    /// <summary>The inverse of <see cref="Odd"/> modulo 2^64, which takes a product back to the pair.</summary>
    private static readonly ulong Inverse = Invert(Odd);

    private readonly ColumnShape[] _shapes;
    private readonly KeyFacts? _facts;
    private readonly GroupKeys[] _parts;
    private readonly int[][] _ids;
    private readonly GroupRanges[] _partRanges;
    private readonly bool[] _ranged;
    private TKey[] _keys = new TKey[16];

    // The groups by open addressing, the slots a power of two at most half full: a slot's tag, zero
    // for none, its top bit set and seven bits of the word below those of its home; and its group
    // number, read only where the tags agree, as RawKeys finds its words.
    private byte[] _tags = new byte[32];
    private int[] _hashed = new int[32];

    // The table of groups, -1 for a tuple not met yet, null once the parts outgrow it: each part's
    // number in its own bits, from bit _shifts[p], _bits[p] of them.
    private int[]? _table;
    private readonly int[] _bits;
    private readonly int[] _shifts;
    private int[] _slots = [];

    // Scratch kept from one call to the next: a part's numbers for an append, and for a keep the
    // numbers the kept groups hold and their new ones.
    private int[] _partIds = [];
    private int[] _renumbered = [];
    private int[] _held = [];

    // The lane's shelf its tables grow from, under the query's memory; null for tables nothing counts.
    private readonly ArrayShelf? _shelf;

    /// <param name="shapes">The key's columns.</param>
    /// <param name="facts">What the statistics say of them.</param>
    /// <param name="shared">The indexes of the columns, shared by the parts of a parallel merge; fresh ones when null.</param>
    /// <param name="shelf">The lane's shelf its tables and its columns' indexes grow from; null for tables nothing counts.</param>
    internal PackedKeys(ColumnShape[] shapes, KeyFacts? facts, GroupKeys[]? shared = null, ArrayShelf? shelf = null)
    {
        _shapes = shapes;
        _facts = facts;
        _shelf = shelf;
        _parts = new GroupKeys[shapes.Length];
        _ids = new int[shapes.Length][];
        _partRanges = new GroupRanges[shapes.Length];
        _ranged = new bool[shapes.Length];
        _bits = new int[shapes.Length];
        _shifts = new int[shapes.Length];
        for (int p = 0; p < shapes.Length; p++)
        {
            _parts[p] = shared?[p] ?? AggregationPlan.Single(shapes[p], facts?.Sorted[p] ?? false, facts?.Bounds[p], shelf: shelf);
            _ids[p] = [];
            _partRanges[p] = new GroupRanges();
            _bits[p] = 2;
        }

        // A part of a merge is merged into, never assigned rows: no table of numbers.
        if (shared is null)
        {
            Rebuild();
        }
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        bool ranged = true;
        for (int p = 0; p < _parts.Length; p++)
        {
            Scratch.Grow(ref _ids[p], rows);
            _partRanges[p].Clear();
            _ranged[p] = _parts[p].Assign(arena, nodes.Slice(p, 1), rows, selection, _ids[p], _partRanges[p]);
            ranged &= _ranged[p];
        }

        // The table covers every number the parts have handed out, before a slot is reckoned.
        Cover();
        if (ranged)
        {
            Cut(selection, ranges);
            return true;
        }

        for (int p = 0; p < _parts.Length; p++)
        {
            if (_ranged[p])
            {
                GroupRanges partRanges = _partRanges[p];
                Span<int> ids = _ids[p].AsSpan(0, rows);
                for (int r = 0; r < partRanges.Count; r++)
                {
                    ids[partRanges.StartAt(r)..partRanges.EndAt(r)].Fill(partRanges.GroupAt(r));
                }
            }
        }

        if (_table is not null)
        {
            TableRows(rows, selection, rowGroups);
        }
        else
        {
            HashedRows(rows, selection, rowGroups);
        }

        return false;
    }

    /// <summary>The parts' ranges cut at the union of their boundaries: a group a piece every part covers and the selection reaches.</summary>
    private void Cut(ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        if (_parts.Length == 2)
        {
            CutPair(selection, ranges);
            return;
        }

        Span<int> at = stackalloc int[_parts.Length];
        Span<int> ids = stackalloc int[_parts.Length];
        at.Clear();
        int lastGroup = -1;
        TKey last = default;
        while (true)
        {
            int start = 0;
            int end = int.MaxValue;
            for (int p = 0; p < _parts.Length; p++)
            {
                GroupRanges partRanges = _partRanges[p];
                if (at[p] >= partRanges.Count)
                {
                    return;
                }

                start = Math.Max(start, partRanges.StartAt(at[p]));
                end = Math.Min(end, partRanges.EndAt(at[p]));
            }

            if (start < end && RowMasks.Count(selection, start, end) > 0)
            {
                for (int p = 0; p < _parts.Length; p++)
                {
                    ids[p] = _partRanges[p].GroupAt(at[p]);
                }

                TKey key = Pack(ids);
                if (lastGroup < 0 || !key.Equals(last))
                {
                    last = key;
                    lastGroup = GroupOf(ids, key);
                }

                ranges.Add(start, end, lastGroup);
            }

            // Past the pieces that end first: every part whose range ends there steps to its next.
            for (int p = 0; p < _parts.Length; p++)
            {
                if (_partRanges[p].EndAt(at[p]) <= end)
                {
                    at[p]++;
                }
            }
        }
    }

    /// <summary><see cref="Cut"/> for two parts, their ranges read as spans.</summary>
    private void CutPair(ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        ReadOnlySpan<int> firstStarts = _partRanges[0].Starts;
        ReadOnlySpan<int> firstEnds = _partRanges[0].Ends;
        ReadOnlySpan<int> firstGroups = _partRanges[0].Groups;
        ReadOnlySpan<int> secondStarts = _partRanges[1].Starts;
        ReadOnlySpan<int> secondEnds = _partRanges[1].Ends;
        ReadOnlySpan<int> secondGroups = _partRanges[1].Groups;
        Span<int> ids = stackalloc int[2];
        int a = 0;
        int b = 0;
        int lastFirst = -1;
        int lastSecond = -1;
        int lastGroup = -1;
        while (a < firstStarts.Length && b < secondStarts.Length)
        {
            int start = Math.Max(firstStarts[a], secondStarts[b]);
            int end = Math.Min(firstEnds[a], secondEnds[b]);
            if (start < end && (selection.IsEmpty || RowMasks.Count(selection, start, end) > 0))
            {
                if (firstGroups[a] != lastFirst || secondGroups[b] != lastSecond)
                {
                    lastFirst = firstGroups[a];
                    lastSecond = secondGroups[b];
                    ids[0] = lastFirst;
                    ids[1] = lastSecond;
                    lastGroup = GroupOf(ids, Pack(ids));
                }

                ranges.Add(start, end, lastGroup);
            }

            int firstEnd = firstEnds[a];
            int secondEnd = secondEnds[b];
            a += firstEnd <= end ? 1 : 0;
            b += secondEnd <= end ? 1 : 0;
        }
    }

    /// <summary>Each selected row's group by its tuple's word, a row whose word is the row before's taking its group.</summary>
    private void HashedRows(int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        TKey last = default;
        int lastGroup = -1;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        if (typeof(TKey) == typeof(ulong) && _parts.Length == 2)
        {
            int[] first = _ids[0];
            int[] second = _ids[1];
            while (cursor.Next(out int row))
            {
                TKey key = Unsafe.BitCast<ulong, TKey>(((uint)first[row] | ((ulong)(uint)second[row] << 32)) * Odd);
                if (lastGroup < 0 || !key.Equals(last))
                {
                    last = key;
                    lastGroup = Lookup(key);
                }

                rowGroups[row] = lastGroup;
            }

            return;
        }

        Span<int> ids = stackalloc int[_parts.Length];
        while (cursor.Next(out int row))
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = _ids[p][row];
            }

            TKey key = Pack(ids);
            if (lastGroup < 0 || !key.Equals(last))
            {
                last = key;
                lastGroup = Lookup(key);
            }

            rowGroups[row] = lastGroup;
        }
    }

    /// <summary>Each selected row's group read from the table, a tuple not met yet looked up in the hash once.</summary>
    private void TableRows(int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        // The slots a part at a time; a row the selection leaves out holds a number of no
        // consequence, never read below.
        Scratch.Grow(ref _slots, rows);
        Span<int> slots = _slots.AsSpan(0, rows);
        _ids[0].AsSpan(0, rows).CopyTo(slots);
        int mask = (1 << TableBits) - 1;
        for (int p = 1; p < _parts.Length; p++)
        {
            ReadOnlySpan<int> ids = _ids[p].AsSpan(0, rows);
            int shift = _shifts[p];
            for (int row = 0; row < rows; row++)
            {
                slots[row] = (slots[row] | (ids[row] << shift)) & mask;
            }
        }

        int[] table = _table!;
        Span<int> tuple = stackalloc int[_parts.Length];
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int slot = slots[row];
            int group = table[slot];
            if (group < 0)
            {
                for (int p = 0; p < _parts.Length; p++)
                {
                    tuple[p] = _ids[p][row];
                }

                group = table[slot] = Lookup(Pack(tuple));
            }

            rowGroups[row] = group;
        }
    }

    /// <summary>The group of a tuple, through the table when there is one.</summary>
    private int GroupOf(ReadOnlySpan<int> ids, TKey key)
    {
        if (_table is not int[] table)
        {
            return Lookup(key);
        }

        int slot = Slot(ids);
        int group = table[slot];
        if (group < 0)
        {
            group = table[slot] = Lookup(key);
        }

        return group;
    }

    /// <summary>The table's slot of a tuple whose numbers the table covers.</summary>
    private int Slot(ReadOnlySpan<int> ids)
    {
        int slot = 0;
        for (int p = 0; p < ids.Length; p++)
        {
            slot |= ids[p] << _shifts[p];
        }

        return slot;
    }

    /// <summary>
    /// Widens the table to every number the parts have handed out, each part's bits rounded up to
    /// a power of two, or drops it for good once they need more than <see cref="TableBits"/>.
    /// </summary>
    private void Cover()
    {
        if (_table is null)
        {
            return;
        }

        bool grown = false;
        int total = 0;
        for (int p = 0; p < _parts.Length; p++)
        {
            int count = _parts[p].Count;
            while (count > 1 << _bits[p])
            {
                _bits[p]++;
                grown = true;
            }

            total += _bits[p];
        }

        if (total > TableBits)
        {
            DropTable();
            return;
        }

        if (grown)
        {
            Rebuild();
        }
    }

    /// <summary>The table of groups let go, its array given back: the parts outgrew it.</summary>
    private void DropTable()
    {
        if (_table is { } held)
        {
            _shelf?.Give(held);
        }

        _table = null;
    }

    /// <summary>The table at the parts' bits, filled with every group's slot.</summary>
    private void Rebuild()
    {
        int shift = 0;
        for (int p = 0; p < _parts.Length; p++)
        {
            _shifts[p] = shift;
            shift += _bits[p];
        }

        if (shift > TableBits)
        {
            DropTable();
            return;
        }

        int[] table = _table is { } held && held.Length == 1 << shift ? held : NewTable(1 << shift);
        table.AsSpan().Fill(-1);
        Span<int> ids = stackalloc int[_parts.Length];
        for (int g = 0; g < Count; g++)
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = Id(_keys[g], p);
            }

            table[SlotAt(ids, shift)] = g;
        }

        if (!ReferenceEquals(table, _table))
        {
            DropTable();
        }

        _table = table;
    }

    /// <summary>A table of groups of <paramref name="length"/> slots, from the shelf when the keys have one.</summary>
    private int[] NewTable(int length) => _shelf is null ? new int[length] : _shelf.Take<int>(length, zeroed: false);

    private int SlotAt(ReadOnlySpan<int> ids, int bits)
    {
        int slot = Slot(ids);
        return slot & ((1 << bits) - 1);
    }

    /// <summary>The word of a tuple of the parts' numbers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey Pack(ReadOnlySpan<int> ids)
    {
        ulong low = (uint)ids[0] | ((ulong)(uint)ids[1] << 32);
        if (typeof(TKey) == typeof(ulong))
        {
            return Unsafe.BitCast<ulong, TKey>(low * Odd);
        }

        ulong high = (uint)ids[2] | (ids.Length > 3 ? (ulong)(uint)ids[3] << 32 : 0);
        return Unsafe.BitCast<UInt128, TKey>(new UInt128(high, low));
    }

    /// <summary>The number part <paramref name="part"/> has in a word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Id(TKey key, int part)
    {
        if (typeof(TKey) == typeof(ulong))
        {
            return (int)(uint)((Unsafe.BitCast<TKey, ulong>(key) * Inverse) >> (32 * part));
        }

        return (int)(uint)(Unsafe.BitCast<TKey, UInt128>(key) >> (32 * part));
    }

    internal override GroupKeys Fresh() => new PackedKeys<TKey>(_shapes, _facts);

    /// <summary>The parts' indexes, the packed keys of the groups, the slots or the table that find them, and the scratch a keep reuses.</summary>
    internal override long Footprint
    {
        get
        {
            long bytes = ((long)_keys.Length * Unsafe.SizeOf<TKey>()) + _tags.Length
                + ((long)(_hashed.Length + (_table?.Length ?? 0) + _slots.Length + _partIds.Length + _renumbered.Length + _held.Length) * sizeof(int));
            foreach (GroupKeys part in _parts)
            {
                bytes += part.Footprint;
            }

            return bytes;
        }
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // Each part keeps the numbers the kept groups hold, numbered again in their order, so that
        // no part holds more values than the open groups do; the words are packed again from them.
        Span<int> ids = stackalloc int[_parts.Length];
        for (int p = 0; p < _parts.Length; p++)
        {
            int count = _parts[p].Count;
            Scratch.Grow(ref _renumbered, count);
            Span<int> renumbered = _renumbered.AsSpan(0, count);
            renumbered.Fill(-1);
            foreach (int group in groups)
            {
                renumbered[Id(_keys[group], p)] = 0;
            }

            Scratch.Grow(ref _held, count);
            int held = 0;
            for (int id = 0; id < count; id++)
            {
                if (renumbered[id] == 0)
                {
                    renumbered[id] = held;
                    _held[held++] = id;
                }
            }

            _parts[p].Keep(_held.AsSpan(0, held));

            // The part's new numbers go into the words now: the next part reads its own from them.
            for (int i = 0; i < groups.Length; i++)
            {
                TKey key = _keys[groups[i]];
                for (int q = 0; q < _parts.Length; q++)
                {
                    ids[q] = q == p ? renumbered[Id(key, q)] : Id(key, q);
                }

                _keys[groups[i]] = Pack(ids);
            }
        }

        for (int i = 0; i < groups.Length; i++)
        {
            _keys[i] = _keys[groups[i]];
        }

        Count = groups.Length;
        Rehash(_hashed.Length);
        if (_table is not null)
        {
            Rebuild();
        }
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        PackedKeys<TKey> into = (PackedKeys<TKey>)target;
        if (ReferenceEquals(into._parts[0], _parts[0]))
        {
            // Both read the same indexes of the columns, rebased for a merge in parts: the words are
            // the same.
            for (int i = 0; i < groups.Length; i++)
            {
                map[i] = into.Lookup(_keys[groups[i]]);
            }

            MergeSeen(target);
            return;
        }

        // The parts' numbers here become their numbers in the target's parts, then each tuple its
        // group there. The target's table learns them as its rows meet them: a tuple new to it had
        // no slot, and its next batch covers its parts' new numbers first.
        int[][] partMaps = new int[_parts.Length][];
        for (int p = 0; p < _parts.Length; p++)
        {
            partMaps[p] = new int[_parts[p].Count];
            _parts[p].MergeInto(into._parts[p], partMaps[p]);
        }

        Span<int> ids = stackalloc int[_parts.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            TKey key = _keys[groups[i]];
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = partMaps[p][Id(key, p)];
            }

            map[i] = into.Lookup(Pack(ids));
        }

        MergeSeen(target);
    }

    /// <summary>By the word's hash, once the partitions are rebased on shared indexes of the columns: the same tuple, the same word everywhere.</summary>
    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            (ulong low, ulong high) = KeyWords.Of(_keys[g]);
            parts[g] = (byte)((Unsafe.SizeOf<TKey>() <= sizeof(ulong) ? MergeHash.Of(low, seed) : MergeHash.Of(low, high, seed)) >> shift);
        }
    }

    internal override GroupKeys ForPart() => new PackedKeys<TKey>(_shapes, _facts, _parts);

    internal override bool Spills => Array.TrueForAll(_parts, part => part.Spills);

    /// <summary>
    /// By the hash of the columns' values, not of the word: a lane's numbers of its columns' values are
    /// its own, and each table it empties numbers them again (<see cref="Hashes"/>).
    /// </summary>
    internal override void Sections(Span<byte> sections)
    {
        ulong[] hashes = new ulong[Count];
        Hashes(hashes);
        for (int g = 0; g < Count; g++)
        {
            sections[g] = (byte)(hashes[g] >> (64 - SpillRun.SectionBits));
        }
    }

    /// <summary>Each column's hash of the group's value, 0 for its null, folded into the next's: the same for one tuple in every lane, whatever its numbers.</summary>
    internal override void Hashes(Span<ulong> hashes)
    {
        ulong[][] parts = new ulong[_parts.Length][];
        for (int p = 0; p < _parts.Length; p++)
        {
            parts[p] = new ulong[_parts[p].Count];
            _parts[p].Hashes(parts[p]);
        }

        for (int g = 0; g < Count; g++)
        {
            ulong hash = parts[0][Id(_keys[g], 0)];
            for (int p = 1; p < _parts.Length; p++)
            {
                hash = MergeHash.Of(hash, parts[p][Id(_keys[g], p)], MergeHash.Seed);
            }

            hashes[g] = hash;
        }
    }

    /// <summary>Each column's values, a column after the other, as its index writes them.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        Scratch.Grow(ref _partIds, groups.Length);
        Span<int> ids = _partIds.AsSpan(0, groups.Length);
        for (int p = 0; p < _parts.Length; p++)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                ids[i] = Id(_keys[groups[i]], p);
            }

            _parts[p].WriteKeys(ids, buffer);
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        int count = groups.Length;
        int[] ids = new int[_parts.Length * count];
        for (int p = 0; p < _parts.Length; p++)
        {
            _parts[p].ReadKeys(ref reader, ids.AsSpan(p * count, count));
        }

        Span<int> tuple = stackalloc int[_parts.Length];
        for (int i = 0; i < count; i++)
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                tuple[p] = ids[(p * count) + i];
            }

            groups[i] = Lookup(Pack(tuple));
        }
    }

    /// <summary>Its columns' indexes hashed too, the statistics' bounds left out: a part holds values from all over each column's span.</summary>
    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new PackedKeys<TKey>(_shapes, facts: null, shelf: shelf);

    /// <summary>The words' array, the slots at most half full, and each column's index, a new value a new group at most.</summary>
    internal override long GrowthFor(int more)
    {
        long bytes = TableGrowth.Of(Count, more, _keys.Length, _keys.Length, Unsafe.SizeOf<TKey>())
            + TableGrowth.Of(Count, more, _tags.Length, _tags.Length / 2, sizeof(byte) + sizeof(int));
        foreach (GroupKeys part in _parts)
        {
            bytes += part.GrowthFor(more);
        }

        return bytes;
    }

    internal override void Reserve(int groups)
    {
        if (_keys.Length < groups)
        {
            Array.Resize(ref _keys, groups);
        }

        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(32, groups * 2));
        if (_hashed.Length < slots)
        {
            Rehash(slots);
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) =>
        _parts[component].CompareKeys(Id(_keys[a], component), Id(((PackedKeys<TKey>)other)._keys[b], component), 0);

    /// <summary>
    /// The indexes of the columns of every partition merged into this one's, a task per column, then
    /// each partition's words rewritten with their numbers there, a task per partition: the indexes
    /// are merged once, not once per part of the merge.
    /// </summary>
    internal override async Task RebaseAsync(GroupKeys[] partitions, CancellationToken cancellationToken)
    {
        int[][][] maps = new int[partitions.Length][][];
        for (int l = 1; l < partitions.Length; l++)
        {
            maps[l] = new int[_parts.Length][];
        }

        Task[] columns = new Task[_parts.Length];
        for (int p = 0; p < _parts.Length; p++)
        {
            int part = p;
            columns[p] = Task.Run(
                () =>
                {
                    for (int l = 1; l < partitions.Length; l++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        GroupKeys from = ((PackedKeys<TKey>)partitions[l])._parts[part];
                        maps[l][part] = new int[from.Count];
                        from.MergeInto(_parts[part], maps[l][part]);
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(columns).ConfigureAwait(false);
        Task[] rewrites = new Task[partitions.Length - 1];
        for (int l = 1; l < partitions.Length; l++)
        {
            PackedKeys<TKey> lane = (PackedKeys<TKey>)partitions[l];
            int[][] laneMaps = maps[l];
            rewrites[l - 1] = Task.Run(() => lane.Rewrite(laneMaps, _parts), cancellationToken);
        }

        await Task.WhenAll(rewrites).ConfigureAwait(false);
    }

    /// <summary>Every word with each part's number taken through <paramref name="maps"/>, and the indexes of the columns those numbers are in.</summary>
    private void Rewrite(int[][] maps, GroupKeys[] parts)
    {
        Span<int> ids = stackalloc int[_parts.Length];
        for (int g = 0; g < Count; g++)
        {
            for (int p = 0; p < _parts.Length; p++)
            {
                ids[p] = maps[p][Id(_keys[g], p)];
            }

            _keys[g] = Pack(ids);
        }

        // Its own slots and table no longer find the words; a rebased index is read, not assigned.
        parts.CopyTo(_parts, 0);
        DropTable();
    }

    internal override int[] Order(bool sorted) => Identity(Count);

    internal override Func<int, T> Reader<T>(int component)
    {
        Func<int, T> part = _parts[component].Reader<T>(0);
        return group => part(Id(_keys[group], component));
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _partIds, groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            _partIds[i] = Id(_keys[groups[i]], component);
        }

        _parts[component].Append(0, store, _partIds.AsSpan(0, groups.Length));
    }

    internal override bool Orders(int component) => _parts[component].Orders(0);

    internal override int CompareKeys(int a, int b, int component) =>
        _parts[component].CompareKeys(Id(_keys[a], component), Id(_keys[b], component), 0);

    /// <summary>The group of a word, added when it is new.</summary>
    private int Lookup(TKey key)
    {
        // The tags first, as RawKeys: a new tuple finds its free slot without reading a group number
        // or a word, where it read the word of each group its chain passed.
        byte[] tags = _tags;
        int mask = tags.Length - 1;
        int shift = Shift(tags.Length);
        ulong hash = Hash(key);
        int slot = (int)(hash >> shift);
        byte tag = Tag(hash, shift);
        while (true)
        {
            byte seen = tags[slot];
            if (seen == 0)
            {
                break;
            }

            if (seen == tag)
            {
                int group = _hashed[slot];
                if (_keys[group].Equals(key))
                {
                    return group;
                }
            }

            slot = (slot + 1) & mask;
        }

        if (Count == _keys.Length)
        {
            ArrayShelf.Resize(_shelf, ref _keys, Doubled(Count));
        }

        int added = Count++;
        _keys[added] = key;
        tags[slot] = tag;
        _hashed[slot] = added;
        if (Count * 2 > tags.Length)
        {
            Rehash(Doubled(tags.Length));
        }

        return added;
    }

    /// <summary>The slots at <paramref name="length"/>, every group placed again from its word.</summary>
    private void Rehash(int length)
    {
        byte[] tags = _tags;
        int[] hashed = _hashed;
        if (tags.Length == length)
        {
            tags.AsSpan().Clear();
        }
        else
        {
            tags = _shelf is null ? new byte[length] : _shelf.Take<byte>(length, zeroed: true);
            hashed = _shelf is null ? GC.AllocateUninitializedArray<int>(length) : _shelf.Take<int>(length, zeroed: false);
            _shelf?.Give(_tags);
            _shelf?.Give(_hashed);
        }

        int mask = length - 1;
        int shift = Shift(length);
        for (int g = 0; g < Count; g++)
        {
            ulong hash = Hash(_keys[g]);
            int slot = (int)(hash >> shift);
            while (tags[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            tags[slot] = Tag(hash, shift);
            hashed[slot] = g;
        }

        _tags = tags;
        _hashed = hashed;
    }

    /// <summary>A word's hash, whose top bits are its home slot: the word itself, mixed already, or a wide one's halves mixed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(TKey key)
    {
        if (typeof(TKey) == typeof(ulong))
        {
            return Unsafe.BitCast<TKey, ulong>(key);
        }

        UInt128 wide = Unsafe.BitCast<TKey, UInt128>(key);
        return (((ulong)wide * Odd) ^ (ulong)(wide >> 64)) * Odd;
    }

    /// <summary>The shift that leaves a hash's home among <paramref name="length"/> slots, a power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Shift(int length) => 64 - BitOperations.Log2((uint)length);

    /// <summary>A slot's tag: its top bit, so that none is zero, over the seven bits of the hash below the home's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Tag(ulong hash, int shift) => (byte)(0x80 | (hash >> (shift - 7)));

    /// <summary>The inverse of an odd number modulo 2^64, by Newton's iteration: each step doubles the bits it holds.</summary>
    private static ulong Invert(ulong odd)
    {
        ulong inverse = odd;
        for (int step = 0; step < 6; step++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }
}
