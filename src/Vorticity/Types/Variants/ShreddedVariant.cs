using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types.Numerics;
using Vorticity.Writing;

namespace Vorticity.Types.Variants;

/// <summary>
/// The value of a Parquet Variant put back together from the columns shredded out of it: each
/// row's bytes as the standard's <c>construct_variant</c> builds them from the <c>value</c> column
/// and the <c>typed_value</c> beside it, through objects and arrays at any depth.
/// </summary>
/// <remarks>
/// <para>
/// The plan is compiled once, from the type of the variant's group: a struct of <c>metadata</c>,
/// <c>value</c> and <c>typed_value</c>, a shredded object a struct of such groups by field name,
/// a shredded array a list of them. A batch binds the plan to the group's canonical node and
/// builds a binary node of the rows' values. A row whose typed column is null takes its
/// <c>value</c> as it is: its view is copied and its bytes are not, and a group with no
/// <c>typed_value</c>, of a <c>value</c> that may not be null, is handed back without a row read.
/// The rows a typed column defines are written into a scratch the plan keeps from batch to batch,
/// which the arena takes in one copy.
/// </para>
/// <para>
/// An object is written in its fields' name order, the unsigned order of their UTF-8 bytes,
/// whichever column each comes from: the shredded fields, sorted once, are merged with the fields
/// of a partially shredded row's <c>value</c>, which the standard keeps in that order. A name both
/// hold is refused, since a field the typed columns hold may not be in <c>value</c>, whether the row
/// defines it or not. Ids, offsets and counts take the fewest bytes that hold them. The ids are those of
/// the row's metadata, looked up again only when a row's metadata differs from the last one's.
/// </para>
/// <para>
/// A value both of whose columns are null is missing: an object's field is then not in the object,
/// and where a value is required, at the top and as an array's element, it is the variant null. A
/// primitive or an array whose two columns are both non-null is refused, as is a value that is not
/// an object beside a typed object, whether or not the object defines a field.
/// </para>
/// </remarks>
internal sealed class ShreddedVariant
{
    private const string Id = "vortex.parquet.variant";

    /// <summary>Bytes in one view.</summary>
    private const int ViewSize = CanonicalSupport.ViewSize;

    private readonly Shred _root;
    private readonly int _metadata;
    private readonly DType _binary;

    /// <summary>Whether an object is shredded anywhere: only then are a row's field names looked up in its metadata.</summary>
    private readonly bool _objects;

    /// <summary>
    /// The written values of the batch being built, rented and given back once they are copied into the arena: an
    /// assembler is often made for one span of rows, and its buffer, made at 256 bytes and doubled to the batch's values,
    /// was most of what a read of a log's attributes allocated.
    /// </summary>
    private byte[] _scratch = [];
    private int _used;

    /// <summary>The bytes the last batch's values took, which the next one's scratch is rented at.</summary>
    private int _lastUsed;

    /// <summary>The fields and offsets of the objects and arrays being written, the innermost last.</summary>
    private int[] _stack = new int[64];
    private int _top;

    private VortexBuffer[] _buffers = new VortexBuffer[2];

    /// <summary>The metadata the objects' field ids were last looked up in, and how many times it changed.</summary>
    private byte[] _lastMetadata = [];
    private int _lastLength = -1;
    private int _generation;

    private ShreddedVariant(Shred root, int metadata, DType binary, bool objects)
    {
        _root = root;
        _metadata = metadata;
        _binary = binary;
        _objects = objects;
    }

    private enum ShredKind : byte
    {
        /// <summary>No typed column: the value as written.</summary>
        Value,
        Bool,

        /// <summary>A value of a fixed width, copied after its header: an integer, a float, a date, a time, a timestamp or a uuid.</summary>
        Fixed,
        Decimal,
        String,
        Binary,
        Object,
        Array,
    }

    /// <summary>The plan of a variant whose group is of <paramref name="group"/>.</summary>
    /// <param name="group">A struct of <c>metadata</c>, <c>value</c> and <c>typed_value</c>.</param>
    /// <param name="types">The arena the dtype of the rebuilt values is made in.</param>
    /// <exception cref="VortexFormatException">The group is not shaped as the standard says.</exception>
    /// <exception cref="VortexUnsupportedException">A typed column is of a type no variant type matches.</exception>
    internal static ShreddedVariant Compile(VortexType group, DTypeArena types)
    {
        int metadata = group.Kind == VortexTypeKind.Struct ? group.IndexOfField("metadata") : -1;
        if (metadata < 0 || group.Fields[metadata].Type is not { Kind: VortexTypeKind.Binary, IsNullable: false })
        {
            throw new VortexFormatException("A variant's group holds no metadata of binary that may not be null.");
        }

        bool objects = false;
        Shred root = Pair(group, "", top: true, ref objects);
        return new ShreddedVariant(root, metadata, types.Binary(Nullability.NonNullable), objects);
    }

    /// <summary>
    /// The plan of a variant whose group is of <paramref name="group"/>, as a decode of <paramref name="scan"/> compiled it
    /// last (<see cref="ShreddedPlans"/>): a plan made for every batch, its shredded keys' names and groups, was 270 KB a
    /// batch of 8,192 rows of 200 shredded keys.
    /// </summary>
    /// <exception cref="VortexFormatException">The group is not shaped as the standard says.</exception>
    /// <exception cref="VortexUnsupportedException">A typed column is of a type no variant type matches.</exception>
    internal static ShreddedVariant For(ScanContext scan, DType group) => (scan.ShreddedPlans ??= new ShreddedPlans()).Take(scan.Types, group);

    /// <summary>Whether the plan writes slices now, which another decode may not interleave with.</summary>
    private bool _busy;

    /// <summary>
    /// The variant node of the group node <paramref name="group"/>: a struct of the rows' metadata
    /// and of their values, which wears <paramref name="variant"/> and is valid where the group is.
    /// </summary>
    /// <exception cref="VortexFormatException">A row is not what the standard allows.</exception>
    internal int Assemble(CanonicalArena arena, DType variant, int group)
    {
        CanonicalNode node = arena.GetNode(group);
        int rows = node.Length;
        Validity validity = node.Validity;
        int metadata = node.GetFieldIndex(_metadata);
        int value = _root.Kind == ShredKind.Value && !arena.GetNode(node.GetFieldIndex(_root.Value)).DType.IsNullable
            ? node.GetFieldIndex(_root.Value)
            : Rebuild(arena, group, rows);
        Span<int> fields = stackalloc int[2];
        fields[0] = metadata;
        fields[1] = value;
        return arena.AddStruct(variant, rows, validity, fields);
    }

    /// <summary>The binary node of the group's rows' values: a null row's empty, every other one's as <c>construct_variant</c> builds it.</summary>
    private int Rebuild(CanonicalArena arena, int group, int rows)
    {
        int bytes = checked(rows * ViewSize);
        VortexBuffer views = arena.AllocateUninitialized(Math.Max(bytes, 1), 64, out Span<byte> into).Slice(0, bytes);

        // The scratch goes back to the pool however the rows end, a malformed one included.
        BeginSlices();
        try
        {
            WriteSlice(arena, group, into);
            Shred root = _root;
            return CompleteSlices(arena, rows, views, root.ValueBuffers.AsSpan(0, root.Value >= 0 ? root.ValueBufferCount : 0));
        }
        finally
        {
            EndSlices();
        }
    }

    /// <summary>
    /// Begins the values of rows rebuilt a slice at a time: <see cref="WriteSlice"/> for each slice in turn, then
    /// <see cref="CompleteSlices"/>, and <see cref="EndSlices"/> however they end.
    /// </summary>
    internal void BeginSlices()
    {
        _busy = true;
        _used = 0;
        _top = 0;
        if (_scratch.Length == 0)
        {
            // As large as the last batch's values, which a plan kept from a batch to the next knows: doubled from 256 bytes,
            // each batch copied its values eleven times over and took its last array from the shelf.
            _scratch = WorkArrays.Rent<byte>(Math.Max(256, _lastUsed));
        }
    }

    /// <summary>
    /// The views of the group node <paramref name="group"/>'s rows written into <paramref name="into"/>, the values rebuilt
    /// after those of the slices before: a row that takes its value as it is keeps the value column's view, whose data
    /// buffers every slice's value column holds alike, those of the node it is a slice of.
    /// </summary>
    internal void WriteSlice(CanonicalArena arena, int group, Span<byte> into)
    {
        Shred root = _root;
        Bind(arena, root, group);
        ReadOnlySpan<byte> metadataViews = default;
        ReadOnlySpan<VortexBuffer> metadataBuffers = default;
        CanonicalNode node = arena.GetNode(group);
        if (_objects)
        {
            CanonicalNode metadata = arena.GetNode(node.GetFieldIndex(_metadata));
            metadataViews = metadata.Views.Span;
            metadataBuffers = metadata.DataBuffers;
        }

        // The written values' buffer follows the value column's, whose views a row taking its value keeps.
        ReadOnlySpan<byte> values = root.Value >= 0 ? root.ValueViews.Span : default;
        List(root);
        try
        {
            WriteRows(root, node.Length, into, values, metadataViews, metadataBuffers, root.Value >= 0 ? root.ValueBufferCount : 0);
        }
        finally
        {
            Unlist(root);
        }
    }

    /// <summary>
    /// For each object under <paramref name="shred"/> of <see cref="ListedFields"/> fields or more, the fields each of its
    /// slots defines, in name order (<see cref="Shred.Listed"/>), found a word of 64 slots at a time from the fields'
    /// validities: a row of a log's attributes defines a dozen of the hundreds of keys shredded, each of which a row tested
    /// for its presence, 1 us a row of 200.
    /// </summary>
    private static void List(Shred shred)
    {
        switch (shred.Kind)
        {
            case ShredKind.Object:
                foreach (Shred field in shred.Fields)
                {
                    List(field);
                }

                if (shred.Typed >= 0 && shred.Fields.Length >= ListedFields)
                {
                    ListDefined(shred);
                }

                break;

            case ShredKind.Array:
                List(shred.Element!);
                break;
        }
    }

    /// <summary>The fields each slot of the object <paramref name="shred"/> defines, counted, then laid out slot after slot.</summary>
    private static void ListDefined(Shred shred)
    {
        int rows = shred.Length;
        int words = (rows + 63) >> 6;
        int[] starts = WorkArrays.Rent<int>(rows + 1);
        starts.AsSpan(0, rows + 1).Clear();
        int total = 0;
        foreach (Shred field in shred.Fields)
        {
            for (int w = 0; w < words; w++)
            {
                for (ulong bits = Presence(field, w, rows); bits != 0; bits &= bits - 1)
                {
                    starts[(w << 6) + BitOperations.TrailingZeroCount(bits) + 1]++;
                    total++;
                }
            }
        }

        for (int slot = 1; slot <= rows; slot++)
        {
            starts[slot] += starts[slot - 1];
        }

        // Each slot's fields at its start, which moves to the next slot's start as they are laid; then moved back.
        int[] defined = WorkArrays.Rent<int>(Math.Max(total, 1));
        for (int f = 0; f < shred.Fields.Length; f++)
        {
            Shred field = shred.Fields[f];
            for (int w = 0; w < words; w++)
            {
                for (ulong bits = Presence(field, w, rows); bits != 0; bits &= bits - 1)
                {
                    defined[starts[(w << 6) + BitOperations.TrailingZeroCount(bits)]++] = f;
                }
            }
        }

        for (int slot = rows; slot > 0; slot--)
        {
            starts[slot] = starts[slot - 1];
        }

        starts[0] = 0;
        shred.DefinedStarts = starts;
        shred.DefinedFields = defined;
        shred.Listed = true;
    }

    /// <summary>The slots of word <paramref name="word"/> of the <paramref name="rows"/> where <paramref name="field"/> is defined (<see cref="Defined"/>).</summary>
    private static ulong Presence(Shred field, int word, int rows)
    {
        ulong held = (field.Value >= 0 ? field.ValuePresent.Word(word, rows) : 0) | (field.Typed >= 0 ? field.TypedPresent.Word(word, rows) : 0);
        return held == 0 ? 0 : held & field.Group.Word(word, rows);
    }

    /// <summary>Gives back the lists <see cref="List"/> made under <paramref name="shred"/>.</summary>
    private static void Unlist(Shred shred)
    {
        if (shred.Listed)
        {
            WorkArrays.Return(shred.DefinedStarts);
            WorkArrays.Return(shred.DefinedFields);
            shred.DefinedStarts = [];
            shred.DefinedFields = [];
            shred.Listed = false;
        }

        foreach (Shred field in shred.Fields)
        {
            Unlist(field);
        }

        if (shred.Element is { } element)
        {
            Unlist(element);
        }
    }

    /// <summary>
    /// The next field of <paramref name="shred"/> the slot <paramref name="slot"/> defines, from <paramref name="at"/>, which
    /// moves past it: its index in the slot's list when the object lists them, among every field otherwise. The fields'
    /// count when no other is.
    /// </summary>
    private static int NextDefined(Shred shred, int slot, ReadOnlySpan<int> listed, ref int at)
    {
        if (shred.Listed)
        {
            return at < listed.Length ? listed[at++] : shred.Fields.Length;
        }

        while (at < shred.Fields.Length && !Defined(shred.Fields[at], slot))
        {
            at++;
        }

        return at < shred.Fields.Length ? at++ : shred.Fields.Length;
    }

    /// <summary>The fields slot <paramref name="slot"/> of <paramref name="shred"/> defines, when it lists them.</summary>
    private static ReadOnlySpan<int> ListedAt(Shred shred, int slot) =>
        shred.Listed ? shred.DefinedFields.AsSpan(shred.DefinedStarts[slot], shred.DefinedStarts[slot + 1] - shred.DefinedStarts[slot]) : default;

    /// <summary>
    /// The binary node of the <paramref name="rows"/> rows the slices wrote: their <paramref name="views"/> over the value
    /// column's <paramref name="valueBuffers"/> and the values rebuilt, copied into <paramref name="arena"/> in one buffer.
    /// </summary>
    internal int CompleteSlices(CanonicalArena arena, int rows, VortexBuffer views, ReadOnlySpan<VortexBuffer> valueBuffers)
    {
        int own = valueBuffers.Length;
        int count = own;
        if (_buffers.Length < own + 1)
        {
            _buffers = new VortexBuffer[own + 1];
        }

        valueBuffers.CopyTo(_buffers);
        if (_used > 0)
        {
            VortexBuffer data = arena.AllocateUninitialized(_used, 64, out Span<byte> written).Slice(0, _used);
            _scratch.AsSpan(0, _used).CopyTo(written);
            _buffers[count++] = data;
        }

        return arena.AddVarBinView(_binary, rows, Validity.NonNullable, views, _buffers.AsSpan(0, count));
    }

    /// <summary>Gives back the scratch the slices' values were written in.</summary>
    internal void EndSlices()
    {
        if (_scratch.Length != 0)
        {
            WorkArrays.Return(_scratch);
        }

        _scratch = [];
        _lastUsed = _used;
        _used = 0;
        _busy = false;
    }

    /// <summary>
    /// The plans of a context's shredded variants, kept from a batch to the next: a few, a scan's shredded columns, each by
    /// the group's type and the arena of types it was compiled with, unless that arena was cleared since.
    /// </summary>
    internal sealed class ShreddedPlans
    {
        private const int Kept = 4;

        private readonly ShreddedVariant?[] _plans = new ShreddedVariant?[Kept];
        private readonly DType[] _groups = new DType[Kept];
        private readonly DTypeArena?[] _arenas = new DTypeArena?[Kept];
        private readonly int[] _generations = new int[Kept];
        private int _next;

        /// <summary>The plan for <paramref name="group"/> compiled with <paramref name="types"/>, compiled now when none is kept or the one kept writes slices.</summary>
        internal ShreddedVariant Take(DTypeArena types, DType group)
        {
            for (int i = 0; i < Kept; i++)
            {
                if (_plans[i] is { } kept && ReferenceEquals(_arenas[i], types) && _generations[i] == types.Generation && _groups[i] == group)
                {
                    return kept._busy ? Compile(VortexTypes.FromDType(group), types) : kept;
                }
            }

            ShreddedVariant plan = Compile(VortexTypes.FromDType(group), types);
            int at = _next;
            _next = (at + 1) % Kept;
            _plans[at] = plan;
            _groups[at] = group;
            _arenas[at] = types;
            _generations[at] = types.Generation;
            return plan;
        }
    }

    /// <summary>Each row's view into <paramref name="into"/>, its value written into the scratch where the row is rebuilt.</summary>
    private void WriteRows(
        Shred root, int rows, Span<byte> into, ReadOnlySpan<byte> values, ReadOnlySpan<byte> metadataViews,
        ReadOnlySpan<VortexBuffer> metadataBuffers, int own)
    {
        VariantDictionary dictionary = default;
        ulong lastLow = 0;
        ulong lastHigh = 0;
        bool seen = false;
        for (int row = 0; row < rows; row++)
        {
            Span<byte> view = into.Slice(row * ViewSize, ViewSize);
            if (!root.Group[row])
            {
                view.Clear();
                continue;
            }

            if (root.Typed < 0 || !root.TypedPresent[row])
            {
                if (root.Value >= 0 && root.ValuePresent[row])
                {
                    values.Slice(row * ViewSize, ViewSize).CopyTo(view);
                }
                else
                {
                    // Missing where a value is required: the variant null, one byte, inline.
                    view.Clear();
                    view[0] = 1;
                }

                continue;
            }

            if (_objects)
            {
                // A view equal to the last row's views the same bytes: a column of one metadata,
                // whose views all point at one value of its dictionary, compares sixteen bytes a row.
                ReadOnlySpan<byte> metadataView = metadataViews.Slice(row * ViewSize, ViewSize);
                ulong low = MemoryMarshal.Read<ulong>(metadataView);
                ulong high = MemoryMarshal.Read<ulong>(metadataView[sizeof(ulong)..]);
                if (!seen || low != lastLow || high != lastHigh)
                {
                    dictionary = Dictionary(BlockStatsPass.Value(metadataBuffers, metadataViews, row));
                    (lastLow, lastHigh, seen) = (low, high, true);
                }
            }

            int start = _used;
            Write(root, row, in dictionary);
            CanonicalSupport.WriteView(ref MemoryMarshal.GetReference(view), ref _scratch[start], _used - start, own, start);
        }
    }

    /// <summary>The dictionary of <paramref name="metadata"/>; a new generation of field ids when it is not the last row's.</summary>
    private VariantDictionary Dictionary(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length != _lastLength || !metadata.SequenceEqual(_lastMetadata.AsSpan(0, _lastLength)))
        {
            if (_lastMetadata.Length < metadata.Length)
            {
                _lastMetadata = new byte[Math.Max(metadata.Length, 2 * _lastMetadata.Length)];
            }

            metadata.CopyTo(_lastMetadata);
            _lastLength = metadata.Length;
            _generation++;
        }

        return VariantDictionary.Read(_lastMetadata.AsSpan(0, _lastLength));
    }

    /// <summary>Appends the value of <paramref name="shred"/> at <paramref name="slot"/>; false when it is missing.</summary>
    private bool Write(Shred shred, int slot, scoped in VariantDictionary dictionary)
    {
        if (!shred.Group[slot])
        {
            return false;
        }

        bool value = shred.Value >= 0 && shred.ValuePresent[slot];
        if (shred.Typed < 0 || !shred.TypedPresent[slot])
        {
            if (!value)
            {
                return false;
            }

            Append(ValueAt(shred, slot));
            return true;
        }

        switch (shred.Kind)
        {
            case ShredKind.Object:
                WriteObject(shred, slot, value, in dictionary);
                return true;

            case ShredKind.Array:
                Conflict(shred, value);
                WriteArray(shred, slot, in dictionary);
                return true;

            default:
                Conflict(shred, value);
                WritePrimitive(shred, slot);
                return true;
        }
    }

    /// <summary>
    /// Appends the object of <paramref name="shred"/> at <paramref name="slot"/>: its shredded
    /// fields and, when <paramref name="hasValue"/>, those of the object its value holds, in name order.
    /// </summary>
    private void WriteObject(Shred shred, int slot, bool hasValue, scoped in VariantDictionary dictionary)
    {
        VariantNested unshredded = default;
        if (hasValue)
        {
            ReadOnlySpan<byte> value = ValueAt(shred, slot);
            if (value.IsEmpty || (value[0] & 0x03) != ParquetVariant.BasicObject)
            {
                throw new VortexFormatException($"{Name(shred.Path)} has a value that is not an object beside a typed object, which only an object may.");
            }

            unshredded = VariantNested.Read(value);
        }
        else if (shred.Leaves)
        {
            WriteLeaves(shred, slot, Ids(shred), in dictionary);
            return;
        }

        _ = Ids(shred);

        // The fields in name order, as pairs of an id and a source on the stack: a shredded field's
        // index, or the complement of the index of one of the value's.
        int pairs = _top;
        int count = 0;
        uint largest = 0;
        ReadOnlySpan<int> listed = ListedAt(shred, slot);
        int cursor = 0;
        int field = NextDefined(shred, slot, listed, ref cursor);
        int other = 0;
        int named = -1;
        int otherId = 0;
        int bound = 0;
        ReadOnlySpan<byte> previous = default;
        while (field < shred.Fields.Length || other < unshredded.Count)
        {
            if (other < unshredded.Count && named != other)
            {
                otherId = unshredded.IdAt(other);
                ReadOnlySpan<byte> name = dictionary[otherId];
                if (other > 0 && previous.SequenceCompareTo(name) >= 0)
                {
                    throw new VortexFormatException($"{Name(shred.Path)} holds an object whose fields are not in the order of their names.");
                }

                previous = name;
                named = other;

                // The shredded names sorted, those before the value's next one found by halves rather than each set
                // against it: 200 shredded beside a row's residual of two were 200 comparisons a row.
                (bound, bool same) = Bound(shred.Names, bound, name);
                if (same)
                {
                    // A shredded name never appears in the value, as the Arrow reader the reference
                    // relies on also refuses: the row would hold two values of one key.
                    throw new VortexFormatException(
                        $"{Name(shred.Path)} holds the field '{System.Text.Encoding.UTF8.GetString(name)}' both shredded and in its value.");
                }
            }

            if (other >= unshredded.Count || field < bound)
            {
                int id = IdOf(shred, field, in dictionary);
                if (id < 0)
                {
                    throw Unnamed(shred, field);
                }

                Push(id, field);
                largest = Math.Max(largest, (uint)id);
                count++;
                field = NextDefined(shred, slot, listed, ref cursor);
            }
            else
            {
                Push(otherId, ~other);
                largest = Math.Max(largest, (uint)otherId);
                count++;
                other++;
            }
        }

        int countWidth = count > byte.MaxValue ? 4 : 1;
        int idWidth = ParquetVariant.WidthOf(largest);
        int start = _used;
        int reserved = 1 + countWidth + (count * idWidth) + ((count + 1) * sizeof(uint));
        Reserve(reserved);
        _used += reserved;
        int offsets = Grow(count + 1);
        for (int i = 0; i < count; i++)
        {
            _stack[offsets + i] = _used - start - reserved;
            int source = _stack[pairs + (2 * i) + 1];
            if (source >= 0)
            {
                Write(shred.Fields[source], slot, in dictionary);
            }
            else
            {
                Append(unshredded.ValueAt(~source));
            }
        }

        int offsetWidth = Close(start, reserved, count, countWidth, idWidth, offsets);
        Span<byte> header = _scratch.AsSpan(start);
        header[0] = ObjectHeader(countWidth, idWidth, offsetWidth);
        int at = 1;
        ParquetVariant.Put(header, ref at, (uint)count, countWidth);
        for (int i = 0; i < count; i++)
        {
            ParquetVariant.Put(header, ref at, (uint)_stack[pairs + (2 * i)], idWidth);
        }

        for (int i = 0; i <= count; i++)
        {
            ParquetVariant.Put(header, ref at, (uint)_stack[offsets + i], offsetWidth);
        }

        _top = pairs;
    }

    /// <summary>Appends the array of <paramref name="shred"/> at <paramref name="slot"/>, a missing element the variant null.</summary>
    private void WriteArray(Shred shred, int slot, scoped in VariantDictionary dictionary)
    {
        int first = BinaryPrimitives.ReadInt32LittleEndian(shred.Data.Span[(slot * sizeof(int))..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(shred.Sizes.Span[(slot * sizeof(int))..]);
        Shred element = shred.Element!;
        if (first < 0 || count < 0 || first > element.Length - count)
        {
            throw new VortexFormatException($"{Name(shred.Path)} is a list of [{first}, {(long)first + count}) of {element.Length} elements.");
        }

        if (element.Kind is not (ShredKind.Object or ShredKind.Array))
        {
            WriteLeafArray(element, first, count);
            return;
        }

        int countWidth = count > byte.MaxValue ? 4 : 1;
        int start = _used;
        int reserved = 1 + countWidth + ((count + 1) * sizeof(uint));
        Reserve(reserved);
        _used += reserved;
        int offsets = Grow(count + 1);
        for (int i = 0; i < count; i++)
        {
            _stack[offsets + i] = _used - start - reserved;
            if (!Write(element, first + i, in dictionary))
            {
                Reserve(1);
                _scratch[_used++] = ParquetVariant.Primitive(0);
            }
        }

        int offsetWidth = Close(start, reserved, count, countWidth, 0, offsets);
        Span<byte> header = _scratch.AsSpan(start);
        header[0] = ArrayHeader(countWidth, offsetWidth);
        int at = 1;
        ParquetVariant.Put(header, ref at, (uint)count, countWidth);
        for (int i = 0; i <= count; i++)
        {
            ParquetVariant.Put(header, ref at, (uint)_stack[offsets + i], offsetWidth);
        }

        _top = offsets;
    }

    /// <summary>
    /// Appends an object of leaves whose row has no value beside its typed fields: each field's size
    /// is known before it is written, so the header goes first, at its final widths, and nothing moves.
    /// </summary>
    private void WriteLeaves(Shred shred, int slot, int[] ids, scoped in VariantDictionary dictionary)
    {
        ReadOnlySpan<int> listed = ListedAt(shred, slot);
        int fields = shred.Listed ? listed.Length : shred.Fields.Length;
        int sizes = Grow(fields);
        int count = 0;
        uint largest = 0;
        int size = 0;
        for (int i = 0; i < fields; i++)
        {
            int f = shred.Listed ? listed[i] : i;
            int bytes = LeafSize(shred.Fields[f], slot);
            _stack[sizes + i] = bytes;
            if (bytes >= 0)
            {
                if (IdOf(shred, f, in dictionary) < 0)
                {
                    throw Unnamed(shred, f);
                }

                largest = Math.Max(largest, (uint)ids[f]);
                size = checked(size + bytes);
                count++;
            }
        }

        int countWidth = count > byte.MaxValue ? 4 : 1;
        int idWidth = ParquetVariant.WidthOf(largest);
        int offsetWidth = ParquetVariant.WidthOf((uint)size);
        int length = 1 + countWidth + (count * idWidth) + ((count + 1) * offsetWidth);
        Reserve(length + size);
        Span<byte> header = _scratch.AsSpan(_used, length);
        header[0] = ObjectHeader(countWidth, idWidth, offsetWidth);
        int at = 1;
        ParquetVariant.Put(header, ref at, (uint)count, countWidth);
        for (int i = 0; i < fields; i++)
        {
            if (_stack[sizes + i] >= 0)
            {
                ParquetVariant.Put(header, ref at, (uint)ids[shred.Listed ? listed[i] : i], idWidth);
            }
        }

        uint offset = 0;
        for (int i = 0; i < fields; i++)
        {
            if (_stack[sizes + i] >= 0)
            {
                ParquetVariant.Put(header, ref at, offset, offsetWidth);
                offset += (uint)_stack[sizes + i];
            }
        }

        ParquetVariant.Put(header, ref at, offset, offsetWidth);
        _used += length;
        for (int i = 0; i < fields; i++)
        {
            if (_stack[sizes + i] >= 0)
            {
                WriteLeaf(shred.Fields[shred.Listed ? listed[i] : i], slot);
            }
        }

        _top = sizes;
    }

    /// <summary>
    /// Appends an array of the leaf <paramref name="element"/>'s <paramref name="count"/> slots from
    /// <paramref name="first"/>, header first as an object of leaves is, a missing element the variant null.
    /// </summary>
    private void WriteLeafArray(Shred element, int first, int count)
    {
        int sizes = Grow(count);
        int size = 0;
        for (int i = 0; i < count; i++)
        {
            int bytes = LeafSize(element, first + i);
            _stack[sizes + i] = bytes;
            size = checked(size + Math.Max(bytes, 1));
        }

        int countWidth = count > byte.MaxValue ? 4 : 1;
        int offsetWidth = ParquetVariant.WidthOf((uint)size);
        int length = 1 + countWidth + ((count + 1) * offsetWidth);
        Reserve(length + size);
        Span<byte> header = _scratch.AsSpan(_used, length);
        header[0] = ArrayHeader(countWidth, offsetWidth);
        int at = 1;
        ParquetVariant.Put(header, ref at, (uint)count, countWidth);
        uint offset = 0;
        for (int i = 0; i < count; i++)
        {
            ParquetVariant.Put(header, ref at, offset, offsetWidth);
            offset += (uint)Math.Max(_stack[sizes + i], 1);
        }

        ParquetVariant.Put(header, ref at, offset, offsetWidth);
        _used += length;
        for (int i = 0; i < count; i++)
        {
            if (_stack[sizes + i] >= 0)
            {
                WriteLeaf(element, first + i);
            }
            else
            {
                _scratch[_used++] = ParquetVariant.Primitive(0);
            }
        }

        _top = sizes;
    }

    /// <summary>
    /// The bytes the leaf <paramref name="shred"/> takes at <paramref name="slot"/>, or -1 when it is
    /// missing: its typed primitive's, or its value's. A leaf holds no object and no array.
    /// </summary>
    private static int LeafSize(Shred shred, int slot)
    {
        if (!shred.Group[slot])
        {
            return -1;
        }

        bool value = shred.Value >= 0 && shred.ValuePresent[slot];
        if (shred.Typed >= 0 && shred.TypedPresent[slot])
        {
            Conflict(shred, value);
            switch (shred.Kind)
            {
                case ShredKind.Bool:
                    return 1;
                case ShredKind.Fixed:
                    return 1 + shred.Width;
                case ShredKind.Decimal:
                    return 2 + shred.Width;
                default:
                    int length = BinaryPrimitives.ReadInt32LittleEndian(shred.Data.Span[(slot * ViewSize)..]);
                    return (shred.Kind == ShredKind.String && length <= ParquetVariant.MaxShortString ? 1 : 5) + length;
            }
        }

        if (!value)
        {
            return -1;
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(shred.ValueViews.Span[(slot * ViewSize)..]);
        if (size <= 0)
        {
            throw new VortexFormatException("A variant's value is empty.");
        }

        return size;
    }

    /// <summary>Appends the leaf <paramref name="shred"/> at <paramref name="slot"/>, where <see cref="LeafSize"/> found it.</summary>
    private void WriteLeaf(Shred shred, int slot)
    {
        if (shred.Typed >= 0 && shred.TypedPresent[slot])
        {
            WritePrimitive(shred, slot);
        }
        else
        {
            Append(ValueAt(shred, slot));
        }
    }

    private static byte ObjectHeader(int countWidth, int idWidth, int offsetWidth) =>
        (byte)(((((countWidth == 4 ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicObject);

    private static byte ArrayHeader(int countWidth, int offsetWidth) =>
        (byte)(((((countWidth == 4 ? 1 : 0) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicArray);

    private static VortexFormatException Unnamed(Shred shred, int field) =>
        new($"{Name(shred.Path)} has a field '{Encoding.UTF8.GetString(shred.Names[field])}' its row's metadata does not name.");

    /// <summary>
    /// Ends an object or an array written at <paramref name="start"/> past a header of
    /// <paramref name="reserved"/> bytes, which reserved four bytes an offset: the last offset, the
    /// values' size, is set and the values moved back to where the header the offsets need ends.
    /// </summary>
    /// <returns>The width of the offsets.</returns>
    private int Close(int start, int reserved, int count, int countWidth, int idWidth, int offsets)
    {
        int size = _used - start - reserved;
        _stack[offsets + count] = size;
        int offsetWidth = ParquetVariant.WidthOf((uint)size);
        int header = 1 + countWidth + (count * idWidth) + ((count + 1) * offsetWidth);
        if (header < reserved)
        {
            _scratch.AsSpan(start + reserved, size).CopyTo(_scratch.AsSpan(start + header));
            _used = start + header + size;
        }

        return offsetWidth;
    }

    /// <summary>Appends the primitive the typed column of <paramref name="shred"/> holds at <paramref name="slot"/>.</summary>
    private void WritePrimitive(Shred shred, int slot)
    {
        switch (shred.Kind)
        {
            case ShredKind.Bool:
            {
                int bit = shred.BitOffset + slot;
                bool set = ((shred.Data.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
                Reserve(1);
                _scratch[_used++] = ParquetVariant.Primitive(set ? 1 : 2);
                return;
            }

            case ShredKind.Fixed:
            {
                int width = shred.Width;
                Reserve(1 + width);
                ReadOnlySpan<byte> source = shred.Data.Span.Slice(slot * width, width);
                ref byte target = ref _scratch[_used];
                target = ParquetVariant.Primitive(shred.TypeId);
                Copy(ref Unsafe.Add(ref target, 1), ref MemoryMarshal.GetReference(source), width);
                _used += 1 + width;
                return;
            }

            case ShredKind.Decimal:
            {
                int width = shred.Width;
                Reserve(2 + width);
                _scratch[_used] = ParquetVariant.Primitive(shred.TypeId);
                _scratch[_used + 1] = shred.Scale;
                Unscaled(shred, slot, _scratch.AsSpan(_used + 2, width));
                _used += 2 + width;
                return;
            }

            default:
            {
                ReadOnlySpan<byte> bytes = BlockStatsPass.Value(shred.DataBuffers.AsSpan(0, shred.DataBufferCount), shred.Data.Span, slot);
                if (shred.Kind == ShredKind.String && bytes.Length <= ParquetVariant.MaxShortString)
                {
                    Reserve(1 + bytes.Length);
                    _scratch[_used] = (byte)((bytes.Length << 2) | ParquetVariant.BasicShortString);
                    bytes.CopyTo(_scratch.AsSpan(_used + 1));
                    _used += 1 + bytes.Length;
                    return;
                }

                Reserve(5 + bytes.Length);
                _scratch[_used] = ParquetVariant.Primitive(shred.TypeId);
                BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(_used + 1), (uint)bytes.Length);
                bytes.CopyTo(_scratch.AsSpan(_used + 5));
                _used += 5 + bytes.Length;
                return;
            }
        }
    }

    /// <summary>
    /// Copies the one to sixteen bytes of a fixed width as one word or two, where a general copy
    /// would cost a call more than the bytes it moves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy(ref byte target, ref byte source, int width)
    {
        switch (width)
        {
            case 1:
                target = source;
                break;
            case 2:
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<ushort>(ref source));
                break;
            case 4:
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<uint>(ref source));
                break;
            case 8:
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<ulong>(ref source));
                break;
            default:
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<UInt128>(ref source));
                break;
        }
    }

    /// <summary>
    /// The unscaled value of the decimal at <paramref name="slot"/>, little-endian at the variant's
    /// width, which its precision bounds; a stored value past that width is refused, not cut.
    /// </summary>
    private static void Unscaled(Shred shred, int slot, Span<byte> destination)
    {
        int storage = shred.Storage;
        ReadOnlySpan<byte> stored = shred.Data.Span.Slice(slot * storage, storage);
        if (storage == destination.Length)
        {
            stored.CopyTo(destination);
            return;
        }

        Int128 value = storage switch
        {
            1 => (sbyte)stored[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(stored),
            4 => BinaryPrimitives.ReadInt32LittleEndian(stored),
            8 => BinaryPrimitives.ReadInt64LittleEndian(stored),
            _ => BinaryPrimitives.ReadInt128LittleEndian(stored),
        };
        Int128 kept = destination.Length switch
        {
            4 => (int)value,
            8 => (long)value,
            _ => value,
        };
        if (kept != value)
        {
            throw new VortexFormatException($"{Name(shred.Path)} holds the decimal {value}, past the {destination.Length} bytes its precision allows.");
        }

        Span<byte> wide = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(wide, value);
        wide[..destination.Length].CopyTo(destination);
    }

    /// <summary>
    /// The first of <paramref name="names"/>, sorted, from <paramref name="from"/> on, not before <paramref name="name"/>, and
    /// whether it is <paramref name="name"/>; the names' count when every one is before it.
    /// </summary>
    private static (int Bound, bool Same) Bound(byte[][] names, int from, ReadOnlySpan<byte> name)
    {
        int low = from;
        int high = names.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (names[middle].AsSpan().SequenceCompareTo(name) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return (low, low < names.Length && names[low].AsSpan().SequenceEqual(name));
    }

    /// <summary>The fields from which an object lists those each slot defines rather than testing each one a slot.</summary>
    private const int ListedFields = 32;

    /// <summary>An id of <see cref="Shred.Ids"/> not looked up in the row's metadata yet.</summary>
    private const int Unresolved = int.MinValue;

    /// <summary>
    /// The ids of <paramref name="shred"/>'s fields in the row's metadata, each looked up when a row first defines its field
    /// (<see cref="IdOf"/>): a row of its own metadata, as a log's attributes are, defines a dozen fields of the hundreds
    /// shredded, whose every name searched for took 4.6 us a row of 200.
    /// </summary>
    private int[] Ids(Shred shred)
    {
        if (shred.Generation != _generation)
        {
            shred.Ids.AsSpan().Fill(Unresolved);
            shred.Generation = _generation;
        }

        return shred.Ids;
    }

    /// <summary>The id of field <paramref name="field"/> of <paramref name="shred"/> in the row's metadata, -1 for a name it lacks.</summary>
    private static int IdOf(Shred shred, int field, scoped in VariantDictionary dictionary)
    {
        int id = shred.Ids[field];
        if (id == Unresolved)
        {
            shred.Ids[field] = id = dictionary.Find(shred.Names[field]);
        }

        return id;
    }

    /// <summary>Whether the value of <paramref name="shred"/> at <paramref name="slot"/> is there: one of its columns is not null.</summary>
    private static bool Defined(Shred shred, int slot) =>
        shred.Group[slot] && ((shred.Value >= 0 && shred.ValuePresent[slot]) || (shred.Typed >= 0 && shred.TypedPresent[slot]));

    /// <summary>Refuses a primitive or an array whose value column is not null beside its typed one.</summary>
    private static void Conflict(Shred shred, bool value)
    {
        if (value)
        {
            throw new VortexFormatException($"{Name(shred.Path)} has both a value and a typed_value, which only an object may.");
        }
    }

    private static ReadOnlySpan<byte> ValueAt(Shred shred, int slot) =>
        BlockStatsPass.Value(shred.ValueBuffers.AsSpan(0, shred.ValueBufferCount), shred.ValueViews.Span, slot);

    /// <summary>Appends a value from a value column, which must hold one.</summary>
    private void Append(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new VortexFormatException("A variant's value is empty.");
        }

        Reserve(value.Length);
        value.CopyTo(_scratch.AsSpan(_used));
        _used += value.Length;
    }

    private void Reserve(int bytes)
    {
        int needed = checked(_used + bytes);
        if (needed > _scratch.Length)
        {
            byte[] larger = WorkArrays.Rent<byte>(Math.Max(needed, 2 * _scratch.Length));
            _scratch.AsSpan(0, _used).CopyTo(larger);
            if (_scratch.Length > 0)
            {
                WorkArrays.Return(_scratch);
            }

            _scratch = larger;
        }
    }

    private void Push(int id, int source)
    {
        int at = Grow(2);
        _stack[at] = id;
        _stack[at + 1] = source;
    }

    /// <summary>Takes <paramref name="count"/> ints at the top of the stack; their index.</summary>
    private int Grow(int count)
    {
        int at = _top;
        int needed = checked(at + count);
        if (needed > _stack.Length)
        {
            Array.Resize(ref _stack, Math.Max(needed, 2 * _stack.Length));
        }

        _top = needed;
        return at;
    }

    /// <summary>Binds <paramref name="shred"/> and the shreds under it to the group node <paramref name="group"/> and its children.</summary>
    private static void Bind(CanonicalArena arena, Shred shred, int group)
    {
        CanonicalNode node = arena.GetNode(group);
        shred.Length = node.Length;
        shred.Group = Present.Of(arena, node.Validity);
        if (shred.Value >= 0)
        {
            CanonicalNode values = arena.GetNode(node.GetFieldIndex(shred.Value));
            shred.ValuePresent = Present.Of(arena, values.Validity);
            shred.ValueViews = values.Views;
            shred.ValueBufferCount = Keep(ref shred.ValueBuffers, values.DataBuffers);
        }

        if (shred.Typed < 0)
        {
            return;
        }

        CanonicalNode typed = arena.GetNode(node.GetFieldIndex(shred.Typed));
        shred.TypedPresent = Present.Of(arena, typed.Validity);
        if (typed.Kind == CanonicalKind.Extension)
        {
            typed = arena.GetNode(typed.StorageIndex);
        }

        switch (shred.Kind)
        {
            case ShredKind.Bool:
                shred.Data = typed.Bits;
                shred.BitOffset = typed.BitOffset;
                break;

            case ShredKind.Fixed:
                if (typed.Kind == CanonicalKind.FixedSizeList && typed.FixedSize == (uint)shred.Width)
                {
                    typed = arena.GetNode(typed.ElementsIndex);
                    shred.Data = typed.Values;
                    break;
                }

                if (typed.Kind != CanonicalKind.Primitive || typed.PType.ByteWidth() != shred.Width)
                {
                    throw new VortexFormatException($"{Name(shred.Path)} is shredded as {shred.Width}-byte values its column does not hold.");
                }

                shred.Data = typed.Values;
                break;

            case ShredKind.Decimal:
                shred.Data = typed.Values;
                shred.Storage = DecimalStorage.ByteWidth(typed.Storage);
                if (shred.Storage > 16)
                {
                    throw new VortexUnsupportedException(Id, VortexComponentKind.Array, $"{Name(shred.Path)} is shredded as decimals stored in {shred.Storage} bytes.");
                }

                break;

            case ShredKind.String:
            case ShredKind.Binary:
                shred.Data = typed.Views;
                shred.DataBufferCount = Keep(ref shred.DataBuffers, typed.DataBuffers);
                break;

            case ShredKind.Array:
                if (typed.Kind != CanonicalKind.ListView || typed.OffsetPType != PType.I32 || typed.SizePType != PType.I32)
                {
                    throw new VortexUnsupportedException(Id, VortexComponentKind.Array, $"{Name(shred.Path)} is shredded as a list this build does not read: a list view of i32 offsets and sizes is read.");
                }

                shred.Data = typed.Offsets;
                shred.Sizes = typed.Sizes;
                Bind(arena, shred.Element!, typed.ElementsIndex);
                break;

            case ShredKind.Object:
                for (int i = 0; i < shred.Fields.Length; i++)
                {
                    Bind(arena, shred.Fields[i], typed.GetFieldIndex(shred.Columns[i]));
                }

                break;
        }
    }

    /// <summary>Copies <paramref name="buffers"/> into <paramref name="kept"/>, grown as needed; their count.</summary>
    private static int Keep(ref VortexBuffer[] kept, ReadOnlySpan<VortexBuffer> buffers)
    {
        if (kept.Length < buffers.Length)
        {
            kept = new VortexBuffer[buffers.Length];
        }

        buffers.CopyTo(kept);
        return buffers.Length;
    }

    /// <summary>The plan of a group of a value and a typed_value, the variant's own when <paramref name="top"/>.</summary>
    private static Shred Pair(VortexType group, string path, bool top, ref bool objects)
    {
        Shred shred = new() { Path = path };
        ReadOnlySpan<VortexField> fields = group.Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            switch (fields[i].Name)
            {
                case "value" when shred.Value < 0:
                    if (fields[i].Type.Kind != VortexTypeKind.Binary)
                    {
                        throw new VortexFormatException($"{Name(path)} has a value of {fields[i].Type}, not of binary.");
                    }

                    shred.Value = i;
                    break;

                case "typed_value" when shred.Typed < 0:
                    shred.Typed = i;
                    break;

                case "metadata" when top:
                    break;

                default:
                    throw new VortexFormatException($"{Name(path)} holds a field '{fields[i].Name}', which is neither its value nor its typed_value.");
            }
        }

        if (shred.Value < 0 && shred.Typed < 0)
        {
            throw new VortexFormatException($"{Name(path)} holds neither a value nor a typed_value.");
        }

        if (shred.Typed < 0)
        {
            return shred;
        }

        VortexType typed = fields[shred.Typed].Type;
        switch (typed.Kind)
        {
            case VortexTypeKind.Struct:
                objects = true;
                Object(shred, typed, path, ref objects);
                break;

            case VortexTypeKind.List:
                if (typed.ElementType is not { Kind: VortexTypeKind.Struct } element)
                {
                    throw new VortexFormatException($"{Name(path)} is shredded as a list whose element is not a group of a value and a typed_value.");
                }

                shred.Kind = ShredKind.Array;
                shred.Element = Pair(element, path + "[]", top: false, ref objects);
                break;

            default:
                Primitive(shred, typed);
                break;
        }

        return shred;
    }

    /// <summary>The plan of a shredded object: its fields' groups, in the unsigned order of their names' bytes.</summary>
    private static void Object(Shred shred, VortexType typed, string path, ref bool objects)
    {
        ReadOnlySpan<VortexField> fields = typed.Fields;
        byte[][] names = new byte[fields.Length][];
        int[] columns = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(fields[i].Name);
            columns[i] = i;
        }

        Array.Sort(names, columns, Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b)));
        shred.Kind = ShredKind.Object;
        shred.Names = names;
        shred.Columns = columns;
        shred.Ids = new int[fields.Length];
        shred.Fields = new Shred[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            VortexField field = fields[columns[i]];
            if (i > 0 && names[i - 1].AsSpan().SequenceEqual(names[i]))
            {
                throw new VortexFormatException($"{Name(path)} shreds the field '{field.Name}' twice.");
            }

            if (field.Type.Kind != VortexTypeKind.Struct)
            {
                throw new VortexFormatException($"{Name(path)} shreds the field '{field.Name}' as {field.Type}, not as a group of a value and a typed_value.");
            }

            shred.Fields[i] = Pair(field.Type, path.Length == 0 ? field.Name : path + "." + field.Name, top: false, ref objects);
        }

        shred.Leaves = shred.Fields.All(f => f.Kind is not (ShredKind.Object or ShredKind.Array));
    }

    /// <summary>The variant type a typed column of <paramref name="type"/> holds, by the standard's table of shredded types.</summary>
    private static void Primitive(Shred shred, VortexType type)
    {
        (ShredKind kind, int typeId, int width) = type.Kind switch
        {
            VortexTypeKind.Bool => (ShredKind.Bool, 1, 0),
            VortexTypeKind.Primitive => type.PrimitiveType switch
            {
                PType.I8 => (ShredKind.Fixed, 3, 1),
                PType.I16 => (ShredKind.Fixed, 4, 2),
                PType.I32 => (ShredKind.Fixed, 5, 4),
                PType.I64 => (ShredKind.Fixed, 6, 8),
                PType.F32 => (ShredKind.Fixed, 14, 4),
                PType.F64 => (ShredKind.Fixed, 7, 8),
                _ => default,
            },
            VortexTypeKind.Decimal when type.Scale is >= 0 and <= 38 => type.Precision switch
            {
                <= 9 => (ShredKind.Decimal, 8, 4),
                <= 18 => (ShredKind.Decimal, 9, 8),
                <= 38 => (ShredKind.Decimal, 10, 16),
                _ => default,
            },
            VortexTypeKind.Utf8 => (ShredKind.String, 16, 0),
            VortexTypeKind.Binary => (ShredKind.Binary, 15, 0),
            VortexTypeKind.Extension => type.ExtensionId switch
            {
                ExtensionIds.Date when type.Unit == TimeUnit.Days => (ShredKind.Fixed, 11, 4),
                ExtensionIds.Time when type.Unit == TimeUnit.Microseconds => (ShredKind.Fixed, 17, 8),
                ExtensionIds.Timestamp when type.Unit == TimeUnit.Microseconds => (ShredKind.Fixed, type.TimeZone is null ? 13 : 12, 8),
                ExtensionIds.Timestamp when type.Unit == TimeUnit.Nanoseconds => (ShredKind.Fixed, type.TimeZone is null ? 19 : 18, 8),
                ExtensionIds.Uuid => (ShredKind.Fixed, 20, 16),
                _ => default,
            },
            _ => default,
        };

        if (kind == ShredKind.Value)
        {
            throw new VortexUnsupportedException(Id, VortexComponentKind.Array, $"{Name(shred.Path)} is shredded as {type}, which no variant type matches.");
        }

        shred.Kind = kind;
        shred.TypeId = (byte)typeId;
        shred.Width = width;
        shred.Scale = (byte)type.Scale;
    }

    /// <summary>What a message calls the value at <paramref name="path"/>: the variant, a field by its dotted path, or an element of either.</summary>
    private static string Name(string path) => path.EndsWith("[]", StringComparison.Ordinal)
        ? $"An element of {Of(path[..^2])}"
        : path.Length == 0 ? "The variant" : $"The variant's field '{path}'";

    private static string Of(string path) => path.EndsWith("[]", StringComparison.Ordinal)
        ? $"an element of {Of(path[..^2])}"
        : path.Length == 0 ? "the variant" : $"the variant's field '{path}'";

    /// <summary>Which slots of a node hold a value: every one, none, or those a bitmap sets.</summary>
    private struct Present
    {
        private const byte All = 0;
        private const byte None = 1;
        private const byte Bits = 2;

        private VortexBuffer _bits;
        private int _offset;
        private byte _mode;

        internal static Present Of(CanonicalArena arena, Validity validity)
        {
            Present present = default;
            if (validity.Kind == ValidityKind.AllInvalid)
            {
                present._mode = None;
            }
            else if (validity.Kind == ValidityKind.Bitmap)
            {
                CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                present._bits = bits.Bits;
                present._offset = bits.BitOffset;
                present._mode = Bits;
            }

            return present;
        }

        internal readonly bool this[int slot]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_mode == All)
                {
                    return true;
                }

                int at = _offset + slot;
                return _mode == Bits && ((_bits.Span[at >> 3] >> (at & 7)) & 1) != 0;
            }
        }

        /// <summary>The slots <c>64 * word</c> to <c>64 * word + 63</c> of the <paramref name="rows"/> as bits, the first lowest; those past the rows clear.</summary>
        internal readonly ulong Word(int word, int rows)
        {
            if (_mode != Bits)
            {
                int count = Math.Min(64, rows - (word << 6));
                return _mode == None ? 0 : count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            }

            return BitsWord(_bits.Span, _offset, word, rows);
        }
    }

    /// <summary>
    /// Bits <c>64 * word</c> to <c>64 * word + 63</c> of the <paramref name="rows"/> bits <paramref name="offset"/> bits into
    /// <paramref name="bits"/>, least significant first, as one word, those past the rows clear.
    /// </summary>
    internal static ulong BitsWord(ReadOnlySpan<byte> bits, int offset, int word, int rows)
    {
        int first = word << 6;
        int count = Math.Min(64, rows - first);
        ulong mask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
        int at = offset + first;
        int start = at >> 3;
        int shift = at & 7;
        int available = bits.Length - start;
        ulong value;
        if (available >= 8)
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(bits[start..]) >> shift;
            if (shift != 0 && available > 8)
            {
                value |= (ulong)bits[start + 8] << (64 - shift);
            }
        }
        else
        {
            value = 0;
            for (int b = 0; b < available; b++)
            {
                value |= (ulong)bits[start + b] << (8 * b);
            }

            value >>= shift;
        }

        return value & mask;
    }

    /// <summary>
    /// A group of a value and a typed_value, compiled once; and, for the batch being built, the
    /// buffers of its columns.
    /// </summary>
    private sealed class Shred
    {
        internal ShredKind Kind;

        /// <summary>The field of the group the value and the typed value are, or -1.</summary>
        internal int Value = -1;
        internal int Typed = -1;
        internal string Path = "";

        /// <summary>A primitive's type id, its width past the header, and a decimal's scale.</summary>
        internal byte TypeId;
        internal int Width;
        internal byte Scale;

        /// <summary>An object's fields, in name order; each one's field of the typed struct, its name, and its id in the row's metadata.</summary>
        internal Shred[] Fields = [];
        internal int[] Columns = [];
        internal byte[][] Names = [];
        internal int[] Ids = [];

        /// <summary>
        /// Whether the object lists the fields each slot bound defines (<see cref="List"/>): the slot's in
        /// <see cref="DefinedFields"/> from <see cref="DefinedStarts"/> at the slot to it at the next, rented for a slice.
        /// </summary>
        internal bool Listed;
        internal int[] DefinedStarts = [];
        internal int[] DefinedFields = [];

        /// <summary>Whether an object's fields are all leaves, which hold no object and no array.</summary>
        internal bool Leaves;

        /// <summary>The generation of metadata <see cref="Ids"/> was looked up in.</summary>
        internal int Generation = -1;

        /// <summary>An array's element.</summary>
        internal Shred? Element;

        // The batch's.
        internal int Length;
        internal Present Group;
        internal Present ValuePresent;
        internal VortexBuffer ValueViews;
        internal VortexBuffer[] ValueBuffers = [];
        internal int ValueBufferCount;
        internal Present TypedPresent;

        /// <summary>A fixed width's or a decimal's values, a boolean's bits, a string's views, or an array's offsets.</summary>
        internal VortexBuffer Data;
        internal int BitOffset;
        internal VortexBuffer[] DataBuffers = [];
        internal int DataBufferCount;

        /// <summary>An array's sizes.</summary>
        internal VortexBuffer Sizes;

        /// <summary>A decimal's bytes a value as the column stores it.</summary>
        internal int Storage;
    }
}
