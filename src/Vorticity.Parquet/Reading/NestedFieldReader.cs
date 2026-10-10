using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Parquet.Schema;
using Vorticity.Types;
using Vorticity.Types.Variant;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// A field of lists, maps, structs or variants, assembled a batch at a time from the levels of the
/// columns under it: the standard's shredding of records into columns, read backwards.
/// </summary>
/// <remarks>
/// <para>
/// A field's slots, in the context the field holding it gives, are the entries of a column under it
/// whose repetition level is at most the context's — a value of the holder starts there — and whose
/// definition level reaches the context's — the holder is there to hold one. A top-level field's
/// context is (0, 0), and its slots are the rows. A struct gives its fields its own context; a list
/// or a map gives its elements its repetition level and the definition level from which it holds
/// one, and a slot's elements are the entry that starts it, when that entry reaches the elements'
/// level, and the entries after it that repeat at the list's level. A slot is valid where its entry
/// reaches the field's own definition level; a slot of a field that may not be null whose holder is
/// null is a zero, an empty list or a struct of zeros.
/// </para>
/// <para>
/// The first column under a field describes its structure. Every column under it describes the same
/// one in a well-formed file; each node's children are counted again from their own first column
/// and checked against what the node made of them, so that a file whose columns disagree is refused
/// rather than read past a node's end.
/// </para>
/// <para>
/// A variant's group is assembled as the struct of its fields, from which the variant is put back
/// together: its metadata as it is, and its value rebuilt from the columns shredded out of it.
/// </para>
/// </remarks>
internal sealed class NestedFieldReader
{
    private readonly Part _root;
    private readonly DType _validity;

    /// <summary>
    /// A reader of <paramref name="field"/>, of the dtype <paramref name="type"/>, and of a reader per
    /// column under it; a variant read as its group when <paramref name="storage"/>, its columns'
    /// values as the file holds them.
    /// </summary>
    internal NestedFieldReader(ParquetField field, DType type, ParquetSchema schema, DTypeArena types, DType validity, AlignedBufferPool pool, long cap, bool storage = false)
    {
        _validity = validity;
        List<ColumnChunkReader> readers = [];
        _root = Plan(field, type, new Columns(schema, types, validity, pool, cap, readers, storage), 0, 0);
        Readers = readers.ToArray();
    }

    /// <summary>The readers of the columns under the field, in the columns' order.</summary>
    internal ColumnChunkReader[] Readers { get; }

    /// <summary>The field's next <paramref name="rows"/> rows, as a node of <paramref name="context"/>'s arena.</summary>
    internal int Read(ScanContext context, int rows)
    {
        foreach (ColumnChunkReader reader in Readers)
        {
            reader.ReadNested(context, rows);
        }

        return Build(context, _root, 0, 0, rows);
    }

    /// <summary>Steps over the field's next <paramref name="rows"/> rows in every column under it.</summary>
    internal void Skip(ScanContext context, int rows)
    {
        foreach (ColumnChunkReader reader in Readers)
        {
            reader.SkipNested(context, rows);
        }
    }

    private static Part Plan(ParquetField field, DType type, Columns columns, int repetition, int definition)
    {
        // An extension over a group, a FILE's: the group's struct, wrapped once it is built.
        DType wrapper = default;
        if (field.Shape == FieldShape.Struct && type.Kind == DTypeKind.Extension)
        {
            wrapper = type;
            type = type.StorageType;
        }

        Part part = new() { Field = field, Type = type, Wrapper = wrapper };
        switch (field.Shape)
        {
            case FieldShape.Leaf:
                if (field.Column >= 0)
                {
                    ColumnChunkReader reader = new(
                        columns.Schema.Columns[field.Column], type, columns.Validity, columns.Pool, columns.Cap, nested: true);
                    columns.Readers.Add(reader);
                    part.Reader = reader;
                    part.Levels = reader;
                }

                // Else a map's value its file does not store: null wherever the map has an entry.
                return part;

            case FieldShape.Struct:
            case FieldShape.Variant when columns.Storage:
                part.Children = new Part[field.Children.Length];
                part.Nodes = new int[field.Children.Length];
                for (int i = 0; i < part.Children.Length; i++)
                {
                    part.Children[i] = Plan(field.Children[i], type.GetField(i), columns, repetition, definition);
                }

                part.Levels = part.Children[0].Levels;
                return part;

            case FieldShape.Variant:
            {
                if (field.Refusal is { } refusal)
                {
                    ParquetThrow.Unsupported("VARIANT", ParquetComponentKind.LogicalType, refusal);
                }

                DType storage = VortexTypes.ToDType(field.Storage!, columns.Types);
                part.Storage = storage;
                part.Children = new Part[field.Children.Length];
                part.Nodes = new int[field.Children.Length];
                for (int i = 0; i < part.Children.Length; i++)
                {
                    part.Children[i] = Plan(field.Children[i], storage.GetField(i), columns, repetition, definition);
                }

                part.Levels = part.Children[0].Levels;
                try
                {
                    part.Variant = ShreddedVariant.Compile(field.Storage!, columns.Types);
                }
                catch (VortexException e)
                {
                    Refuse(field, e);
                }

                return part;
            }

            case FieldShape.List:
                RequireLevels(field, repetition, definition);
                part.Children = [Plan(field.Children[0], type.ElementType, columns, field.RepeatedAt, field.ElementsAt)];
                part.Levels = part.Children[0].Levels;
                return part;

            default:
                RequireLevels(field, repetition, definition);
                DTypeArena types = columns.Types;
                DType key = type.KeyType;
                DType value = type.ValueType;
                part.Entries = types.Struct([types.InternName("key"), types.InternName("value")], [key, value], Nullability.NonNullable);
                part.Children =
                [
                    Plan(field.Children[0], key, columns, field.RepeatedAt, field.ElementsAt),
                    Plan(field.Children[1], value, columns, field.RepeatedAt, field.ElementsAt),
                ];
                part.Nodes = new int[2];
                part.Levels = part.Children[0].Levels;
                return part;
        }
    }

    /// <summary>
    /// Requires a list's or a map's levels to be those the schema's tree gives a repeated field in
    /// its holder's context: one repetition level deeper, and holding elements past its own definition.
    /// </summary>
    private static void RequireLevels(ParquetField field, int repetition, int definition)
    {
        if (field.RepeatedAt != repetition + 1 || field.DefinedAt < definition || field.ElementsAt <= field.DefinedAt)
        {
            ParquetThrow.Format($"The levels of the field '{field.Name}' do not follow its place in the schema.");
        }
    }

    /// <summary>The node of <paramref name="part"/>'s <paramref name="length"/> slots in the context of <paramref name="repetition"/> and <paramref name="definition"/>.</summary>
    private int Build(ScanContext context, Part part, int repetition, int definition, int length)
    {
        CanonicalArena arena = context.Canonical;
        ColumnChunkReader? levels = part.Levels;
        if (levels is null)
        {
            return arena.AddNull(part.Type, length);
        }

        ParquetField field = part.Field;
        ReadOnlySpan<byte> rep = levels.Repetition;
        ReadOnlySpan<byte> def = levels.Definition;
        int bitmapBytes = CanonicalSupport.BitmapByteCount(length);
        switch (field.Shape)
        {
            case FieldShape.Leaf:
            {
                VortexBuffer present = Bitmap(context, bitmapBytes, out Span<byte> bits);
                int valid = Mark(rep, def, repetition, definition, field.DefinedAt, bits, length, levels);
                RequireValid(field, valid, length);
                return part.Reader!.LeafNode(context, length, present, valid);
            }

            case FieldShape.Struct:
            case FieldShape.Variant when part.Variant is null:
            {
                Validity validity = Validity.NonNullable;
                if (part.Type.IsNullable || field.RefusesNull)
                {
                    VortexBuffer present = Bitmap(context, bitmapBytes, out Span<byte> bits);
                    int valid = Mark(rep, def, repetition, definition, field.DefinedAt, bits, length, levels);
                    RequireValid(field, valid, length);
                    validity = Of(arena, part.Type, present, valid, length);
                }

                // Each field counts its slots from its own first column, the first field's being the struct's.
                for (int i = 0; i < part.Children.Length; i++)
                {
                    part.Nodes[i] = Build(context, part.Children[i], repetition, definition, length);
                }

                int node = arena.AddStruct(part.Type, length, validity, part.Nodes);
                return part.Wrapper.IsDefault ? node : arena.AddExtension(part.Wrapper, length, node);
            }

            case FieldShape.Variant:
            {
                Validity validity = Validity.NonNullable;
                if (part.Type.IsNullable)
                {
                    VortexBuffer present = Bitmap(context, bitmapBytes, out Span<byte> bits);
                    int valid = Mark(rep, def, repetition, definition, field.DefinedAt, bits, length, levels);
                    validity = Of(arena, part.Type, present, valid, length);
                }

                for (int i = 0; i < part.Children.Length; i++)
                {
                    part.Nodes[i] = Build(context, part.Children[i], repetition, definition, length);
                }

                int group = arena.AddStruct(part.Storage, length, validity, part.Nodes);
                try
                {
                    return part.Variant!.Assemble(arena, part.Type, group);
                }
                catch (VortexException e) when (e is VortexFormatException or VortexUnsupportedException)
                {
                    Refuse(field, e);
                    return 0;
                }
            }

            default:
            {
                int offsetBytes = checked(length * sizeof(int));
                VortexBuffer offsets = CanonicalSupport.AllocateUninitialized(context.Decode, Math.Max(offsetBytes, 1), 64, out Span<byte> offsetSpan).Slice(0, offsetBytes);
                VortexBuffer sizes = CanonicalSupport.AllocateUninitialized(context.Decode, Math.Max(offsetBytes, 1), 64, out Span<byte> sizeSpan).Slice(0, offsetBytes);
                VortexBuffer present = default;
                Span<byte> bits = default;
                if (part.Type.IsNullable)
                {
                    present = Bitmap(context, bitmapBytes, out bits);
                }

                int total = Lists(
                    context, rep, def, repetition, definition, field, bits,
                    MemoryMarshal.Cast<byte, int>(offsetSpan[..offsetBytes]),
                    MemoryMarshal.Cast<byte, int>(sizeSpan[..offsetBytes]),
                    length, out int valid, levels);
                RequireValid(field, valid, length);
                Validity validity = Of(arena, part.Type, present, valid, length);
                int elements;
                if (field.Shape == FieldShape.List)
                {
                    elements = Build(context, part.Children[0], field.RepeatedAt, field.ElementsAt, total);
                }
                else
                {
                    part.Nodes[0] = Build(context, part.Children[0], field.RepeatedAt, field.ElementsAt, total);
                    part.Nodes[1] = Build(context, part.Children[1], field.RepeatedAt, field.ElementsAt, total);
                    elements = arena.AddStruct(part.Entries, total, Validity.NonNullable, part.Nodes);
                }

                return arena.AddListView(part.Type, length, validity, elements, offsets, PType.I32, sizes, PType.I32);
            }
        }
    }

    /// <summary>A bitmap of the arena's, every byte of which the kernel that marks it writes.</summary>
    private static VortexBuffer Bitmap(ScanContext context, int bytes, out Span<byte> bits)
    {
        VortexBuffer buffer = CanonicalSupport.AllocateUninitialized(context.Decode, Math.Max(bytes, 1), 64, out bits).Slice(0, bytes);
        bits = bits[..bytes];
        return buffer;
    }

    /// <summary>The validity of a node of <paramref name="type"/> whose slots <paramref name="present"/> marks, <paramref name="valid"/> of <paramref name="length"/>.</summary>
    private Validity Of(CanonicalArena arena, DType type, VortexBuffer present, int valid, int length)
    {
        if (!type.IsNullable)
        {
            return Validity.NonNullable;
        }

        return valid == length ? Validity.AllValid
            : valid == 0 ? Validity.AllInvalid
            : Validity.Bitmap(arena.AddBool(_validity, length, Validity.NonNullable, present, 0));
    }

    /// <summary>
    /// Sets a bit of <paramref name="present"/> per slot of the field whose entry reaches
    /// <paramref name="definedAt"/>; the bits set. Throws unless the context gives the field
    /// exactly <paramref name="length"/> slots.
    /// </summary>
    private static int Mark(
        ReadOnlySpan<byte> rep, ReadOnlySpan<byte> def, int repetition, int definition, int definedAt, Span<byte> present, int length, ColumnChunkReader levels)
    {
        int slots = LevelKernels.Slots(rep, def, repetition, definition, definedAt, present, length, out int valid);
        if (slots != length)
        {
            Disagree(levels, slots, length);
        }

        return valid;
    }

    /// <summary>
    /// A list's or a map's slots: each one's validity into <paramref name="present"/> when it is
    /// given, and its offset and its count of elements; the elements of every slot.
    /// </summary>
    private static int Lists(
        ScanContext context, ReadOnlySpan<byte> rep, ReadOnlySpan<byte> def, int repetition, int definition, ParquetField field,
        Span<byte> present, Span<int> offsets, Span<int> sizes, int length, out int valid, ColumnChunkReader levels)
    {
        // The kernel's scratch, two ints per entry, is the batch's: it goes when the batch does.
        int bytes = checked(2 * def.Length * sizeof(int));
        context.Canonical.AllocateUninitialized(Math.Max(bytes, 1), 64, out Span<byte> scratch);
        int slots = LevelKernels.Lists(
            rep, def, repetition, definition, field.DefinedAt, field.RepeatedAt, field.ElementsAt, present, offsets, sizes,
            MemoryMarshal.Cast<byte, int>(scratch[..bytes]),
            out valid, out int total, out bool stray);
        if (slots != length)
        {
            Disagree(levels, slots, length);
        }

        if (stray)
        {
            ParquetThrow.Format($"An entry of '{levels.Column.DottedPath}' repeats a list that holds no element.");
        }

        return total;
    }

    /// <summary>Refuses a null where the field reads as not null though its file lets it be.</summary>
    private static void RequireValid(ParquetField field, int valid, int length)
    {
        if (field.RefusesNull && valid != length)
        {
            ParquetThrow.Format($"The field '{field.Name}' is null where it reads as not null: a map's key may not be null.");
        }
    }

    /// <summary>The core's refusal of a variant, as the Parquet one it is: a malformed one or one this build does not read.</summary>
    [DoesNotReturn]
    private static void Refuse(ParquetField field, VortexException e)
    {
        if (e is VortexUnsupportedException)
        {
            ParquetThrow.Unsupported("VARIANT", ParquetComponentKind.LogicalType, $"The variant '{field.Name}' cannot be read: {e.Message}");
        }

        ParquetThrow.Format($"The variant '{field.Name}' is malformed: {e.Message}");
    }

    private static void Disagree(ColumnChunkReader levels, int found, int expected) =>
        ParquetThrow.Format($"The levels of '{levels.Column.DottedPath}' give a field {found} slots where its holder gives {expected}.");

    /// <summary>What the plan of a field's parts is made with.</summary>
    /// <summary>What the plan of a field's parts is made with; <c>Storage</c> reads a variant as its group.</summary>
    private sealed record Columns(ParquetSchema Schema, DTypeArena Types, DType Validity, AlignedBufferPool Pool, long Cap, List<ColumnChunkReader> Readers, bool Storage);

    /// <summary>A node of the field's tree: its dtype, its children, and the column that describes its structure.</summary>
    private sealed class Part
    {
        internal required ParquetField Field { get; init; }

        internal DType Type { get; init; }

        /// <summary>A map's key-value struct.</summary>
        internal DType Entries { get; set; }

        /// <summary>The extension a group's struct is wrapped in, or the default dtype.</summary>
        internal DType Wrapper { get; init; }

        /// <summary>A variant's group, as the struct of its fields.</summary>
        internal DType Storage { get; set; }

        /// <summary>What puts a variant back together from its group.</summary>
        internal ShreddedVariant? Variant { get; set; }

        internal Part[] Children { get; set; } = [];

        /// <summary>The children's nodes of the batch being built.</summary>
        internal int[] Nodes { get; set; } = [];

        /// <summary>A leaf's reader.</summary>
        internal ColumnChunkReader? Reader { get; set; }

        /// <summary>The reader of the first column under the part, whose levels describe it; null for a map's value its file does not store.</summary>
        internal ColumnChunkReader? Levels { get; set; }
    }
}
