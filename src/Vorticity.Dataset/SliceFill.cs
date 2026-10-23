using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A slice's rows copied in as they come, straight into its columns, laid out for all of the slice's rows at its first
/// batch: a primitive's values, an extension's storage's, and a string column's views and the bytes past them, each batch's
/// where it goes, its views rebased onto the bytes copied. Where a copy of each batch, then a join of the copies, moved
/// every byte twice and held both until the slice was written, the slice holds its rows once. A column of another form,
/// or one that admits nulls, is copied a batch at a time and joined at the end, as before; so is every column of a row
/// type that admits nulls.
/// </summary>
internal sealed class SliceFill
{
    private const int ViewSize = 16;
    private const int MaxInline = 12;

    /// <summary>The bytes past a block of strings' last a copy of sixteen bytes at a time may write over.</summary>
    private const int Slack = 16;

    /// <summary>The rows a slice's columns are laid out for are a multiple of this (<see cref="Laid"/>).</summary>
    private const int LaidRows = 1 << 16;

    private Column[] _columns = [];
    private DType _schema;
    private int _rows;
    private int _filled;
    private bool _whole;

    // Whether the slice's rows were known when it began; otherwise _rows is what its columns are laid out for, which grows.
    private bool _exact;

    // The batches copied whole, for a row type that admits nulls.
    private readonly List<int> _parts = [];

    private enum Form : byte
    {
        Values,
        Views,
        Parts,
    }

    /// <summary>A column of the slice: how its batches come in, where they go, and the batches copied for a join.</summary>
    private sealed class Column
    {
        internal Form Form;
        internal DType DType;
        internal DType? Storage;
        internal PType PType;
        internal int Width;
        internal VortexBuffer Into;
        internal List<VortexBuffer> Data { get; } = [];
        internal bool Padded;
        internal List<int> Parts { get; } = [];

        internal void Clear()
        {
            Data.Clear();
            Parts.Clear();
            Into = VortexBuffer.Empty;
        }
    }

    /// <summary>
    /// The rows a column of a slice of <paramref name="rows"/> rows is laid out for: past 65,536, the next multiple of it. The
    /// slices after it, a few rows more or fewer, then ask their arena for blocks of the sizes it holds, which it gives again:
    /// a block past the pool's classes is found again only at its exact size, and one that is not stays with the arena a slice
    /// longer, beside the new one. A smaller slice's blocks fall in the pool's classes, found again as they are.
    /// </summary>
    internal static int Laid(int rows) =>
        rows <= LaidRows ? rows : (int)Math.Min(int.MaxValue, ((long)rows + LaidRows - 1) / LaidRows * LaidRows);

    /// <summary>The bytes a block of a slice of <paramref name="bytes"/> bytes is laid out for, for the same reason: past a mebibyte, the next one.</summary>
    internal static int LaidBytes(long bytes) =>
        bytes <= 1 << 20 ? checked((int)bytes) : checked((int)((bytes + (1 << 20) - 1) >> 20 << 20));

    /// <summary>
    /// Lays a slice of <paramref name="rows"/> rows of <paramref name="schema"/> out in <paramref name="arena"/>; when the slice's
    /// rows are not known, not <paramref name="exact"/>, <paramref name="rows"/> is what its columns are laid out for first, as
    /// many as <see cref="Laid"/> gives, laid out again twice as large when its rows outgrow them, and the slice holds the rows
    /// it was handed. A slice whose rows are known holds them in blocks laid out alike.
    /// </summary>
    internal void Begin(CanonicalArena arena, DType schema, int rows, bool exact = true)
    {
        int laid = Laid(rows);
        _schema = schema;
        _rows = exact ? rows : laid;
        _exact = exact;
        _filled = 0;
        _whole = schema.Kind != DTypeKind.Struct || schema.IsNullable;
        _parts.Clear();
        if (_whole)
        {
            return;
        }

        int fields = schema.FieldCount;
        if (_columns.Length < fields)
        {
            int had = _columns.Length;
            Array.Resize(ref _columns, fields);
            for (int f = had; f < fields; f++)
            {
                _columns[f] = new Column();
            }
        }

        for (int f = 0; f < fields; f++)
        {
            Column column = _columns[f];
            column.Clear();
            column.DType = schema.GetField(f);
            column.Storage = null;
            column.Padded = true;
            DType values = column.DType;
            if (values.Kind == DTypeKind.Extension && !values.IsNullable)
            {
                column.Storage = values.StorageType;
                values = values.StorageType;
            }

            column.Form = values.IsNullable ? Form.Parts : values.Kind switch
            {
                DTypeKind.Primitive => Form.Values,
                DTypeKind.Utf8 or DTypeKind.Binary when column.Storage is null => Form.Views,
                _ => Form.Parts,
            };
            if (column.Form == Form.Values)
            {
                column.PType = values.PType;
                column.Width = values.PType.ByteWidth();
                column.Into = rows == 0 ? VortexBuffer.Empty : Block(arena, laid, _rows, column.Width, Math.Max(column.Width, 1));
            }
            else if (column.Form == Form.Views)
            {
                column.Width = ViewSize;
                column.Into = rows == 0 ? VortexBuffer.Empty : Block(arena, laid, _rows, ViewSize, ViewSize);
            }
        }
    }

    /// <summary>A buffer of <paramref name="rows"/> rows of <paramref name="width"/> bytes, in a block of <paramref name="laid"/> rows.</summary>
    private static VortexBuffer Block(CanonicalArena arena, int laid, int rows, int width, int alignment) =>
        arena.AllocateUninitialized(checked(laid * width), alignment, out _).Slice(0, rows * width);

    /// <summary>
    /// Copies the <paramref name="count"/> rows of the struct <paramref name="root"/> of <paramref name="source"/>, the next of
    /// the slice, into <paramref name="arena"/>, the slice's.
    /// </summary>
    internal void Append(CanonicalArena arena, CanonicalArena source, int root, int count)
    {
        if (_filled + count > _rows)
        {
            if (_exact)
            {
                throw new InvalidOperationException($"A slice of {_rows} rows was handed {_filled + count}.");
            }

            Grow(arena, Math.Max(checked(_rows * 2), _filled + count));
        }

        if (_whole)
        {
            _parts.Add(arena.CopyFrom(source, root));
            _filled += count;
            return;
        }

        int fields = _schema.FieldCount;
        for (int f = 0; f < fields; f++)
        {
            Column column = _columns[f];
            int node = source.GetNode(root).GetFieldIndex(f);
            switch (column.Form)
            {
                case Form.Values:
                    AppendValues(column, arena, source, node, count);
                    break;
                case Form.Views:
                    AppendViews(column, arena, source, node, count);
                    break;
                default:
                    column.Parts.Add(arena.CopyFrom(source, node));
                    break;
            }
        }

        _filled += count;
    }

    /// <summary>The slice's rows, all of them handed in, as one struct of the arena they were copied to.</summary>
    internal int Finish(ScanContext context)
    {
        if (_filled != _rows)
        {
            if (_exact)
            {
                throw new InvalidOperationException($"A slice of {_rows} rows was handed {_filled}.");
            }

            // The columns laid out for more rows than came hold the rows that did.
            foreach (Column column in _columns.AsSpan(0, _whole ? 0 : _schema.FieldCount))
            {
                if (column.Form is Form.Values or Form.Views)
                {
                    column.Into = column.Into.Slice(0, _filled * column.Width);
                }
            }

            _rows = _filled;
        }

        CanonicalArena arena = context.Canonical;
        if (_whole)
        {
            return _parts.Count == 1
                ? _parts[0]
                : CanonicalConcat.Concat(context.Decode, _schema, _rows, CollectionsMarshal.AsSpan(_parts));
        }

        int[] fields = new int[_schema.FieldCount];
        for (int f = 0; f < fields.Length; f++)
        {
            Column column = _columns[f];
            fields[f] = column.Form switch
            {
                Form.Values => Values(arena, column),
                Form.Views => arena.AddVarBinView(column.DType, _rows, Validity.NonNullable, column.Into, CollectionsMarshal.AsSpan(column.Data), column.Padded),
                _ => column.Parts.Count == 1
                    ? column.Parts[0]
                    : CanonicalConcat.Concat(context.Decode, column.DType, _rows, CollectionsMarshal.AsSpan(column.Parts)),
            };
        }

        return arena.AddStruct(_schema, _rows, Validity.NonNullable, fields);
    }

    /// <summary>Lays the columns out again for <paramref name="rows"/> rows, as many as <see cref="Laid"/> gives, the rows they hold copied over: a slice whose rows outgrew them.</summary>
    private void Grow(CanonicalArena arena, int rows)
    {
        rows = Laid(rows);
        for (int f = 0; !_whole && f < _schema.FieldCount; f++)
        {
            Column column = _columns[f];
            if (column.Form is Form.Values or Form.Views)
            {
                VortexBuffer grown = arena.AllocateUninitialized(checked(rows * column.Width), Math.Max(column.Width, 1), out Span<byte> into);
                column.Into.Span[..(_filled * column.Width)].CopyTo(into);
                column.Into = grown;
            }
        }

        _rows = rows;
    }

    /// <summary>Forgets the slice, for the next one: its buffers go with its arena.</summary>
    internal void Clear()
    {
        foreach (Column column in _columns)
        {
            column.Clear();
        }

        _parts.Clear();
        _rows = 0;
        _filled = 0;
    }

    private int Values(CanonicalArena arena, Column column)
    {
        int values = arena.AddPrimitive(column.Storage ?? column.DType, _rows, Validity.NonNullable, column.PType, column.Into);
        return column.Storage is null ? values : arena.AddExtension(column.DType, _rows, values);
    }

    /// <summary>A batch's values of <paramref name="column"/>, at the rows the slice holds so far.</summary>
    private void AppendValues(Column column, CanonicalArena arena, CanonicalArena source, int node, int count)
    {
        if (column.Storage is not null)
        {
            node = source.GetNode(node).StorageIndex;
        }

        ref readonly CanonicalRecord record = ref source.RecordRef(node);
        ReadOnlySpan<byte> values;
        if (record.Kind == CanonicalKind.Primitive && record.PType == column.PType)
        {
            values = record.BufferA.Span;
        }
        else
        {
            // An encoded batch, a constant or a dictionary, decoded in the slice's arena: copied twice, as every batch was.
            int decoded = EncodedForms.Canonical(arena, arena.CopyFrom(source, node));
            values = arena.RecordRef(decoded).BufferA.Span;
        }

        values[..(count * column.Width)].CopyTo(Writable(column.Into)[(_filled * column.Width)..]);
    }

    /// <summary>
    /// A batch's views of <paramref name="column"/>, at the rows the slice holds so far, and the bytes past them in a block
    /// of its own, in the rows' order, each view rebased onto it.
    /// </summary>
    private void AppendViews(Column column, CanonicalArena arena, CanonicalArena source, int node, int count)
    {
        if (source.RecordRef(node).Kind != CanonicalKind.VarBinView)
        {
            node = EncodedForms.Canonical(arena, arena.CopyFrom(source, node));
            source = arena;
        }

        CanonicalNode views = source.GetNode(node);
        ref readonly CanonicalRecord record = ref source.RecordRef(node);
        column.Padded &= record.PaddedViews;
        ReadOnlySpan<Int128> from = MemoryMarshal.Cast<byte, Int128>(record.BufferA.Span)[..count];
        Span<Int128> into = MemoryMarshal.Cast<byte, Int128>(Writable(column.Into)).Slice(_filled, count);
        long bytes = 0;
        foreach (Int128 view in from)
        {
            uint size = (uint)(ulong)view;
            bytes += size > MaxInline ? size : 0;
        }

        if (bytes == 0)
        {
            from.CopyTo(into);
            return;
        }

        VortexBuffer room = arena.AllocateUninitialized(checked((int)bytes + Slack), 1, out _);
        Span<byte> heap = Writable(room);
        int block = column.Data.Count;
        int at = 0;
        for (int i = 0; i < from.Length; i++)
        {
            Int128 view = from[i];
            uint size = (uint)(ulong)view;
            if (size <= MaxInline)
            {
                into[i] = view;
                continue;
            }

            ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(new ReadOnlySpan<Int128>(in view));
            ReadOnlySpan<byte> data = views.GetDataBuffer(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw[8..])).Span;
            int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw[12..]);
            int blocks = ((int)size + 15) & ~15;
            if ((uint)offset + (uint)blocks <= (uint)data.Length)
            {
                ref byte sourceByte = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
                ref byte target = ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), at);
                for (int b = 0; b < blocks; b += 16)
                {
                    Vector128.LoadUnsafe(ref sourceByte, (nuint)b).StoreUnsafe(ref target, (nuint)b);
                }
            }
            else
            {
                data.Slice(offset, (int)size).CopyTo(heap[at..]);
            }

            into[i] = ((Int128)(ulong)(uint)at << 96) | ((Int128)(ulong)(uint)block << 64) | (Int128)(ulong)view;
            at += (int)size;
        }

        column.Data.Add(room.Slice(0, (int)bytes));
    }

    private static Span<byte> Writable(VortexBuffer buffer) =>
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(buffer.Span), buffer.Length);
}
