using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// Rows of states a synopsis gives in place of rows (<see cref="RecordBatch.CarriesStates"/>), as batches in the columns a
/// request lays out (<see cref="CountsAnswer.Layout"/>): each key's column, the rows, each measure's states, then the
/// sketches a quantile asks. What each row holds is the synopsis's own; how a batch is built of them is shared.
/// </summary>
internal abstract class StateTable
{
    /// <summary>The rows of states a batch holds at most.</summary>
    internal const int BatchRows = 1 << 14;

    private readonly int _keys;
    private readonly (string Name, int Field, bool Floats, bool Sketch)[] _measures;
    private readonly DType _type;
    private readonly DType _bool;

    /// <param name="keys">The key columns of a batch.</param>
    /// <param name="measures">The measures, in their order.</param>
    /// <param name="type">The batches' type.</param>
    /// <param name="validity">The type of a validity's bits.</param>
    protected StateTable(int keys, (string Name, int Field, bool Floats, bool Sketch)[] measures, DType type, DType validity)
    {
        _keys = keys;
        _measures = measures;
        _type = type;
        _bool = validity;
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the word <paramref name="word"/> of rows <c>[start, start + into.Length)</c>:
    /// 0 their rows, then each measure's words in <see cref="MeasureLayout"/>'s order, the measures one after another.
    /// </summary>
    protected abstract void FillWords(Span<long> into, int start, int word);

    /// <summary>The bytes of row <paramref name="row"/>'s key <paramref name="key"/>, as its column stores them; false for a null.</summary>
    protected abstract bool KeyOf(int row, int key, out ReadOnlySpan<byte> value);

    /// <summary>The sketch of row <paramref name="row"/>'s measure <paramref name="measure"/>, as it encodes; false for none.</summary>
    protected abstract bool SketchOf(int row, int measure, out ReadOnlySpan<byte> sketch);

    /// <summary>Row <paramref name="row"/>'s key <paramref name="column"/>, or the sketch of its measure <paramref name="column"/>; false for a null.</summary>
    private bool ValueOf(int row, int column, bool key, out ReadOnlySpan<byte> value)
    {
        if (!key)
        {
            return SketchOf(row, column, out value);
        }

        return KeyOf(row, column, out value);
    }

    /// <summary>
    /// The batch of states of rows <c>[start, start + count)</c>, built in <paramref name="arena"/>, its rows counted from
    /// <paramref name="firstRow"/>: bound into <paramref name="spare"/>, the stream's previous batch of states, disposed,
    /// when there is one.
    /// </summary>
    internal RecordBatch Batch(CanonicalArena arena, int start, int count, long firstRow, RecordBatch? spare = null)
    {
        Span<int> nodes = stackalloc int[_type.FieldCount];
        int at = 0;
        for (int k = 0; k < _keys; k++)
        {
            nodes[at] = Bytes(arena, _type.GetField(at), start, count, k, key: true);
            at++;
        }

        int words = 1;
        foreach ((_, _, bool floats, _) in _measures)
        {
            words += MeasureLayout.Words(floats);
        }

        for (int word = 0; word < words; word++)
        {
            nodes[at] = Words(arena, _type.GetField(at), start, count, word);
            at++;
        }

        for (int i = 0; i < _measures.Length; i++)
        {
            if (_measures[i].Sketch)
            {
                nodes[at] = Bytes(arena, _type.GetField(at), start, count, i, key: false);
                at++;
            }
        }

        int root = arena.AddStruct(_type, count, Validity.NonNullable, nodes);
        return RecordBatch.OfStates(arena, root, firstRow, spare);
    }

    /// <summary>A column of one word of each row's states.</summary>
    private int Words(CanonicalArena arena, DType dtype, int start, int count, int word)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        FillWords(MemoryMarshal.Cast<byte, long>(bytes)[..count], start, word);
        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }

    /// <summary>
    /// A column of each row's bytes, null for none: key <paramref name="column"/>'s value as its column stores it, or the
    /// sketch of measure <paramref name="column"/>; fixed-width values in a buffer, texts and binaries as views, the long
    /// ones in one data buffer.
    /// </summary>
    private int Bytes(CanonicalArena arena, DType dtype, int start, int count, int column, bool key)
    {
        DType storage = Storage(dtype);
        bool nulls = false;
        int data = 0;
        for (int row = start; row < start + count; row++)
        {
            bool held = ValueOf(row, column, key, out ReadOnlySpan<byte> value);
            nulls |= !held;
            data += value.Length > CanonicalSupport.MaxInlineViewLength ? value.Length : 0;
        }

        Validity validity = Valid(storage);
        if (nulls)
        {
            VortexBuffer bits = arena.Allocate((count + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < count; i++)
            {
                set[i >> 3] |= ValueOf(start + i, column, key, out _) ? (byte)(1 << (i & 7)) : (byte)0;
            }

            validity = Validity.Bitmap(arena.AddBool(_bool, count, Validity.NonNullable, bits, 0));
        }

        int node;
        if (storage.Kind == DTypeKind.Primitive)
        {
            int width = storage.PType.ByteWidth();
            VortexBuffer buffer = arena.Allocate(count * width, Math.Max(width, 1), out Span<byte> bytes);
            for (int i = 0; i < count; i++)
            {
                if (ValueOf(start + i, column, key, out ReadOnlySpan<byte> value))
                {
                    value[..Math.Min(width, value.Length)].CopyTo(bytes[(i * width)..]);
                }
            }

            node = arena.AddPrimitive(storage, count, validity, storage.PType, buffer);
        }
        else
        {
            VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> view);
            VortexBuffer heap = arena.Allocate(Math.Max(data, 1), 1, out Span<byte> into);
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                ValueOf(start + i, column, key, out ReadOnlySpan<byte> value);
                if (CanonicalSupport.WriteView(view.Slice(i * 16, 16), value, value.Length, 0, offset))
                {
                    value.CopyTo(into[offset..]);
                    offset += value.Length;
                }
            }

            node = arena.AddVarBinView(storage, count, validity, views, [heap]);
        }

        return Wrap(arena, dtype, node, count);
    }

    /// <summary>The validity of a column of <paramref name="dtype"/> with no null: every row valid, which a nullable type says so.</summary>
    private static Validity Valid(DType dtype) => dtype.Nullability == Nullability.Nullable ? Validity.AllValid : Validity.NonNullable;

    /// <summary>The type <paramref name="dtype"/> stores its values as, under every extension.</summary>
    private static DType Storage(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    /// <summary>The node <paramref name="storage"/> under every extension <paramref name="dtype"/> wraps it in.</summary>
    private static int Wrap(CanonicalArena arena, DType dtype, int storage, int rows)
    {
        if (dtype.Kind != DTypeKind.Extension)
        {
            return storage;
        }

        return arena.AddExtension(dtype, rows, Wrap(arena, dtype.StorageType, storage, rows));
    }
}

/// <summary>
/// The rows of states the granules of an object give (<see cref="GranuleAnswer"/>): each one's keys' values, its rows, its
/// measures' states and the histograms a quantile asks, added granule by granule; a granule that turns out not to answer
/// takes its rows back. Its arrays are rented from the shared pools and given back once its batches are built
/// (<see cref="Release"/>).
/// </summary>
internal sealed class GranuleStateRows(GranuleAnswer answer)
    : StateTable(answer.KeyFields.Length, answer.Measures, answer.Type, answer.Bool)
{
    private readonly int _keys = answer.KeyFields.Length;
    private readonly int _width = answer.Width;
    private readonly int _measures = answer.Measures.Length;
    private byte[]?[] _values = ArrayPool<byte[]?>.Shared.Rent(16 * answer.KeyFields.Length);

    // The keys set from their bytes (SetKey of a span), copied one after another into a rented array, each one's place a
    // word of its row's: its start, shifted, and its length; -1 for none. A granule's runs of a text of many values set one
    // a run, where each made an array of its own.
    private long[] _placed = ArrayPool<long>.Shared.Rent(16 * answer.KeyFields.Length);
    private byte[] _heap = [];
    private int _heapUsed;
    private long[] _words = ArrayPool<long>.Shared.Rent(16 * answer.Width);

    // Each row's histogram of each measure, a granule's statistics' own bytes; empty for none.
    private ReadOnlyMemory<byte>[] _sketches = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(16 * answer.Measures.Length);
    private int _count;

    /// <summary>The rows of states it holds.</summary>
    internal int Count => _count;

    /// <summary>Adds a row of states of <paramref name="rows"/> rows, its keys null and its measures cleared; its place.</summary>
    internal int Add(long rows)
    {
        Grow(ref _words, (_count + 1) * _width);
        Grow(ref _values, (_count + 1) * _keys);
        Grow(ref _placed, (_count + 1) * _keys);
        Grow(ref _sketches, (_count + 1) * _measures);
        int row = _count++;
        Span<long> words = Words(row);
        words.Clear();
        words[0] = rows;
        _values.AsSpan(row * _keys, _keys).Clear();
        _placed.AsSpan(row * _keys, _keys).Fill(-1);
        _sketches.AsSpan(row * _measures, _measures).Clear();
        return row;
    }

    /// <summary>Sets row <paramref name="row"/>'s key <paramref name="key"/> to <paramref name="value"/>, as its column stores it.</summary>
    internal void SetKey(int row, int key, byte[]? value)
    {
        _values[(row * _keys) + key] = value;
        _placed[(row * _keys) + key] = -1;
    }

    /// <summary>Sets row <paramref name="row"/>'s key <paramref name="key"/> to a copy of <paramref name="value"/>, as its column stores it, or to a null.</summary>
    internal void SetKey(int row, int key, ReadOnlySpan<byte> value, bool isNull)
    {
        _values[(row * _keys) + key] = null;
        if (isNull)
        {
            _placed[(row * _keys) + key] = -1;
            return;
        }

        if (_heap.Length - _heapUsed < value.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_heapUsed + value.Length, Math.Max(1024, 2 * _heap.Length)));
            _heap.AsSpan(0, _heapUsed).CopyTo(grown);
            if (_heap.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(_heap);
            }

            _heap = grown;
        }

        value.CopyTo(_heap.AsSpan(_heapUsed));
        _placed[(row * _keys) + key] = ((long)_heapUsed << 32) | (uint)value.Length;
        _heapUsed += value.Length;
    }

    /// <summary>Sets row <paramref name="row"/>'s sketch of measure <paramref name="measure"/> to <paramref name="sketch"/>, a histogram as it encodes.</summary>
    internal void SetSketch(int row, int measure, ReadOnlyMemory<byte> sketch) => _sketches[(row * _measures) + measure] = sketch;

    /// <summary>Row <paramref name="row"/>'s words: its rows, then each measure's states.</summary>
    internal Span<long> Words(int row) => _words.AsSpan(row * _width, _width);

    /// <summary>Takes back the rows added from <paramref name="count"/> on.</summary>
    internal void Truncate(int count) => _count = Math.Min(_count, count);

    /// <summary>Gives the rows' arrays back to the pools they were rented from: no row is read after, and none is added.</summary>
    internal void Release()
    {
        ArrayPool<long>.Shared.Return(_words);
        ArrayPool<byte[]?>.Shared.Return(_values, clearArray: true);
        ArrayPool<long>.Shared.Return(_placed);
        if (_heap.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_heap);
        }

        (_placed, _heap, _heapUsed) = ([], [], 0);
        ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(_sketches, clearArray: true);
        (_words, _values, _sketches, _count) = ([], [], [], 0);
    }

    /// <summary>Makes <paramref name="array"/>, rented, hold <paramref name="length"/> at least: a larger one rented, twice as large at least, its items copied.</summary>
    private static void Grow<T>(ref T[] array, int length)
    {
        if (length <= array.Length)
        {
            return;
        }

        T[] grown = ArrayPool<T>.Shared.Rent(Math.Max(length, array.Length * 2));
        array.CopyTo(grown, 0);
        ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        array = grown;
    }

    /// <inheritdoc/>
    protected override void FillWords(Span<long> into, int start, int word)
    {
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = _words[((start + i) * _width) + word];
        }
    }

    /// <inheritdoc/>
    protected override bool KeyOf(int row, int key, out ReadOnlySpan<byte> value)
    {
        if (_values[(row * _keys) + key] is { } bytes)
        {
            value = bytes;
            return true;
        }

        long placed = _placed[(row * _keys) + key];
        value = placed < 0 ? default : _heap.AsSpan((int)(placed >> 32), (int)(uint)placed);
        return placed >= 0;
    }

    /// <inheritdoc/>
    protected override bool SketchOf(int row, int measure, out ReadOnlySpan<byte> sketch)
    {
        sketch = _sketches[(row * _measures) + measure].Span;
        return !sketch.IsEmpty;
    }
}
