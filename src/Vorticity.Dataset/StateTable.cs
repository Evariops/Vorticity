using System;
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

    /// <summary>The rows of states.</summary>
    internal abstract int Count { get; }

    /// <summary>
    /// Fills <paramref name="into"/> with the word <paramref name="word"/> of rows <c>[start, start + into.Length)</c>:
    /// 0 their rows, then each measure's words in <see cref="MeasureLayout"/>'s order, the measures one after another.
    /// </summary>
    protected abstract void FillWords(Span<long> into, int start, int word);

    /// <summary>The bytes of row <paramref name="row"/>'s key <paramref name="key"/>, as its column stores them; null for a null.</summary>
    protected abstract byte[]? KeyOf(int row, int key);

    /// <summary>The sketch of row <paramref name="row"/>'s measure <paramref name="measure"/>; null for none.</summary>
    protected abstract byte[]? SketchOf(int row, int measure);

    /// <summary>The batch of states of rows <c>[start, start + count)</c>, built in <paramref name="arena"/>, its rows counted from <paramref name="firstRow"/>.</summary>
    internal RecordBatch Batch(CanonicalArena arena, int start, int count, long firstRow)
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
        return RecordBatch.OfStates(arena, root, firstRow);
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
            byte[]? value = key ? KeyOf(row, column) : SketchOf(row, column);
            nulls |= value is null;
            data += value is { Length: > CanonicalSupport.MaxInlineViewLength } ? value.Length : 0;
        }

        Validity validity = Valid(storage);
        if (nulls)
        {
            VortexBuffer bits = arena.Allocate((count + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < count; i++)
            {
                set[i >> 3] |= (key ? KeyOf(start + i, column) : SketchOf(start + i, column)) is null ? (byte)0 : (byte)(1 << (i & 7));
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
                if ((key ? KeyOf(start + i, column) : SketchOf(start + i, column)) is { } value)
                {
                    value.AsSpan(0, Math.Min(width, value.Length)).CopyTo(bytes[(i * width)..]);
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
                byte[] value = (key ? KeyOf(start + i, column) : SketchOf(start + i, column)) ?? [];
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
/// The rows of states the blocks of an object give (<see cref="BlockAnswer"/>): each one's keys' values, its rows and
/// its measures' states, added block by block; a block that turns out not to answer takes its rows back.
/// </summary>
internal sealed class BlockStateRows(BlockAnswer answer)
    : StateTable(answer.KeyFields.Length, answer.Measures, answer.Type, answer.Bool)
{
    private readonly int _keys = answer.KeyFields.Length;
    private readonly int _width = answer.Width;
    private byte[]?[] _values = new byte[]?[16 * answer.KeyFields.Length];
    private long[] _words = new long[16 * answer.Width];
    private int _count;

    /// <inheritdoc/>
    internal override int Count => _count;

    /// <summary>Adds a row of states of <paramref name="rows"/> rows, its keys null and its measures cleared; its place.</summary>
    internal int Add(long rows)
    {
        if ((_count + 1) * _width > _words.Length)
        {
            Array.Resize(ref _words, _words.Length * 2);
            Array.Resize(ref _values, Math.Max(_values.Length * 2, 16));
        }

        int row = _count++;
        Span<long> words = Words(row);
        words.Clear();
        words[0] = rows;
        _values.AsSpan(row * _keys, _keys).Clear();
        return row;
    }

    /// <summary>Sets row <paramref name="row"/>'s key <paramref name="key"/> to <paramref name="value"/>, as its column stores it.</summary>
    internal void SetKey(int row, int key, byte[]? value) => _values[(row * _keys) + key] = value;

    /// <summary>Row <paramref name="row"/>'s words: its rows, then each measure's states.</summary>
    internal Span<long> Words(int row) => _words.AsSpan(row * _width, _width);

    /// <summary>Takes back the rows added from <paramref name="count"/> on.</summary>
    internal void Truncate(int count) => _count = Math.Min(_count, count);

    /// <inheritdoc/>
    protected override void FillWords(Span<long> into, int start, int word)
    {
        for (int i = 0; i < into.Length; i++)
        {
            into[i] = _words[((start + i) * _width) + word];
        }
    }

    /// <inheritdoc/>
    protected override byte[]? KeyOf(int row, int key) => _values[(row * _keys) + key];

    /// <inheritdoc/>
    protected override byte[]? SketchOf(int row, int measure) => null;
}
