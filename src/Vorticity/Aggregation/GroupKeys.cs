using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>Row ranges of one batch that each belong to a single group, for the aggregates to fold a range at a time.</summary>
internal sealed class GroupRanges
{
    private int[] _starts = new int[16];
    private int[] _ends = new int[16];
    private int[] _groups = new int[16];

    internal int Count { get; private set; }

    /// <summary>The ranges' first rows, ascending: a range starts where the one before it ends, or past it.</summary>
    internal ReadOnlySpan<int> Starts => _starts.AsSpan(0, Count);

    /// <summary>The rows past the ranges.</summary>
    internal ReadOnlySpan<int> Ends => _ends.AsSpan(0, Count);

    /// <summary>The ranges' groups.</summary>
    internal ReadOnlySpan<int> Groups => _groups.AsSpan(0, Count);

    internal int StartAt(int index) => _starts[index];

    internal int EndAt(int index) => _ends[index];

    internal int GroupAt(int index) => _groups[index];

    internal void Clear() => Count = 0;

    internal void Add(int start, int end, int group)
    {
        if (Count > 0 && _groups[Count - 1] == group && _ends[Count - 1] == start)
        {
            _ends[Count - 1] = end;
            return;
        }

        if (Count == _starts.Length)
        {
            int grown = Count * 2;
            Array.Resize(ref _starts, grown);
            Array.Resize(ref _ends, grown);
            Array.Resize(ref _groups, grown);
        }

        _starts[Count] = start;
        _ends[Count] = end;
        _groups[Count] = group;
        Count++;
    }
}

/// <summary>
/// The groups of one partition of a grouped scan: the distinct keys numbered in the order they are
/// first seen, and how each batch's rows map to them.
/// </summary>
internal abstract class GroupKeys
{
    /// <summary>The longest array a table of groups doubles from: twice as long, an int no longer counts it.</summary>
    private const int MostDoubled = 1 << 30;

    // The key blocks grouped by their runs, by their dictionary's codes, and row by row.
    private long _rangeBlocks;
    private long _dictionaryBlocks;
    private long _hashedBlocks;
    private int[] _codeGroups = [];
    private CanonicalOrigin _codeOrigin;

    internal int Count { get; private protected set; }

    /// <summary>
    /// The length an array of a table of groups, or of a distinct count's pairs, doubles to. Groups and
    /// pairs are numbered by 32-bit integers, and their
    /// tables double: an open table, twice its entries long, holds 2^29 of them at most, a list 2^30.
    /// Past that, the query fails with a typed exception that says so, never an overflow.
    /// </summary>
    /// <exception cref="VortexUnsupportedException">The array cannot double.</exception>
    internal static int Doubled(int length) => length < MostDoubled ? length * 2 : throw TooMany();

    /// <summary>
    /// <paramref name="length"/> doubled, or <paramref name="bound"/> when it lies between them: an array
    /// that would double past the groups foretold stops at them, and doubles again once they are passed.
    /// </summary>
    internal static int DoubledUpTo(int length, int bound)
    {
        int doubled = Doubled(length);
        return bound > length && bound < doubled ? bound : doubled;
    }

    private static VortexUnsupportedException TooMany() => new VortexUnsupportedException(
        "group by of more than 2^29 groups",
        ComponentKind.Feature,
        "A group by numbers its groups, and a distinct count its pairs, by 32-bit integers, and its tables double: an open table holds "
        + "536,870,912 of them at most, a list 1,073,741,824. Group by fewer keys at once, or filter the rows first.");

    /// <summary>Whether every key block was dictionary-encoded, which makes the key source ordered.</summary>
    internal bool OnlyDictionaries => _dictionaryBlocks > 0 && _rangeBlocks == 0 && _hashedBlocks == 0;

    /// <summary>The key blocks the index grouped, by how: by their runs, constant or run-end; by their dictionary's codes; row by row.</summary>
    internal (long ByRange, long ByCode, long Hashed) Blocks => (_rangeBlocks, _dictionaryBlocks, _hashedBlocks);

    /// <summary>
    /// Maps the selected rows of a batch to groups, creating the groups of keys not seen before: as
    /// ranges of one group each when the key's form yields them, a group per row otherwise.
    /// </summary>
    /// <returns>True when <paramref name="ranges"/> holds the answer, false when <paramref name="rowGroups"/> does.</returns>
    internal abstract bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges);

    /// <summary>An empty index of the same kind, for another partition.</summary>
    internal abstract GroupKeys Fresh();

    /// <summary>The bytes the index holds, its tables and its keys at their capacity.</summary>
    internal abstract long Footprint { get; }

    /// <summary>Adds this partition's keys to <paramref name="target"/>; group <c>g</c> here is <c>map[g]</c> there.</summary>
    internal void MergeInto(GroupKeys target, Span<int> map) => MergeInto(target, Numbers.Upto(Count), map);

    /// <summary>
    /// Adds the keys of <paramref name="groups"/> to <paramref name="target"/>; group
    /// <c>groups[i]</c> here is <c>map[i]</c> there: the part of a partition a task of a parallel
    /// merge takes.
    /// </summary>
    internal abstract void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map);

    /// <summary>
    /// The part of a parallel merge each group's key falls in: the top bits, past
    /// <paramref name="shift"/>, of a hash of the key under <paramref name="seed"/>, the same for one
    /// key in every partition of a query whatever its group there; the first part for the null group.
    /// </summary>
    internal abstract void Parts(ulong seed, int shift, Span<byte> parts);

    /// <summary>An empty index a part of a parallel merge is merged into: <see cref="Fresh"/>, or one sharing what the partitions were rebased on.</summary>
    internal virtual GroupKeys ForPart() => Fresh();

    /// <summary>
    /// The span of values the index numbers its groups by, its least value and its width; null when it
    /// hashes them. Partitions that number one span by value merge by value.
    /// </summary>
    internal virtual (long Least, ulong Span)? ValueSpan => null;

    /// <summary>
    /// For an index that numbers its groups by value, before a row is assigned: each value of the span a
    /// group from the start, its number, when the span's groups at <paramref name="groupBytes"/> of
    /// states each fit the private cache (<see cref="FixedKeys{TValue}.NumberWhole"/>); false otherwise.
    /// </summary>
    internal virtual bool NumberWhole(long groupBytes) => false;

    /// <summary>
    /// The groups an index that numbered its span whole keeps once the rows are folded, the values no
    /// row met left out; null when it keeps them all. It numbers values as they first come from then on.
    /// </summary>
    internal virtual int[]? Met() => null;

    /// <summary>
    /// For an index that numbers its groups by value over a span of <paramref name="least"/> values or
    /// more: the bins of <paramref name="map"/>, 4 096 bits over the span, that the selected values of a
    /// batch fall in, the bins the span takes, and the values read; null otherwise. Nothing is grouped.
    /// </summary>
    internal virtual (int Set, int Bins, int Values)? Spread(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, ulong least, Span<ulong> map) => null;

    /// <summary>
    /// The part of a merge by value each group's key falls in: the span cut into 2^<paramref name="partBits"/>
    /// runs of numbers, the first part for the null group and for a value past the span.
    /// </summary>
    internal virtual void PartsByValue(int partBits, Span<byte> parts) => throw new NotSupportedException("Only keys numbered by value are cut by value.");

    /// <summary>An empty index the part <paramref name="part"/> of a merge by value is merged into: numbered by value over its run of the span.</summary>
    internal virtual GroupKeys ForValuePart(int part, int partBits) => ForPart();

    /// <summary>An empty index a sub-table of the core holds its groups in, its arrays taken from and given back to <paramref name="shelf"/>.</summary>
    internal virtual GroupKeys ForTable(ArrayShelf shelf) => ForPart();

    /// <summary>
    /// An empty index of a part of a core that pages its key, its arrays from <paramref name="shelf"/>: the
    /// part's pages side by side, <paramref name="pages"/> in their order, each of 2^<paramref name="bits"/>
    /// values from <paramref name="least"/> plus its number shifted by them, <paramref name="slots"/> each
    /// page's place among its part's.
    /// </summary>
    internal virtual GroupKeys ForPages(long least, int bits, int[] slots, long[] pages, ArrayShelf shelf) =>
        throw new NotSupportedException("Only keys numbered by value are paged.");

    /// <summary>The groups some row met: <see cref="Count"/>, less the values of a span numbered whole that none did yet.</summary>
    internal virtual int MetGroups => Count;

    /// <summary>Gives the index's arrays back to its shelf, if it has one: a sub-table split, whose groups another holds. The index is empty after.</summary>
    internal virtual void Release()
    {
    }

    /// <summary>Makes room for <paramref name="groups"/> groups at once, where the index can: the part of a merge, whose keys the partitions count.</summary>
    internal virtual void Reserve(int groups)
    {
    }

    /// <summary>
    /// Room for a lane whose first rows were all new groups: every row new to the end, or a key whose values
    /// come back later, at most half as many groups as rows. The index, which places every group again as
    /// it grows, takes <paramref name="groups"/>; the keys' arrays half, which double once, up to
    /// <paramref name="groups"/>, when every row stays new: growing an array copies it, in order. Keys whose
    /// index is not apart from their arrays reserve both (<see cref="Reserve"/>).
    /// </summary>
    internal virtual void ReserveAllNew(int groups) => Reserve(groups);

    /// <summary>
    /// The bytes a key takes beside its group's record in an entry of a part's batch, a power of two; 0
    /// for a key that does not travel in batches, whose
    /// query keeps a table on each lane and merges them at the end.
    /// </summary>
    internal virtual int EntryBytes => 0;

    /// <summary>
    /// An index of the same kind that looks no key up: each row's key a group of its own, but a row
    /// whose key is the row before's, a run's or a code's; the null group one. What a lane folds its
    /// rows into when its cache finds too few of its keys, the bypass.
    /// Null for a key that does not travel in batches.
    /// </summary>
    internal virtual GroupKeys? Appending() => null;

    /// <summary>
    /// Copies every group but the null one into an entry of the lane's batch of the part its key falls
    /// in (<see cref="LaneCore.PartOf"/>), the top bits of its hash under <see cref="MergeHash.Seed"/> as
    /// <see cref="Parts"/> cuts, or of its page's in a core that pages it: the group's record, read from
    /// <paramref name="records"/>, then its key.
    /// </summary>
    internal virtual void Scatter(ReadOnlySpan<ulong> records, LaneCore lane) => throw NotEntries();

    /// <summary>Counts every group but the null one by the part <see cref="Scatter"/> copies it to in <paramref name="lane"/>'s core.</summary>
    internal virtual void CountParts(Span<int> counts, LaneCore lane) => throw NotEntries();

    /// <summary>
    /// The groups from <paramref name="from"/> on, but the null one, copied into entries of
    /// <paramref name="shape"/> one after the other, their records read from <paramref name="records"/>,
    /// as many as <paramref name="entries"/> holds.
    /// </summary>
    /// <returns>The group to copy next.</returns>
    internal virtual int CopyEntries(ReadOnlySpan<ulong> records, EntryShape shape, int from, Span<ulong> entries, out int written) => throw NotEntries();

    /// <summary>The sub-table of each entry of <paramref name="batch"/> in its part's directory: the bits of its key's hash from <paramref name="shift"/> up, under <paramref name="mask"/>.</summary>
    internal virtual void TablesOf(PartBatch batch, EntryShape shape, int shift, int mask, Span<int> tables) => throw NotEntries();

    /// <summary>
    /// The group here of the key of each entry of <paramref name="batch"/> that <paramref name="entries"/>
    /// names, added when it is new, new keys numbered in the entries' order; <paramref name="scratch"/>
    /// the caller's, of <see cref="EntryScratch"/> words.
    /// </summary>
    internal virtual void GroupsOf(PartBatch batch, EntryShape shape, ReadOnlySpan<int> entries, Span<int> groups, Span<ulong> scratch) => throw NotEntries();

    /// <summary>The words of scratch <see cref="GroupsOf"/> takes for <paramref name="entries"/> entries.</summary>
    internal static int EntryScratch(int entries, int keyBytes) => entries * (((keyBytes + sizeof(ulong) - 1) / sizeof(ulong)) + 1);

    /// <summary>The hash of the key of entry <paramref name="entry"/> of <paramref name="batch"/> under <see cref="MergeHash.Seed"/>, as <see cref="Parts"/> takes it.</summary>
    internal virtual ulong HashAt(PartBatch batch, EntryShape shape, int entry) => throw NotEntries();

    private static NotSupportedException NotEntries() => new NotSupportedException("These keys do not travel in a part's batches.");

    /// <summary>
    /// Whether the keys go to a lane's spill and come back (<see cref="WriteKeys"/>, <see cref="ReadKeys"/>):
    /// every index a lane folds rows into; not one that appends, nor the parts of a merge read as one.
    /// </summary>
    internal virtual bool Spills => false;

    /// <summary>
    /// The section of a spill's run each group goes to: the top byte of its key's hash under
    /// <see cref="MergeHash.Seed"/>, the same for one key in every lane and every table a lane empties,
    /// as <see cref="Parts"/> cuts by values; the first section for the null group.
    /// </summary>
    internal virtual void Sections(Span<byte> sections) => Parts(MergeHash.Seed, 64 - SpillRun.SectionBits, sections);

    /// <summary>
    /// The hash of each group's key under <see cref="MergeHash.Seed"/>, 0 for the null group: what a key
    /// of several columns takes its sections from, its columns' combined.
    /// </summary>
    internal virtual void Hashes(Span<ulong> hashes) => throw NotSpilled();

    /// <summary>Writes the keys of <paramref name="groups"/>, in order: what <see cref="ReadKeys"/> reads back.</summary>
    internal virtual void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer) => throw NotSpilled();

    /// <summary>Reads as many keys as <paramref name="groups"/> holds, written by <see cref="WriteKeys"/>, each one's group here into it, added when new.</summary>
    internal virtual void ReadKeys(ref SpillReader reader, Span<int> groups) => throw NotSpilled();

    /// <summary>An empty index a part of a spill is read back into: hashed, never by value, its arrays from <paramref name="shelf"/>.</summary>
    internal virtual GroupKeys ForSpill(ArrayShelf? shelf) => Fresh();

    /// <summary>
    /// The bytes the index's arrays would take more were <paramref name="more"/> new groups to come: what a
    /// lane whose table spills asks the budget for before a batch, as many new groups as rows at most
    /// (<see cref="TableGrowth"/>). Twice what it holds when it cannot tell.
    /// </summary>
    internal virtual long GrowthFor(int more) => 2 * Footprint;

    /// <summary>
    /// Room for <paramref name="more"/> new groups without the index doubling, filled tighter than its
    /// speed asks: for a table the budget cannot double, which the retired lanes share
    /// (<see cref="LaneRetirement"/>). Whether it has that room; false for an index that does not fill tighter.
    /// </summary>
    internal virtual bool Squeeze(int more) => false;

    private static NotSupportedException NotSpilled() => new NotSupportedException("These keys do not go to a spill.");

    /// <summary>
    /// Readies the indexes of a parallel merge's partitions, this one the first of them, for their
    /// hashes and their merge in parts: what they share is merged once, a composite's indexes of its
    /// columns, so that a key has the same words in every partition. Nothing for a key of one column.
    /// </summary>
    internal virtual Task RebaseAsync(GroupKeys[] partitions, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Group <paramref name="a"/> here and group <paramref name="b"/> of <paramref name="other"/>, an
    /// index of the same kind and the same merge, by component <paramref name="component"/> of their
    /// keys, as <see cref="CompareKeys(int, int, int)"/> orders them: what orders the parts of a
    /// parallel merge read as one.
    /// </summary>
    /// <exception cref="NotSupportedException">The keys are not ordered here (<see cref="Orders"/>).</exception>
    internal virtual int CompareKeys(GroupKeys other, int a, int b, int component) =>
        throw new NotSupportedException("These keys are ordered by the column of their values.");

    /// <summary>The groups in the order they are delivered: by key, nulls last, when <paramref name="sorted"/>; as first seen otherwise.</summary>
    internal abstract int[] Order(bool sorted);

    /// <summary>Reads component <paramref name="component"/> of a group's key as <typeparamref name="T"/>.</summary>
    internal abstract Func<int, T> Reader<T>(int component);

    /// <summary>
    /// Appends component <paramref name="component"/> of the keys of <paramref name="groups"/>, in
    /// order, to a store of the key column's own type: the values as the index holds them, a null
    /// group as a null.
    /// </summary>
    internal abstract void Append(int component, ColumnStore store, ReadOnlySpan<int> groups);

    /// <summary>
    /// Keeps the keys of <paramref name="groups"/> alone, group <c>groups[i]</c> becoming group
    /// <c>i</c>, the index keeping its size: a top's best groups, or none, a lane's table emptied under its
    /// budget or the core's cache once flushed. <paramref name="groups"/> ascend.
    /// </summary>
    internal abstract void Keep(ReadOnlySpan<int> groups);

    /// <summary>
    /// <see cref="Keep"/> for a stream, the groups it has not closed, whose next batch brings about as many
    /// groups as the one it closed: an index makes room for them now, beside the kept ones, rather than
    /// grow in the middle of that batch.
    /// </summary>
    internal virtual void Carry(ReadOnlySpan<int> groups) => Keep(groups);

    /// <summary>
    /// The rows of <paramref name="selection"/> whose key does not lie past the key of group
    /// <paramref name="frontier"/> in an order on it, into <paramref name="narrowed"/>, or false when
    /// these keys do not tell: the rows a top that holds its best groups need not group. A null lies
    /// past every value, and the frontier's own key is kept.
    /// </summary>
    internal virtual bool Narrow(
        CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int frontier, bool descending, Span<ulong> narrowed) => false;

    /// <summary>
    /// Whether the groups are numbered by the key's value, in a table the statistics bound: such a key
    /// takes the core from what its zones say, before the pass, and a hashed one from what its first
    /// rows show.
    /// </summary>
    internal virtual bool NumberedByValue => false;

    /// <summary>
    /// Whether the keys of groups <paramref name="from"/> to <paramref name="to"/>, as they were met, rise:
    /// a key in the order of the rows, whose every batch is new groups that the core would take no better.
    /// False where the keys cannot tell.
    /// </summary>
    internal virtual bool Ascending(int from, int to) => false;

    /// <summary>
    /// Whether group <paramref name="group"/>'s key, an integer, lies below <paramref name="bound"/>:
    /// the groups a floor the zones give proves final. False for the null group, and for keys these
    /// do not read as integers.
    /// </summary>
    internal virtual bool Below(int group, long bound) => false;

    /// <summary>Sorts <paramref name="groups"/>, the null group not among them, by their keys, compared as <see cref="CompareKeys(GroupKeys, int, int, int)"/> compares them.</summary>
    internal virtual void SortByKey(Span<int> groups) => groups.Sort(new KeyComparer(this));

    /// <summary>Groups of one index by their keys, a call to the index a comparison.</summary>
    private sealed class KeyComparer(GroupKeys keys) : IComparer<int>
    {
        public int Compare(int x, int y) => keys.CompareKeys(keys, x, y, 0);
    }

    /// <summary>The group of the null key, or -1 when there is none: of a key of one column.</summary>
    internal virtual int NullNumber => -1;

    /// <summary>
    /// Whether <see cref="CompareKeys(int, int, int)"/> orders the groups by component
    /// <paramref name="component"/> of their keys as the column of those keys would, so that an order
    /// breaks its ties without the keys built: an integer's, a decimal's, a text's, a boolean's, a
    /// composite's part of one of those; not a float's, whose NaN and zeros the column orders apart
    /// from their values' comparison.
    /// </summary>
    internal virtual bool Orders(int component) => false;

    /// <summary>Two groups by component <paramref name="component"/> of their keys, ascending, the null group last.</summary>
    /// <exception cref="NotSupportedException">The keys are not ordered here (<see cref="Orders"/>).</exception>
    internal virtual int CompareKeys(int a, int b, int component) =>
        throw new NotSupportedException("These keys are ordered by the column of their values.");

    /// <summary>Forgets the groups the codes of the last dictionary were given: they were numbered again.</summary>
    private protected void Renumbered() => _codeOrigin = default;

    private protected void Saw(ColumnEncoding encoding)
    {
        switch (encoding)
        {
            case ColumnEncoding.Dictionary:
                _dictionaryBlocks++;
                break;
            case ColumnEncoding.Constant or ColumnEncoding.RunEnd:
                _rangeBlocks++;
                break;
            default:
                _hashedBlocks++;
                break;
        }
    }

    private protected void MergeSeen(GroupKeys target)
    {
        target._rangeBlocks += _rangeBlocks;
        target._dictionaryBlocks += _dictionaryBlocks;
        target._hashedBlocks += _hashedBlocks;
    }

    /// <summary>
    /// The group of each code of a dictionary whose values are node <paramref name="values"/>, -1 for
    /// a code not met yet: the table of the batch before when both view the same retained values,
    /// since a key keeps its group, and a cleared one otherwise.
    /// </summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="values">The dictionary's values node.</param>
    /// <param name="entries">How many values the dictionary has.</param>
    private protected Span<int> CodeGroups(CanonicalArena arena, int values, int entries)
    {
        CanonicalOrigin origin = arena.OriginOf(values);
        if (!origin.IsKnown || origin != _codeOrigin)
        {
            Scratch.Grow(ref _codeGroups, entries);
            _codeGroups.AsSpan(0, entries).Fill(-1);
            _codeOrigin = origin;
        }

        return _codeGroups.AsSpan(0, entries);
    }

    private protected static int[] Identity(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        return order;
    }
}

/// <summary>The smallest and the largest value of an integer key, as the file statistics hold them exactly.</summary>
internal readonly record struct KeyBounds(long Min, long Max);

/// <summary>
/// The hash a parallel merge cuts the keys by: a mix of 64 bits under <see cref="Seed"/>, the same in
/// every partition, apart from each table's own hash, which a table may draw again alone: a key
/// hashed by its table could fall in two parts. The default hash of an integer is its value, whose
/// high bits cut nothing.
/// </summary>
internal static class MergeHash
{
    /// <summary>
    /// The seed, drawn once a process: a text key is hashed once, by its
    /// table under this seed, and the merge cuts by that hash while the table has no seed of its own;
    /// two runs of a query cut their groups alike.
    /// </summary>
    internal static readonly ulong Seed = ((ulong)Random.Shared.NextInt64() << 1) | 1;

    /// <summary>
    /// A key of one word: one round of the mix, a bijection whose every input bit reaches every bit
    /// of the hash, where the key of two words takes two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of(ulong word, ulong seed) => Mix(word ^ seed);

    /// <summary>A key of two words, every bit of each reaching every bit of the hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of(ulong low, ulong high, ulong seed) => Mix(Mix(low ^ seed) ^ high);

    /// <summary>A key of bytes: XXH3 of 64 bits under <paramref name="seed"/>, the same bits as <see cref="System.IO.Hashing.XxHash3"/>.</summary>
    /// <remarks>
    /// Up to 16 bytes, XXH3's own short paths, inlined here: the library's dispatch and its two calls
    /// took a fifth of a group by of short names. Longer keys go to
    /// the library.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Of(ReadOnlySpan<byte> bytes, ulong seed) =>
        bytes.Length <= 16 ? Short(bytes, seed) : System.IO.Hashing.XxHash3.HashToUInt64(bytes, unchecked((long)seed));

    // XXH3's default secret, the words its paths of 16 bytes and less read, folded two by two: its
    // first two of 32 bits, then its words of 64 bits from byte 8, 24, 40 and 56.
    private const uint OneToThree = 0x396CFEB8U ^ 0xBE4BA423U;
    private const ulong FourToEight = 0x1CAD21F7_2C81017CUL ^ 0xDB979083_E96DD4DEUL;
    private const ulong NineToSixteenLow = 0x1F67B3B7_A4A44072UL ^ 0x78E5C0CC_4EE679CBUL;
    private const ulong NineToSixteenHigh = 0x2172FFCC_7DD05A82UL ^ 0x8E2443F7_744608B8UL;
    private const ulong Empty = 0x4C263A81_E69035E0UL ^ 0xCB00C391_BB52283CUL;

    /// <summary>XXH3 of at most 16 bytes, as its reference writes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Short(ReadOnlySpan<byte> bytes, ulong seed)
    {
        int length = bytes.Length;
        ref byte first = ref MemoryMarshal.GetReference(bytes);
        if (length > 8)
        {
            ulong low = Unsafe.ReadUnaligned<ulong>(ref first) ^ (NineToSixteenLow + seed);
            ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - 8)) ^ (NineToSixteenHigh - seed);
            ulong upper = Math.BigMul(low, high, out ulong lower);
            ulong acc = (ulong)length + BinaryPrimitives.ReverseEndianness(low) + high + (upper ^ lower);
            acc ^= acc >> 37;
            acc *= 0x165667919E3779F9UL;
            return acc ^ (acc >> 32);
        }

        if (length >= 4)
        {
            ulong mixed = seed ^ ((ulong)BinaryPrimitives.ReverseEndianness((uint)seed) << 32);
            ulong word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, length - 4))
                + ((ulong)Unsafe.ReadUnaligned<uint>(ref first) << 32);
            ulong h = word ^ (FourToEight - mixed);
            h ^= BitOperations.RotateLeft(h, 49) ^ BitOperations.RotateLeft(h, 24);
            h *= 0x9FB21C651E98DF25UL;
            h ^= (h >> 35) + (ulong)length;
            h *= 0x9FB21C651E98DF25UL;
            return h ^ (h >> 28);
        }

        if (length > 0)
        {
            uint combined = ((uint)first << 16) | ((uint)Unsafe.Add(ref first, length >> 1) << 24)
                | Unsafe.Add(ref first, length - 1) | ((uint)length << 8);
            return Avalanche(combined ^ (OneToThree + seed));
        }

        return Avalanche(seed ^ Empty);
    }

    /// <summary>XXH64's avalanche, which XXH3's shortest keys end with.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Avalanche(ulong h)
    {
        h ^= h >> 33;
        h *= 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 29;
        h *= 0x165667B19E3779F9UL;
        return h ^ (h >> 32);
    }

    /// <remarks>Inlined: the native compiler left it a call a group where a merge cuts its keys into parts.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51_AFD7_ED55_8CCDUL;
        x ^= x >> 33;
        x *= 0xC4CE_B9FE_1A85_EC53UL;
        return x ^ (x >> 33);
    }
}

/// <summary>
/// A key of one fixed-width column: a table of its own from the storage value to its group
/// (<see cref="KeyTable{TValue}"/>; for a key wider than a word past a few thousand groups,
/// <see cref="WideKeyTable{TValue}"/>, whose slots hold no key), and a group for null. An integer key the statistics bound to a
/// span of <see cref="DirectValues"/> values, or of up to <see cref="DirectPerRow"/> values a row of
/// the source, is numbered by its value less the least instead: a table
/// from the number to its group, in pages allocated as values meet them, so that no row is hashed and
/// the memory follows the pages the values touch.
/// </summary>
internal sealed class FixedKeys<TValue> : GroupKeys
    where TValue : unmanaged, IEquatable<TValue>, IComparable<TValue>
{
    /// <summary>The span of values a table of groups covers whatever the rows: 2^16, a quarter of a megabyte.</summary>
    internal const long DirectValues = 1 << 16;

    /// <summary>The values a row of the source a table of groups may span past <see cref="DirectValues"/>: four, the budget of a numbering by value.</summary>
    internal const long DirectPerRow = 4;

    /// <summary>The values of a page of the table of groups, as a power of two: 4 096, 16 KiB.</summary>
    private const int PageBits = 12;

    private const int PageMask = (1 << PageBits) - 1;

    private readonly ColumnShape _shape;
    private readonly bool _sorted;
    private readonly KeyBounds? _bounds;

    // The index: a table whose slots hold their keys, and for a key wider than a word past CompactFrom
    // groups one whose slots hold a hash and a group (Condense), the other then left empty.
    private KeyTable<TValue> _index = new KeyTable<TValue>();
    private WideKeyTable<TValue> _wide = new WideKeyTable<TValue>();
    private bool _compact;
    private TValue[] _keys = new TValue[16];
    private int _null = -1;
    private TValue[] _values = [];
    private ValuesCache<TValue> _entries;

    // The hashed path in two passes (AggregationPlan.ProbeAhead): each row's home slot, then the rows
    // the first pass left; what its reads ahead found, written so that they stay.
    private readonly int _probeAhead;
    private uint[] _homes = [];
    private int[] _left = [];
    private int _sink;

    // The group of each value by its number, the value less the statistics' smallest, in pages of
    // 2^PageBits numbers allocated as values meet them, -1 for a value not met yet, every page Unmet
    // until then; a value past the bounds, which exact statistics never leave, goes to the index alone.
    // A page lies in a slab of pages at its start (Page): the slab of each page, and where it starts.
    private readonly int[][]? _pages;
    private readonly int[]? _pageStarts;
    private readonly long _directMin;
    private readonly ulong _span;
    private readonly long _rows;

    // The slab the next pages come from, its pages handed out, the pages of the next slab, and the
    // numbers every slab holds.
    private int[]? _slab;
    private int _slabUsed;
    private int _slabPages = 1;
    private long _slabsHeld;

    // Whether the last chunk of rows met mostly values for the first time: the next goes by the lookup alone (DirectTwoPasses).
    private bool _directNew;

    // Numbering the span whole (NumberWhole): the values of the span no row met yet, the group of each
    // value its number; -1 when the keys number values as they first come.
    private int _unmet = -1;

    // A part of a core that pages its key (ForPages): its pages side by side, a value's number its page's
    // place among them then its place in the page. The bits of a page, each page's place among its
    // part's, the core's, and the part's pages in that order; null for a span in one piece.
    private readonly int _mapBits;
    private readonly int[]? _mapSlots;
    private readonly long[]? _mapPages;

    // A paged core's part numbered whole: a bit a number, set once an entry met its value, where a group's
    // number is its group and needs no table; null before NumberWhole, and once the part's unmet values are
    // dropped, when its keys are final.
    private ulong[]? _metBits;

    // Every key a group of its own, no table looked up (Appending); the shelf a sub-table's arrays come from (ForTable).
    private readonly bool _appending;
    private readonly ArrayShelf? _shelf;

    internal FixedKeys(
        ColumnShape shape, bool sorted, KeyBounds? bounds = null, int probeAhead = AggregationPlan.DefaultProbeAhead, long rows = -1, bool appending = false,
        ArrayShelf? shelf = null)
    {
        _shape = shape;
        _sorted = sorted;
        _bounds = bounds;
        _probeAhead = probeAhead;
        _rows = rows;
        _appending = appending;
        _shelf = shelf;
        if (shelf is not null)
        {
            _index = new KeyTable<TValue>(shelf);
            if (Wide)
            {
                _wide = new WideKeyTable<TValue>(shelf);
            }
        }

        if (Integers && bounds is { } known && known.Max >= known.Min)
        {
            // The span counts both ends; a span of every long wraps to none.
            ulong span = (ulong)(known.Max - known.Min) + 1;
            if (span != 0 && (span <= DirectValues || (rows > 0 && span <= (ulong)(DirectPerRow * rows))))
            {
                _pages = new int[(int)((span + PageMask) >> PageBits)][];
                _pages.AsSpan().Fill(Unmet);
                _pageStarts = new int[_pages.Length];
                _directMin = known.Min;
                _span = span;
            }
        }
    }

    /// <summary>
    /// A part of a core that pages its key (<see cref="GroupKeys.ForPages"/>): the part's pages side by
    /// side, numbered by value, from <paramref name="least"/>, 2^<paramref name="bits"/> values a page,
    /// <paramref name="slots"/> each page's place among its part's, <paramref name="pages"/> the part's.
    /// </summary>
    private FixedKeys(ColumnShape shape, long least, int bits, int[] slots, long[] pages, ArrayShelf shelf)
        : this(shape, sorted: false, shelf: shelf)
    {
        ulong span = (ulong)pages.Length << bits;
        _pages = new int[(int)((span + PageMask) >> PageBits)][];
        _pages.AsSpan().Fill(Unmet);
        _pageStarts = new int[_pages.Length];
        _directMin = least;
        _span = span;
        _mapBits = bits;
        _mapSlots = slots;
        _mapPages = pages;
    }

    /// <summary>
    /// The number of <paramref name="integer"/> in a part of a core that pages its key: its page's place
    /// among the part's, then its place in the page; false for a value of another part's page or past the
    /// span, which exact statistics never give.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Mapped(long integer, out ulong number)
    {
        long offset = integer - _directMin;
        long page = offset >> _mapBits;
        number = 0;
        if ((ulong)page >= (ulong)_mapSlots!.Length)
        {
            return false;
        }

        int slot = _mapSlots[page];
        if ((uint)slot >= (uint)_mapPages!.Length || _mapPages[slot] != page)
        {
            return false;
        }

        number = ((ulong)slot << _mapBits) | (ulong)(offset & ((1L << _mapBits) - 1));
        return true;
    }

    /// <summary>The value numbered <paramref name="number"/> in the span, in one piece or in a paged core's part's pages.</summary>
    private long ValueOf(ulong number) =>
        _mapPages is null ? _directMin + (long)number : _directMin + (_mapPages[(int)(number >> _mapBits)] << _mapBits) + (long)(number & ((1UL << _mapBits) - 1));

    /// <summary>
    /// The page of the table of groups every number reads until its own is allocated: no value met, -1
    /// throughout, and never written. A first pass reads it as any other, with no branch on the keys.
    /// </summary>
    private static readonly int[] Unmet = NewUnmet();

    private static int[] NewUnmet()
    {
        int[] page = new int[1 << PageBits];
        page.AsSpan().Fill(-1);
        return page;
    }

    /// <summary>Whether the key is wider than a word, a decimal, a UUID or a short text's word: a constant once compiled.</summary>
    private static bool Wide
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.SizeOf<TValue>() > sizeof(ulong);
    }

    /// <summary>
    /// The groups past which a key wider than a word leaves slots that hold it for slots of its hash and
    /// its group (<see cref="WideKeyTable{TValue}"/>): 4 096, a table of a few hundred kilobytes. Measured on
    /// 2026-10-08 against slots that hold the key: a hundred short texts took ×1.10 the time in slots of a
    /// hash and a group, a second read every row; a million UUIDs took a quarter of the memory (38.5 MB
    /// against 139.7), ≈ ×0.91 the time at one lane and ×0.93 at fourteen once the first pass read slots
    /// and keys in two loops. The value itself is not swept.
    /// </summary>
    private const int CompactFrom = 1 << 12;

    /// <summary>Whether the groups are in the table of a hash and a group: a key wider than a word, past <see cref="CompactFrom"/> groups.</summary>
    private bool Compact
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Wide && _compact;
    }

    /// <summary>The first pass of the index the groups are in (<see cref="KeyTable{TValue}.FindAtHome"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindAtHome(ReadOnlySpan<TValue> keys, Span<int> groups, Span<uint> homes, int ahead, out bool missed) =>
        Compact ? _wide.FindAtHome(keys, groups, homes, ahead, _keys.AsSpan(0, Count), out missed) : _index.FindAtHome(keys, groups, homes, ahead, out missed);

    /// <summary>The group of <paramref name="value"/> in the index the groups are in, <paramref name="next"/> when it is new.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrAdd(TValue value, int next) => Compact ? _wide.GetOrAdd(value, next, _keys) : _index.GetOrAdd(value, next);

    /// <summary>
    /// The groups moved to slots of a hash and a group, past <see cref="CompactFrom"/>: every key placed
    /// again from the groups' keys, room made for as many again, the table that held them given back.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Condense()
    {
        _wide.Reserve(2 * Count);
        for (int g = 0; g < Count; g++)
        {
            if (g != _null)
            {
                _wide.GetOrAdd(_keys[g], g, _keys);
            }
        }

        _index.Release();
        _compact = true;
    }

    /// <summary>The bytes the index grows by for <paramref name="more"/> new groups: the compact table's own past <see cref="CompactFrom"/>.</summary>
    private long IndexGrowthFor(int more) =>
        Compact ? _wide.GrowthFor(more)
        : Wide && Count + (long)more > CompactFrom ? WideKeyTable<TValue>.BytesFor(2 * (Count + more))
        : _index.GrowthFor(more);

    /// <summary>The table of a hash and a group fills to six tenths (<see cref="WideKeyTable{TValue}.Squeeze"/>); slots that hold their keys do not.</summary>
    internal override bool Squeeze(int more) => Compact && _wide.Squeeze(more);

    /// <summary>Whether the values are integers, which a table of groups can be indexed by.</summary>
    private static readonly bool Integers =
        typeof(TValue) == typeof(sbyte) || typeof(TValue) == typeof(short) || typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)
        || typeof(TValue) == typeof(byte) || typeof(TValue) == typeof(ushort) || typeof(TValue) == typeof(uint) || typeof(TValue) == typeof(ulong);

    /// <summary>
    /// Integers within a span of four times as many as there are groups placed at their value less the
    /// least, one key a group, and read back in order; any other keys copied beside the groups and
    /// sorted with them, by the values' own order: no call to an index a comparison.
    /// </summary>
    internal override void SortByKey(Span<int> groups)
    {
        if (Integers && typeof(TValue) != typeof(ulong) && TryPlace(groups))
        {
            return;
        }

        TValue[] keys = System.Buffers.ArrayPool<TValue>.Shared.Rent(groups.Length);
        try
        {
            Span<TValue> values = keys.AsSpan(0, groups.Length);
            for (int i = 0; i < groups.Length; i++)
            {
                values[i] = _keys[groups[i]];
            }

            values.Sort(groups);
        }
        finally
        {
            System.Buffers.ArrayPool<TValue>.Shared.Return(keys);
        }
    }

    /// <summary>The groups placed by their key less the least, when the keys' span is narrow enough to pay; false otherwise.</summary>
    private bool TryPlace(Span<int> groups)
    {
        long least = long.MaxValue;
        long most = long.MinValue;
        foreach (int group in groups)
        {
            long key = AsLong(_keys[group]);
            least = Math.Min(least, key);
            most = Math.Max(most, key);
        }

        if ((ulong)(most - least) >= 4UL * (ulong)groups.Length)
        {
            return false;
        }

        int span = (int)(most - least) + 1;
        int[] places = System.Buffers.ArrayPool<int>.Shared.Rent(span);
        try
        {
            Span<int> at = places.AsSpan(0, span);
            at.Fill(-1);
            foreach (int group in groups)
            {
                at[(int)(AsLong(_keys[group]) - least)] = group;
            }

            int next = 0;
            foreach (int group in at)
            {
                if (group >= 0)
                {
                    groups[next++] = group;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(places);
        }

        return true;
    }

    /// <summary>An integer key of any type but <see cref="ulong"/> as a long, which holds every one of them.</summary>
    private static long AsLong(TValue key) =>
        typeof(TValue) == typeof(long) ? Unsafe.As<TValue, long>(ref key)
        : typeof(TValue) == typeof(int) ? Unsafe.As<TValue, int>(ref key)
        : typeof(TValue) == typeof(short) ? Unsafe.As<TValue, short>(ref key)
        : typeof(TValue) == typeof(sbyte) ? Unsafe.As<TValue, sbyte>(ref key)
        : typeof(TValue) == typeof(uint) ? Unsafe.As<TValue, uint>(ref key)
        : typeof(TValue) == typeof(ushort) ? Unsafe.As<TValue, ushort>(ref key)
        : Unsafe.As<TValue, byte>(ref key);

    internal override bool Below(int group, long bound)
    {
        if (group == _null)
        {
            return false;
        }

        TValue key = _keys[group];
        return typeof(TValue) == typeof(long) ? Unsafe.As<TValue, long>(ref key) < bound
            : typeof(TValue) == typeof(int) ? Unsafe.As<TValue, int>(ref key) < bound
            : typeof(TValue) == typeof(short) ? Unsafe.As<TValue, short>(ref key) < bound
            : typeof(TValue) == typeof(sbyte) ? Unsafe.As<TValue, sbyte>(ref key) < bound
            : typeof(TValue) == typeof(uint) ? Unsafe.As<TValue, uint>(ref key) < bound
            : typeof(TValue) == typeof(ushort) ? Unsafe.As<TValue, ushort>(ref key) < bound
            : typeof(TValue) == typeof(byte) && Unsafe.As<TValue, byte>(ref key) < bound;
    }

    /// <summary>
    /// A column of integers read as it is, whose order is their values': the encoded forms, which
    /// group per run or per code, and the floats, whose NaN and zeros an order places apart from
    /// their values, are left whole.
    /// </summary>
    internal override bool Narrow(
        CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int frontier, bool descending, Span<ulong> narrowed)
    {
        int node = nodes[0];
        StorageKind kind = _shape.Kind;
        if (!Integers || frontier == _null || FixedReader.EncodingOf(arena, node, kind) != ColumnEncoding.Canonical)
        {
            return false;
        }

        ReadOnlySpan<TValue> values = FixedReader.Values(arena, node, kind, ref _values, out ReadOnlySpan<ulong> validity);
        TValue edge = _keys[frontier];
        int words = (rows + 63) >> 6;
        for (int w = 0; w < words; w++)
        {
            ulong word = selection.IsEmpty ? (w < words - 1 || (rows & 63) == 0 ? ulong.MaxValue : (1UL << (rows & 63)) - 1) : selection[w];
            if (!validity.IsEmpty)
            {
                word &= validity[w];
            }

            if (word == ulong.MaxValue)
            {
                narrowed[w] = Reached(values.Slice(w << 6, 64), edge, descending);
                continue;
            }

            ulong kept = 0;
            while (word != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                int order = values[(w << 6) + bit].CompareTo(edge);
                kept |= (descending ? order >= 0 : order <= 0) ? 1UL << bit : 0;
                word &= word - 1;
            }

            narrowed[w] = kept;
        }

        return true;
    }

    /// <summary>
    /// The bits of the 64 <paramref name="values"/> of a word every row of which is kept that reach the
    /// edge, compared with no branch, as <see cref="ValueFrontier"/> compares its values: from the
    /// word's bits a row at a time, each row waited on the one before it.
    /// </summary>
    private static ulong Reached(ReadOnlySpan<TValue> values, TValue edge, bool descending)
    {
        ulong reached = 0;
        if (descending)
        {
            for (int i = 0; i < values.Length; i++)
            {
                reached |= (values[i].CompareTo(edge) >= 0 ? 1UL : 0UL) << i;
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                reached |= (values[i].CompareTo(edge) <= 0 ? 1UL : 0UL) << i;
            }
        }

        return reached;
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        StorageKind kind = _shape.Kind;
        ColumnEncoding encoding = FixedReader.EncodingOf(arena, node, kind);
        Saw(encoding);
        switch (encoding)
        {
            case ColumnEncoding.Constant:
            {
                ValidityKind uniform = arena.RecordRef(node).Validity.Kind;
                if (uniform is ValidityKind.NonNullable or ValidityKind.AllValid or ValidityKind.AllInvalid)
                {
                    ranges.Add(0, rows, uniform == ValidityKind.AllInvalid ? NullGroup() : Lookup(FixedReader.Constant<TValue>(arena, node, kind)));
                    return true;
                }

                break;
            }

            case ColumnEncoding.RunEnd:
            {
                if (ArenaWords.NullCount(arena, node) > 0)
                {
                    break;
                }

                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                ReadOnlySpan<TValue> values = FixedReader.Values(arena, runs, kind, ref _values, out ReadOnlySpan<ulong> valid);
                int runStart = 0;
                for (int r = 0; r < ends.Length && runStart < rows; r++)
                {
                    int runEnd = Math.Min((int)ends[r], rows);
                    if (RowMasks.Count(selection, runStart, runEnd) > 0)
                    {
                        ranges.Add(runStart, runEnd, StorageValues.IsValid(valid, r) ? Lookup(values[r]) : NullGroup());
                    }

                    runStart = runEnd;
                }

                return true;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _entries.Of(arena, 0, entries, kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, node);
                Span<int> codeGroups = CodeGroups(arena, entries, dictionary.Length);
                RowCursor cursor = new RowCursor(selection, 0, rows);
                while (cursor.Next(out int row))
                {
                    if (!StorageValues.IsValid(present, row))
                    {
                        rowGroups[row] = NullGroup();
                        continue;
                    }

                    int code = (int)codes[row];
                    int group = codeGroups[code];
                    if (group < 0)
                    {
                        group = codeGroups[code] = StorageValues.IsValid(valid, code) ? Lookup(dictionary[code]) : NullGroup();
                    }

                    rowGroups[row] = group;
                }

                return false;
            }

            default:
                break;
        }

        ReadOnlySpan<TValue> canonical = FixedReader.Values(arena, node, kind, ref _values, out ReadOnlySpan<ulong> validity);
        return AssignValues(canonical, validity, rows, selection, rowGroups, ranges);
    }

    /// <summary>
    /// <see cref="Assign"/> over a block's values in canonical form, which a caller may have made: a short
    /// text key's words (<see cref="ShortTextKeys"/>).
    /// </summary>
    internal bool AssignValues(ReadOnlySpan<TValue> canonical, ReadOnlySpan<ulong> validity, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        if (_sorted)
        {
            Runs(canonical, validity, rows, selection, ranges);
            return true;
        }

        // A paged core's part is merged into, never assigned rows: its span is not in one piece.
        if (_pages is not null && _mapSlots is null)
        {
            if (_probeAhead >= 0 && selection.IsEmpty)
            {
                DirectTwoPasses(canonical, validity, rows, rowGroups);
            }
            else
            {
                Direct(canonical, validity, rows, selection, rowGroups);
            }

            return false;
        }

        if (_probeAhead >= 0 && selection.IsEmpty && !_appending)
        {
            TwoPasses(canonical[..rows], validity, 0, rowGroups);
            return false;
        }

        RowCursor selected = new RowCursor(selection, 0, rows);
        bool hasLast = false;
        TValue last = default;
        int lastGroup = -1;
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            TValue value = canonical[row];
            if (!hasLast || !value.Equals(last))
            {
                last = value;
                lastGroup = Lookup(value);
                hasLast = true;
            }

            rowGroups[row] = lastGroup;
        }

        return false;
    }

    /// <summary>
    /// The group of each row from <paramref name="start"/> of a block, <paramref name="values"/> theirs,
    /// every row selected, in two passes: the group of each key that sits in its home slot, with no branch
    /// on the keys; then, in their order, the rows that pass left (keys past their home, new keys, nulls)
    /// through the whole lookup, which numbers a new key as it first comes. The rows left are gathered
    /// without a branch either. A block whole, or a short text's words a few thousand rows at a time
    /// (<see cref="ShortTextKeys"/>); never keys in runs, numbered by value or appended.
    /// </summary>
    internal void TwoPasses(ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> validity, int start, int[] rowGroups)
    {
        int rows = values.Length;
        if (_probeAhead > 0)
        {
            Scratch.Grow(ref _homes, rows);
        }

        Span<int> groups = rowGroups.AsSpan(start, rows);
        _sink ^= FindAtHome(values, groups, _homes, _probeAhead, out bool missed);

        // Every row found its group, none null: the usual batch once the keys are known.
        if (!missed && validity.IsEmpty)
        {
            return;
        }

        Scratch.Grow(ref _left, rows);
        int[] left = _left;
        int count = 0;
        if (validity.IsEmpty)
        {
            for (int i = 0; i < rows; i++)
            {
                left[count] = i;
                count += groups[i] >>> 31;
            }
        }
        else
        {
            for (int i = 0; i < rows; i++)
            {
                int row = start + i;
                left[count] = i;
                count += (groups[i] >>> 31) | (int)(~(validity[row >> 6] >> (row & 63)) & 1);
            }
        }

        for (int k = 0; k < count; k++)
        {
            int i = left[k];
            groups[i] = StorageValues.IsValid(validity, start + i) ? Lookup(values[i]) : NullGroup();
        }
    }

    /// <summary>
    /// Each selected row's group read from the table of groups by the value's number, a value not met
    /// yet numbered there; a value past the statistics' bounds, which exact statistics never leave, in
    /// the index alone.
    /// </summary>
    private void Direct(ReadOnlySpan<TValue> canonical, ReadOnlySpan<ulong> validity, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        long min = _directMin;
        ulong span = _span;
        RowCursor selected = new RowCursor(selection, 0, rows);
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            TValue value = canonical[row];
            ulong number = (ulong)(Integer(value) - min);
            rowGroups[row] = number < span ? Numbered(value, number) : Lookup(value);
        }
    }

    /// <summary>The rows <see cref="DirectTwoPasses"/> takes at a time: its rows left on the stack, and the lines its first pass read still in the first level of cache when the second comes.</summary>
    private const int DirectChunk = 4096;

    /// <summary>
    /// Every row's group read from the table of groups in two passes,
    /// <see cref="DirectChunk"/> rows at a time: the group at each row's number, with no branch on the
    /// keys, a page not allocated yet read as <see cref="Unmet"/> and a value past the bounds as none;
    /// then, in their order, the rows the first pass left (values met for the first time, values past
    /// the bounds, nulls) through the whole lookup, which numbers a value as it first comes. At a
    /// million keys in no order, a row in four meets its value for the first time: a branch on it, a row
    /// at a time, missed as often.
    /// </summary>
    /// <remarks>
    /// A chunk whose first pass leaves more than seven rows in eight sends the next through the lookup
    /// alone, a row at a time, until one makes fewer new groups than that: values met for the first
    /// time one after the other, ten million keys in the order of the rows, made the first pass read
    /// every page for nothing, 14 % of the scan.
    /// </remarks>
    private void DirectTwoPasses(ReadOnlySpan<TValue> canonical, ReadOnlySpan<ulong> validity, int rows, int[] rowGroups)
    {
        long min = _directMin;
        ulong span = _span;
        int[][] pages = _pages!;
        int[] starts = _pageStarts!;
        Span<int> left = stackalloc int[DirectChunk];
        for (int start = 0; start < rows; start += DirectChunk)
        {
            int end = Math.Min(rows, start + DirectChunk);
            ReadOnlySpan<TValue> values = canonical[start..end];
            Span<int> groups = rowGroups.AsSpan(start, end - start);
            if (_directNew)
            {
                int before = Count;
                for (int i = 0; i < values.Length; i++)
                {
                    if (!StorageValues.IsValid(validity, start + i))
                    {
                        groups[i] = NullGroup();
                        continue;
                    }

                    TValue value = values[i];
                    ulong number = (ulong)(Integer(value) - min);
                    groups[i] = number < span ? Numbered(value, number) : Lookup(value);
                }

                _directNew = Count - before > values.Length - (values.Length >> 3);
                continue;
            }

            int missed = 0;
            if (_unmet == 0)
            {
                missed = Numbers(values, groups, min, span);
            }
            else if (pages.Length == 1)
            {
                // A span of one page, a few thousand values: its numbers read from the page itself, taken once
                // a chunk, where the page and its start read again at every row made a chain of three loads.
                ref int numbers = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pages[0]), starts[0]);
                for (int i = 0; i < values.Length; i++)
                {
                    ulong number = (ulong)(Integer(values[i]) - min);
                    bool inside = number < span;
                    int group = Unsafe.Add(ref numbers, (nint)(inside ? number : 0));
                    group = inside ? group : -1;
                    groups[i] = group;
                    missed |= group;
                }
            }
            else
            {
                for (int i = 0; i < values.Length; i++)
                {
                    ulong number = (ulong)(Integer(values[i]) - min);
                    bool inside = number < span;
                    ulong at = inside ? number : 0;
                    int page = (int)(at >> PageBits);
                    int group = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pages[page]), starts[page] + ((int)at & PageMask));
                    group = inside ? group : -1;
                    groups[i] = group;
                    missed |= group;
                }
            }

            // Every row found its group, none null: the usual chunk once the values are known.
            if (missed >= 0 && validity.IsEmpty)
            {
                _directNew = false;
                continue;
            }

            // The rows left, gathered without a branch: no group, or a null.
            int count = 0;
            if (validity.IsEmpty)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    left[count] = i;
                    count += groups[i] >>> 31;
                }
            }
            else
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    int row = start + i;
                    left[count] = i;
                    count += (groups[i] >>> 31) | (int)(~(validity[row >> 6] >> (row & 63)) & 1);
                }
            }

            for (int l = 0; l < count; l++)
            {
                int i = left[l];
                if (!StorageValues.IsValid(validity, start + i))
                {
                    groups[i] = NullGroup();
                    continue;
                }

                TValue value = values[i];
                ulong number = (ulong)(Integer(value) - min);
                groups[i] = number < span ? Numbered(value, number) : Lookup(value);
            }

            _directNew = count > values.Length - (values.Length >> 3);
        }
    }

    /// <summary>
    /// The group of a value within the bounds, by its number: added when it is new, its page allocated at
    /// the first value it holds; numbering the span whole, the number itself, the value met.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Numbered(TValue value, ulong number)
    {
        int at = (int)(number >> PageBits);
        int[] slab = _pages![at];
        if (slab == Unmet)
        {
            slab = Page(at);
        }

        ref int group = ref slab[_pageStarts![at] + ((int)number & PageMask)];
        if (group < 0)
        {
            group = _unmet >= 0 ? Meet(number) : Add(value);
        }

        return group;
    }

    /// <summary>A value of the span met for the first time while the keys number it whole: its group is its number.</summary>
    private int Meet(ulong number)
    {
        _unmet--;
        return (int)number;
    }

    /// <summary>
    /// The bytes a span numbered whole may take at most, its groups' states, keys and numbers: 1 MiB, the
    /// private second level of cache of a current core, which <see cref="AggregationPartition"/>'s windows
    /// take as the line past which records leave it.
    /// </summary>
    internal const long WholeBytes = 1 << 20;

    /// <summary>
    /// Numbers the span whole before a row is assigned, when its groups, at
    /// <paramref name="groupBytes"/> of states each, fit <see cref="WholeBytes"/>: each value of the span
    /// is a group from the start, its number, whether a row holds it or not, a null's group and the
    /// values past the bounds after them. The pages tell the values met from the others; once every one
    /// is met, a row's group is its number, read with no page (<see cref="Numbers"/>). The groups no row
    /// met are dropped once the rows are folded (<see cref="Met"/>).
    /// </summary>
    /// <returns>Whether the keys number the span whole.</returns>
    internal override bool NumberWhole(long groupBytes)
    {
        // A paged core's part holds its pages whatever their bytes: the span's density chose them.
        if (_pages is null || _sorted || _appending || Count != 0
            || (_mapSlots is null && (long)_span * (groupBytes + Unsafe.SizeOf<TValue>() + sizeof(int)) > WholeBytes))
        {
            return false;
        }

        int span = (int)_span;
        if (_keys.Length < span)
        {
            Grow(span);
        }

        for (int number = 0; number < span; number++)
        {
            _keys[number] = FromInteger(ValueOf((ulong)number));
        }

        if (_mapSlots is not null)
        {
            int words = (span + 63) >> 6;
            _metBits = _shelf?.Take<ulong>(words, zeroed: true) ?? new ulong[words];
        }

        Count = span;
        _unmet = span;
        return true;
    }

    /// <summary>
    /// The groups a span numbered whole keeps once its rows are folded: the values a row met, in their
    /// order, then a null's group and the values past the bounds; null when every value was met, or the
    /// span is not numbered whole. The keys number values as they first come from then on.
    /// </summary>
    internal override int[]? Met()
    {
        int unmet = _unmet;
        _unmet = -1;
        if (unmet <= 0)
        {
            return null;
        }

        // The groups are still the span's numbers in order, which the keep that follows reads once (KeepNumbered).
        _numbered = true;

        int[] met = new int[Count - unmet];
        int kept = 0;
        int span = (int)_span;
        if (_metBits is { } bits)
        {
            // A paged core's part: its bits, a word at a time.
            for (int w = 0; w < bits.Length; w++)
            {
                for (ulong word = bits[w]; word != 0; word &= word - 1)
                {
                    met[kept++] = (w << 6) + BitOperations.TrailingZeroCount(word);
                }
            }
        }
        else
        {
            for (int at = 0; at < _pages!.Length; at++)
            {
                int[] slab = _pages[at];
                if (slab == Unmet)
                {
                    continue;
                }

                int start = _pageStarts![at];
                int first = at << PageBits;
                int numbers = Math.Min(1 << PageBits, span - first);
                for (int n = 0; n < numbers; n++)
                {
                    if (slab[start + n] >= 0)
                    {
                        met[kept++] = first + n;
                    }
                }
            }
        }

        for (int group = span; group < Count; group++)
        {
            met[kept++] = group;
        }

        return met;
    }

    /// <summary>The first pass of a chunk once every value of a span numbered whole is met: a row's group is its number, -1 past the bounds.</summary>
    /// <returns>The groups or-ed together: negative when a row is left for the lookup.</returns>
    private static int Numbers(ReadOnlySpan<TValue> values, Span<int> groups, long min, ulong span)
    {
        int left = 0;
        int i = 0;
        ref int group = ref MemoryMarshal.GetReference(groups);
        if (Vector.IsHardwareAccelerated && Unsafe.SizeOf<TValue>() == sizeof(int) && values.Length >= Vector<int>.Count)
        {
            // Four bytes, an int or a uint: the number less the least wraps as the value does, and the span
            // never does, so that one comparison of the difference as unsigned tells a value within it.
            ref int value = ref Unsafe.As<TValue, int>(ref MemoryMarshal.GetReference(values));
            Vector<int> least = new Vector<int>((int)min);
            Vector<uint> width = new Vector<uint>((uint)span);
            Vector<int> lefts = Vector<int>.Zero;
            for (; i <= values.Length - Vector<int>.Count; i += Vector<int>.Count)
            {
                Vector<int> number = Vector.LoadUnsafe(ref value, (nuint)i) - least;
                Vector<int> numbered = number | Vector.AsVectorInt32(Vector.GreaterThanOrEqual(Vector.AsVectorUInt32(number), width));
                numbered.StoreUnsafe(ref group, (nuint)i);
                lefts |= numbered;
            }

            left = Vector.LessThanAny(lefts, Vector<int>.Zero) ? -1 : 0;
        }

        for (; i < values.Length; i++)
        {
            ulong number = (ulong)(Integer(values[i]) - min);
            int numbered = number < span ? (int)number : -1;
            Unsafe.Add(ref group, i) = numbered;
            left |= numbered;
        }

        return left;
    }

    /// <summary>The value of <paramref name="integer"/> as a key, the inverse of <see cref="Integer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue FromInteger(long integer) =>
        typeof(TValue) == typeof(sbyte) ? Unsafe.BitCast<sbyte, TValue>((sbyte)integer)
        : typeof(TValue) == typeof(short) ? Unsafe.BitCast<short, TValue>((short)integer)
        : typeof(TValue) == typeof(int) ? Unsafe.BitCast<int, TValue>((int)integer)
        : typeof(TValue) == typeof(long) ? Unsafe.BitCast<long, TValue>(integer)
        : typeof(TValue) == typeof(byte) ? Unsafe.BitCast<byte, TValue>((byte)integer)
        : typeof(TValue) == typeof(ushort) ? Unsafe.BitCast<ushort, TValue>((ushort)integer)
        : typeof(TValue) == typeof(uint) ? Unsafe.BitCast<uint, TValue>((uint)integer)
        : Unsafe.BitCast<ulong, TValue>((ulong)integer);

    /// <summary>The most pages a slab holds: 256 KiB.</summary>
    private const int MostSlabPages = 16;

    /// <summary>
    /// The page of the table of groups at <paramref name="at"/>, allocated, every number in it not met
    /// yet; the slab it lies in. Pages come from slabs of one page, then two, four, up to sixteen: a
    /// table whose values are many takes its pages sixteen at a time, one that meets a few keeps them
    /// a page each. Measured on the Mac on 2026-10-07, at fourteen lanes, a million keys in no order
    /// made 3 400 pages of 16 KiB, each through the lock the runtime allocates by: 14 % of the cycles,
    /// most of them waiting on it.
    /// </summary>
    /// <returns>The slab the page lies in, where <see cref="_pageStarts"/> says.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int[] Page(int at)
    {
        if (_slab is null || _slabUsed == _slab.Length >> PageBits)
        {
            int length = _slabPages << PageBits;
            _slab = _shelf is null ? GC.AllocateUninitializedArray<int>(length) : _shelf.Take<int>(length, zeroed: false);
            _slabUsed = 0;
            _slabsHeld += length;
            _slabPages = Math.Min(MostSlabPages, _slabPages * 2);
        }

        int start = _slabUsed++ << PageBits;
        _slab.AsSpan(start, 1 << PageBits).Fill(-1);
        _pageStarts![at] = start;
        return _pages![at] = _slab;
    }

    internal override (int Set, int Bins, int Values)? Spread(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, ulong least, Span<ulong> map)
    {
        if (_pages is null || _span < least)
        {
            return null;
        }

        // A bin a power of two of values, so that 4 096 of them or fewer cover the span.
        int shift = Math.Max(0, 64 - System.Numerics.BitOperations.LeadingZeroCount(_span - 1) - 12);
        int bins = (int)((_span - 1) >> shift) + 1;
        ReadOnlySpan<TValue> values = FixedReader.Values(arena, nodes[0], _shape.Kind, ref _values, out ReadOnlySpan<ulong> validity);
        long min = _directMin;
        ulong span = _span;
        int read = 0;
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            if (StorageValues.IsValid(validity, row))
            {
                ulong number = (ulong)(Integer(values[row]) - min);
                if (number < span)
                {
                    int bin = (int)(number >> shift);
                    map[bin >> 6] |= 1UL << (bin & 63);
                    read++;
                }
            }
        }

        int set = 0;
        foreach (ulong word in map[..((bins + 63) >> 6)])
        {
            set += System.Numerics.BitOperations.PopCount(word);
        }

        return (set, bins, read);
    }

    /// <summary>An integer value as a long; an unsigned one past the longs as a negative, which no table holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Integer(TValue value) =>
        typeof(TValue) == typeof(sbyte) ? Unsafe.BitCast<TValue, sbyte>(value)
        : typeof(TValue) == typeof(short) ? Unsafe.BitCast<TValue, short>(value)
        : typeof(TValue) == typeof(int) ? Unsafe.BitCast<TValue, int>(value)
        : typeof(TValue) == typeof(long) ? Unsafe.BitCast<TValue, long>(value)
        : typeof(TValue) == typeof(byte) ? Unsafe.BitCast<TValue, byte>(value)
        : typeof(TValue) == typeof(ushort) ? Unsafe.BitCast<TValue, ushort>(value)
        : typeof(TValue) == typeof(uint) ? Unsafe.BitCast<TValue, uint>(value)
        : (long)Unsafe.BitCast<TValue, ulong>(value);

    /// <summary>The number <paramref name="value"/>'s group is held at in the table of groups; false past its bounds, or with no such table.</summary>
    private bool DirectSlot(TValue value, out ulong number)
    {
        // Only an integer key has the table: no other value is read as a long.
        if (_pages is null)
        {
            number = 0;
            return false;
        }

        if (_mapSlots is not null)
        {
            return Mapped(Integer(value), out number);
        }

        number = (ulong)(Integer(value) - _directMin);
        return number < _span;
    }

    internal override GroupKeys Fresh() => new FixedKeys<TValue>(_shape, _sorted, _bounds, _probeAhead, _rows);

    /// <summary>Whether the groups are numbered by value, in the pages of a table of groups, rather than hashed.</summary>
    internal bool ByValue => _pages is not null;

    internal override bool NumberedByValue => ByValue;

    /// <summary>The index, the keys of the groups, the pages of the table of groups, the values a batch reads and its homes and rows left.</summary>
    internal override long Footprint =>
        _index.Footprint + _wide.Footprint + ((long)(_keys.Length + _values.Length) * Unsafe.SizeOf<TValue>())
        + (_slabsHeld * sizeof(int)) + ((long)(_pages?.Length ?? 0) * (IntPtr.Size + sizeof(int)))
        + ((long)(_homes.Length + _left.Length) * sizeof(int)) + ((long)(_metBits?.Length ?? 0) * sizeof(ulong));

    /// <summary>A part of a merge is merged into, never assigned rows: no table of groups.</summary>
    internal override GroupKeys ForPart() => new FixedKeys<TValue>(_shape, _sorted);

    internal override (long Least, ulong Span)? ValueSpan => _pages is null || _mapSlots is not null ? null : (_directMin, _span);

    /// <summary>The bits of a number below its part's in a merge by value: the span cut into 2^<paramref name="partBits"/> runs of a power of two each.</summary>
    private int ValuePartShift(int partBits) => Math.Max(0, 64 - System.Numerics.BitOperations.LeadingZeroCount(_span - 1) - partBits);

    internal override void PartsByValue(int partBits, Span<byte> parts)
    {
        int shift = ValuePartShift(partBits);
        for (int g = 0; g < Count; g++)
        {
            ulong number = g == _null ? 0 : (ulong)(Integer(_keys[g]) - _directMin);
            parts[g] = (byte)(number < _span ? number >> shift : 0);
        }
    }

    /// <summary>
    /// A part of a merge by value numbers its run of the span whatever its entries: the lanes numbered
    /// all of it, and its pages come as its values meet them.
    /// </summary>
    internal override GroupKeys ForValuePart(int part, int partBits)
    {
        int shift = ValuePartShift(partBits);
        long least = _directMin + ((long)part << shift);
        long most = Math.Min(_directMin + (long)(_span - 1), least + ((1L << shift) - 1));
        return new FixedKeys<TValue>(_shape, _sorted, new KeyBounds(least, most), _probeAhead, rows: 1L << shift);
    }

    internal override void Reserve(int groups)
    {
        ReserveIndex(groups);
        if (_keys.Length < groups)
        {
            Grow(groups);
        }
    }

    internal override void ReserveAllNew(int groups)
    {
        _doublesUpTo = groups;
        ReserveIndex(groups);
        if (_keys.Length < groups / 2)
        {
            Grow(groups / 2);
        }
    }

    /// <summary>The index's room for <paramref name="groups"/> groups, the slots of a hash and a group past a few thousand of a wide key.</summary>
    private void ReserveIndex(int groups)
    {
        // Keys numbered by value take the index only past the bounds, which exact statistics never leave.
        if (_pages is null)
        {
            if (Wide && !_compact && groups > CompactFrom)
            {
                Condense();
            }

            if (Compact)
            {
                _wide.Reserve(groups);
            }
            else
            {
                _index.Reserve(groups);
            }
        }
    }

    // The length the keys' array doubles to at most, once a lane reserved the room of first rows all new
    // (ReserveAllNew): 10⁶ names at one lane, each once then all again, foretold as 2M, held 2.25M keys.
    private int _doublesUpTo = int.MaxValue;

    /// <summary>The keys' array grown to <paramref name="length"/>: from the shelf of a sub-table, the old one given back.</summary>
    private void Grow(int length)
    {
        TValue[] grown = _shelf is null ? new TValue[length] : _shelf.Take<TValue>(length, zeroed: false);
        _keys.AsSpan(0, Count).CopyTo(grown);
        _shelf?.Give(_keys);
        _keys = grown;
    }

    /// <summary>A sub-table of the core: the keys hashed, never by value, their arrays from the query's shelf.</summary>
    internal override GroupKeys ForTable(ArrayShelf shelf) => new FixedKeys<TValue>(_shape, sorted: false, shelf: shelf);

    internal override void Release()
    {
        _index.Release();
        if (Wide)
        {
            _wide.Release();
            _compact = false;
        }

        _shelf?.Give(_keys);
        _keys = [];
        Count = 0;
        _null = -1;
        _unmet = -1;
        if (_metBits is not null)
        {
            _shelf?.Give(_metBits);
            _metBits = null;
        }
    }

    internal override int NullNumber => _null;

    /// <summary>A float orders its NaN last and its zeros together, which its comparison does not.</summary>
    private static readonly bool Floats = typeof(TValue) == typeof(double) || typeof(TValue) == typeof(float) || typeof(TValue) == typeof(Half);

    internal override bool Orders(int component) => !Floats;

    internal override int CompareKeys(int a, int b, int component)
    {
        if (a == _null || b == _null)
        {
            return a == b ? 0 : a == _null ? 1 : -1;
        }

        return _keys[a].CompareTo(_keys[b]);
    }

    internal override void Keep(ReadOnlySpan<int> groups) => Keep(groups, room: 0);

    // Room for as many groups as the batch closed brought, beside those kept, and an eighth: a batch a few
    // groups fuller than the last grew the table in its middle, past the stream's first read. The same room
    // for every keep doubled a lane's table as the lane emptied it under its budget, and each cache of the
    // core's at its first flush.
    internal override void Carry(ReadOnlySpan<int> groups) => Keep(groups, Count + groups.Length + (Count / 8));

    private void Keep(ReadOnlySpan<int> groups, int room)
    {
        if (_numbered)
        {
            KeepNumbered(groups, room);
            return;
        }

        // The table of groups forgets every group's number, then learns the kept ones' new ones; a
        // value it holds is in no index. A span numbered whole is numbered as values come from then on.
        _unmet = -1;
        for (int g = 0; g < Count; g++)
        {
            if (g == _null || !DirectSlot(_keys[g], out ulong number))
            {
                continue;
            }

            // A value of a span numbered whole that no row met may lie in a page never allocated.
            int page = (int)(number >> PageBits);
            if (_pages![page] != Unmet)
            {
                _pages[page][_pageStarts![page] + ((int)number & PageMask)] = -1;
            }
        }

        int nullGroup = -1;
        for (int i = 0; i < groups.Length; i++)
        {
            nullGroup = groups[i] == _null ? i : nullGroup;
            _keys[i] = _keys[groups[i]];
        }

        if (Compact)
        {
            _wide.Clear(room);
        }
        else
        {
            _index.Clear(room);
        }

        for (int i = 0; i < groups.Length; i++)
        {
            if (i == nullGroup)
            {
                continue;
            }

            if (DirectSlot(_keys[i], out ulong number))
            {
                int page = (int)(number >> PageBits);
                _pages![page][_pageStarts![page] + ((int)number & PageMask)] = i;
            }
            else
            {
                GetOrAdd(_keys[i], i);
            }
        }

        _null = nullGroup;
        Count = groups.Length;
        Renumbered();
    }

    // Whether the groups are the numbers of a span numbered whole, in order, the groups past it after them,
    // and Met just listed those some row met: the keep that follows reads the table of groups once.
    private bool _numbered;

    /// <summary>
    /// The keep that follows <see cref="Met"/>: the groups kept, the numbers met in order then the groups
    /// past the span. A met number's entry in the table of groups is its rank among them, read in order,
    /// with no lookup; the keys move down, and those past the span are found again in the index.
    /// </summary>
    private void KeepNumbered(ReadOnlySpan<int> groups, int room)
    {
        _numbered = false;
        int span = (int)_span;
        int rank = 0;
        if (_metBits is not null)
        {
            // A paged core's part: no table of groups to follow the keep, its keys final from now on.
            while (rank < groups.Length && groups[rank] < span)
            {
                rank++;
            }

            _shelf?.Give(_metBits);
            _metBits = null;
        }

        for (int at = 0; _mapSlots is null && at < _pages!.Length; at++)
        {
            int[] slab = _pages[at];
            if (slab == Unmet)
            {
                continue;
            }

            int first = at << PageBits;
            Span<int> numbers = slab.AsSpan(_pageStarts![at], Math.Min(1 << PageBits, span - first));
            for (int n = 0; n < numbers.Length; n++)
            {
                if (numbers[n] >= 0)
                {
                    numbers[n] = rank++;
                }
            }
        }

        for (int i = 0; i < groups.Length; i++)
        {
            _keys[i] = _keys[groups[i]];
        }

        if (Compact)
        {
            _wide.Clear(room);
        }
        else
        {
            _index.Clear(room);
        }

        int nullGroup = -1;
        for (int i = rank; i < groups.Length; i++)
        {
            if (groups[i] == _null)
            {
                nullGroup = i;
            }
            else
            {
                GetOrAdd(_keys[i], i);
            }
        }

        _null = nullGroup;
        Count = groups.Length;
        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        FixedKeys<TValue> into = (FixedKeys<TValue>)target;
        for (int i = 0; i < groups.Length; i++)
        {
            int g = groups[i];
            map[i] = g == _null ? into.NullGroup() : into.Lookup(_keys[g]);
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = g == _null ? (byte)0 : (byte)(EntryKeys.Hash(_keys[g], seed) >> shift);
        }
    }

    internal override int EntryBytes => Unsafe.SizeOf<TValue>();

    internal override bool Spills => !_appending;

    internal override void Hashes(Span<ulong> hashes)
    {
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = g == _null ? 0 : EntryKeys.Hash(_keys[g], MergeHash.Seed);
        }
    }

    /// <summary>The null group's place among <paramref name="groups"/>, -1 for none, then every key, the null group's as the default value.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        buffer.Write(_null < 0 ? -1 : groups.IndexOf(_null));
        Span<byte> keys = buffer.Take(groups.Length * Unsafe.SizeOf<TValue>());
        for (int i = 0; i < groups.Length; i++)
        {
            MemoryMarshal.Write(keys[(i * Unsafe.SizeOf<TValue>())..], in _keys[groups[i]]);
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        int nullAt = reader.Read<int>();
        for (int i = 0; i < groups.Length; i++)
        {
            TValue key = reader.Read<TValue>();
            groups[i] = i == nullAt ? NullGroup() : Lookup(key);
        }
    }

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new FixedKeys<TValue>(_shape, sorted: false, probeAhead: _probeAhead, shelf: shelf);

    /// <summary>The keys' array, and the index, or numbered by value the pages of the span still to come, a page a new group at most.</summary>
    internal override long GrowthFor(int more)
    {
        long bytes = TableGrowth.Of(Count, more, _keys.Length, _keys.Length, Unsafe.SizeOf<TValue>());
        if (_pages is null)
        {
            return bytes + IndexGrowthFor(more);
        }

        long left = Math.Max(0, ((long)_pages.Length << PageBits) - _slabsHeld) >> PageBits;
        return bytes + (Math.Min(more, left) << PageBits) * sizeof(int);
    }

    internal override GroupKeys? Appending() => new FixedKeys<TValue>(_shape, sorted: false, appending: true);

    internal override void Scatter(ReadOnlySpan<ulong> records, LaneCore lane) => EntryKeys.Scatter<TValue>(_keys.AsSpan(0, Count), _null, records, lane);

    internal override void CountParts(Span<int> counts, LaneCore lane) => EntryKeys.CountParts<TValue>(_keys.AsSpan(0, Count), _null, counts, lane);

    internal override GroupKeys ForPages(long least, int bits, int[] slots, long[] pages, ArrayShelf shelf) =>
        new FixedKeys<TValue>(_shape, least, bits, slots, pages, shelf);

    internal override int MetGroups => _unmet >= 0 ? Count - _unmet : Count;

    internal override int CopyEntries(ReadOnlySpan<ulong> records, EntryShape shape, int from, Span<ulong> entries, out int written) =>
        EntryKeys.Copy<TValue>(_keys.AsSpan(0, Count), _null, records, shape, from, entries, out written);

    internal override void TablesOf(PartBatch batch, EntryShape shape, int shift, int mask, Span<int> tables) =>
        EntryKeys.TablesOf<TValue>(batch, shape, shift, mask, tables);

    internal override ulong HashAt(PartBatch batch, EntryShape shape, int entry) => EntryKeys.Hash(EntryKeys.KeyAt<TValue>(batch, shape, entry), MergeHash.Seed);

    /// <summary>
    /// The keys gathered first, then found in two passes as a batch's rows are (<see cref="TwoPasses"/>):
    /// each key in its home slot with no branch on the keys, then the rest in their order, which
    /// numbers a new key as it first comes.
    /// </summary>
    internal override void GroupsOf(PartBatch batch, EntryShape shape, ReadOnlySpan<int> entries, Span<int> groups, Span<ulong> scratch)
    {
        int count = entries.Length;
        if (_unmet >= 0 && _mapSlots is { } slots)
        {
            PagedGroupsOf(batch, shape, entries, groups, slots);
            return;
        }

        if (_pages is not null || _appending)
        {
            for (int i = 0; i < count; i++)
            {
                groups[i] = Lookup(EntryKeys.KeyAt<TValue>(batch, shape, entries[i]));
            }

            return;
        }

        int keyWords = (Unsafe.SizeOf<TValue>() + sizeof(ulong) - 1) / sizeof(ulong);
        Span<TValue> keys = MemoryMarshal.Cast<ulong, TValue>(scratch[..(count * keyWords)])[..count];
        Span<uint> homes = MemoryMarshal.Cast<ulong, uint>(scratch.Slice(count * keyWords, count))[..count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = EntryKeys.KeyAt<TValue>(batch, shape, entries[i]);
        }

        groups = groups[..count];
        _sink ^= FindAtHome(keys, groups, homes, 0, out bool missed);
        if (!missed)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (groups[i] < 0)
            {
                groups[i] = Lookup(keys[i]);
            }
        }
    }

    /// <summary>
    /// <see cref="GroupsOf"/> for a paged core's part, numbered whole: an entry's group is its number, its
    /// page's place among the part's then its place in the page, read off its value with no call, its bit
    /// set; one past the span looked up.
    /// </summary>
    private void PagedGroupsOf(PartBatch batch, EntryShape shape, ReadOnlySpan<int> entries, Span<int> groups, int[] slots)
    {
        long least = _directMin;
        int bits = _mapBits;
        long mask = (1L << bits) - 1;
        long[] pages = _mapPages!;
        ulong[] met = _metBits!;
        for (int i = 0; i < entries.Length; i++)
        {
            TValue key = EntryKeys.KeyAt<TValue>(batch, shape, entries[i]);
            long offset = Integer(key) - least;
            long page = offset >> bits;
            int slot = (ulong)page < (ulong)slots.Length ? slots[page] : -1;
            if ((uint)slot < (uint)pages.Length && pages[slot] == page)
            {
                int number = (slot << bits) | (int)(offset & mask);
                ref ulong word = ref met[number >> 6];
                _unmet -= (int)((~word >> number) & 1);
                word |= 1UL << number;
                groups[i] = number;
            }
            else
            {
                groups[i] = Lookup(key);
            }
        }
    }

    /// <summary>The group of a number in a paged core's part numbered whole: the number itself, its bit set, counted met the first time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MeetBit(ulong number)
    {
        ref ulong word = ref _metBits![(int)(number >> 6)];
        _unmet -= (int)((~word >> (int)number) & 1);
        word |= 1UL << (int)number;
        return (int)number;
    }

    /// <summary>A value looked up in a paged core's part once its unmet values are dropped, which nothing does: its keys are final.</summary>
    private static InvalidOperationException FinalPart() =>
        new InvalidOperationException("A part of a paged core is looked up once its unmet values are dropped: its groups are final.");

    internal override int CompareKeys(GroupKeys other, int a, int b, int component)
    {
        FixedKeys<TValue> right = (FixedKeys<TValue>)other;
        bool leftNull = a == _null;
        bool rightNull = b == right._null;
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        return _keys[a].CompareTo(right._keys[b]);
    }

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(_keys, _null));
        }

        return order;
    }

    /// <summary>Groups by their key, the null group last.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly TValue[] _keys;
        private readonly int _null;

        internal ByKey(TValue[] keys, int nullGroup)
        {
            _keys = keys;
            _null = nullGroup;
        }

        public int Compare(int a, int b) =>
            a == _null ? (b == _null ? 0 : 1) : b == _null ? -1 : _keys[a].CompareTo(_keys[b]);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => group == _null ? default! : StorageValues.ToClr<TValue, T>(_keys[group], shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        TValue[] keys = _keys;
        int nullGroup = _null;
        StorageKind kind = _shape.Kind;
        if (kind == StorageKind.Primitive && store.Leaf is FixedStore leaf && leaf.Width == Unsafe.SizeOf<TValue>())
        {
            foreach (int group in groups)
            {
                if (group == nullGroup)
                {
                    KeyStores.AppendNull(store);
                }
                else
                {
                    leaf.Append(keys[group]);
                }
            }

            return;
        }

        foreach (int group in groups)
        {
            if (group == nullGroup)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                KeyStores.AppendFixed(store, keys[group], kind);
            }
        }
    }

    /// <summary>The rows of a sorted column as runs of one key: one comparison per row, one lookup per run.</summary>
    private void Runs(ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> validity, int rows, ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        int start = 0;
        for (int row = 1; row <= rows; row++)
        {
            if (row < rows && SameKey(values, validity, start, row))
            {
                continue;
            }

            if (RowMasks.Count(selection, start, row) > 0)
            {
                ranges.Add(start, row, StorageValues.IsValid(validity, start) ? Lookup(values[start]) : NullGroup());
            }

            start = row;
        }
    }

    private static bool SameKey(ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> validity, int left, int right)
    {
        if (validity.IsEmpty)
        {
            return values[left].Equals(values[right]);
        }

        bool leftValid = StorageValues.IsValid(validity, left);
        return leftValid == StorageValues.IsValid(validity, right) && (!leftValid || values[left].Equals(values[right]));
    }

    /// <summary>The group of <paramref name="value"/>, numbered as it first comes.</summary>
    internal int Lookup(TValue value)
    {
        if (_appending)
        {
            return Add(value);
        }

        if (DirectSlot(value, out ulong number))
        {
            if (_mapSlots is not null)
            {
                return _metBits is not null ? MeetBit(number) : throw FinalPart();
            }

            return Numbered(value, number);
        }

        int group = GetOrAdd(value, Count);
        if (group == Count)
        {
            Add(value);
            if (Wide && !_compact && Count > CompactFrom)
            {
                Condense();
            }
        }

        return group;
    }

    /// <summary>The group of the null key, numbered as it first comes.</summary>
    internal int NullGroup()
    {
        if (_null < 0)
        {
            _null = Add(default);
        }

        return _null;
    }

    /// <summary>The key of group <paramref name="group"/>; the null group's is the default value.</summary>
    internal TValue KeyAt(int group) => _keys[group];

    internal override bool Ascending(int from, int to)
    {
        if (to - from < 2)
        {
            return false;
        }

        for (int g = from + 1; g < to; g++)
        {
            if (_keys[g].CompareTo(_keys[g - 1]) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private int Add(TValue value)
    {
        if (Count == _keys.Length)
        {
            Grow(DoubledUpTo(Count, _doublesUpTo));
        }

        _keys[Count] = value;
        return Count++;
    }
}

/// <summary>A key of one text or binary column, in a byte table, and a group for null.</summary>
internal sealed class BytesKeys : GroupKeys
{
    private readonly ColumnShape _shape;
    private readonly bool _sorted;

    // A group is the table's entry of its key; the null group an entry no key finds.
    private readonly ByteKeyTable _table;
    private int _null = -1;

    /// <param name="shape">The key's column.</param>
    /// <param name="sorted">Whether the statistics say the column is sorted.</param>
    /// <param name="shelf">The lane's shelf the table grows from, under its query's memory; null for a table nothing counts.</param>
    internal BytesKeys(ColumnShape shape, bool sorted, ArrayShelf? shelf = null)
    {
        _shape = shape;
        _sorted = sorted;
        _table = new ByteKeyTable(shelf);
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        ColumnEncoding encoding = EncodedForms.EncodingOf(arena, node);
        Saw(encoding);
        switch (encoding)
        {
            case ColumnEncoding.Constant:
            {
                ValidityKind uniform = arena.RecordRef(node).Validity.Kind;
                if (uniform is ValidityKind.NonNullable or ValidityKind.AllValid or ValidityKind.AllInvalid)
                {
                    ranges.Add(0, rows, uniform == ValidityKind.AllInvalid ? NullGroup() : Lookup(BytesBlock.Constant(arena, node)));
                    return true;
                }

                break;
            }

            case ColumnEncoding.RunEnd:
            {
                if (ArenaWords.NullCount(arena, node) > 0)
                {
                    break;
                }

                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                BytesBlock values = BytesBlock.Canonical(arena, runs, out ReadOnlySpan<ulong> valid);
                int runStart = 0;
                for (int r = 0; r < ends.Length && runStart < rows; r++)
                {
                    int runEnd = Math.Min((int)ends[r], rows);
                    if (RowMasks.Count(selection, runStart, runEnd) > 0)
                    {
                        ranges.Add(runStart, runEnd, StorageValues.IsValid(valid, r) ? Lookup(values[r]) : NullGroup());
                    }

                    runStart = runEnd;
                }

                return true;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, node);
                Span<int> codeGroups = CodeGroups(arena, entries, dictionary.Length);
                RowCursor cursor = new RowCursor(selection, 0, rows);
                while (cursor.Next(out int row))
                {
                    if (!StorageValues.IsValid(present, row))
                    {
                        rowGroups[row] = NullGroup();
                        continue;
                    }

                    int code = (int)codes[row];
                    int group = codeGroups[code];
                    if (group < 0)
                    {
                        group = codeGroups[code] = StorageValues.IsValid(valid, code) ? Lookup(dictionary[code]) : NullGroup();
                    }

                    rowGroups[row] = group;
                }

                return false;
            }

            default:
                break;
        }

        BytesBlock canonical = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);
        if (_sorted)
        {
            int start = 0;
            for (int row = 1; row <= rows; row++)
            {
                if (row < rows && SameKey(canonical, validity, start, row))
                {
                    continue;
                }

                if (RowMasks.Count(selection, start, row) > 0)
                {
                    ranges.Add(start, row, StorageValues.IsValid(validity, start) ? Lookup(canonical[start]) : NullGroup());
                }

                start = row;
            }

            return true;
        }

        if (selection.IsEmpty && !_table.Reseeded)
        {
            Chunked(canonical, validity, rows, rowGroups);
            return false;
        }

        RowCursor selected = new RowCursor(selection, 0, rows);
        int lastRow = -1;
        int lastGroup = -1;
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            ReadOnlySpan<byte> value = canonical[row];
            if (lastRow < 0 || !value.SequenceEqual(canonical[lastRow]))
            {
                lastGroup = Lookup(value);
            }

            lastRow = row;
            rowGroups[row] = lastGroup;
        }

        return false;
    }

    internal override GroupKeys Fresh() => new BytesKeys(_shape, _sorted);

    internal override long Footprint => _table.Footprint;

    internal override int NullNumber => _null;

    internal override bool Orders(int component) => true;

    internal override int CompareKeys(int a, int b, int component) => Compare(a, b);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The kept groups ascend, and are the entries the table keeps, the null group's with them.
        int nullGroup = groups.IndexOf(_null);
        _table.Retain(groups);
        _null = _null < 0 ? -1 : nullGroup;
        Count = groups.Length;
        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        BytesKeys into = (BytesKeys)target;
        for (int i = 0; i < groups.Length; i++)
        {
            int g = groups[i];
            map[i] = g == _null ? into.NullGroup() : into.Lookup(_table.KeyOf(g), HashOf(g));
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = g == _null ? (byte)0 : (byte)((seed == MergeHash.Seed ? HashOf(g) : MergeHash.Of(_table.KeyOf(g), seed)) >> shift);
        }
    }

    /// <summary>The hash of group <paramref name="group"/>'s key under <see cref="MergeHash.Seed"/>: the table's, unless it took a seed of its own.</summary>
    private ulong HashOf(int group) => _table.Reseeded ? MergeHash.Of(_table.KeyOf(group), MergeHash.Seed) : _table.HashOf(group);

    internal override bool Spills => true;

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new BytesKeys(_shape, sorted: false, shelf);

    internal override long GrowthFor(int more) => _table.GrowthFor(more);

    internal override void Hashes(Span<ulong> hashes)
    {
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = g == _null ? 0 : HashOf(g);
        }
    }

    /// <summary>The null group's place among <paramref name="groups"/>, -1 for none, then each other key's hash and bytes: read back, no text is hashed again.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        buffer.Write(_null < 0 ? -1 : groups.IndexOf(_null));
        foreach (int group in groups)
        {
            if (group != _null)
            {
                buffer.Write(HashOf(group));
                buffer.WriteBytes(_table.KeyOf(group));
            }
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        int nullAt = reader.Read<int>();
        for (int i = 0; i < groups.Length; i++)
        {
            if (i == nullAt)
            {
                groups[i] = NullGroup();
                continue;
            }

            ulong hash = reader.Read<ulong>();
            groups[i] = Lookup(reader.Bytes(), hash);
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component)
    {
        BytesKeys right = (BytesKeys)other;
        bool leftNull = a == _null;
        bool rightNull = b == right._null;
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        return _table.KeyOf(a).SequenceCompareTo(right._table.KeyOf(b));
    }

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(this));
        }

        return order;
    }

    /// <summary>Groups by their key's bytes, the null group last.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly BytesKeys _keys;

        internal ByKey(BytesKeys keys) => _keys = keys;

        public int Compare(int a, int b) => _keys.Compare(a, b);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => group == _null ? default! : StorageValues.BytesToClr<T>(_table.KeyOf(group), shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        // The keys came from the column the reader checked as it decoded it: no text function makes a key
        // yet (16-queries.md §3), and one that would must check what it makes.
        VarBinStore leaf = (VarBinStore)store.Leaf;
        foreach (int group in groups)
        {
            if (group == _null)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                leaf.AppendValidated(_table.KeyOf(group));
            }
        }
    }

    private int Compare(int a, int b)
    {
        if (a == _null || b == _null)
        {
            return a == b ? 0 : a == _null ? 1 : -1;
        }

        return _table.KeyOf(a).SequenceCompareTo(_table.KeyOf(b));
    }

    private static bool SameKey(BytesBlock values, ReadOnlySpan<ulong> validity, int left, int right)
    {
        bool leftValid = StorageValues.IsValid(validity, left);
        return leftValid == StorageValues.IsValid(validity, right) && (!leftValid || values[left].SequenceEqual(values[right]));
    }

    /// <summary>The group of <paramref name="value"/>, its entry in the table, numbered as it first comes.</summary>
    internal int Lookup(ReadOnlySpan<byte> value) => Lookup(value, MergeHash.Of(value, MergeHash.Seed));

    /// <summary>The bytes of group <paramref name="group"/>'s key; none for the null group.</summary>
    internal ReadOnlySpan<byte> KeyOf(int group) => _table.KeyOf(group);

    /// <summary>As <see cref="Lookup(ReadOnlySpan{byte})"/>, the value's hash under <see cref="MergeHash.Seed"/> known.</summary>
    private int Lookup(ReadOnlySpan<byte> value, ulong hash)
    {
        int group = _table.GetOrAdd(value, hash, out bool added);
        if (added)
        {
            Count = _table.Count;
        }

        return group;
    }

    /// <summary>The rows <see cref="Chunked"/> takes at a time: their hashes and numbers on the stack.</summary>
    private const int TextChunk = 256;

    /// <summary>
    /// Every row's group, <see cref="TextChunk"/> rows at a time: their
    /// hashes, then each found where its home slot holds it, the table read with no row waiting on
    /// another (<see cref="ByteKeyTable.FindAtHome"/>); then, in their order, the rows left through the
    /// whole lookup, their hashes known, which numbers a key as it first comes.
    /// </summary>
    [SkipLocalsInit]
    private void Chunked(BytesBlock canonical, ReadOnlySpan<ulong> validity, int rows, int[] rowGroups)
    {
        Span<ulong> hashes = stackalloc ulong[TextChunk];
        Span<int> found = stackalloc int[TextChunk];
        ulong seed = MergeHash.Seed;
        for (int start = 0; start < rows; start += TextChunk)
        {
            int count = Math.Min(TextChunk, rows - start);
            Span<ulong> chunk = hashes[..count];
            for (int i = 0; i < count; i++)
            {
                int row = start + i;
                chunk[i] = StorageValues.IsValid(validity, row) ? MergeHash.Of(canonical[row], seed) : 0;
            }

            _table.FindAtHome(chunk, canonical, start, validity, found);
            for (int i = 0; i < count; i++)
            {
                int row = start + i;
                rowGroups[row] = found[i] >= 0 ? found[i]
                    : StorageValues.IsValid(validity, row) ? Lookup(canonical[row], chunk[i])
                    : NullGroup();
            }
        }
    }

    /// <summary>The group of the null key, numbered as it first comes.</summary>
    internal int NullGroup()
    {
        if (_null < 0)
        {
            _null = _table.AddDetached();
            Count = _table.Count;
        }

        return _null;
    }
}

/// <summary>A key of one boolean column: at most false, true and null.</summary>
internal sealed class BoolKeys : GroupKeys
{
    private readonly ColumnShape _shape;
    private readonly int[] _groups = [-1, -1, -1];
    private readonly byte[] _keyOf = new byte[3];
    private ulong[] _bits = [];
    private ulong[] _validity = [];

    internal BoolKeys(ColumnShape shape) => _shape = shape;

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        Saw(EncodedForms.EncodingOf(arena, node));
        ReadOnlySpan<ulong> bits = BoolWords.Values(arena, ref node, ref _bits);
        ReadOnlySpan<ulong> validity = BoolWords.Validity(arena, node, ref _validity);
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int key = !StorageValues.IsValid(validity, row) ? 2 : ((bits[row >> 6] >> (row & 63)) & 1) != 0 ? 1 : 0;
            rowGroups[row] = GroupOf(key);
        }

        return false;
    }

    internal override GroupKeys Fresh() => new BoolKeys(_shape);

    /// <summary>Three groups at most; the words a batch reads.</summary>
    internal override long Footprint => (long)(_bits.Length + _validity.Length) * sizeof(ulong);

    internal override int NullNumber => _groups[2];

    internal override bool Orders(int component) => true;

    /// <summary>False, true, then null: the order of their codes.</summary>
    internal override int CompareKeys(int a, int b, int component) => _keyOf[a].CompareTo(_keyOf[b]);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        _groups.AsSpan().Fill(-1);
        for (int i = 0; i < groups.Length; i++)
        {
            _keyOf[i] = _keyOf[groups[i]];
            _groups[_keyOf[i]] = i;
        }

        Count = groups.Length;
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        BoolKeys into = (BoolKeys)target;
        for (int i = 0; i < groups.Length; i++)
        {
            map[i] = into.GroupOf(_keyOf[groups[i]]);
        }

        MergeSeen(target);
    }

    /// <summary>False and true by their codes, the null group, code 2, to the first part.</summary>
    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = _keyOf[g] == 2 ? (byte)0 : (byte)(MergeHash.Of(_keyOf[g] + 1UL, seed) >> shift);
        }
    }

    internal override bool Spills => true;

    /// <summary>Three groups at most, held from the start.</summary>
    internal override long GrowthFor(int more) => 0;

    internal override void Hashes(Span<ulong> hashes)
    {
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = _keyOf[g] == 2 ? 0 : MergeHash.Of(_keyOf[g] + 1UL, MergeHash.Seed);
        }
    }

    /// <summary>Each key's code: 0 for false, 1 for true, 2 for null.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            buffer.Write(_keyOf[group]);
        }
    }

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = GroupOf(reader.Read<byte>());
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) => _keyOf[a].CompareTo(((BoolKeys)other)._keyOf[b]);

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(_keyOf));
        }

        return order;
    }

    /// <summary>Groups by false, true, then null: the order of their codes.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly byte[] _keys;

        internal ByKey(byte[] keys) => _keys = keys;

        public int Compare(int a, int b) => _keys[a].CompareTo(_keys[b]);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => _keyOf[group] == 2 ? default! : StorageValues.BoolToClr<T>(_keyOf[group] == 1, shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        BoolStore leaf = (BoolStore)store.Leaf;
        foreach (int group in groups)
        {
            byte key = _keyOf[group];
            if (key == 2)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                leaf.Append(key == 1);
            }
        }
    }

    /// <summary>The group of false (0), true (1) or null (2).</summary>
    private int GroupOf(int key)
    {
        if (_groups[key] < 0)
        {
            _groups[key] = Count;
            _keyOf[Count] = (byte)key;
            Count++;
        }

        return _groups[key];
    }
}
