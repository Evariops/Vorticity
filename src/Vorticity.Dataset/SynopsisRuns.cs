using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>How a synopsis key's values lie in the records of a run, and how two of them compare.</summary>
internal enum KeyForm : byte
{
    Signed,
    Unsigned,
    Float,
    Bool,
    Bytes,
}

/// <summary>How a measure's states are kept: an exact signed or unsigned integer sum, or a float sum.</summary>
internal enum MeasureKind : byte
{
    Signed,
    Unsigned,
    Float,
}

/// <summary>A key's form, the width of a fixed one, and the primitive it is stored as.</summary>
internal readonly record struct KeyShape(KeyForm Form, int Width, PType PType)
{
    /// <summary>The shape of the values of <paramref name="dtype"/>, its storage's: a primitive of a width, a boolean, or bytes.</summary>
    internal static KeyShape Of(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype.Kind switch
        {
            DTypeKind.Primitive => new KeyShape(
                dtype.PType.IsFloat() ? KeyForm.Float : dtype.PType.IsUnsignedInteger() ? KeyForm.Unsigned : KeyForm.Signed, dtype.PType.ByteWidth(), dtype.PType),
            DTypeKind.Bool => new KeyShape(KeyForm.Bool, 1, default),
            _ => new KeyShape(KeyForm.Bytes, 0, default),
        };
    }
}

/// <summary>
/// The keys of a run's records: each component a byte that says whether it holds a value, then the value, a
/// primitive's bytes in its width, a boolean's byte, or bytes behind their length. They compare as a slice orders
/// its rows (<see cref="SliceSort.Compare"/>): by value, nulls last, a float's zeros of either sign one value and
/// its NaNs another, so that two keys compare equal exactly when a slice codes them alike, whatever their bits.
/// </summary>
internal static class KeyCodec
{
    /// <summary>Appends the value of row <paramref name="row"/> of canonical column <paramref name="column"/>, of <paramref name="shape"/>.</summary>
    internal static void Append(RunBuffer into, CanonicalArena arena, int column, int row, KeyShape shape)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(column);
        if (record.Kind == CanonicalKind.Null || !ArenaWords.IsValid(arena, column, row))
        {
            into.Byte(0);
            return;
        }

        into.Byte(1);
        switch (shape.Form)
        {
            case KeyForm.Bool:
                into.Byte(((ArenaWords.Bits(arena, column)[row >> 6] >> (row & 63)) & 1) != 0 ? (byte)1 : (byte)0);
                break;
            case KeyForm.Bytes:
                ReadOnlySpan<byte> bytes = ColumnData.Bytes(arena, column, row);
                into.Varint((uint)bytes.Length);
                into.Bytes(bytes);
                break;
            default:
                into.Bytes(record.BufferA.Span.Slice(row * shape.Width, shape.Width));
                break;
        }
    }

    /// <summary>Reads the component at <paramref name="at"/>: whether it holds a value, the value's bytes, and where the next starts.</summary>
    internal static int Read(ReadOnlySpan<byte> key, int at, KeyShape shape, out bool valid, out ReadOnlySpan<byte> payload)
    {
        valid = key[at++] != 0;
        if (!valid)
        {
            payload = default;
            return at;
        }

        int length = shape.Form switch
        {
            KeyForm.Bool => 1,
            KeyForm.Bytes => (int)ReadVarint(key, ref at),
            _ => shape.Width,
        };
        payload = key.Slice(at, length);
        return at + length;
    }

    /// <summary>
    /// How keys <paramref name="left"/> and <paramref name="right"/> compare over their first <paramref name="components"/>
    /// components, and the first component they differ at, <paramref name="components"/> when they agree.
    /// </summary>
    internal static int Compare(ReadOnlySpan<KeyShape> shapes, int components, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, out int differs)
    {
        // A component whose bytes the two keys share is the same component in both, its encoding delimiting itself, and so
        // equal: the components the keys' common prefix holds whole are stepped over by their lengths alone, and only the
        // one the first byte that differs lies in is read as values. A merge's keys mostly share all but their last.
        int common = left.CommonPrefixLength(right);
        int l = 0;
        int k = 0;
        for (; k < components; k++)
        {
            int end = Skip(left, l, shapes[k]);
            if (end > common)
            {
                break;
            }

            l = end;
        }

        int r = l;
        for (; k < components; k++)
        {
            l = Read(left, l, shapes[k], out bool leftValid, out ReadOnlySpan<byte> leftValue);
            r = Read(right, r, shapes[k], out bool rightValid, out ReadOnlySpan<byte> rightValue);
            int compared = Compare(shapes[k], leftValid, leftValue, rightValid, rightValue);
            if (compared != 0)
            {
                differs = k;
                return compared;
            }
        }

        differs = components;
        return 0;
    }

    /// <summary>Where the component at <paramref name="at"/> of <paramref name="key"/> ends, its value not read.</summary>
    private static int Skip(ReadOnlySpan<byte> key, int at, KeyShape shape)
    {
        if (key[at++] == 0)
        {
            return at;
        }

        return shape.Form switch
        {
            KeyForm.Bool => at + 1,
            KeyForm.Bytes => SkipBytes(key, at),
            _ => at + shape.Width,
        };

        static int SkipBytes(ReadOnlySpan<byte> key, int at)
        {
            uint length = ReadVarint(key, ref at);
            return at + (int)length;
        }
    }

    /// <summary>The bytes the first <paramref name="components"/> components of <paramref name="key"/> take.</summary>
    internal static int Prefix(ReadOnlySpan<KeyShape> shapes, int components, ReadOnlySpan<byte> key)
    {
        int at = 0;
        for (int k = 0; k < components; k++)
        {
            at = Read(key, at, shapes[k], out _, out _);
        }

        return at;
    }

    private static int Compare(KeyShape shape, bool leftValid, ReadOnlySpan<byte> left, bool rightValid, ReadOnlySpan<byte> right)
    {
        if (!leftValid || !rightValid)
        {
            return leftValid == rightValid ? 0 : leftValid ? -1 : 1;
        }

        switch (shape.Form)
        {
            case KeyForm.Signed:
                return Signed(left).CompareTo(Signed(right));
            case KeyForm.Unsigned:
                return Unsigned(left).CompareTo(Unsigned(right));
            case KeyForm.Float:
                double a = Float(left, shape.PType);
                double b = Float(right, shape.PType);
                return (a == 0 ? 0 : a).CompareTo(b == 0 ? 0 : b);
            case KeyForm.Bool:
                return left[0].CompareTo(right[0]);
            default:
                return left.SequenceCompareTo(right);
        }
    }

    private static long Signed(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => (sbyte)value[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadInt64LittleEndian(value),
    };

    private static ulong Unsigned(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(value),
    };

    private static double Float(ReadOnlySpan<byte> value, PType ptype) => ptype switch
    {
        PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(value),
        PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(value),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(value),
    };

    private static uint ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        uint value = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[at++];
            value |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }

            shift += 7;
        }
    }
}

/// <summary>
/// The states of a group as the words a run's record holds them in, the order a table's columns take: a count of
/// rows, then per measure its count of values, its exact sum's words (five for a float, two for an integer), its
/// least and its greatest, a float's extremes NaN while it has seen none but NaN.
/// </summary>
internal static class StateWords
{
    private static readonly long NoFloat = BitConverter.DoubleToInt64Bits(double.NaN);

    /// <summary>The words a group's states take.</summary>
    internal static int Count(ReadOnlySpan<MeasureKind> kinds)
    {
        int words = 1;
        foreach (MeasureKind kind in kinds)
        {
            words += kind == MeasureKind.Float ? 8 : 5;
        }

        return words;
    }

    /// <summary>The states of a group of no row.</summary>
    internal static void Clear(Span<long> words, ReadOnlySpan<MeasureKind> kinds)
    {
        words.Clear();
        int at = 1;
        foreach (MeasureKind kind in kinds)
        {
            if (kind == MeasureKind.Float)
            {
                words[at + 6] = NoFloat;
                words[at + 7] = NoFloat;
                at += 8;
            }
            else
            {
                at += 5;
            }
        }
    }

    /// <summary>Merges the states <paramref name="from"/> into <paramref name="into"/>, as two groups' rows together.</summary>
    internal static void Merge(Span<long> into, ReadOnlySpan<long> from, ReadOnlySpan<MeasureKind> kinds)
    {
        into[0] += from[0];
        int at = 1;
        foreach (MeasureKind kind in kinds)
        {
            if (kind == MeasureKind.Float)
            {
                into[at] += from[at];
                WideIndexedSum sum = Sum(into, at + 1);
                WideIndexedSum other = Sum(from, at + 1);
                sum.Merge(in other);
                (into[at + 1], into[at + 2], into[at + 3], into[at + 4], into[at + 5]) = (sum.Sum.M0, sum.Sum.M1, sum.Sum.M2, sum.Sum.Meta, sum.High);
                double low = BitConverter.Int64BitsToDouble(from[at + 6]);
                double high = BitConverter.Int64BitsToDouble(from[at + 7]);
                double intoLow = BitConverter.Int64BitsToDouble(into[at + 6]);
                double intoHigh = BitConverter.Int64BitsToDouble(into[at + 7]);
                into[at + 6] = !double.IsNaN(low) && (double.IsNaN(intoLow) || low < intoLow) ? from[at + 6] : into[at + 6];
                into[at + 7] = !double.IsNaN(high) && (double.IsNaN(intoHigh) || high > intoHigh) ? from[at + 7] : into[at + 7];
                at += 8;
                continue;
            }

            if (from[at] != 0)
            {
                bool none = into[at] == 0;
                into[at] += from[at];
                Int128 total = new Int128(unchecked((ulong)into[at + 2]), unchecked((ulong)into[at + 1]))
                    + new Int128(unchecked((ulong)from[at + 2]), unchecked((ulong)from[at + 1]));
                into[at + 1] = unchecked((long)(ulong)total);
                into[at + 2] = (long)(total >> 64);
                bool unsigned = kind == MeasureKind.Unsigned;
                long least = from[at + 3];
                long greatest = from[at + 4];
                into[at + 3] = none || (unsigned ? (ulong)least < (ulong)into[at + 3] : least < into[at + 3]) ? least : into[at + 3];
                into[at + 4] = none || (unsigned ? (ulong)greatest > (ulong)into[at + 4] : greatest > into[at + 4]) ? greatest : into[at + 4];
            }

            at += 5;
        }
    }

    private static WideIndexedSum Sum(ReadOnlySpan<long> words, int at)
    {
        WideIndexedSum sum = default;
        sum.Sum.M0 = words[at];
        sum.Sum.M1 = words[at + 1];
        sum.Sum.M2 = words[at + 2];
        sum.Sum.Meta = words[at + 3];
        sum.High = words[at + 4];
        return sum;
    }
}

/// <summary>A growing buffer of bytes from the shared pool, the records of a run before it goes to its scratch store.</summary>
internal sealed class RunBuffer : IDisposable
{
    private byte[] _bytes = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int _length;

    internal int Length => _length;

    internal ReadOnlySpan<byte> Span => _bytes.AsSpan(0, _length);

    internal ReadOnlyMemory<byte> Memory => _bytes.AsMemory(0, _length);

    internal void Clear() => _length = 0;

    internal void Byte(byte value) => Grow(1)[0] = value;

    internal void Bytes(ReadOnlySpan<byte> value) => value.CopyTo(Grow(value.Length));

    internal void Words(ReadOnlySpan<long> words) => MemoryMarshal.AsBytes(words).CopyTo(Grow(words.Length * sizeof(long)));

    internal void Varint(uint value)
    {
        Span<byte> into = Grow(5);
        int written = 0;
        while (value >= 0x80)
        {
            into[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        into[written++] = (byte)value;
        _length -= 5 - written;
    }

    /// <summary>Drops the bytes past <paramref name="length"/>.</summary>
    internal void Truncate(int length) => _length = length;

    private Span<byte> Grow(int bytes)
    {
        if (_length + bytes > _bytes.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_bytes.Length * 2, _length + bytes));
            _bytes.AsSpan(0, _length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = larger;
        }

        Span<byte> span = _bytes.AsSpan(_length, bytes);
        _length += bytes;
        return span;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_bytes);
        _bytes = [];
    }
}

/// <summary>
/// The keys of a slice's groups at each depth, each encoded once, in the order of the groups' first rows: a group's key is
/// its parent's, then its first row's value of the depth's column, so that a depth costs a component a group, where the key
/// of a group read again from its row cost every component before it, seven for the design's deepest groups. The keys
/// are those <see cref="KeyCodec.Append"/> gives a row component by component, and stay until the next slice's.
/// </summary>
internal sealed class SliceKeys : IDisposable
{
    private RunBuffer[] _bytes = [];

    // Each depth's groups' keys: where each ends in the depth's bytes, and where its own component starts in it.
    private int[][] _ends = [];
    private int[][] _components = [];

    /// <summary>
    /// The keys of the groups of depths 0 to <paramref name="deepest"/>: <paramref name="counts"/> groups at each, the first
    /// row of each in <paramref name="firsts"/>, each group's parent at the depth above in <paramref name="parents"/>, the
    /// depth's values in <paramref name="columns"/> of <paramref name="arena"/>, of <paramref name="shapes"/>.
    /// </summary>
    internal void Build(CanonicalArena arena, int[] columns, KeyShape[] shapes, int[] counts, int[][] firsts, int[][] parents, int deepest)
    {
        Grow(deepest, counts);
        _bytes[0].Clear();
        _ends[0][0] = 0;
        _components[0][0] = 0;
        for (int d = 1; d <= deepest; d++)
        {
            RunBuffer into = _bytes[d];
            into.Clear();
            ReadOnlySpan<byte> above = _bytes[d - 1].Span;
            int[] parentEnds = _ends[d - 1];
            int[] ends = _ends[d];
            int[] components = _components[d];
            int[] first = firsts[d];
            int[] parentOf = parents[d];
            for (int g = 0; g < counts[d]; g++)
            {
                int row = first[g];
                int parent = parentOf[g];
                int from = parent == 0 ? 0 : parentEnds[parent - 1];
                into.Bytes(above[from..parentEnds[parent]]);
                components[g] = into.Length;
                KeyCodec.Append(into, arena, columns[d - 1], row, shapes[d - 1]);
                ends[g] = into.Length;
            }
        }
    }

    /// <summary>The key of group <paramref name="group"/> of depth <paramref name="depth"/>: its components down to the depth.</summary>
    internal ReadOnlySpan<byte> Of(int depth, int group) =>
        _bytes[depth].Span[(group == 0 ? 0 : _ends[depth][group - 1]).._ends[depth][group]];

    /// <summary>The last component of group <paramref name="group"/>'s key at depth <paramref name="depth"/>, past 0: its own column's value.</summary>
    internal ReadOnlySpan<byte> Component(int depth, int group) =>
        _bytes[depth].Span[_components[depth][group].._ends[depth][group]];

    private void Grow(int deepest, int[] counts)
    {
        if (_bytes.Length <= deepest)
        {
            int had = _bytes.Length;
            Array.Resize(ref _bytes, deepest + 1);
            Array.Resize(ref _ends, deepest + 1);
            Array.Resize(ref _components, deepest + 1);
            for (int d = had; d <= deepest; d++)
            {
                _bytes[d] = new RunBuffer();
                _ends[d] = [];
                _components[d] = [];
            }
        }

        for (int d = 0; d <= deepest; d++)
        {
            if (_ends[d].Length < counts[d])
            {
                _ends[d] = new int[Math.Max(counts[d], _ends[d].Length * 2)];
                _components[d] = new int[_ends[d].Length];
            }
        }
    }

    public void Dispose()
    {
        foreach (RunBuffer bytes in _bytes)
        {
            bytes.Dispose();
        }

        _bytes = [];
    }
}

/// <summary>A run of records in a scratch store: where it starts, its bytes, and how many components its keys hold.</summary>
internal readonly record struct RunSpan(long At, long Length, int Components);

/// <summary>
/// Reads a run's records in order from its scratch store, a block at a time: each record's key, cut to the first
/// <c>components</c> of the components it holds, and its states' words.
/// </summary>
internal sealed class RunCursor : IDisposable
{
    private const int BlockBytes = 1 << 18;

    private readonly RunScratch _scratch;
    private readonly KeyShape[] _shapes;
    private readonly int _components;
    private readonly int _stored;
    private readonly int _words;
    private readonly long _end;
    private long _next;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BlockBytes);
    private int _position;
    private int _filled;
    private int _record;
    private int _keyEnd;
    private int _wordsAt;

    internal RunCursor(RunScratch scratch, RunSpan run, KeyShape[] shapes, int components, int words)
    {
        _scratch = scratch;
        _shapes = shapes;
        _components = Math.Min(components, run.Components);
        _stored = run.Components;
        _words = words;
        _next = run.At;
        _end = run.At + run.Length;
    }

    /// <summary>The current record's key, cut to the components read.</summary>
    internal ReadOnlySpan<byte> Key => _buffer.AsSpan(_record, _keyEnd - _record);

    /// <summary>The current record's states.</summary>
    internal ReadOnlySpan<long> Words => MemoryMarshal.Cast<byte, long>(_buffer.AsSpan(_wordsAt, _words * sizeof(long)));

    /// <summary>Moves to the next record; false past the run's last.</summary>
    internal async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryParse())
            {
                return true;
            }

            if (_next >= _end)
            {
                return false;
            }

            // The bytes of a record cut at the block's end move to the buffer's start, and the next block follows them.
            int left = _filled - _position;
            if (left == _buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                _buffer.AsSpan(_position, left).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
            }
            else
            {
                _buffer.AsSpan(_position, left).CopyTo(_buffer);
            }

            _position = 0;
            _filled = left;
            int read = (int)Math.Min(_buffer.Length - left, _end - _next);
            await _scratch.ReadAsync(_next, _buffer.AsMemory(left, read), cancellationToken).ConfigureAwait(false);
            _next += read;
            _filled += read;
        }
    }

    /// <summary>Parses the record at the position when the buffer holds it whole.</summary>
    private bool TryParse()
    {
        ReadOnlySpan<byte> held = _buffer.AsSpan(_position, _filled - _position);
        int at = 0;
        int keyEnd = 0;
        for (int k = 0; k < _stored; k++)
        {
            if (!Holds(held, at, _shapes[k], out int next))
            {
                return false;
            }

            at = next;
            if (k + 1 == _components)
            {
                keyEnd = at;
            }
        }

        if (at + (_words * sizeof(long)) > held.Length)
        {
            return false;
        }

        _record = _position;
        _keyEnd = _position + (_components == 0 ? 0 : keyEnd);
        _wordsAt = _position + at;
        _position += at + (_words * sizeof(long));
        return true;
    }

    /// <summary>Whether <paramref name="held"/> holds the whole component at <paramref name="at"/>, and where the next starts.</summary>
    private static bool Holds(ReadOnlySpan<byte> held, int at, KeyShape shape, out int next)
    {
        next = at;
        if (at >= held.Length)
        {
            return false;
        }

        if (held[at] == 0)
        {
            next = at + 1;
            return true;
        }

        at++;
        int length;
        switch (shape.Form)
        {
            case KeyForm.Bool:
                length = 1;
                break;
            case KeyForm.Bytes:
                uint value = 0;
                int shift = 0;
                while (true)
                {
                    if (at >= held.Length)
                    {
                        return false;
                    }

                    byte b = held[at++];
                    value |= (uint)(b & 0x7F) << shift;
                    if (b < 0x80)
                    {
                        break;
                    }

                    shift += 7;
                }

                length = (int)value;
                break;
            default:
                length = shape.Width;
                break;
        }

        next = at + length;
        return next <= held.Length;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}

/// <summary>
/// The records of several runs, each in the order of its keys, merged into one stream in that order: the records
/// of equal keys merged into one, their states together, under the key of the earliest run's, as the first row of a
/// group gives the slice's. A tree of losers over the runs' cursors, by their current key, then by their run's place:
/// a record costs a comparison a level of the tree, where a heap's took two to take its least and more to put the
/// next one back.
/// </summary>
internal sealed class RunMerger : IDisposable
{
    private readonly KeyShape[] _shapes;
    private readonly int _components;
    private readonly MeasureKind[] _kinds;
    private readonly RunBuffer _key = new RunBuffer();
    private readonly long[] _words;

    // Each run's cursor by its place, null once the run is read; and the tree: the place of the run whose record is
    // least at its root, 0, and at each node past it the place of the run that lost there.
    private readonly RunCursor?[] _cursors;
    private readonly int[] _tree;

    private RunMerger(KeyShape[] shapes, int components, MeasureKind[] kinds, int words, int runs)
    {
        _shapes = shapes;
        _components = components;
        _kinds = kinds;
        _words = new long[words];
        _cursors = new RunCursor?[runs];
        _tree = new int[Math.Max(runs, 1)];
    }

    /// <summary>A merge of <paramref name="runs"/> of <paramref name="scratch"/>, their keys cut to <paramref name="components"/>.</summary>
    internal static async ValueTask<RunMerger> OpenAsync(
        RunScratch scratch, IReadOnlyList<RunSpan> runs, KeyShape[] shapes, int components, MeasureKind[] kinds, CancellationToken cancellationToken)
    {
        RunMerger merger = new RunMerger(shapes, components, kinds, StateWords.Count(kinds), runs.Count);
        try
        {
            for (int r = 0; r < runs.Count; r++)
            {
                RunCursor cursor = new RunCursor(scratch, runs[r], shapes, components, merger._words.Length);
                if (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    merger._cursors[r] = cursor;
                }
                else
                {
                    cursor.Dispose();
                }
            }

            merger.Build();
            return merger;
        }
        catch
        {
            merger.Dispose();
            throw;
        }
    }

    /// <summary>The current group's key.</summary>
    internal ReadOnlySpan<byte> Key => _key.Span;

    /// <summary>The current group's states.</summary>
    internal ReadOnlySpan<long> Words => _words;

    /// <summary>Moves to the next group of the merged stream; false past the last.</summary>
    internal async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken)
    {
        if (_cursors.Length == 0 || _cursors[_tree[0]] is not { } first)
        {
            return false;
        }

        _key.Clear();
        _key.Bytes(first.Key);
        first.Words.CopyTo(_words);
        await AdvanceAsync(_tree[0], cancellationToken).ConfigureAwait(false);
        while (_cursors[_tree[0]] is { } same && KeyCodec.Compare(_shapes, _components, same.Key, _key.Span, out _) == 0)
        {
            StateWords.Merge(_words, same.Words, _kinds);
            await AdvanceAsync(_tree[0], cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Moves run <paramref name="place"/>'s cursor past its record, and plays its next one up the tree.</summary>
    private async ValueTask AdvanceAsync(int place, CancellationToken cancellationToken)
    {
        RunCursor cursor = _cursors[place]!;
        if (!await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            cursor.Dispose();
            _cursors[place] = null;
        }

        Replay(place);
    }

    /// <summary>The tree played from every run's first record: a node keeps the first to reach it until the second does.</summary>
    private void Build()
    {
        Array.Fill(_tree, -1);
        for (int place = _cursors.Length - 1; place >= 0; place--)
        {
            int winner = place;
            int node = (place + _cursors.Length) >> 1;
            for (; node > 0; node >>= 1)
            {
                if (_tree[node] < 0)
                {
                    _tree[node] = winner;
                    break;
                }

                if (Less(_tree[node], winner))
                {
                    (_tree[node], winner) = (winner, _tree[node]);
                }
            }

            if (node == 0)
            {
                _tree[0] = winner;
            }
        }
    }

    /// <summary>Plays run <paramref name="place"/>'s record from its leaf to the root against the losers on the way.</summary>
    private void Replay(int place)
    {
        int winner = place;
        for (int node = (place + _cursors.Length) >> 1; node > 0; node >>= 1)
        {
            int loser = _tree[node];
            if (Less(loser, winner))
            {
                _tree[node] = winner;
                winner = loser;
            }
        }

        _tree[0] = winner;
    }

    /// <summary>Whether run <paramref name="left"/>'s record comes before run <paramref name="right"/>'s: a run read comes last.</summary>
    private bool Less(int left, int right)
    {
        RunCursor? a = _cursors[left];
        RunCursor? b = _cursors[right];
        if (a is null || b is null)
        {
            return a is not null || (b is null && left < right);
        }

        int compared = KeyCodec.Compare(_shapes, _components, a.Key, b.Key, out _);
        return compared != 0 ? compared < 0 : left < right;
    }

    public void Dispose()
    {
        for (int r = 0; r < _cursors.Length; r++)
        {
            _cursors[r]?.Dispose();
            _cursors[r] = null;
        }

        _key.Dispose();
    }
}

/// <summary>
/// The rows of a chunk of a synopsis table as they come, in the order of their buckets and keys: each row's bucket,
/// its keys and its states, and for a table of sketches each measure's sketch, held column by column in arrays of its
/// own, reused from chunk to chunk, until the chunk is built into an arena and handed to the writer.
/// </summary>
internal sealed class ChunkStage
{
    /// <summary>How a sketch lies in a row: bytes, as a key of bytes does.</summary>
    private static readonly KeyShape Sketched = new KeyShape(KeyForm.Bytes, 0, default);

    private readonly KeyShape[] _shapes;
    private readonly int _words;
    private readonly KeyColumn[] _keys;
    private readonly KeyColumn[] _sketches;
    private long[] _buckets = new long[64];
    private long[][] _states;
    private int _rows;

    /// <summary>
    /// A stage of rows of keys of <paramref name="shapes"/> and the words <paramref name="words"/> names of each record's
    /// states, then <paramref name="sketches"/> sketches.
    /// </summary>
    internal ChunkStage(KeyShape[] shapes, int[] words, int sketches = 0)
    {
        _shapes = shapes;
        _wordsKept = words;
        _words = words.Length;
        _keys = new KeyColumn[shapes.Length];
        for (int k = 0; k < shapes.Length; k++)
        {
            _keys[k] = new KeyColumn(shapes[k]);
        }

        _sketches = new KeyColumn[sketches];
        for (int s = 0; s < sketches; s++)
        {
            _sketches[s] = new KeyColumn(Sketched);
        }

        _states = new long[_words][];
        for (int w = 0; w < _words; w++)
        {
            _states[w] = new long[64];
        }
    }

    // The words of a record's states the table keeps: its count, and the states of its measures.
    private readonly int[] _wordsKept;

    /// <summary>The rows held.</summary>
    internal int Rows => _rows;

    /// <summary>The bytes the rows held take before they are encoded: their buckets, their states, their keys' values and their sketches.</summary>
    internal long RawBytes
    {
        get
        {
            long bytes = (long)_rows * sizeof(long) * (1 + _words);
            foreach (KeyColumn key in _keys)
            {
                bytes += key.ValueBytes;
            }

            foreach (KeyColumn sketch in _sketches)
            {
                bytes += sketch.ValueBytes;
            }

            return bytes;
        }
    }

    /// <summary>The bucket of the first row held, and of the last.</summary>
    internal long FirstBucket => _buckets[0];

    internal long LastBucket => _buckets[_rows - 1];

    /// <summary>Holds a row: its bucket, its key's components, encoded as a run's records hold them, and its states.</summary>
    internal void Append(long bucket, ReadOnlySpan<byte> key, ReadOnlySpan<long> words)
    {
        if (_rows == _buckets.Length)
        {
            int capacity = _rows * 2;
            Array.Resize(ref _buckets, capacity);
            for (int w = 0; w < _words; w++)
            {
                Array.Resize(ref _states[w], capacity);
            }
        }

        _buckets[_rows] = bucket;
        int at = 0;
        for (int k = 0; k < _keys.Length; k++)
        {
            at = KeyCodec.Read(key, at, _shapes[k], out bool valid, out ReadOnlySpan<byte> value);
            _keys[k].Add(_rows, valid, value);
        }

        for (int w = 0; w < _words; w++)
        {
            _states[w][_rows] = words[_wordsKept[w]];
        }

        _rows++;
    }

    /// <summary>Holds sketch <paramref name="sketch"/> of the last row appended: its bytes (<see cref="QuantileSketch.Encode"/>).</summary>
    internal void Sketch(int sketch, ReadOnlySpan<byte> bytes) => _sketches[sketch].Add(_rows - 1, valid: true, bytes);

    /// <summary>The rows held as a struct of <paramref name="rowType"/> in <paramref name="arena"/>: the bucket, the keys, the states, the sketches.</summary>
    internal int Build(CanonicalArena arena, DType rowType, DTypeArena types)
    {
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        int[] fields = new int[1 + _keys.Length + _words + _sketches.Length];
        fields[0] = Longs(arena, longs, _buckets.AsSpan(0, _rows));
        for (int k = 0; k < _keys.Length; k++)
        {
            fields[1 + k] = _keys[k].Build(arena, rowType.GetField(1 + k), types, _rows);
        }

        for (int w = 0; w < _words; w++)
        {
            fields[1 + _keys.Length + w] = Longs(arena, longs, _states[w].AsSpan(0, _rows));
        }

        for (int s = 0; s < _sketches.Length; s++)
        {
            int field = 1 + _keys.Length + _words + s;
            fields[field] = _sketches[s].Build(arena, rowType.GetField(field), types, _rows);
        }

        return arena.AddStruct(rowType, _rows, Validity.NonNullable, fields);
    }

    /// <summary>Forgets the rows held, keeping the arrays.</summary>
    internal void Clear()
    {
        _rows = 0;
        foreach (KeyColumn key in _keys)
        {
            key.Clear();
        }

        foreach (KeyColumn sketch in _sketches)
        {
            sketch.Clear();
        }
    }

    /// <summary>Forgets the rows held and gives up the arrays grown for them, for a table that holds no more.</summary>
    internal void Release()
    {
        _rows = 0;
        _buckets = new long[64];
        for (int w = 0; w < _words; w++)
        {
            _states[w] = new long[64];
        }

        for (int k = 0; k < _keys.Length; k++)
        {
            _keys[k] = new KeyColumn(_shapes[k]);
        }

        for (int s = 0; s < _sketches.Length; s++)
        {
            _sketches[s] = new KeyColumn(Sketched);
        }
    }

    private static int Longs(CanonicalArena arena, DType longs, ReadOnlySpan<long> values)
    {
        VortexBuffer buffer = arena.Allocate(Math.Max(values.Length * sizeof(long), sizeof(long)), 64, out Span<byte> into).Slice(0, values.Length * sizeof(long));
        MemoryMarshal.AsBytes(values).CopyTo(into);
        return arena.AddPrimitive(longs, values.Length, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>A key column as its rows come: their values, fixed or of bytes, and which hold one.</summary>
    private sealed class KeyColumn(KeyShape shape)
    {
        private byte[] _values = new byte[64];
        private int _valueBytes;
        private int[] _ends = new int[64];
        private ulong[] _valid = new ulong[1];
        private bool _anyNull;

        /// <summary>The bytes of the values held.</summary>
        internal int ValueBytes => _valueBytes;

        internal void Add(int row, bool valid, ReadOnlySpan<byte> value)
        {
            if ((row >> 6) >= _valid.Length)
            {
                Array.Resize(ref _valid, _valid.Length * 2);
            }

            if ((row & 63) == 0)
            {
                _valid[row >> 6] = 0;
            }

            _anyNull |= !valid;
            _valid[row >> 6] |= valid ? 1UL << (row & 63) : 0;
            int width = shape.Form == KeyForm.Bytes ? value.Length : shape.Width;
            if (_valueBytes + width > _values.Length)
            {
                Array.Resize(ref _values, Math.Max(_values.Length * 2, _valueBytes + width));
            }

            Span<byte> into = _values.AsSpan(_valueBytes, width);
            if (valid)
            {
                value.CopyTo(into);
            }
            else
            {
                into.Clear();
            }

            _valueBytes += width;
            if (shape.Form == KeyForm.Bytes)
            {
                if (row == _ends.Length)
                {
                    Array.Resize(ref _ends, _ends.Length * 2);
                }

                _ends[row] = _valueBytes;
            }
        }

        internal void Clear()
        {
            _valueBytes = 0;
            _anyNull = false;
        }

        /// <summary>The column of <paramref name="rows"/> rows at <paramref name="dtype"/>: its storage, under the extension it stores.</summary>
        internal int Build(CanonicalArena arena, DType dtype, DTypeArena types, int rows)
        {
            DType storage = dtype;
            while (storage.Kind == DTypeKind.Extension)
            {
                storage = storage.StorageType;
            }

            Validity validity = !storage.IsNullable ? Validity.NonNullable
                : !_anyNull ? Validity.AllValid
                : Validity.Bitmap(Bits(arena, types, MemoryMarshal.AsBytes(_valid.AsSpan(0, (rows + 63) >> 6)), rows));
            int node;
            switch (shape.Form)
            {
                case KeyForm.Bool:
                {
                    VortexBuffer bits = arena.Allocate(Math.Max(((rows + 63) >> 6) * sizeof(ulong), sizeof(ulong)), sizeof(ulong), out Span<byte> into);
                    Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(into);
                    for (int r = 0; r < rows; r++)
                    {
                        words[r >> 6] |= _values[r] != 0 ? 1UL << (r & 63) : 0;
                    }

                    node = arena.AddBool(storage, rows, validity, bits, 0);
                    break;
                }

                case KeyForm.Bytes:
                {
                    VortexBuffer views = arena.Allocate(Math.Max(rows * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize), CanonicalSupport.ViewSize, out Span<byte> viewBytes);
                    VortexBuffer data = arena.AllocateUninitialized(Math.Max(_valueBytes, 1), 1, out Span<byte> heap);
                    _values.AsSpan(0, _valueBytes).CopyTo(heap);
                    bool outOfLine = false;
                    int start = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        int end = _ends[r];
                        ReadOnlySpan<byte> value = _values.AsSpan(start, end - start);
                        Span<byte> view = viewBytes.Slice(r * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
                        if (value.Length <= CanonicalSupport.MaxInlineViewLength)
                        {
                            CanonicalSupport.WriteInlineView(view, value);
                        }
                        else
                        {
                            CanonicalSupport.WriteReferenceView(view, value.Length, value, bufferIndex: 0, offset: start);
                            outOfLine = true;
                        }

                        start = end;
                    }

                    Span<VortexBuffer> buffers = stackalloc VortexBuffer[1];
                    buffers[0] = data;
                    node = arena.AddVarBinView(storage, rows, validity, views, outOfLine ? buffers : default);
                    break;
                }

                default:
                {
                    VortexBuffer values = arena.AllocateUninitialized(Math.Max(_valueBytes, shape.Width), 64, out Span<byte> into);
                    _values.AsSpan(0, _valueBytes).CopyTo(into);
                    node = arena.AddPrimitive(storage, rows, validity, shape.PType, values.Slice(0, _valueBytes));
                    break;
                }
            }

            return dtype.Kind == DTypeKind.Extension ? arena.AddExtension(dtype, rows, node) : node;
        }

        private static int Bits(CanonicalArena arena, DTypeArena types, ReadOnlySpan<byte> bits, int rows)
        {
            VortexBuffer buffer = arena.AllocateUninitialized(Math.Max(bits.Length, sizeof(ulong)), sizeof(ulong), out Span<byte> into);
            into.Clear();
            bits.CopyTo(into);
            return arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, buffer, 0);
        }
    }
}
