using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Keys;

/// <summary>
/// The entries of a column held in memory: a query's result, which has no zone map nor index to
/// walk. Its non-null values in the cursor's total order, ties by their row -- their place in the
/// result -- so that a positioning is a bisection, a step, or a gallop over a key's entries. They
/// are sorted once, unless the result delivers them in that order already.
/// </summary>
internal abstract class MemoryKeySource : KeySource
{
    private int _at = -1;

    // What the entries hold of their query's memory budget, given back once the cursor is.
    private QueryMemory? _memory;

    private protected MemoryKeySource(FilterLiteralKind kind) => KeyKind = kind;

    internal sealed override FilterLiteralKind KeyKind { get; }

    internal sealed override long? EntryCount => Count;

    internal sealed override int Runs => 1;

    internal sealed override bool IsValid => _at >= 0;

    /// <summary>The entries.</summary>
    private protected int Count { get; set; }

    /// <summary>The current entry's position in key order.</summary>
    private protected int At => _at;

    /// <summary>
    /// A source over the values of <paramref name="column"/> in <paramref name="batches"/>, each
    /// entry's row its place among them; the batches are read to their end and disposed.
    /// </summary>
    /// <param name="batches">The result's batches, in the order it delivers them.</param>
    /// <param name="column">The column.</param>
    /// <param name="kind">Its key domain: integers, floats or bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="memory">What the entries are reserved under, which the source gives back once disposed; null for none.</param>
    internal static async ValueTask<MemoryKeySource> ReadAsync(
        IAsyncEnumerator<RecordBatch> batches, FieldExpr column, FilterLiteralKind kind, CancellationToken cancellationToken, QueryMemory? memory = null)
    {
        MemoryKeySource source = kind == FilterLiteralKind.Bytes ? new BytesKeys() : new FixedKeys(kind);
        source._memory = memory;
        try
        {
            while (await batches.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecordBatch batch = batches.Current;
                CanonicalArena arena = batch.Arena;
                int rows = batch.RowCount;
                source.Add(arena, ComparisonKernels.Unwrap(arena, FilterEvaluator.Resolve(arena, batch.RootIndex, column, rows)), rows, batch.StartRow);
            }
        }
        finally
        {
            await batches.DisposeAsync().ConfigureAwait(false);
        }

        source.Sort();
        return source;
    }

    internal sealed override ValueTask<bool> SeekAsync(FilterLiteral key, SeekMode op, CancellationToken cancellationToken)
    {
        if (op == SeekMode.Exact)
        {
            int first = Bound(key, upper: false);
            _at = first < Bound(key, upper: true) ? first : -1;
        }
        else
        {
            _at = op switch
            {
                SeekMode.AtOrAfter => Bound(key, upper: false),
                SeekMode.After => Bound(key, upper: true),
                SeekMode.AtOrBefore => Bound(key, upper: true) - 1,
                _ => Bound(key, upper: false) - 1,
            };
        }

        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken)
    {
        _at = 0;
        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken)
    {
        _at = Count - 1;
        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> NextAsync(CancellationToken cancellationToken)
    {
        _at++;
        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> PreviousAsync(CancellationToken cancellationToken)
    {
        _at--;
        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken)
    {
        _at = KeyGallop.Past(new Run(this, _at), _at, Count - 1, forward: true);
        return PositionedAsync();
    }

    internal sealed override ValueTask<bool> PreviousKeyAsync(CancellationToken cancellationToken)
    {
        _at = KeyGallop.Past(new Run(this, _at), _at, 0, forward: false);
        return PositionedAsync();
    }

    internal sealed override ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        new ValueTask<long>(Bound(key, upper: false));

    internal sealed override ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        new ValueTask<long>(Bound(key, upper: true));

    internal sealed override ValueTask<long> RankOfAsync(KeySource other, bool upper, CancellationToken cancellationToken) =>
        new ValueTask<long>(BoundOf(other, upper));

    internal sealed override ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken)
    {
        _at = rank >= 0 && rank < Count ? (int)rank : -1;
        return PositionedAsync();
    }

    internal sealed override ValueTask<long> CountAtKeyAsync(CancellationToken cancellationToken)
    {
        int end = KeyGallop.Past(new Run(this, _at), _at, Count - 1, forward: true);
        int before = KeyGallop.Past(new Run(this, _at), _at, 0, forward: false);
        return new ValueTask<long>((end < 0 ? Count : end) - before - 1);
    }

    internal sealed override void Invalidate() => _at = -1;

    public sealed override ValueTask DisposeAsync()
    {
        _memory?.Dispose();
        _memory = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds the non-null values of <paramref name="node"/>, its row 0 being row <paramref name="start"/>.</summary>
    private protected abstract void Add(CanonicalArena arena, int node, int rows, long start);

    /// <summary>Puts the entries in key order, ties by row, unless they are in it already.</summary>
    private protected abstract void Sort();

    /// <summary>Whether the entries at positions <paramref name="left"/> and <paramref name="right"/> hold one key.</summary>
    private protected abstract bool Same(int left, int right);

    /// <summary>The first position whose key is at or after <paramref name="key"/>, or after it when <paramref name="upper"/>.</summary>
    private protected abstract int Bound(FilterLiteral key, bool upper);

    /// <summary>The same against the key <paramref name="other"/> is on, a byte key read where it lends it.</summary>
    private protected abstract int BoundOf(KeySource other, bool upper);

    /// <summary>
    /// Makes <paramref name="array"/> hold <paramref name="length"/> elements, its content kept; the new
    /// array reserved under the query's memory first, the old one given back once replaced.
    /// </summary>
    /// <exception cref="VortexMemoryException">The budget does not grant the array.</exception>
    private protected void Grow<T>(ref T[] array, long length)
        where T : unmanaged
    {
        if (array.Length >= length)
        {
            return;
        }

        if (length > Array.MaxLength)
        {
            throw new InvalidOperationException("The keys of a result held in memory outgrow the largest array: walk a smaller result.");
        }

        int grown = (int)Math.Min(Math.Max(length, array.Length * 2L), Array.MaxLength);
        _memory?.Hold((long)grown * Unsafe.SizeOf<T>(), "keys of a result");
        long old = (long)array.Length * Unsafe.SizeOf<T>();
        Array.Resize(ref array, grown);
        _memory?.LetGo(old);
    }

    private ValueTask<bool> PositionedAsync()
    {
        if ((uint)_at >= (uint)Count)
        {
            _at = -1;
            return new ValueTask<bool>(false);
        }

        return new ValueTask<bool>(true);
    }

    /// <summary>The entries holding the key of one position.</summary>
    private readonly struct Run(MemoryKeySource source, int at) : KeyGallop.IKeyed
    {
        public bool Holds(int index) => source.Same(index, at);
    }

    /// <summary>
    /// Integer and float keys: an entry is its key as an unsigned integer in the same order, high,
    /// and its row, low, so that the entries sort as integers, ties by row.
    /// </summary>
    private sealed class FixedKeys(FilterLiteralKind kind) : MemoryKeySource(kind)
    {
        private const ulong SignBit = 1UL << 63;

        private UInt128[] _entries = [];

        internal override FilterLiteral Key
        {
            get
            {
                ulong key = (ulong)(_entries[At] >> 64);
                return KeyKind switch
                {
                    FilterLiteralKind.Signed => FilterLiteral.From((long)(key ^ SignBit)),
                    FilterLiteralKind.Unsigned => FilterLiteral.From(key),
                    _ => FilterLiteral.From(BitConverter.UInt64BitsToDouble(key ^ ((key >> 63) == 0 ? ulong.MaxValue : SignBit))),
                };
            }
        }

        internal override ReadOnlySpan<byte> KeyBytes => default;

        internal override long Row => (long)(ulong)_entries[At];

        private protected override void Add(CanonicalArena arena, int node, int rows, long start)
        {
            Grow(ref _entries, (long)Count + rows);
            CanonicalNode values = arena.GetNode(node);
            if (values.Kind != CanonicalKind.Primitive)
            {
                for (int row = 0; row < rows; row++)
                {
                    if (LiteralReader.TryRead(arena, node, row, out FilterLiteral key))
                    {
                        _entries[Count++] = Entry(Encode(key), start + row);
                    }
                }

                return;
            }

            ValidityMask valid = ValidityMask.From(arena, values.Validity);
            ReadOnlySpan<byte> bytes = values.Values.Span;
            switch (values.PType)
            {
                case PType.I8:
                    Add<sbyte>(bytes, valid, rows, start);
                    break;
                case PType.I16:
                    Add<short>(bytes, valid, rows, start);
                    break;
                case PType.I32:
                    Add<int>(bytes, valid, rows, start);
                    break;
                case PType.I64:
                    Add<long>(bytes, valid, rows, start);
                    break;
                case PType.U8:
                    Add<byte>(bytes, valid, rows, start);
                    break;
                case PType.U16:
                    Add<ushort>(bytes, valid, rows, start);
                    break;
                case PType.U32:
                    Add<uint>(bytes, valid, rows, start);
                    break;
                case PType.U64:
                    Add<ulong>(bytes, valid, rows, start);
                    break;
                case PType.F16:
                    Add<Half>(bytes, valid, rows, start);
                    break;
                case PType.F32:
                    Add<float>(bytes, valid, rows, start);
                    break;
                default:
                    Add<double>(bytes, valid, rows, start);
                    break;
            }
        }

        private protected override void Sort()
        {
            Span<UInt128> entries = _entries.AsSpan(0, Count);
            for (int i = 1; i < entries.Length; i++)
            {
                if (entries[i] < entries[i - 1])
                {
                    entries.Sort();
                    return;
                }
            }
        }

        private protected override bool Same(int left, int right) => (ulong)(_entries[left] >> 64) == (ulong)(_entries[right] >> 64);

        private protected override int Bound(FilterLiteral key, bool upper) => Bound(Encode(key), upper);

        private protected override int BoundOf(KeySource other, bool upper) => Bound(Encode(other.Key), upper);

        private static UInt128 Entry(ulong key, long row) => ((UInt128)key << 64) | (ulong)row;

        /// <summary>A key in the order of unsigned integers: an integer's sign bit flipped, a float's bits the way the total order reads them.</summary>
        private static ulong Encode(FilterLiteral key) => key.Kind switch
        {
            FilterLiteralKind.Signed => (ulong)key.SignedValue ^ SignBit,
            FilterLiteralKind.Unsigned => key.UnsignedValue,
            _ => KeyOrder.Ordered(BitConverter.DoubleToUInt64Bits(key.FloatValue)),
        };

        /// <summary>The same for a value of a primitive column, its type resolved once a batch.</summary>
        private static ulong Encode<T>(T value)
            where T : INumberBase<T>
        {
            if (typeof(T) == typeof(Half) || typeof(T) == typeof(float) || typeof(T) == typeof(double))
            {
                return KeyOrder.Ordered(BitConverter.DoubleToUInt64Bits(double.CreateTruncating(value)));
            }

            return typeof(T) == typeof(sbyte) || typeof(T) == typeof(short) || typeof(T) == typeof(int) || typeof(T) == typeof(long)
                ? (ulong)long.CreateTruncating(value) ^ SignBit
                : ulong.CreateTruncating(value);
        }

        private void Add<T>(ReadOnlySpan<byte> bytes, ValidityMask valid, int rows, long start)
            where T : unmanaged, INumberBase<T>
        {
            ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
            for (int row = 0; row < rows; row++)
            {
                if (valid.IsValid(row))
                {
                    _entries[Count++] = Entry(Encode(values[row]), start + row);
                }
            }
        }

        private int Bound(ulong key, bool upper)
        {
            int low = 0;
            int high = Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                ulong at = (ulong)(_entries[middle] >> 64);
                if (at < key || (upper && at == key))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    /// <summary>
    /// Text and binary keys: their bytes end to end in one buffer, an entry's row by entry, and the
    /// entries' order when they did not arrive in it.
    /// </summary>
    private sealed class BytesKeys() : MemoryKeySource(FilterLiteralKind.Bytes)
    {
        private byte[] _bytes = [];
        private int[] _starts = [0];
        private long[] _rows = [];

        // The entry at each position, ties by entry, which is by row; null when they arrived in key order.
        private int[]? _order;

        internal override FilterLiteral Key => FilterLiteral.From(KeyBytes);

        internal override ReadOnlySpan<byte> KeyBytes => Bytes(Entry(At));

        internal override long Row => _rows[Entry(At)];

        private protected override void Add(CanonicalArena arena, int node, int rows, long start)
        {
            CanonicalNode values = arena.GetNode(node);
            if (values.Kind != CanonicalKind.VarBinView)
            {
                for (int row = 0; row < rows; row++)
                {
                    if (LiteralReader.TryRead(arena, node, row, out FilterLiteral key))
                    {
                        Append(key.BytesValue, start + row);
                    }
                }

                return;
            }

            ValidityMask valid = ValidityMask.From(arena, values.Validity);
            for (int row = 0; row < rows; row++)
            {
                if (valid.IsValid(row))
                {
                    Append(LiteralReader.ViewAt(values, row), start + row);
                }
            }
        }

        private protected override void Sort()
        {
            for (int entry = 1; entry < Count; entry++)
            {
                if (Bytes(entry - 1).SequenceCompareTo(Bytes(entry)) > 0)
                {
                    int[] order = new int[Count];
                    for (int i = 0; i < order.Length; i++)
                    {
                        order[i] = i;
                    }

                    order.AsSpan().Sort(new ByKey(this));
                    _order = order;
                    return;
                }
            }
        }

        private protected override bool Same(int left, int right) => Bytes(Entry(left)).SequenceEqual(Bytes(Entry(right)));

        private protected override int Bound(FilterLiteral key, bool upper) => Bound(key.BytesValue, upper);

        private protected override int BoundOf(KeySource other, bool upper) => Bound(other.KeyBytes, upper);

        private int Entry(int position) => _order is null ? position : _order[position];

        private ReadOnlySpan<byte> Bytes(int entry) => _bytes.AsSpan(_starts[entry], _starts[entry + 1] - _starts[entry]);

        private void Append(ReadOnlySpan<byte> key, long row)
        {
            int used = _starts[Count];
            Grow(ref _bytes, (long)used + key.Length);
            Grow(ref _starts, Count + 2L);
            Grow(ref _rows, Count + 1L);
            key.CopyTo(_bytes.AsSpan(used));
            _starts[Count + 1] = used + key.Length;
            _rows[Count] = row;
            Count++;
        }

        private int Bound(ReadOnlySpan<byte> key, bool upper)
        {
            int low = 0;
            int high = Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                int order = Bytes(Entry(middle)).SequenceCompareTo(key);
                if (order < 0 || (upper && order == 0))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        /// <summary>Entries by their bytes, ties by entry.</summary>
        private readonly struct ByKey(BytesKeys keys) : IComparer<int>
        {
            public int Compare(int x, int y)
            {
                int order = keys.Bytes(x).SequenceCompareTo(keys.Bytes(y));
                return order != 0 ? order : x.CompareTo(y);
            }
        }
    }
}
