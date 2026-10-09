using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A short text as one key of 128 bits: the 16 bytes of its canonical view, its length in the low 32 bits,
/// its bytes above, zero past them (<see cref="ShortTextKeys"/>). Its order is not the text's: the keys
/// order their groups by their bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct TextWord : IEquatable<TextWord>, IComparable<TextWord>
{
    internal readonly ulong Low;
    internal readonly ulong High;

    internal TextWord(ulong low, ulong high)
    {
        Low = low;
        High = high;
    }

    /// <remarks>
    /// Both words at once, with no branch: the first passes of the key tables compare a row's word with a
    /// slot's and take the group or none by a mask, and a <c>&amp;&amp;</c> compiled to a branch on the low
    /// word, mispredicted at every key that is not in its home slot (2026-10-09, the disassembly of
    /// <see cref="KeyTable{TValue}.FindAtHome"/>).
    /// </remarks>
    public bool Equals(TextWord other) => ((Low ^ other.Low) | (High ^ other.High)) == 0;

    public override bool Equals(object? obj) => obj is TextWord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Low, High);

    public int CompareTo(TextWord other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);

    /// <summary>
    /// The 32 bits a key table homes a word by: rapidhash's path for 16 bytes (V3), two products of 128
    /// bits, under the <paramref name="seed"/> a table takes after a long chain, 0 until then. A short
    /// text's low half is its length and its first bytes, the same for many keys, and the table's fold of
    /// two words, made for integers, homed db-benchmark's ids of 12 bytes, 10⁵ of them that differ in their
    /// last digits alone, so badly that a group by took 639 ms where a table of bytes took 191 (2026-10-08,
    /// one lane).
    /// </summary>
    /// <remarks>
    /// <para>
    /// CRC32C, an instruction a half, homed those ids faster, by the luck of their digits: against it,
    /// rapidhash took ×0.96 to ×1.13 at one lane (q1 to 2·10⁶ names, <c>id3</c> ×1.10), ×1.00 to ×1.06 at
    /// 14, and XXH3 of System.IO.Hashing ×1.13 to ×1.55, its call the cost (2026-10-09). But a CRC is
    /// linear: a seed moves every word's home by one mask, so that words built to share a CRC share their
    /// home whatever the seed and chain past any seed a table takes. SMHasher fails CRC32C on its bias, its
    /// collisions and its distribution, and passes rapidhash, at 22 cycles a small key where xxh3 takes 29.
    /// </para>
    /// <para>
    /// Sources: github.com/Nicoshev/rapidhash, rapidhash.h, its secrets and its path for 4 to 16 bytes;
    /// github.com/rurban/smhasher, its table of hashes.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Home(ulong low, ulong high, ulong seed) => HomePrepared(low, high, Prepared(seed));

    /// <summary>
    /// <see cref="Home"/> of a seed already prepared by <see cref="Prepared"/>: a table's first pass prepares
    /// it once a batch, where preparing it at every row cost a test and four instructions of the constant a
    /// row, in the loop the native compiler left it in (2026-10-09).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint HomePrepared(ulong low, ulong high, ulong prepared)
    {
        ulong upper = Math.BigMul(low ^ Secret1, high ^ prepared, out ulong lower);
        return (uint)(Mix(lower ^ Secret7, upper ^ Secret1 ^ 16) >> 32);
    }

    /// <summary>The seed as rapidhash prepares it, the length of 16 folded in: <c>seed ^ mix(seed ^ secret[2], secret[1]) ^ 16</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Prepared(ulong seed) => seed == 0 ? Unseeded : seed ^ Mix(seed ^ Secret2, Secret1) ^ 16;

    // rapidhash's secrets (V3) its path for 16 bytes reads.
    private const ulong Secret1 = 0x8bb84b93962eacc9UL;
    private const ulong Secret2 = 0x4b33a62ed433d4a3UL;
    private const ulong Secret7 = 0xaaaaaaaaaaaaaaaaUL;

    // A seed of 0 prepared as rapidhash prepares its seed, the length folded in: seed ^ mix(seed ^ secret[2], secret[1]) ^ 16.
    private const ulong Unseeded = 0x422765567D8FBFC6UL;

    /// <summary>The two halves of the 128-bit product of <paramref name="a"/> and <paramref name="b"/>, folded: rapidhash's mix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong a, ulong b)
    {
        ulong upper = Math.BigMul(a, b, out ulong lower);
        return lower ^ upper;
    }
}

/// <summary>
/// A key of one text or binary column whose values are short, grouped as words: a value of 12 bytes or less
/// is the 16 bytes of its canonical view, its length then its bytes, zero past them, which a table of
/// 128-bit keys finds by two comparisons where a table of bytes hashes and compares them
/// (<see cref="FixedKeys{TValue}"/>, whose slots hold the words while the groups are few, and their hash
/// past that). The first value longer than that turns the lane's table into one of bytes for good, every
/// group of the words added again in its order, so that each keeps its number (<see cref="BytesKeys"/>). A
/// merge, its parts, a spill's sections and its runs read the keys as bytes, hashed as a table of bytes
/// hashes them, so that a key falls in the same part in every lane whatever its table: lanes of words and
/// lanes of bytes merge.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 2026-10-08 (M4 Pro), db-benchmark's rows, 10⁷ of them, against the table of bytes: a sum by
/// <c>id1</c>, 100 texts of 5 bytes, ×0.69 at one lane and ×0.84 at fourteen; by <c>id1</c> and
/// <c>id2</c>, ×0.71 and ×0.70.
/// </para>
/// <para>
/// Words were bounded to 4 096 groups while a slot held its word, 24 bytes, the word kept twice: twice the
/// memory of bytes at 10⁶ keys, and by <c>id3</c>, 10⁵ texts of 12 bytes, ×1.26 the time of bytes at
/// fourteen lanes. Unbounded, in slots of a hash and a group past 4 096 groups, against the bytes a lane
/// turned into there (2026-10-09): by <c>id3</c> ×0.603 the time at one lane and ×0.765 at fourteen, q3
/// ×0.690 and ×0.814, 10⁶ names ×0.753 and ×0.924, q10 ×0.948 and ×0.971; the state by <c>id3</c> ×0.66
/// and ×0.71, by 10⁶ names ×1.54 at one lane, whose first rows, all new, foretold twice as many, and
/// ×1.01 at fourteen. Such a lane now reserves the keys for half its rows: the names' state ×1.00 of the
/// bytes' at one lane (<see cref="GroupKeys.ReserveAllNew"/>).
/// </para>
/// <para>
/// Kept to the lanes' tables: the core, which holds a key's entries apart from any lane, stays with the
/// keys whose width the plan knows (<see cref="GroupKeys.EntryBytes"/>).
/// </para>
/// </remarks>
internal sealed class ShortTextKeys : GroupKeys
{
    /// <summary>The longest value a word holds: what a canonical view holds whole.</summary>
    private const int Longest = 12;

    /// <summary>
    /// The rows of a block whose words are made and found at a time, every row selected: 64 KB of words a
    /// lane, where a block of 65 536 rows at once held 1 MB of them and 256 KB of homes, a hundred groups'
    /// state ×17 (<see cref="FixedKeys{TValue}.TwoPasses"/>).
    /// </summary>
    private const int Chunk = 1 << 12;

    private readonly ColumnShape _shape;
    private readonly bool _sorted;
    private readonly int _probeAhead;
    private readonly ArrayShelf? _shelf;

    // The words while every value met is short, then the bytes for good: one of them, never both.
    private FixedKeys<TextWord>? _words;
    private BytesKeys? _bytes;
    private TextWord[] _block = [];

    internal ShortTextKeys(ColumnShape shape, bool sorted, int probeAhead = AggregationPlan.DefaultProbeAhead, ArrayShelf? shelf = null)
    {
        _shape = shape;
        _sorted = sorted;
        _probeAhead = probeAhead;
        _shelf = shelf;
        _words = new FixedKeys<TextWord>(shape, sorted, probeAhead: probeAhead, shelf: shelf);
    }

    /// <summary>Whether the keys are still words: every value met so far 12 bytes or less.</summary>
    internal bool Words => _words is not null;

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        if (_words is not { } words)
        {
            return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
        }

        int node = nodes[0];
        ColumnEncoding encoding = EncodedForms.EncodingOf(arena, node);
        switch (encoding)
        {
            case ColumnEncoding.Constant:
            {
                ValidityKind uniform = arena.RecordRef(node).Validity.Kind;
                if (uniform is ValidityKind.NonNullable or ValidityKind.AllValid or ValidityKind.AllInvalid)
                {
                    if (uniform == ValidityKind.AllInvalid)
                    {
                        Saw(encoding);
                        ranges.Add(0, rows, Counted(words.NullGroup()));
                        return true;
                    }

                    if (!TryWord(BytesBlock.Constant(arena, node), out TextWord word))
                    {
                        Demote();
                        return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
                    }

                    Saw(encoding);
                    ranges.Add(0, rows, Counted(words.Lookup(word)));
                    return true;
                }

                break;
            }

            case ColumnEncoding.RunEnd:
            case ColumnEncoding.Dictionary:
                // Their values are few: as bytes, each looked up once, then its runs or its codes.
                return AssignEncoded(arena, nodes, rows, selection, rowGroups, ranges, encoding);

            default:
                break;
        }

        BytesBlock canonical = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);

        // A value too long for a word turns the lane into bytes, the block done again as bytes, each key
        // its first chunks gave a number keeping it.
        if (selection.IsEmpty && !_sorted && _probeAhead >= 0)
        {
            Scratch.Grow(ref _block, Math.Min(rows, Chunk));
            for (int start = 0; start < rows; start += Chunk)
            {
                int end = Math.Min(rows, start + Chunk);
                if (!FillWords(canonical, validity, start, end))
                {
                    Demote();
                    return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
                }

                words.TwoPasses(_block.AsSpan(0, end - start), validity, start, rowGroups);
            }

            Saw(encoding);
            Count = words.Count;
            return false;
        }

        Scratch.Grow(ref _block, rows);
        if (!FillWords(canonical, validity, 0, rows))
        {
            Demote();
            return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
        }

        Saw(encoding);
        bool byRange = words.AssignValues(_block.AsSpan(0, rows), validity, rows, selection, rowGroups, ranges);
        Count = words.Count;
        return byRange;
    }

    /// <summary>The words of rows <paramref name="start"/> to <paramref name="end"/> into the block's scratch from its start; false at the first value too long for one.</summary>
    private bool FillWords(BytesBlock canonical, ReadOnlySpan<ulong> validity, int start, int end)
    {
        if (validity.IsEmpty)
        {
            return canonical.TryWords(start, _block.AsSpan(0, end - start));
        }

        TextWord[] block = _block;
        for (int row = start; row < end; row++)
        {
            if (!StorageValues.IsValid(validity, row))
            {
                block[row - start] = default;
            }
            else if (!canonical.TryWord(row, out block[row - start]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A block of runs or of codes, its few values looked up as bytes then as words, each once.</summary>
    private bool AssignEncoded(
        CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges, ColumnEncoding encoding)
    {
        int node = nodes[0];
        if (encoding == ColumnEncoding.RunEnd)
        {
            if (ArenaWords.NullCount(arena, node) > 0)
            {
                return AssignPlain(arena, nodes, rows, selection, rowGroups, ranges);
            }

            int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
            BytesBlock values = BytesBlock.Canonical(arena, runs, out ReadOnlySpan<ulong> valid);
            if (!AllShort(values, valid))
            {
                Demote();
                return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
            }

            Saw(encoding);
            FixedKeys<TextWord> words = _words!;
            int runStart = 0;
            for (int r = 0; r < ends.Length && runStart < rows; r++)
            {
                int runEnd = Math.Min((int)ends[r], rows);
                if (RowMasks.Count(selection, runStart, runEnd) > 0)
                {
                    ranges.Add(runStart, runEnd, StorageValues.IsValid(valid, r) && values.TryWord(r, out TextWord word) ? words.Lookup(word) : words.NullGroup());
                }

                runStart = runEnd;
            }

            Count = words.Count;
            return true;
        }

        int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
        BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> validEntries);
        if (!AllShort(dictionary, validEntries))
        {
            Demote();
            return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
        }

        Saw(encoding);
        FixedKeys<TextWord> keys = _words!;
        ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, node);
        Span<int> codeGroups = CodeGroups(arena, entries, dictionary.Length);
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            if (!StorageValues.IsValid(present, row))
            {
                rowGroups[row] = keys.NullGroup();
                continue;
            }

            int code = (int)codes[row];
            int group = codeGroups[code];
            if (group < 0)
            {
                group = codeGroups[code] = StorageValues.IsValid(validEntries, code) && dictionary.TryWord(code, out TextWord word) ? keys.Lookup(word) : keys.NullGroup();
            }

            rowGroups[row] = group;
        }

        Count = keys.Count;
        return false;
    }

    /// <summary>A run-end block with nulls among its runs, in canonical form row by row.</summary>
    private bool AssignPlain(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        BytesBlock canonical = BytesBlock.Canonical(arena, nodes[0], out ReadOnlySpan<ulong> validity);
        Scratch.Grow(ref _block, rows);
        TextWord[] block = _block;
        for (int row = 0; row < rows; row++)
        {
            if (!StorageValues.IsValid(validity, row))
            {
                block[row] = default;
            }
            else if (!canonical.TryWord(row, out block[row]))
            {
                Demote();
                return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
            }
        }

        Saw(ColumnEncoding.RunEnd);
        bool byRange = _words!.AssignValues(block.AsSpan(0, rows), validity, rows, selection, rowGroups, ranges);
        Count = _words.Count;
        return byRange;
    }

    /// <summary>Whether every valid value of <paramref name="values"/> is short.</summary>
    private static bool AllShort(BytesBlock values, ReadOnlySpan<ulong> validity)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (StorageValues.IsValid(validity, i) && !values.TryWord(i, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The block through the table of bytes, the keys turned into it.</summary>
    private bool AssignBytes(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        Saw(EncodedForms.EncodingOf(arena, nodes[0]));
        bool byRange = _bytes!.Assign(arena, nodes, rows, selection, rowGroups, ranges);
        Count = _bytes.Count;
        return byRange;
    }

    /// <summary><paramref name="group"/>, the count of groups brought up to the words'.</summary>
    private int Counted(int group)
    {
        Count = _words!.Count;
        return group;
    }

    /// <summary>
    /// The words turned into a table of bytes, for good: every group's key added again in the order of
    /// the groups, which keeps each its number, the null group's too; the words' arrays given back.
    /// </summary>
    private void Demote()
    {
        FixedKeys<TextWord> words = _words!;
        BytesKeys bytes = new BytesKeys(_shape, _sorted, _shelf);
        Span<byte> buffer = stackalloc byte[16];
        int nullGroup = words.NullNumber;
        for (int g = 0; g < words.Count; g++)
        {
            int added = g == nullGroup ? bytes.NullGroup() : bytes.Lookup(BytesOf(words.KeyAt(g), buffer));
            if (added != g)
            {
                throw new InvalidOperationException($"A short text key's group {g} became {added} in its table of bytes.");
            }
        }

        words.Release();
        _words = null;
        _bytes = bytes;
        _block = [];
        Renumbered();
    }

    /// <summary>The bytes a word holds, written into <paramref name="buffer"/> of 16 bytes.</summary>
    internal static ReadOnlySpan<byte> BytesOf(TextWord word, Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, word.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], word.High);
        return buffer.Slice(4, (int)(uint)word.Low);
    }

    /// <summary><paramref name="value"/> as a word, as a block's view gives it; false when it is longer than a word holds.</summary>
    internal static bool TryWord(ReadOnlySpan<byte> value, out TextWord word)
    {
        if (value.Length > Longest)
        {
            word = default;
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];
        buffer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value.Length);
        value.CopyTo(buffer[4..]);
        word = new TextWord(BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]));
        return true;
    }

    /// <summary>The bytes of group <paramref name="group"/>'s key, through <paramref name="buffer"/> of 16 bytes for a word.</summary>
    private ReadOnlySpan<byte> KeyOf(int group, Span<byte> buffer) =>
        _words is { } words ? BytesOf(words.KeyAt(group), buffer) : _bytes!.KeyOf(group);

    internal override GroupKeys Fresh() => new ShortTextKeys(_shape, _sorted, _probeAhead);

    internal override GroupKeys ForTable(ArrayShelf shelf) => new ShortTextKeys(_shape, sorted: false, _probeAhead, shelf);

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => new ShortTextKeys(_shape, sorted: false, _probeAhead, shelf);

    internal override long Footprint => (_words?.Footprint ?? _bytes!.Footprint) + ((long)_block.Length * 16);

    internal override long GrowthFor(int more) => _words?.GrowthFor(more) ?? _bytes!.GrowthFor(more);

    internal override bool Squeeze(int more) => _words?.Squeeze(more) ?? false;

    internal override int NullNumber => _words?.NullNumber ?? _bytes!.NullNumber;

    internal override void Release()
    {
        _words?.Release();
        _bytes?.Release();
    }

    /// <summary>Room for <paramref name="groups"/> groups, foretold by a lane's first rows, in the table the keys are in.</summary>
    internal override void Reserve(int groups)
    {
        _words?.Reserve(groups);
        _bytes?.Reserve(groups);
    }

    internal override void ReserveAllNew(int groups)
    {
        _words?.ReserveAllNew(groups);
        _bytes?.Reserve(groups);
    }

    internal override void Keep(ReadOnlySpan<int> groups) => Keep(groups, carry: false);

    internal override void Carry(ReadOnlySpan<int> groups) => Keep(groups, carry: true);

    private void Keep(ReadOnlySpan<int> groups, bool carry)
    {
        if (_words is { } words)
        {
            if (carry)
            {
                words.Carry(groups);
            }
            else
            {
                words.Keep(groups);
            }

            Count = words.Count;
        }
        else
        {
            _bytes!.Keep(groups);
            Count = _bytes.Count;
        }

        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        ShortTextKeys into = (ShortTextKeys)target;
        if (_words is { } words && into._words is { } intoWords)
        {
            words.MergeInto(intoWords, groups, map);
            into.Count = intoWords.Count;
        }
        else
        {
            if (into._words is not null)
            {
                into.Demote();
            }

            BytesKeys intoBytes = into._bytes!;
            if (_bytes is { } bytes)
            {
                bytes.MergeInto(intoBytes, groups, map);
            }
            else
            {
                Span<byte> buffer = stackalloc byte[16];
                int nullGroup = NullNumber;
                for (int i = 0; i < groups.Length; i++)
                {
                    int g = groups[i];
                    map[i] = g == nullGroup ? intoBytes.NullGroup() : intoBytes.Lookup(KeyOf(g, buffer));
                }
            }

            into.Count = intoBytes.Count;
        }

        MergeSeen(target);
    }

    /// <summary>Each group's part by the hash of its bytes, as a table of bytes cuts them: the same part for a key in every lane, of words or of bytes.</summary>
    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        if (_bytes is { } bytes)
        {
            bytes.Parts(seed, shift, parts);
            return;
        }

        Span<byte> buffer = stackalloc byte[16];
        int nullGroup = NullNumber;
        for (int g = 0; g < Count; g++)
        {
            parts[g] = g == nullGroup ? (byte)0 : (byte)(MergeHash.Of(KeyOf(g, buffer), seed) >> shift);
        }
    }

    internal override bool Spills => true;

    internal override void Hashes(Span<ulong> hashes)
    {
        if (_bytes is { } bytes)
        {
            bytes.Hashes(hashes);
            return;
        }

        Span<byte> buffer = stackalloc byte[16];
        int nullGroup = NullNumber;
        for (int g = 0; g < Count; g++)
        {
            hashes[g] = g == nullGroup ? 0 : MergeHash.Of(KeyOf(g, buffer), MergeHash.Seed);
        }
    }

    /// <summary>As a table of bytes writes them: the null group's place, then each key's hash and bytes, whatever the lane held them as.</summary>
    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        if (_bytes is { } bytes)
        {
            bytes.WriteKeys(groups, buffer);
            return;
        }

        int nullGroup = NullNumber;
        buffer.Write(nullGroup < 0 ? -1 : groups.IndexOf(nullGroup));
        Span<byte> scratch = stackalloc byte[16];
        foreach (int group in groups)
        {
            if (group != nullGroup)
            {
                ReadOnlySpan<byte> key = KeyOf(group, scratch);
                buffer.Write(MergeHash.Of(key, MergeHash.Seed));
                buffer.WriteBytes(key);
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
                groups[i] = _words?.NullGroup() ?? _bytes!.NullGroup();
                Count = _words?.Count ?? _bytes!.Count;
                continue;
            }

            reader.Read<ulong>();
            ReadOnlySpan<byte> key = reader.Bytes();
            if (_words is { } words && TryWord(key, out TextWord word))
            {
                groups[i] = words.Lookup(word);
                Count = words.Count;
                continue;
            }

            if (_words is not null)
            {
                Demote();
            }

            groups[i] = _bytes!.Lookup(key);
            Count = _bytes.Count;
        }
    }

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            Array.Sort(order, new ByKey(this));
        }

        return order;
    }

    internal override bool Orders(int component) => true;

    internal override int CompareKeys(int a, int b, int component) => Compare(this, a, b);

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) => Compare((ShortTextKeys)other, a, b);

    /// <summary>Group <paramref name="a"/> here against group <paramref name="b"/> of <paramref name="other"/>, by their bytes, the null group last.</summary>
    private int Compare(ShortTextKeys other, int a, int b)
    {
        bool leftNull = a == NullNumber;
        bool rightNull = b == other.NullNumber;
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        return KeyOf(a, left).SequenceCompareTo(other.KeyOf(b, right));
    }

    /// <summary>Groups by their key's bytes, the null group last.</summary>
    private sealed class ByKey(ShortTextKeys keys) : IComparer<int>
    {
        public int Compare(int x, int y) => keys.Compare(keys, x, y);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        byte[] buffer = new byte[16];
        return group => group == NullNumber ? default! : StorageValues.BytesToClr<T>(KeyOf(group, buffer), shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        // The keys came from the column the reader checked as it decoded it, as a table of bytes appends them.
        VarBinStore leaf = (VarBinStore)store.Leaf;
        Span<byte> buffer = stackalloc byte[16];
        int nullGroup = NullNumber;
        foreach (int group in groups)
        {
            if (group == nullGroup)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                leaf.AppendValidated(KeyOf(group, buffer));
            }
        }
    }
}
