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
        int l = 0;
        int r = 0;
        for (int k = 0; k < components; k++)
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

    internal RunCursor(RunScratch scratch, RunSpan run, KeyShape[] shapes, int components, int words, int order = 0)
    {
        Order = order;
        _scratch = scratch;
        _shapes = shapes;
        _components = Math.Min(components, run.Components);
        _stored = run.Components;
        _words = words;
        _next = run.At;
        _end = run.At + run.Length;
    }

    /// <summary>The run's place among those a merge reads, which breaks its ties: the earliest run's record first.</summary>
    internal int Order { get; }

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
/// group gives the slice's. A heap of the runs' cursors, by their current key, then by their run's place.
/// </summary>
internal sealed class RunMerger : IDisposable
{
    private readonly KeyShape[] _shapes;
    private readonly int _components;
    private readonly MeasureKind[] _kinds;
    private readonly List<RunCursor> _heap = [];
    private readonly RunBuffer _key = new RunBuffer();
    private readonly long[] _words;

    private RunMerger(KeyShape[] shapes, int components, MeasureKind[] kinds, int words)
    {
        _shapes = shapes;
        _components = components;
        _kinds = kinds;
        _words = new long[words];
    }

    /// <summary>A merge of <paramref name="runs"/> of <paramref name="scratch"/>, their keys cut to <paramref name="components"/>.</summary>
    internal static async ValueTask<RunMerger> OpenAsync(
        RunScratch scratch, IReadOnlyList<RunSpan> runs, KeyShape[] shapes, int components, MeasureKind[] kinds, CancellationToken cancellationToken)
    {
        RunMerger merger = new RunMerger(shapes, components, kinds, StateWords.Count(kinds));
        try
        {
            for (int r = 0; r < runs.Count; r++)
            {
                RunCursor cursor = new RunCursor(scratch, runs[r], shapes, components, merger._words.Length, r);
                if (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    merger.Push(cursor);
                }
                else
                {
                    cursor.Dispose();
                }
            }

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
        if (_heap.Count == 0)
        {
            return false;
        }

        RunCursor first = Pop();
        _key.Clear();
        _key.Bytes(first.Key);
        first.Words.CopyTo(_words);
        await AdvanceAsync(first, cancellationToken).ConfigureAwait(false);
        while (_heap.Count > 0 && KeyCodec.Compare(_shapes, _components, _heap[0].Key, _key.Span, out _) == 0)
        {
            RunCursor same = Pop();
            StateWords.Merge(_words, same.Words, _kinds);
            await AdvanceAsync(same, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async ValueTask AdvanceAsync(RunCursor cursor, CancellationToken cancellationToken)
    {
        if (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            Push(cursor);
        }
        else
        {
            cursor.Dispose();
        }
    }

    private int CompareAt(int left, int right)
    {
        int compared = KeyCodec.Compare(_shapes, _components, _heap[left].Key, _heap[right].Key, out _);
        return compared != 0 ? compared : _heap[left].Order.CompareTo(_heap[right].Order);
    }

    private void Push(RunCursor cursor)
    {
        _heap.Add(cursor);
        int at = _heap.Count - 1;
        while (at > 0)
        {
            int parent = (at - 1) >> 1;
            if (CompareAt(at, parent) >= 0)
            {
                break;
            }

            (_heap[at], _heap[parent]) = (_heap[parent], _heap[at]);
            at = parent;
        }
    }

    private RunCursor Pop()
    {
        RunCursor top = _heap[0];
        int last = _heap.Count - 1;
        _heap[0] = _heap[last];
        _heap.RemoveAt(last);
        int at = 0;
        while (true)
        {
            int left = (2 * at) + 1;
            if (left >= _heap.Count)
            {
                break;
            }

            int smaller = left + 1 < _heap.Count && CompareAt(left + 1, left) < 0 ? left + 1 : left;
            if (CompareAt(smaller, at) >= 0)
            {
                break;
            }

            (_heap[at], _heap[smaller]) = (_heap[smaller], _heap[at]);
            at = smaller;
        }

        return top;
    }

    public void Dispose()
    {
        foreach (RunCursor cursor in _heap)
        {
            cursor.Dispose();
        }

        _heap.Clear();
        _key.Dispose();
    }
}

/// <summary>
/// The rows of a chunk of a synopsis table as they come, in the order of their buckets and keys: each row's bucket,
/// its keys and its states, held column by column in arrays of its own, reused from chunk to chunk, until the chunk
/// is built into an arena and handed to the writer.
/// </summary>
internal sealed class ChunkStage
{
    private readonly KeyShape[] _shapes;
    private readonly int _words;
    private readonly KeyColumn[] _keys;
    private long[] _buckets = new long[64];
    private long[][] _states;
    private int _rows;

    internal ChunkStage(KeyShape[] shapes, int words)
    {
        _shapes = shapes;
        _words = words;
        _keys = new KeyColumn[shapes.Length];
        for (int k = 0; k < shapes.Length; k++)
        {
            _keys[k] = new KeyColumn(shapes[k]);
        }

        _states = new long[words][];
        for (int w = 0; w < words; w++)
        {
            _states[w] = new long[64];
        }
    }

    /// <summary>The rows held.</summary>
    internal int Rows => _rows;

    /// <summary>The bytes the rows held take before they are encoded: their buckets, their states, and their keys' values.</summary>
    internal long RawBytes
    {
        get
        {
            long bytes = (long)_rows * sizeof(long) * (1 + _words);
            foreach (KeyColumn key in _keys)
            {
                bytes += key.ValueBytes;
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
            _states[w][_rows] = words[w];
        }

        _rows++;
    }

    /// <summary>The rows held as a struct of <paramref name="rowType"/> in <paramref name="arena"/>: the bucket, the keys, the states.</summary>
    internal int Build(CanonicalArena arena, DType rowType, DTypeArena types)
    {
        DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
        int[] fields = new int[1 + _keys.Length + _words];
        fields[0] = Longs(arena, longs, _buckets.AsSpan(0, _rows));
        for (int k = 0; k < _keys.Length; k++)
        {
            fields[1 + k] = _keys[k].Build(arena, rowType.GetField(1 + k), types, _rows);
        }

        for (int w = 0; w < _words; w++)
        {
            fields[1 + _keys.Length + w] = Longs(arena, longs, _states[w].AsSpan(0, _rows));
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
