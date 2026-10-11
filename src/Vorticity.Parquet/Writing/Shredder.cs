using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A nested column's rows shredded into the standard's entries: from the top-level field down to the
/// leaf, a row an entry, a list's elements an entry each, every entry with the repetition level at
/// which it starts and the definition level it reaches, and the leaf's row of the value it holds.
/// </summary>
/// <remarks>
/// <para>
/// The walk goes a field at a time over every entry. A field that may be null stops the entries of
/// its null rows, which keep the definition level of the field above it, and raises the others to
/// its own. A list stops the entries of its empty rows likewise, and replaces each other one with
/// an entry per element: the first keeps the row's repetition level, the others repeat at the
/// list's. An entry that stopped is carried down unchanged. A field that may not be null and is
/// given one where its holder is not null is the caller's error.
/// </para>
/// <para>
/// The values are the leaf's rows of the entries that reach it, in entry order: a range of the leaf
/// when its lists lie back to back, as a list's elements mostly do, which the column writer then
/// stages without gathering them. The entries live in blocks of the engine's pool, kept from one
/// block of rows to the next and given back when the writer is.
/// </para>
/// </remarks>
internal sealed class Shredder(AlignedBufferPool pool) : IDisposable
{
    private Entries _current = new(pool);
    private Entries _next = new(pool);
    private NativeSegmentOwner? _valueBlock;

    /// <summary>A list view's offsets and sizes widened, when they are of two types no shape reads as they lie.</summary>
    private long[] _wideOffsets = [];
    private long[] _wideSizes = [];

    /// <summary>The entries of the rows shredded last.</summary>
    internal int Count { get; private set; }

    /// <summary>The entries among them that hold a value.</summary>
    internal int Values { get; private set; }

    /// <summary>The leaf's node, whose rows the values are.</summary>
    internal int Leaf { get; private set; }

    /// <summary>Whether the values' rows are a range of the leaf, from <see cref="ValueRows"/>' first.</summary>
    internal bool Contiguous { get; private set; }

    internal ReadOnlySpan<byte> Repetition => _current.Repetition[..Count];

    internal ReadOnlySpan<byte> Definition => _current.Definition[..Count];

    /// <summary>The leaf's row of each value, in entry order; only the first, when they are a range.</summary>
    internal ReadOnlySpan<int> ValueRows => MemoryMarshal.Cast<byte, int>(_valueBlock!.WritableSpan)[..(Contiguous ? Math.Min(Values, 1) : Values)];

    /// <summary>Shreds <paramref name="count"/> rows of <paramref name="node"/>, the column's top-level field, from <paramref name="start"/>.</summary>
    internal void Shred(CanonicalArena arena, int node, int start, int count, WriteColumn column)
    {
        _current.Ensure(count);
        Span<int> rows = _current.Rows;
        for (int i = 0; i < count; i++)
        {
            rows[i] = start + i;
        }

        _current.Repetition[..count].Clear();
        _current.Definition[..count].Clear();
        Count = count;
        int current = node;
        foreach (ShredStep step in column.Steps)
        {
            CanonicalNode field = arena.GetNode(current);
            Define(arena, field, step.Nullable, step.DefinedAt, column);
            if (step.Kind == ShredKind.Struct)
            {
                // An extension over a struct, a FILE's, holds its fields in its storage, whose validity is its own.
                if (field.Kind == CanonicalKind.Extension)
                {
                    field = arena.GetNode(EncodedForms.Canonical(arena, field.StorageIndex));
                }

                current = EncodedForms.Canonical(arena, field.GetFieldIndex(step.Child));
                continue;
            }

            current = Expand(arena, field, step);
            if (step.Kind == ShredKind.Map)
            {
                current = EncodedForms.Canonical(arena, arena.GetNode(current).GetFieldIndex(step.Child));
            }
        }

        if (column.ThroughStorage)
        {
            current = EncodedForms.Canonical(arena, arena.GetNode(current).StorageIndex);
        }

        Define(arena, arena.GetNode(current), column.Nullable, column.MaxDefinitionLevel, column);
        Leaf = current;
        Collect();
    }

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
        _valueBlock?.Dispose();
        _valueBlock = null;
    }

    /// <summary>
    /// The field at <paramref name="node"/>, for every entry that reaches it: where it is null the
    /// entry stops, else it reaches <paramref name="definedAt"/>.
    /// </summary>
    private void Define(CanonicalArena arena, CanonicalNode node, bool nullable, int definedAt, WriteColumn column)
    {
        Span<int> rows = _current.Rows[..Count];
        Span<byte> definition = _current.Definition[..Count];
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllValid)
        {
            if (nullable)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i] >= 0)
                    {
                        definition[i] = (byte)definedAt;
                    }
                }
            }

            return;
        }

        ReadOnlySpan<byte> bits = mask.Bits;
        int offset = mask.BitOffset;
        bool none = mask.AllInvalid;
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            if (row < 0)
            {
                continue;
            }

            if (!none && CanonicalSupport.BitAt(bits, offset + row))
            {
                definition[i] = (byte)definedAt;
            }
            else if (nullable)
            {
                rows[i] = -1;
            }
            else
            {
                throw new VortexSchemaException($"The column '{string.Join('.', column.Path)}' is not nullable and is given a null.");
            }
        }
    }

    /// <summary>
    /// Replaces every entry at a list's row with an entry per element, or stops it where the list is
    /// empty; the list's elements' node. The offsets and sizes are read at their own width, the
    /// common widths specialized so that no read dispatches on it.
    /// </summary>
    private int Expand(CanonicalArena arena, CanonicalNode list, ShredStep step)
    {
        int elements;
        if (list.Kind == CanonicalKind.FixedSizeList)
        {
            elements = EncodedForms.Canonical(arena, list.ElementsIndex);
            Expand(new FixedShape((int)list.FixedSize), arena.GetNode(elements).Length, step);
            return elements;
        }

        if (list.Kind != CanonicalKind.ListView)
        {
            ArraysThrow.Kind(list.Kind, "ListView or FixedSizeList");
        }

        elements = EncodedForms.Canonical(arena, list.ElementsIndex);
        long count = arena.GetNode(elements).Length;
        ReadOnlySpan<byte> offsets = list.Offsets.Span;
        ReadOnlySpan<byte> sizes = list.Sizes.Span;
        switch ((list.OffsetPType, list.SizePType))
        {
            case (PType.I32, PType.I32):
                Expand(new ViewShape<int>(offsets, sizes), count, step);
                break;
            case (PType.I64, PType.I64):
                Expand(new ViewShape<long>(offsets, sizes), count, step);
                break;
            case (PType.U32, PType.U32):
                Expand(new ViewShape<uint>(offsets, sizes), count, step);
                break;
            case (PType.U64, PType.U64):
                Expand(new ViewShape<ulong>(offsets, sizes), count, step);
                break;
            default:
                // Offsets and sizes of two other types: both widened once, each by its own type,
                // rather than either read by a type switch a row.
                int rows = list.Length;
                if (_wideOffsets.Length < rows)
                {
                    _wideOffsets = new long[Math.Max(rows, 2 * _wideOffsets.Length)];
                    _wideSizes = new long[_wideOffsets.Length];
                }

                Widen(offsets, list.OffsetPType, _wideOffsets.AsSpan(0, rows));
                Widen(sizes, list.SizePType, _wideSizes.AsSpan(0, rows));
                Expand(new ViewShape<long>(MemoryMarshal.AsBytes(_wideOffsets.AsSpan(0, rows)), MemoryMarshal.AsBytes(_wideSizes.AsSpan(0, rows))), count, step);
                break;
        }

        return elements;
    }

    /// <summary>The first integers of <paramref name="source"/>, of <paramref name="type"/>, widened into <paramref name="destination"/>.</summary>
    private static void Widen(ReadOnlySpan<byte> source, PType type, Span<long> destination)
    {
        switch (type)
        {
            case PType.I8:
                Widen<sbyte>(source, destination);
                break;
            case PType.U8:
                Widen<byte>(source, destination);
                break;
            case PType.I16:
                Widen<short>(source, destination);
                break;
            case PType.U16:
                Widen<ushort>(source, destination);
                break;
            case PType.I32:
                Widen<int>(source, destination);
                break;
            case PType.U32:
                Widen<uint>(source, destination);
                break;
            case PType.I64:
                Widen<long>(source, destination);
                break;
            case PType.U64:
                Widen<ulong>(source, destination);
                break;
            default:
                ArraysThrow.Format($"A list's offsets or sizes are {type}, not integers.");
                break;
        }

        static void Widen<T>(ReadOnlySpan<byte> source, Span<long> destination)
            where T : unmanaged, IBinaryInteger<T>
        {
            ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(source)[..destination.Length];
            for (int i = 0; i < values.Length; i++)
            {
                destination[i] = long.CreateTruncating(values[i]);
            }
        }
    }

    private void Expand<TShape>(TShape shape, long elementCount, ShredStep step)
        where TShape : IListShape, allows ref struct
    {
        Span<int> rows = _current.Rows[..Count];

        // A first pass counts the entries the lists make, so that the second writes them in place.
        long total = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            long length = row < 0 ? 0 : shape.Length(row);
            total += length > 0 ? length : 1;
        }

        if (total > Array.MaxLength)
        {
            ArraysThrow.Format($"Lists of {total} elements do not fit a batch's entries.");
        }

        // The first pass sized the next entries exactly: the second writes them without a check per
        // element, each list's bounds checked once against its elements' node.
        _next.Ensure((int)total);
        ReadOnlySpan<byte> repetition = _current.Repetition;
        ReadOnlySpan<byte> definition = _current.Definition;
        ref byte nextRepetition = ref MemoryMarshal.GetReference(_next.Repetition);
        ref byte nextDefinition = ref MemoryMarshal.GetReference(_next.Definition);
        ref int nextRows = ref MemoryMarshal.GetReference(_next.Rows);
        byte repeatedAt = (byte)step.RepeatedAt;
        byte elementsAt = (byte)step.ElementsAt;
        nint at = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            long length = 0;
            long first = 0;
            if (row >= 0)
            {
                length = shape.Length(row);
                first = shape.First(row);
                if (length < 0 || first < 0 || first + length > elementCount)
                {
                    ArraysThrow.Format($"A list's elements at {first}, {length} of them, lie outside its {elementCount}.");
                }
            }

            if (length == 0)
            {
                Unsafe.Add(ref nextRepetition, at) = repetition[i];
                Unsafe.Add(ref nextDefinition, at) = definition[i];
                Unsafe.Add(ref nextRows, at) = -1;
                at++;
                continue;
            }

            Unsafe.Add(ref nextRepetition, at) = repetition[i];
            Unsafe.Add(ref nextDefinition, at) = elementsAt;
            int elementRow = (int)first;
            Unsafe.Add(ref nextRows, at) = elementRow;
            for (nint k = 1; k < (nint)length; k++)
            {
                Unsafe.Add(ref nextRepetition, at + k) = repeatedAt;
                Unsafe.Add(ref nextDefinition, at + k) = elementsAt;
                Unsafe.Add(ref nextRows, at + k) = elementRow + (int)k;
            }

            at += (nint)length;
        }

        (_current, _next) = (_next, _current);
        Count = (int)at;
    }

    /// <summary>
    /// The leaf's rows of the entries that reach it, and whether they are a range: a first pass
    /// finds out, and only rows that are not a range are written out, which a range needs but its first.
    /// </summary>
    private void Collect()
    {
        ReadOnlySpan<int> rows = _current.Rows[..Count];
        int found = 0;
        int first = -1;
        int next = 0;
        bool contiguous = true;
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            if (row >= 0)
            {
                if (found == 0)
                {
                    first = row;
                    next = row;
                }

                contiguous &= row == next;
                next = row + 1;
                found++;
            }
        }

        int bytes = Math.Max(contiguous ? 1 : found, 1) * sizeof(int);
        if (_valueBlock is null || _valueBlock.Length < bytes)
        {
            int size = Math.Max(bytes, 2 * (_valueBlock?.Length ?? 2048));
            _valueBlock?.Dispose();
            _valueBlock = null;
            _valueBlock = pool.Rent(size, 64);
        }

        Span<int> values = MemoryMarshal.Cast<byte, int>(_valueBlock.WritableSpan);
        Values = found;
        Contiguous = contiguous;
        if (contiguous)
        {
            values[0] = first;
            return;
        }

        int at = 0;
        foreach (int row in rows)
        {
            if (row >= 0)
            {
                values[at++] = row;
            }
        }
    }

    /// <summary>A list's elements at a row: how many, and from which of its elements' node.</summary>
    private interface IListShape
    {
        long Length(int row);

        long First(int row);
    }

    /// <summary>A list view's offsets and sizes, both of <typeparamref name="T"/>.</summary>
    private readonly ref struct ViewShape<T>(ReadOnlySpan<byte> offsets, ReadOnlySpan<byte> sizes) : IListShape
        where T : unmanaged, IBinaryInteger<T>
    {
        private readonly ReadOnlySpan<T> _offsets = MemoryMarshal.Cast<byte, T>(offsets);
        private readonly ReadOnlySpan<T> _sizes = MemoryMarshal.Cast<byte, T>(sizes);

        public long Length(int row) => long.CreateTruncating(_sizes[row]);

        public long First(int row) => long.CreateTruncating(_offsets[row]);
    }

    /// <summary>A fixed-size list's: every row the same number of elements, back to back.</summary>
    private readonly struct FixedShape(int size) : IListShape
    {
        public long Length(int row) => size;

        public long First(int row) => (long)row * size;
    }

    /// <summary>Entries' levels and rows, in one block of the pool: repetition levels, definition levels, rows.</summary>
    private sealed class Entries(AlignedBufferPool pool) : IDisposable
    {
        private NativeSegmentOwner? _block;
        private int _capacity;

        internal Span<byte> Repetition => _block is null ? default : _block.WritableSpan[.._capacity];

        internal Span<byte> Definition => _block is null ? default : _block.WritableSpan.Slice(_capacity, _capacity);

        internal Span<int> Rows => _block is null ? default : MemoryMarshal.Cast<byte, int>(_block.WritableSpan.Slice(2 * _capacity, _capacity * sizeof(int)));

        /// <summary>Room for <paramref name="count"/> entries, what was there not kept.</summary>
        internal void Ensure(int count)
        {
            if (count <= _capacity)
            {
                return;
            }

            // A multiple of 64, so that the rows after the two levels start aligned.
            int capacity = (Math.Max(count, Math.Max(2 * _capacity, 1024)) + 63) & ~63;
            _block?.Dispose();
            _block = null;
            _block = pool.Rent(checked(capacity * (2 + sizeof(int))), 64);
            _capacity = capacity;
        }

        public void Dispose()
        {
            _block?.Dispose();
            _block = null;
            _capacity = 0;
        }
    }
}
