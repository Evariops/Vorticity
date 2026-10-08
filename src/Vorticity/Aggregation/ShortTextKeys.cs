using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
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

    public bool Equals(TextWord other) => Low == other.Low && High == other.High;

    public override bool Equals(object? obj) => obj is TextWord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Low, High);

    public int CompareTo(TextWord other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);

    /// <summary>
    /// The 32 bits a key table homes a word by: CRC32C of its two halves, an instruction each, under
    /// <paramref name="seed"/>. A short text's low half is its length and its first bytes, the same for
    /// many keys, and the table's fold of two words, made for integers, homed db-benchmark's ids of 12
    /// bytes, 10⁵ of them that differ in their last digits alone, so badly that a group by took 639 ms
    /// where a table of bytes took 191; mixed under a seed, 140, but a group by of 100 short texts 112
    /// where the fold took 56. The CRC: 119 and 92, measured on 2026-10-08 at one lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Home(ulong low, ulong high, ulong seed)
    {
        if (Crc32.Arm64.IsSupported)
        {
            return Crc32.Arm64.ComputeCrc32C(Crc32.Arm64.ComputeCrc32C((uint)seed, low), high);
        }

        if (Sse42.X64.IsSupported)
        {
            return (uint)Sse42.X64.Crc32(Sse42.X64.Crc32((uint)seed, low), high);
        }

        return (uint)(MergeHash.Of(low, high, seed | 1) >> 32);
    }
}

/// <summary>
/// A key of one text or binary column whose values are short and few, grouped as words: a value of 12 bytes
/// or less is the 16 bytes of its canonical view, its length then its bytes, zero past them, which a table
/// of 128-bit keys finds by two comparisons where a table of bytes hashes and compares them
/// (<see cref="FixedKeys{TValue}"/>). The first value longer than that, or the first group past
/// <see cref="MostWords"/>, turns the lane's table into one of bytes for good, every group of the words
/// added again in its order, so that each keeps its number (<see cref="BytesKeys"/>). A merge, its parts, a
/// spill's sections and its runs read the keys as bytes, hashed as a table of bytes hashes them, so that a
/// key falls in the same part in every lane whatever its table: lanes of words and lanes of bytes merge.
/// </summary>
/// <remarks>
/// Measured on 2026-10-08 (M4 Pro), db-benchmark's rows, 10⁷ of them, against the table of bytes: a sum by
/// <c>id1</c>, 100 texts of 5 bytes, ×0.69 at one lane and ×0.84 at fourteen; by <c>id1</c> and
/// <c>id2</c>, ×0.71 and ×0.70. By <c>id3</c>, 10⁵ texts of 12 bytes, the words won at one lane (×0.70)
/// and lost at fourteen (×1.26) before they were bounded, and a lane now turns: the same as bytes. Kept
/// to the lanes' tables: the core, which holds a key's entries apart from any lane, stays with the keys
/// whose width the plan knows (<see cref="GroupKeys.EntryBytes"/>).
/// </remarks>
internal sealed class ShortTextKeys : GroupKeys
{
    /// <summary>The longest value a word holds: what a canonical view holds whole.</summary>
    private const int Longest = 12;

    /// <summary>
    /// The most groups a table of words holds before it turns into one of bytes: 4 096. Measured on
    /// 2026-10-08 against the table of bytes, 10⁷ rows, while a slot held its word, 24 bytes, the word kept
    /// twice, twice the memory of bytes at 10⁶ keys (137 MB against 66): 100 keys ×0.62 at one lane and
    /// ×0.81 at fourteen; 10⁵ keys ×0.70 and ×1.26; 10⁶ names over 2M rows ×1.10 and ×1.46. A slot now
    /// holds the word's hash and its group (<see cref="WideKeyTable{TValue}"/>).
    /// </summary>
    private const int MostWords = 1 << 12;

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

        // A block that could take the lane past what words hold, a few thousand rows at a time, its words made
        // a chunk at a time: the lane turns into bytes as soon as it passes, the block done again as bytes,
        // each key it gave a number keeping it. A block at once grew a table of words to its own size first,
        // 10 MB a lane over names.
        if (Count + rows > MostWords && selection.IsEmpty && !_sorted && _probeAhead >= 0)
        {
            Scratch.Grow(ref _block, Math.Min(rows, MostWords));
            for (int start = 0; start < rows; start += MostWords)
            {
                int end = Math.Min(rows, start + MostWords);
                if (!FillWords(canonical, validity, start, end))
                {
                    Demote();
                    return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
                }

                words.AssignRange(_block.AsSpan(0, end - start), validity, start, rowGroups);
                Count = words.Count;
                if (Count > MostWords)
                {
                    Demote();
                    return AssignBytes(arena, nodes, rows, selection, rowGroups, ranges);
                }
            }

            Saw(encoding);
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
        if (Count > MostWords)
        {
            Demote();
        }

        return byRange;
    }

    /// <summary>The words of rows <paramref name="start"/> to <paramref name="end"/> into the block's scratch from its start; false at the first value too long for one.</summary>
    private bool FillWords(BytesBlock canonical, ReadOnlySpan<ulong> validity, int start, int end)
    {
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

    internal override int NullNumber => _words?.NullNumber ?? _bytes!.NullNumber;

    internal override void Release()
    {
        _words?.Release();
        _bytes?.Release();
    }

    /// <summary>
    /// Room for <paramref name="groups"/> groups, foretold by a lane's first rows: past what a table of words
    /// holds, the keys turn into bytes first, which the room goes to. Reserved as words, then turned, a
    /// lane's table of bytes grew again from nothing: 11 MB more a lane and a round over 10⁵ keys.
    /// </summary>
    internal override void Reserve(int groups)
    {
        if (_words is not null && groups > MostWords)
        {
            Demote();
        }

        _words?.Reserve(groups);
        _bytes?.Reserve(groups);
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        if (_words is { } words)
        {
            words.Keep(groups);
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
            if (into.Count > MostWords)
            {
                into.Demote();
            }
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
