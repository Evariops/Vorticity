using System;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>A struct column: its fields are columns, row-aligned with it.</summary>
internal sealed class StructStore : ColumnStore
{
    private int _rows;

    internal StructStore(DType dtype, ColumnStore[] children, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        Children = children;
    }

    /// <summary>A struct whose type is given rather than read off the dtype: the one column of a file whose root is not a struct, seen as a struct of it.</summary>
    internal StructStore(DType dtype, VortexType type, ColumnStore[] children, AlignedBufferPool pool)
        : base(dtype, type, pool)
    {
        Children = children;
    }

    internal ColumnStore[] Children { get; }

    /// <summary>The untyped builder over this struct, made once.</summary>
    internal ColumnsBuilder? Facade { get; set; }

    /// <summary>The typed builder over this struct, made once per record type asked for last.</summary>
    internal ColumnsBuilder? TypedFacade { get; set; }

    internal override int Rows => Children.Length > 0 ? Children[0].Rows : _rows;

    internal override long CommittedBytes
    {
        get
        {
            long bytes = ValidityBytes(Committed);
            foreach (ColumnStore child in Children)
            {
                bytes += child.CommittedBytes;
            }

            return bytes;
        }
    }

    internal override void AppendNull()
    {
        RequireNullable();
        int row = Rows;
        foreach (ColumnStore child in Children)
        {
            if (child.Rows != row)
            {
                throw new InvalidOperationException(
                    $"The fields of {Type} hold different row counts; finish the row before appending a null one.");
            }
        }

        MarkNulls(row, 1);
        AppendDefault();
    }

    internal override void AppendDefault()
    {
        foreach (ColumnStore child in Children)
        {
            child.AppendDefault();
        }

        _rows++;
    }

    internal override int Build(CanonicalArena arena, int rows)
    {
        Validity validity = BuildValidity(arena, rows);
        Span<int> fields = Children.Length <= 64 ? stackalloc int[Children.Length] : new int[Children.Length];
        for (int i = 0; i < Children.Length; i++)
        {
            fields[i] = Children[i].Build(arena, rows);
        }

        return arena.AddStruct(DType, rows, validity, fields);
    }

    internal override void Commit()
    {
        Committed = Rows;
        foreach (ColumnStore child in Children)
        {
            child.Commit();
        }
    }

    internal override void Discard(int rows)
    {
        foreach (ColumnStore child in Children)
        {
            child.Discard(rows);
        }

        _rows = Math.Max(0, _rows - rows);
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        foreach (ColumnStore child in Children)
        {
            child.Truncate(rows);
        }

        _rows = Math.Min(_rows, rows);
        TruncateValidity(rows);
    }

    internal override string? Inconsistency()
    {
        int rows = Rows;
        for (int i = 0; i < Children.Length; i++)
        {
            ColumnStore child = Children[i];
            if (child.Rows != rows)
            {
                return $"field {i} of {Type} holds {child.Rows} rows and field 0 holds {rows}";
            }

            if (child.Inconsistency() is { } inner)
            {
                return $"field {i} of {Type}: {inner}";
            }
        }

        return null;
    }

    internal override void Release()
    {
        foreach (ColumnStore child in Children)
        {
            child.Release();
        }

        _rows = 0;
        base.Release();
    }
}

/// <summary>A list column: an offset and a size per row over one elements column, the form the engine's list view reads.</summary>
internal sealed unsafe class ListStore : ColumnStore
{
    private NativeSegmentOwner? _offsetsOwner;
    private byte* _offsets;
    private int _offsetBytes;
    private NativeSegmentOwner? _sizesOwner;
    private byte* _sizes;
    private int _sizeBytes;
    private int _capacity;
    private bool _open;
    private int _openStart;

    internal ListStore(DType dtype, ColumnStore elements, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        Elements = elements;
    }

    internal ColumnStore Elements { get; }

    internal override long CommittedBytes => ((long)Committed * 2 * sizeof(int)) + Elements.CommittedBytes + ValidityBytes(Committed);

    private int* OffsetAt => (int*)_offsets;

    private int* SizeAt => (int*)_sizes;

    /// <summary>Where the elements of the first <paramref name="rows"/> rows end.</summary>
    private int EndAt(int rows) => rows == 0 ? 0 : OffsetAt[rows - 1] + SizeAt[rows - 1];

    internal void BeginList()
    {
        if (_open)
        {
            throw new InvalidOperationException("A list is already open; close it with EndList first.");
        }

        int start = Elements.Rows;
        if (start != EndAt(Count))
        {
            throw new InvalidOperationException("Elements were appended outside BeginList and EndList; they belong to no list.");
        }

        _open = true;
        _openStart = start;
    }

    internal void EndList()
    {
        if (!_open)
        {
            throw new InvalidOperationException("No list is open; open one with BeginList.");
        }

        Push(_openStart, Elements.Rows - _openStart);
        _open = false;
    }

    /// <summary>Drops the open list and its elements, after a one-call append that failed part way.</summary>
    internal void AbortList()
    {
        if (_open)
        {
            Elements.Truncate(_openStart);
            _open = false;
        }
    }

    internal override void AppendNull()
    {
        RequireNullable();
        int row = Count;
        AppendDefault();
        MarkNulls(row, 1);
    }

    internal override void AppendDefault()
    {
        if (_open)
        {
            throw new InvalidOperationException("A list is open; close it with EndList before appending another row.");
        }

        int end = EndAt(Count);
        if (Elements.Rows != end)
        {
            throw new InvalidOperationException("Elements were appended outside BeginList and EndList; they belong to no list.");
        }

        Push(end, 0);
    }

    internal override int Build(CanonicalArena arena, int rows)
    {
        int elements = Elements.Build(arena, EndAt(rows));
        Validity validity = BuildValidity(arena, rows);
        return arena.AddListView(
            DType, rows, validity, elements,
            View(_offsetsOwner, rows * sizeof(int)), PType.I32, View(_sizesOwner, rows * sizeof(int)), PType.I32);
    }

    internal override void Commit()
    {
        Committed = Count;
        Elements.Commit();
    }

    internal override void Discard(int rows)
    {
        int start = rows < Count ? OffsetAt[rows] : EndAt(Count);
        Elements.Discard(start);
        int left = Count - rows;
        if (left > 0)
        {
            Buffer.MemoryCopy(_offsets + ((long)rows * sizeof(int)), _offsets, _offsetBytes, (long)left * sizeof(int));
            Buffer.MemoryCopy(_sizes + ((long)rows * sizeof(int)), _sizes, _sizeBytes, (long)left * sizeof(int));
            int* offsets = OffsetAt;
            for (int i = 0; i < left; i++)
            {
                offsets[i] -= start;
            }
        }

        Count = left;
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        Elements.Truncate(EndAt(rows));
        Count = rows;
        _open = false;
        TruncateValidity(rows);
    }

    internal override string? Inconsistency()
    {
        if (_open)
        {
            return $"a list of {Type} is open";
        }

        if (Elements.Rows != EndAt(Count))
        {
            return $"elements of {Type} were appended outside BeginList and EndList";
        }

        return Elements.Inconsistency();
    }

    internal override void Release()
    {
        Elements.Release();
        if (_offsetsOwner is not null)
        {
            Pool.Return(_offsetsOwner);
            _offsetsOwner = null;
        }

        if (_sizesOwner is not null)
        {
            Pool.Return(_sizesOwner);
            _sizesOwner = null;
        }

        _offsets = null;
        _sizes = null;
        _offsetBytes = 0;
        _sizeBytes = 0;
        _capacity = 0;
        _open = false;
        base.Release();
    }

    private void Push(int offset, int size)
    {
        if (Count == _capacity)
        {
            long needed = ((long)Count + 1) * sizeof(int);
            Grow(Pool, ref _offsetsOwner, ref _offsets, ref _offsetBytes, (long)Count * sizeof(int), needed);
            Grow(Pool, ref _sizesOwner, ref _sizes, ref _sizeBytes, (long)Count * sizeof(int), needed);
            _capacity = Math.Min(_offsetBytes, _sizeBytes) / sizeof(int);
        }

        OffsetAt[Count] = offset;
        SizeAt[Count] = size;
        Count++;
    }
}

/// <summary>A fixed-size list column: exactly <see cref="Size"/> elements per row, a uuid's sixteen bytes among them.</summary>
internal sealed class FixedListStore : ColumnStore
{
    private bool _open;
    private int _openStart;

    internal FixedListStore(DType dtype, ColumnStore elements, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        Elements = elements;
        Size = checked((int)dtype.FixedSize);
    }

    internal ColumnStore Elements { get; }

    internal int Size { get; }

    internal override long CommittedBytes => Elements.CommittedBytes + ValidityBytes(Committed);

    internal void BeginList()
    {
        if (_open)
        {
            throw new InvalidOperationException("A list is already open; close it with EndList first.");
        }

        if (Elements.Rows != (long)Count * Size)
        {
            throw new InvalidOperationException("Elements were appended outside BeginList and EndList; they belong to no list.");
        }

        _open = true;
        _openStart = Elements.Rows;
    }

    internal void EndList()
    {
        if (!_open)
        {
            throw new InvalidOperationException("No list is open; open one with BeginList.");
        }

        int size = Elements.Rows - _openStart;
        if (size != Size)
        {
            throw new VortexSchemaException($"The column is {Type}: a list of it holds exactly {Size} elements, and this one holds {size}.");
        }

        _open = false;
        Count++;
    }

    /// <summary>Drops the open list and its elements, after a one-call append that failed part way.</summary>
    internal void AbortList()
    {
        if (_open)
        {
            Elements.Truncate(_openStart);
            _open = false;
        }
    }

    /// <summary>Appends one row of a sixteen-byte list of bytes: a uuid, in network order.</summary>
    internal void AppendGuid(Guid value)
    {
        value.TryWriteBytes(((FixedStore)Elements).Reserve(16), bigEndian: true, out _);
        Count++;
    }

    internal override void AppendNull()
    {
        RequireNullable();
        int row = Count;
        AppendDefault();
        MarkNulls(row, 1);
    }

    internal override void AppendDefault()
    {
        if (_open)
        {
            throw new InvalidOperationException("A list is open; close it with EndList before appending another row.");
        }

        for (int i = 0; i < Size; i++)
        {
            Elements.AppendDefault();
        }

        Count++;
    }

    internal override int Build(CanonicalArena arena, int rows)
    {
        int elements = Elements.Build(arena, checked(rows * Size));
        Validity validity = BuildValidity(arena, rows);
        return arena.AddFixedSizeList(DType, rows, validity, elements, (uint)Size);
    }

    internal override void Commit()
    {
        Committed = Count;
        Elements.Commit();
    }

    internal override void Discard(int rows)
    {
        Elements.Discard(checked(rows * Size));
        Count -= rows;
        Committed = Math.Max(0, Committed - rows);
        DiscardValidity(rows);
    }

    internal override void Truncate(int rows)
    {
        Elements.Truncate(checked(rows * Size));
        Count = rows;
        _open = false;
        TruncateValidity(rows);
    }

    internal override string? Inconsistency()
    {
        if (_open)
        {
            return $"a list of {Type} is open";
        }

        if (Elements.Rows != (long)Count * Size)
        {
            return $"{Type} holds {Count} rows and {Elements.Rows} elements";
        }

        return Elements.Inconsistency();
    }

    internal override void Release()
    {
        Elements.Release();
        _open = false;
        base.Release();
    }
}

/// <summary>An extension column: its storage, labelled with the extension's dtype when it is built.</summary>
internal sealed class ExtensionStore : ColumnStore
{
    internal ExtensionStore(DType dtype, ColumnStore storage, AlignedBufferPool pool)
        : base(dtype, pool)
    {
        Storage = storage;
    }

    internal ColumnStore Storage { get; }

    internal override ColumnStore Leaf => Storage.Leaf;

    internal override int Rows => Storage.Rows;

    internal override long CommittedBytes => Storage.CommittedBytes;

    internal override void AppendNull() => Storage.AppendNull();

    internal override void AppendDefault() => Storage.AppendDefault();

    internal override int Build(CanonicalArena arena, int rows) => arena.AddExtension(DType, rows, Storage.Build(arena, rows));

    internal override void Commit()
    {
        Storage.Commit();
        Committed = Storage.Committed;
    }

    internal override void Discard(int rows)
    {
        Storage.Discard(rows);
        Committed = Storage.Committed;
    }

    internal override void Truncate(int rows) => Storage.Truncate(rows);

    internal override string? Inconsistency() => Storage.Inconsistency();

    internal override void Release()
    {
        Storage.Release();
        base.Release();
    }
}

/// <summary>Makes the store tree of a dtype.</summary>
internal static class ColumnStores
{
    internal static ColumnStore Create(DType dtype, AlignedBufferPool pool, VortexExtensionRegistry? extensions)
    {
        ColumnStore store = Create(dtype, pool);
        Attach(store, extensions);
        return store;
    }

    private static void Attach(ColumnStore store, VortexExtensionRegistry? extensions)
    {
        store.Extensions = extensions;
        switch (store)
        {
            case StructStore structure:
                foreach (ColumnStore child in structure.Children)
                {
                    Attach(child, extensions);
                }

                break;
            case ListStore list:
                Attach(list.Elements, extensions);
                break;
            case FixedListStore fixedList:
                Attach(fixedList.Elements, extensions);
                break;
            case ExtensionStore extension:
                Attach(extension.Storage, extensions);
                break;
            default:
                break;
        }
    }

    private static ColumnStore Create(DType dtype, AlignedBufferPool pool)
    {
        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                return new NullStore(dtype, pool);
            case DTypeKind.Bool:
                return new BoolStore(dtype, pool);
            case DTypeKind.Primitive:
            case DTypeKind.Decimal:
                return new FixedStore(dtype, pool);
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return new VarBinStore(dtype, pool);
            case DTypeKind.Struct:
            {
                ColumnStore[] children = new ColumnStore[dtype.FieldCount];
                for (int i = 0; i < children.Length; i++)
                {
                    children[i] = Create(dtype.GetField(i), pool);
                }

                return new StructStore(dtype, children, pool);
            }

            case DTypeKind.List:
                return new ListStore(dtype, Create(dtype.ElementType, pool), pool);
            case DTypeKind.FixedSizeList:
                return new FixedListStore(dtype, Create(dtype.ElementType, pool), pool);
            case DTypeKind.Map:
            {
                // A map is its list of entries, each a non-nullable {key, value}: the shape the
                // decoder reads a map back as, so the node this builds is the one a read delivers.
                DType entries = dtype.Arena.Struct(["key", "value"], [dtype.KeyType, dtype.ValueType], Nullability.NonNullable);
                return new ListStore(dtype, Create(entries, pool), pool);
            }
            case DTypeKind.Extension:
            {
                ColumnStore storage = Create(dtype.StorageType, pool);
                ExtensionStore extension = new ExtensionStore(dtype, storage, pool);
                storage.ExtensionMetadata = dtype.ExtensionMetadata.ToArray();
                if (storage is FixedStore fixedStorage)
                {
                    fixedStorage.TemporalUnit = extension.Type.Unit ?? TimeUnit.Microseconds;
                    fixedStorage.UtcTimestamp = extension.Type.ExtensionId == ExtensionIds.Timestamp
                        && extension.Type.TimeZone is { } zone && TimeZones.IsUtc(zone);
                }

                return extension;
            }

            default:
                throw new VortexUnsupportedException(
                    dtype.Kind.ToString(), ComponentKind.DType, $"A builder cannot write a {dtype.Kind} column; write it as a batch.");
        }
    }
}
