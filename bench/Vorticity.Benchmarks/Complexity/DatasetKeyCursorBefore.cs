// The walk of a dataset's keys as the merge of its objects' cursors, each step a pass over every
// object and, while an object waits to be opened, the smallest key encoded into a new array: the
// walking path of the cursor kept line for line as the baseline of DatasetKeyWalkBenchmarks, with
// the single-tuple encoding it used, built in an arena of its own for each key. `DatasetKeyCursor`
// in the library is the same walk over a heap of the open cursors.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A forward walk of a dataset's clustering key, the smallest key found by a scan of every object.</summary>
internal sealed class DatasetKeyCursorBefore : IAsyncDisposable
{
    private readonly DatasetSnapshot _version;
    private readonly ClusteringKey _key;
    private readonly DType[] _dtypes;
    private readonly bool _bounded;
    private readonly bool _indexes;
    private readonly Slot[] _slots;
    private readonly List<int>[] _levels;
    // The walks below start at the first key: no seek sets these.
    private readonly FilterLiteral _target = default;
    private readonly SeekOp _op = SeekOp.AtOrAfter;
    private bool _first;
    private int _current = -1;
    private bool _disposed;

    private DatasetKeyCursorBefore(
        DatasetSnapshot version, ClusteringKey key, DType[] dtypes, bool bounded, bool indexes, Slot[] slots, List<int>[] levels)
    {
        _version = version;
        _key = key;
        _dtypes = dtypes;
        _bounded = bounded;
        _indexes = indexes;
        _slots = slots;
        _levels = levels;
    }

    /// <summary>Whether the cursor is positioned on an entry.</summary>
    public bool IsValid => !_disposed && _current >= 0;

    /// <summary>A walk of the dataset's clustering key over its current version.</summary>
    public static async ValueTask<DatasetKeyCursorBefore> OpenAsync(VortexDataset dataset, CancellationToken cancellationToken = default)
    {
        ClusteringKey key = dataset.Key!;
        DType[] dtypes = new DType[key.Paths.Count];
        for (int i = 0; i < dtypes.Length; i++)
        {
            dtypes[i] = ClusteringKey.Resolve(dataset.DType, key.Paths[i]);
        }

        DatasetSnapshot version = dataset.Snapshot;
        List<Slot> slots = [];
        List<List<int>> levels = [];
        await foreach (PositionedObject held in version
            .WalkAsync(null, 0, long.MaxValue, null, cancellationToken).ConfigureAwait(false))
        {
            while (levels.Count <= held.Level)
            {
                levels.Add([]);
            }

            levels[held.Level].Add(slots.Count);
            byte[] bound = VortexDataset.OrderOf(held.TreeKey).ToArray();
            slots.Add(new Slot(held.Entry, held.FirstRow, bound));
        }

        Slot[] all = [.. slots];
        foreach (List<int> level in levels)
        {
            level.Sort((left, right) =>
            {
                int order = all[left].Bound.AsSpan().SequenceCompareTo(all[right].Bound);
                return order != 0 ? order : left.CompareTo(right);
            });
        }

        return new DatasetKeyCursorBefore(version, key, dtypes, bounded: true, indexes: true, all, [.. levels]);
    }

    /// <summary>Positions on the smallest key.</summary>
    public async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _first = true;
        foreach (Slot slot in _slots)
        {
            slot.Live = false;
            slot.Walked = false;
            slot.Pending = true;
        }

        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <summary>Moves to the next entry in key order.</summary>
    public async ValueTask<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current < 0)
        {
            return false;
        }

        Slot slot = _slots[_current];
        slot.Live = await slot.Cursor!.NextAsync(cancellationToken).ConfigureAwait(false);
        await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return Choose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current = -1;
        foreach (Slot slot in _slots)
        {
            if (slot.Cursor is not null)
            {
                await slot.Cursor.DisposeAsync().ConfigureAwait(false);
            }

            if (slot.Lease is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens and positions every pending object whose minimum is at or below the smallest
    /// key an open cursor holds, so none that could hold the next key stays closed.</summary>
    private async ValueTask ResolveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int next = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Pending
                    && (next < 0 || _slots[i].Bound.AsSpan().SequenceCompareTo(_slots[next].Bound) < 0))
                {
                    next = i;
                }
            }

            if (next < 0)
            {
                return;
            }

            if (_bounded && Smallest() is { } smallest && _slots[next].Bound.AsSpan().SequenceCompareTo(smallest) > 0)
            {
                return;
            }

            Slot slot = _slots[next];
            slot.Pending = false;
            slot.Walked = true;
            KeyCursor cursor = await CursorOfAsync(slot, cancellationToken).ConfigureAwait(false);
            slot.Live = _first
                ? await cursor.SeekFirstAsync(cancellationToken).ConfigureAwait(false)
                : await cursor.SeekAsync(_target, _op, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The smallest key a live cursor holds, encoded as the bounds are.</summary>
    private byte[]? Smallest()
    {
        int smallest = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Live
                && (smallest < 0 || KeyCursor.Compare(_slots[i].Cursor!.Key, _slots[smallest].Cursor!.Key) < 0))
            {
                smallest = i;
            }
        }

        if (smallest < 0)
        {
            return null;
        }

        KeyCursor cursor = _slots[smallest].Cursor!;
        return _key.IsComposite ? cursor.KeyBytes.ToArray() : EncodeKey(cursor.Key, _dtypes[0]);
    }

    private async ValueTask<KeyCursor> CursorOfAsync(Slot slot, CancellationToken cancellationToken)
    {
        if (slot.Cursor is { } open)
        {
            return open;
        }

        slot.Lease = await _version.RentAsync(slot.Entry, cancellationToken).ConfigureAwait(false);
        slot.Cursor = await _key.TryOpenAsync(slot.Lease.File, _indexes, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The object '{slot.Entry.Key}' has no run on the key.");
        return slot.Cursor;
    }

    /// <summary>The live cursor holding the smallest key; ties go to the earlier object.</summary>
    private bool Choose()
    {
        _current = -1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Live)
            {
                continue;
            }

            if (_current < 0 || KeyCursor.Compare(_slots[i].Cursor!.Key, _slots[_current].Cursor!.Key) < 0)
            {
                _current = i;
            }
        }

        return _current >= 0;
    }

    /// <summary>The row encoding of one key, through a one-row column in an arena of its own.</summary>
    private static byte[] EncodeKey(FilterLiteral value, DType dtype)
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            int[] columns = new int[1];
            columns[0] = OneValue(arena, value, dtype);
            using RowKeys keys = RowEncoder.Encode(arena, columns, [RowSortField.Ascending]);
            return keys.Row(0).ToArray();
        }
        finally
        {
            arena.Reset();
        }
    }

    private static int OneValue(CanonicalArena arena, FilterLiteral value, DType dtype)
    {
        Validity validity = Validity.FromNullability(dtype.Nullability);
        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
            {
                VortexBuffer bits = arena.Allocate(1, 1, out Span<byte> bit);
                bit[0] = (byte)(value.BoolValue ? 1 : 0);
                return arena.AddBool(dtype, 1, validity, bits, 0);
            }

            case DTypeKind.Primitive:
            {
                PType ptype = dtype.PType;
                int width = ptype.ByteWidth();
                VortexBuffer buffer = arena.Allocate(width, width, out Span<byte> bytes);
                bytes.Clear();
                ulong bits = value.Kind switch
                {
                    FilterLiteralKind.Signed => unchecked((ulong)value.SignedValue),
                    FilterLiteralKind.Unsigned => value.UnsignedValue,
                    _ => ptype switch
                    {
                        PType.F16 => BitConverter.HalfToUInt16Bits((Half)value.FloatValue),
                        PType.F32 => BitConverter.SingleToUInt32Bits((float)value.FloatValue),
                        _ => BitConverter.DoubleToUInt64Bits(value.FloatValue),
                    },
                };

                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(bits >> (8 * i));
                }

                return arena.AddPrimitive(dtype, 1, validity, ptype, buffer);
            }

            default:
            {
                ReadOnlySpan<byte> text = value.BytesValue;
                VortexBuffer views = arena.Allocate(16, 16, out Span<byte> view);
                view.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)text.Length);
                if (text.Length <= 12)
                {
                    text.CopyTo(view[4..]);
                    return arena.AddVarBinView(dtype, 1, validity, views, default);
                }

                VortexBuffer data = arena.Allocate(text.Length, 1, out Span<byte> heap);
                text.CopyTo(heap);
                text[..4].CopyTo(view[4..]);
                return arena.AddVarBinView(dtype, 1, validity, views, [data]);
            }
        }
    }

    private sealed class Slot(ObjectEntry entry, long firstRow, byte[] bound)
    {
        internal ObjectEntry Entry { get; } = entry;

        internal long FirstRow { get; } = firstRow;

        internal byte[] Bound { get; } = bound;

        internal ObjectLease? Lease { get; set; }

        internal KeyCursor? Cursor { get; set; }

        internal bool Live { get; set; }

        internal bool Pending { get; set; }

        internal bool Walked { get; set; }
    }
}
