using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The values of a key of several columns side by side in 64 bytes, eight words: each text a word, each
/// integer its bytes, a byte of flags for the nulls (<see cref="TupleLayout"/>). Its words compared at
/// once; its order is the words', which only a table needs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KeyTuple : IEquatable<KeyTuple>, IComparable<KeyTuple>
{
    internal ulong W0;
    internal ulong W1;
    internal ulong W2;
    internal ulong W3;
    internal ulong W4;
    internal ulong W5;
    internal ulong W6;
    internal ulong W7;

    public readonly bool Equals(KeyTuple other) =>
        ((W0 ^ other.W0) | (W1 ^ other.W1) | (W2 ^ other.W2) | (W3 ^ other.W3) | (W4 ^ other.W4) | (W5 ^ other.W5) | (W6 ^ other.W6) | (W7 ^ other.W7)) == 0;

    public readonly int CompareTo(KeyTuple other)
    {
        int c = W0.CompareTo(other.W0);
        c = c != 0 ? c : W1.CompareTo(other.W1);
        c = c != 0 ? c : W2.CompareTo(other.W2);
        c = c != 0 ? c : W3.CompareTo(other.W3);
        c = c != 0 ? c : W4.CompareTo(other.W4);
        c = c != 0 ? c : W5.CompareTo(other.W5);
        c = c != 0 ? c : W6.CompareTo(other.W6);
        return c != 0 ? c : W7.CompareTo(other.W7);
    }

    public override readonly bool Equals(object? obj) => obj is KeyTuple other && Equals(other);

    public override readonly int GetHashCode() => (int)KeyWords.Of(this).Low;

    /// <summary>
    /// The tuple's eight words folded into two by four products of 128 bits, the halves of each xored, as
    /// rapidhash mixes: a table that hashes the two words weakly, unseeded, still spreads tuples that differ
    /// in any word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly (ulong Low, ulong High) Words() =>
        (Fold(W0 ^ 0x2D35_8DCC_AA6C_78A5UL, W1 ^ 0x8BB8_4B93_962E_ACC9UL) ^ Fold(W2 ^ 0x4B33_A62E_D433_D4A3UL, W3 ^ 0x4D5A_2DA5_1DE1_AA47UL),
         Fold(W4 ^ 0xA076_1D64_78BD_642FUL, W5 ^ 0xE703_7ED1_A0B4_28DBUL) ^ Fold(W6 ^ 0x9E37_79B9_7F4A_7C15UL, W7 ^ 0xC2B2_AE3D_27D4_EB4FUL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fold(ulong a, ulong b)
    {
        ulong high = Math.BigMul(a, b, out ulong low);
        return low ^ high;
    }
}

/// <summary>
/// Where each column of a key lies in a <see cref="KeyTuple"/>, a run's: a text a word of 16 bytes, its
/// length then its bytes as a canonical view holds a value of 12 bytes or less (<see cref="TextWord"/>);
/// an integer its bytes, as wide as its type; the nulls a bit each in the last byte. A text longer than a
/// word is a mark, a length no word has, and its number among the run's long texts, the same in every
/// lane, numbered once by whichever lane meets it first: a tuple is the same tuple in every lane, which
/// the core takes as a key of a known width. The long texts are held once for the run, under its memory.
/// </summary>
internal sealed class TupleLayout
{
    /// <summary>The bytes of a tuple.</summary>
    internal const int Bytes = 64;

    /// <summary>The byte of the null flags, a bit a column.</summary>
    internal const int FlagsAt = Bytes - 1;

    /// <summary>The bytes of a text's word.</summary>
    internal const int WordBytes = 16;

    /// <summary>The length a long text's mark holds in place of a word's: none has it.</summary>
    private const uint LongMark = uint.MaxValue;

    /// <summary>What a long text holds past its bytes: its array's header, its node in the dictionary and its place in the list.</summary>
    private const int LongOverhead = 96;

    private readonly QueryMemory? _memory;
    private readonly ConcurrentDictionary<byte[], int> _longIds;
    private readonly ConcurrentDictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> _longLookup;
    private readonly Lock _gate = new Lock();
    private byte[][] _longs = new byte[16][];
    private int _longCount;

    private TupleLayout(ColumnShape[] shapes, int[] offsets, QueryMemory? memory)
    {
        Shapes = shapes;
        Offsets = offsets;
        _memory = memory;
        _longIds = new ConcurrentDictionary<byte[], int>(LongComparer.Instance);
        _longLookup = _longIds.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    internal ColumnShape[] Shapes { get; }

    /// <summary>The byte each column starts at.</summary>
    internal int[] Offsets { get; }

    /// <summary>
    /// The layout of a key of <paramref name="shapes"/> for a run under <paramref name="memory"/>, or null
    /// when a tuple cannot hold it: more than eight columns, a column neither a text nor an integer, or more
    /// than 63 bytes. Texts first, each on a word, then the integers from the widest down.
    /// </summary>
    internal static TupleLayout? Of(ColumnShape[] shapes, QueryMemory? memory)
    {
        if (shapes.Length is < 2 or > 8)
        {
            return null;
        }

        int[] widths = new int[shapes.Length];
        for (int p = 0; p < shapes.Length; p++)
        {
            widths[p] = WidthOf(shapes[p]);
            if (widths[p] == 0)
            {
                return null;
            }
        }

        int[] offsets = new int[shapes.Length];
        int at = 0;
        foreach (int width in (int[])[WordBytes, 8, 4, 2, 1])
        {
            for (int p = 0; p < shapes.Length; p++)
            {
                if (widths[p] == width)
                {
                    offsets[p] = at;
                    at += width;
                }
            }
        }

        return at <= FlagsAt ? new TupleLayout(shapes, offsets, memory) : null;
    }

    /// <summary>The bytes column <paramref name="shape"/> takes in a tuple: a word for a text, its width for an integer; 0 for any other.</summary>
    private static int WidthOf(ColumnShape shape) => shape.Kind switch
    {
        StorageKind.Bytes => WordBytes,
        StorageKind.Primitive => shape.PType switch
        {
            PType.I8 or PType.U8 => 1,
            PType.I16 or PType.U16 => 2,
            PType.I32 or PType.U32 => 4,
            PType.I64 or PType.U64 => 8,
            _ => 0,
        },
        _ => 0,
    };

    /// <summary>Whether column <paramref name="component"/> is a text.</summary>
    internal bool Text(int component) => Shapes[component].Kind == StorageKind.Bytes;

    /// <summary>The word of a text longer than a word holds: its mark and its number, numbered the first time.</summary>
    internal TextWord LongWord(ReadOnlySpan<byte> value)
    {
        if (!_longLookup.TryGetValue(value, out int id))
        {
            id = AddLong(value.ToArray());
        }

        return new TextWord(LongMark | ((ulong)(uint)id << 32), 0);
    }

    private int AddLong(byte[] value)
    {
        lock (_gate)
        {
            if (_longIds.TryGetValue(value, out int known))
            {
                return known;
            }

            // Counted before it is kept: a text the run's memory refuses fails the query with nothing kept.
            _memory?.HoldBeside(value.Length + LongOverhead);
            int id = _longCount;
            if (id == _longs.Length)
            {
                Array.Resize(ref _longs, id * 2);
            }

            _longs[id] = value;
            _longCount = id + 1;
            _longIds[value] = id;
            return id;
        }
    }

    /// <summary>The long texts numbered so far.</summary>
    internal int LongTexts => Volatile.Read(ref _longCount);

    /// <summary>The bytes of the text a word holds, through <paramref name="buffer"/> of 16 bytes for a short one.</summary>
    internal ReadOnlySpan<byte> TextOf(TextWord word, Span<byte> buffer) => IsLong(word) ? LongText(word) : ShortTextKeys.BytesOf(word, buffer);

    /// <summary>Whether a word holds a long text's mark and number rather than a text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsLong(TextWord word) => (uint)word.Low == LongMark;

    /// <summary>
    /// The long text whose number a word holds. Read without the lock: a text is in the list before its
    /// number is handed out, and the list a reader sees holds every number below its length, a longer
    /// one being copied from it before it replaces it; a number past the list read is looked up again
    /// under the lock.
    /// </summary>
    internal byte[] LongText(TextWord word)
    {
        int id = (int)(word.Low >> 32);
        byte[][] longs = Volatile.Read(ref _longs);
        if ((uint)id < (uint)longs.Length && longs[id] is { } text)
        {
            return text;
        }

        lock (_gate)
        {
            return _longs[id];
        }
    }

    /// <summary>Byte arrays equal by their bytes, found by a span of them.</summary>
    private sealed class LongComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        internal static readonly LongComparer Instance = new LongComparer();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => (int)MergeHash.Of(obj, MergeHash.Seed);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate) => (int)MergeHash.Of(alternate, MergeHash.Seed);

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}

/// <summary>
/// A key of two to eight text and integer columns as the tuple of its values (<see cref="KeyTuple"/>): one
/// table of tuples, where <see cref="PackedKeys{TKey}"/> numbers each column in an index of its own, then
/// the tuple of the numbers; and a key of a known width, the same in every lane, which the core takes. Its
/// tables, entries, merges and spills are those of <see cref="FixedKeys{TValue}"/> of tuples; its columns
/// come out of the tuples, each in its type.
/// </summary>
/// <remarks>
/// db-benchmark's q10, six columns of which three short texts and a group nearly a row, held a table of
/// numbers a lane at fourteen lanes, ≈ 714 000 groups each, then merged them, copying every one: half its
/// time (2026-10-09). Its tuples go to the core, which holds each group once.
/// </remarks>
internal sealed class TupleKeys : GroupKeys
{
    /// <summary>
    /// The rows whose tuples are made and found at a time: 64 KB of tuples a lane, in the first level of
    /// cache of current cores, where a block of 65 536 rows at once held 4 MB of them, which a lane on the
    /// core reserved twice past its budget.
    /// </summary>
    private const int Chunk = 1 << 10;

    private readonly TupleLayout _layout;
    private readonly FixedKeys<KeyTuple> _tuples;
    private KeyTuple[] _block = [];
    private TextWord[] _words = [];

    internal TupleKeys(TupleLayout layout, FixedKeys<KeyTuple> tuples)
    {
        _layout = layout;
        _tuples = tuples;
        Count = tuples.Count;
    }

    /// <summary>The keys of a plan's columns as tuples, their arrays from <paramref name="shelf"/>.</summary>
    internal TupleKeys(TupleLayout layout, int probeAhead, ArrayShelf? shelf)
        : this(layout, new FixedKeys<KeyTuple>(TupleShape(layout), sorted: false, probeAhead: probeAhead, shelf: shelf))
    {
    }

    /// <summary>The shape a tuple's table reads: the first column's, which it never reads by.</summary>
    private static ColumnShape TupleShape(TupleLayout layout) => layout.Shapes[0];

    /// <summary>Where the tuples' columns lie, and the run's long texts.</summary>
    internal TupleLayout Layout => _layout;

    private TupleKeys Wrap(GroupKeys tuples) => new TupleKeys(_layout, (FixedKeys<KeyTuple>)tuples);

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        Scratch.Grow(ref _block, Math.Min(rows, Chunk));
        for (int start = 0; start < rows; start += Chunk)
        {
            int end = Math.Min(rows, start + Chunk);
            _tuples.AssignChunk(Fill(arena, nodes, start, end), start, selection, rowGroups);
        }

        Count = _tuples.Count;
        return false;
    }

    /// <summary>The tuples of rows <paramref name="start"/> to <paramref name="end"/>, from the scratch's start.</summary>
    private Span<KeyTuple> Fill(CanonicalArena arena, ReadOnlySpan<int> nodes, int start, int end)
    {
        Span<KeyTuple> block = _block.AsSpan(0, end - start);
        block.Clear();
        ref byte first = ref Unsafe.As<KeyTuple, byte>(ref MemoryMarshal.GetReference(block));
        for (int p = 0; p < _layout.Shapes.Length; p++)
        {
            int offset = _layout.Offsets[p];
            if (_layout.Text(p))
            {
                Texts(arena, nodes[p], start, end, ref first, offset, p);
                continue;
            }

            switch (_layout.Shapes[p].PType)
            {
                case PType.I8:
                    Integers<sbyte>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.U8:
                    Integers<byte>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.I16:
                    Integers<short>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.U16:
                    Integers<ushort>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.I32:
                    Integers<int>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.U32:
                    Integers<uint>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                case PType.I64:
                    Integers<long>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
                default:
                    Integers<ulong>(arena, nodes[p], start, end, ref first, offset, p);
                    break;
            }
        }

        return block;
    }

    /// <summary>
    /// A text column's words of rows <paramref name="start"/> to <paramref name="end"/> into the tuples at
    /// <paramref name="offset"/>: rows all short and none null at once, any others a row at a time.
    /// </summary>
    private void Texts(CanonicalArena arena, int node, int start, int end, ref byte first, int offset, int component)
    {
        BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);
        int rows = end - start;
        Scratch.Grow(ref _words, rows);
        Span<TextWord> words = _words.AsSpan(0, rows);
        if (validity.IsEmpty && values.TryWords(start, words))
        {
            for (int i = 0; i < rows; i++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, ((nint)i * TupleLayout.Bytes) + offset), words[i]);
            }

            return;
        }

        byte flag = (byte)(1 << component);
        for (int i = 0; i < rows; i++)
        {
            int row = start + i;
            ref byte tuple = ref Unsafe.Add(ref first, (nint)i * TupleLayout.Bytes);
            if (!StorageValues.IsValid(validity, row))
            {
                Unsafe.Add(ref tuple, TupleLayout.FlagsAt) |= flag;
                continue;
            }

            TextWord word = values.TryWord(row, out TextWord shortWord) ? shortWord : _layout.LongWord(values[row]);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref tuple, offset), word);
        }
    }

    /// <summary>An integer column's values of rows <paramref name="start"/> to <paramref name="end"/> into the tuples at <paramref name="offset"/>, its nulls flagged.</summary>
    private static void Integers<T>(CanonicalArena arena, int node, int start, int end, ref byte first, int offset, int component)
        where T : unmanaged
    {
        // A primitive column is read where it lies: no scratch.
        T[] none = [];
        ReadOnlySpan<T> values = FixedReader.Values(arena, node, StorageKind.Primitive, ref none, out ReadOnlySpan<ulong> validity)[start..end];
        if (validity.IsEmpty)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, ((nint)i * TupleLayout.Bytes) + offset), values[i]);
            }

            return;
        }

        byte flag = (byte)(1 << component);
        for (int i = 0; i < values.Length; i++)
        {
            ref byte tuple = ref Unsafe.Add(ref first, (nint)i * TupleLayout.Bytes);
            if (StorageValues.IsValid(validity, start + i))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref tuple, offset), values[i]);
            }
            else
            {
                Unsafe.Add(ref tuple, TupleLayout.FlagsAt) |= flag;
            }
        }
    }

    /// <summary>The bytes of group <paramref name="group"/>'s tuple, where it lies: a tuple copied to be read took a third of q10's output.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte TupleOf(int group) => ref Unsafe.As<KeyTuple, byte>(ref Unsafe.AsRef(in _tuples.KeyRef(group)));

    /// <summary>Whether column <paramref name="component"/> of a tuple is null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNull(ref byte tuple, int component) => ((Unsafe.Add(ref tuple, TupleLayout.FlagsAt) >> component) & 1) != 0;

    /// <summary>The value of a column of a tuple at byte <paramref name="offset"/>, as its bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(ref byte tuple, int offset)
        where T : unmanaged =>
        Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref tuple, offset));

    /// <summary>The bytes of text column <paramref name="component"/> of a tuple, through <paramref name="buffer"/> of 16 bytes.</summary>
    private ReadOnlySpan<byte> TextOf(ref byte tuple, int component, Span<byte> buffer) =>
        _layout.TextOf(Read<TextWord>(ref tuple, _layout.Offsets[component]), buffer);

    /// <summary>Column <paramref name="component"/> of two tuples, by its type: a text by its bytes, an integer by its value, a null last.</summary>
    private int Compare(ref byte left, ref byte right, int component)
    {
        bool leftNull = IsNull(ref left, component);
        bool rightNull = IsNull(ref right, component);
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        int offset = _layout.Offsets[component];
        if (_layout.Text(component))
        {
            Span<byte> a = stackalloc byte[16];
            Span<byte> b = stackalloc byte[16];
            return TextOf(ref left, component, a).SequenceCompareTo(TextOf(ref right, component, b));
        }

        return _layout.Shapes[component].PType switch
        {
            PType.I8 => Read<sbyte>(ref left, offset).CompareTo(Read<sbyte>(ref right, offset)),
            PType.U8 => Read<byte>(ref left, offset).CompareTo(Read<byte>(ref right, offset)),
            PType.I16 => Read<short>(ref left, offset).CompareTo(Read<short>(ref right, offset)),
            PType.U16 => Read<ushort>(ref left, offset).CompareTo(Read<ushort>(ref right, offset)),
            PType.I32 => Read<int>(ref left, offset).CompareTo(Read<int>(ref right, offset)),
            PType.U32 => Read<uint>(ref left, offset).CompareTo(Read<uint>(ref right, offset)),
            PType.I64 => Read<long>(ref left, offset).CompareTo(Read<long>(ref right, offset)),
            _ => Read<ulong>(ref left, offset).CompareTo(Read<ulong>(ref right, offset)),
        };
    }

    internal override GroupKeys Fresh() => Wrap(_tuples.Fresh());

    internal override GroupKeys ForPart() => Wrap(_tuples.ForPart());

    internal override GroupKeys ForTable(ArrayShelf shelf) => Wrap(_tuples.ForTable(shelf));

    internal override GroupKeys ForSpill(ArrayShelf? shelf) => Wrap(_tuples.ForSpill(shelf));

    internal override GroupKeys? Appending(ArrayShelf? shelf) => _tuples.Appending(shelf) is { } appending ? Wrap(appending) : null;

    internal override long Footprint => _tuples.Footprint + ((long)_block.Length * TupleLayout.Bytes) + ((long)_words.Length * TupleLayout.WordBytes);

    internal override long GrowthFor(int more) => _tuples.GrowthFor(more);

    internal override bool Squeeze(int more) => _tuples.Squeeze(more);

    internal override int NullNumber => _tuples.NullNumber;

    internal override bool Spills => _tuples.Spills;

    internal override void Release()
    {
        _tuples.Release();
        Count = 0;
        Scratch.Return(ref _block);
        Scratch.Return(ref _words);
    }

    internal override void Reserve(int groups) => _tuples.Reserve(groups);

    internal override void ReserveAllNew(int groups) => _tuples.ReserveAllNew(groups);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        _tuples.Keep(groups);
        Count = _tuples.Count;
        Renumbered();
    }

    internal override void Carry(ReadOnlySpan<int> groups)
    {
        _tuples.Carry(groups);
        Count = _tuples.Count;
        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        TupleKeys into = (TupleKeys)target;
        _tuples.MergeInto(into._tuples, groups, map);
        into.Count = into._tuples.Count;
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts) => _tuples.Parts(seed, shift, parts);

    internal override void Hashes(Span<ulong> hashes) => _tuples.Hashes(hashes);

    internal override void WriteKeys(ReadOnlySpan<int> groups, SpillBuffer buffer) => _tuples.WriteKeys(groups, buffer);

    internal override void ReadKeys(ref SpillReader reader, Span<int> groups)
    {
        _tuples.ReadKeys(ref reader, groups);
        Count = _tuples.Count;
    }

    internal override int EntryBytes => _tuples.EntryBytes;

    internal override void Scatter(ReadOnlySpan<ulong> records, LaneCore lane) => _tuples.Scatter(records, lane);

    internal override void CountParts(Span<int> counts, LaneCore lane) => _tuples.CountParts(counts, lane);

    internal override int CopyEntries(ReadOnlySpan<ulong> records, EntryShape shape, int from, Span<ulong> entries, out int written) =>
        _tuples.CopyEntries(records, shape, from, entries, out written);

    internal override void TablesOf(PartBatch batch, EntryShape shape, int shift, int mask, Span<int> tables) => _tuples.TablesOf(batch, shape, shift, mask, tables);

    internal override ulong HashAt(PartBatch batch, EntryShape shape, int entry) => _tuples.HashAt(batch, shape, entry);

    internal override void GroupsOf(PartBatch batch, EntryShape shape, ReadOnlySpan<int> entries, Span<int> groups, Span<ulong> scratch)
    {
        _tuples.GroupsOf(batch, shape, entries, groups, scratch);
        Count = _tuples.Count;
    }

    internal override int[] Order(bool sorted) => Identity(Count);

    internal override bool Orders(int component) => true;

    internal override int CompareKeys(int a, int b, int component) => Compare(ref TupleOf(a), ref TupleOf(b), component);

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) => Compare(ref TupleOf(a), ref ((TupleKeys)other).TupleOf(b), component);

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _layout.Shapes[component];
        int offset = _layout.Offsets[component];
        if (_layout.Text(component))
        {
            byte[] buffer = new byte[16];
            return group =>
            {
                ref byte tuple = ref TupleOf(group);
                return IsNull(ref tuple, component) ? default! : StorageValues.BytesToClr<T>(TextOf(ref tuple, component, buffer), shape);
            };
        }

        return shape.PType switch
        {
            PType.I8 => IntegerReader<sbyte, T>(component, offset, shape),
            PType.U8 => IntegerReader<byte, T>(component, offset, shape),
            PType.I16 => IntegerReader<short, T>(component, offset, shape),
            PType.U16 => IntegerReader<ushort, T>(component, offset, shape),
            PType.I32 => IntegerReader<int, T>(component, offset, shape),
            PType.U32 => IntegerReader<uint, T>(component, offset, shape),
            PType.I64 => IntegerReader<long, T>(component, offset, shape),
            _ => IntegerReader<ulong, T>(component, offset, shape),
        };
    }

    private Func<int, T> IntegerReader<TValue, T>(int component, int offset, ColumnShape shape)
        where TValue : unmanaged =>
        group =>
        {
            ref byte tuple = ref TupleOf(group);
            return IsNull(ref tuple, component) ? default! : StorageValues.ToClr<TValue, T>(Read<TValue>(ref tuple, offset), shape);
        };

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        int offset = _layout.Offsets[component];
        if (_layout.Text(component))
        {
            // The texts came from the column the reader checked as it decoded it, as a table of bytes appends
            // them: a short one its word, which is its view, a long one its bytes. The short ones written in
            // place up to the first null or long one, then one at a time from it.
            VarBinStore leaf = (VarBinStore)store.Leaf;
            int done = 0;
            Span<byte> views = leaf.InlineViews(groups.Length);
            ref byte view = ref MemoryMarshal.GetReference(views);
            for (; done < groups.Length; done++)
            {
                ref byte tuple = ref TupleOf(groups[done]);
                if (IsNull(ref tuple, component))
                {
                    break;
                }

                TextWord word = Read<TextWord>(ref tuple, offset);
                if (TupleLayout.IsLong(word))
                {
                    break;
                }

                ref byte at = ref Unsafe.Add(ref view, (nint)done * (2 * sizeof(ulong)));
                Unsafe.WriteUnaligned(ref at, word.Low);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, sizeof(ulong)), word.High);
            }

            leaf.AdvanceViews(done);
            foreach (int group in groups[done..])
            {
                ref byte tuple = ref TupleOf(group);
                if (IsNull(ref tuple, component))
                {
                    KeyStores.AppendNull(store);
                    continue;
                }

                TextWord word = Read<TextWord>(ref tuple, offset);
                if (TupleLayout.IsLong(word))
                {
                    leaf.AppendValidated(_layout.LongText(word));
                }
                else
                {
                    leaf.AppendInline(word.Low, word.High);
                }
            }

            return;
        }

        switch (_layout.Shapes[component].PType)
        {
            case PType.I8:
                AppendIntegers<sbyte>(component, offset, store, groups);
                break;
            case PType.U8:
                AppendIntegers<byte>(component, offset, store, groups);
                break;
            case PType.I16:
                AppendIntegers<short>(component, offset, store, groups);
                break;
            case PType.U16:
                AppendIntegers<ushort>(component, offset, store, groups);
                break;
            case PType.I32:
                AppendIntegers<int>(component, offset, store, groups);
                break;
            case PType.U32:
                AppendIntegers<uint>(component, offset, store, groups);
                break;
            case PType.I64:
                AppendIntegers<long>(component, offset, store, groups);
                break;
            default:
                AppendIntegers<ulong>(component, offset, store, groups);
                break;
        }
    }

    /// <summary>An integer column's values, read in place in their tuples: into the column's values at once when they are as wide, as <see cref="FixedKeys{TValue}"/> appends its own.</summary>
    private void AppendIntegers<TValue>(int component, int offset, ColumnStore store, ReadOnlySpan<int> groups)
        where TValue : unmanaged
    {
        FixedStore? leaf = store.Leaf is FixedStore fixedStore && fixedStore.Width == Unsafe.SizeOf<TValue>() ? fixedStore : null;

        // Into the values at once up to the first null, then one at a time from it.
        int done = 0;
        if (leaf is not null)
        {
            Span<TValue> into = leaf.GetSpan<TValue>(groups.Length);
            for (; done < groups.Length; done++)
            {
                ref byte tuple = ref TupleOf(groups[done]);
                if (IsNull(ref tuple, component))
                {
                    break;
                }

                into[done] = Read<TValue>(ref tuple, offset);
            }

            leaf.Advance(done);
        }

        foreach (int group in groups[done..])
        {
            ref byte tuple = ref TupleOf(group);
            if (IsNull(ref tuple, component))
            {
                KeyStores.AppendNull(store);
            }
            else if (leaf is not null)
            {
                leaf.Append(Read<TValue>(ref tuple, offset));
            }
            else
            {
                KeyStores.AppendFixed(store, Read<TValue>(ref tuple, offset), StorageKind.Primitive);
            }
        }
    }
}
