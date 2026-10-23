using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Writing;

/// <summary>
/// A chunk of a variant column shredded as the writer writes it, in the form the reference stores: the
/// rows' metadata, the residual, and the shredded tree. Each row's value is read once. What the
/// shredding names goes to typed columns, a key's value of another class to that key's own values, and
/// everything else stays in the residual, so a shredded key never appears in it.
/// </summary>
/// <remarks>
/// <para>
/// The tree follows the Parquet shredding with its wrappers taken off. An object is a struct by key,
/// valid where the row's value is an object. A key whose values all fit its typed column is that
/// column. A key that also holds values of its own is a variant of those values and its typed column,
/// valid where the key is present, over the same rows' metadata. A list is a list of its elements'
/// typed column, valid where every element fits. A row whose value is not of the node's shape keeps it
/// whole in the node's values.
/// </para>
/// <para>
/// Integers of every width go to one 64-bit column, and come back at that width: equal under the
/// format's equivalence classes, not in width.
/// </para>
/// </remarks>
internal static class VariantShredder
{
    private const int ViewSize = 16;

    /// <summary>
    /// Shreds <paramref name="node"/>, a <c>Struct{metadata, value}</c> wearing a variant dtype, as
    /// <paramref name="plan"/> says: a node of three fields, the rows' metadata, the residual and the
    /// shredded tree, wearing the same dtype and validity, which the blob writer writes as a
    /// <c>vortex.variant</c>. The tree's dtypes are made in <paramref name="types"/>.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal static int Shred(CanonicalArena arena, DTypeArena types, int node, VariantShredding plan, Indexes.ChunkBlockBounds? bounds = null)
    {
        CanonicalNode variant = arena.GetNode(node);
        int rows = variant.Length;
        int metadata = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0));
        int value = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(1));
        CanonicalNode metadataNode = arena.GetNode(metadata);
        CanonicalNode valueNode = arena.GetNode(value);
        Group root = Group.For(plan);

        // The keys whose values are counted block by block as they are typed.
        if (bounds is not null && root is ObjectGroup keys)
        {
            foreach (Indexes.ChunkBlockBounds.KeyBlocks key in bounds.Keys)
            {
                if (keys.FieldAt(key.Index) is PrimitiveGroup typed)
                {
                    typed.Bounds = key;
                }
            }
        }

        // Rows in a run of the same metadata share its dictionary, which each object node looks its keys up in once: a run
        // a source's rows, which the slice's sort keeps together.
        ReadOnlySpan<byte> previous = default;
        int epoch = -1;
        for (int row = 0; row < rows; row++)
        {
            if (!ArenaWords.IsValid(arena, node, row))
            {
                root.Absent();
                continue;
            }

            ReadOnlySpan<byte> bytes = ArenaWords.IsValid(arena, value, row) ? LiteralReader.ViewAt(valueNode, row) : VariantEncoding.NullValue;
            ReadOnlySpan<byte> metadataBytes = LiteralReader.ViewAt(metadataNode, row);
            if (epoch < 0 || !metadataBytes.SequenceEqual(previous))
            {
                epoch++;
                previous = metadataBytes;
            }

            VariantDictionary dictionary = VariantDictionary.Read(metadataBytes);
            root.Present(in dictionary, epoch, bytes);
        }

        int tree = root.Typed(arena, types, rows, metadata, out _);
        int residual = root.Values.Build(arena, types, types.Binary(Nullability.Nullable), rows);
        return arena.AddStruct(variant.DType, rows, variant.Validity, [metadata, residual, tree]);
    }

    /// <summary>
    /// The values of each of <paramref name="keys"/>, top-level keys of the objects of <paramref name="node"/>,
    /// a <c>Struct{metadata, value}</c> wearing a variant dtype, each in a typed column of its class over the
    /// rows: null where the row is null or no object, where its object does not hold the key, or holds a
    /// value of another class. Each row's value is read once, and a key's name matched once per run of rows
    /// whose metadata bytes are the same. The columns' dtypes are made in <paramref name="types"/>.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal static int[] KeyColumns(CanonicalArena arena, DTypeArena types, int node, IReadOnlyList<(string Key, VariantClass Class)> keys)
    {
        CanonicalNode variant = arena.GetNode(node);
        int rows = variant.Length;
        int metadata = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0));
        int value = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(1));
        CanonicalNode metadataNode = arena.GetNode(metadata);
        CanonicalNode valueNode = arena.GetNode(value);
        TypedColumn[] columns = new TypedColumn[keys.Count];
        byte[][] utf8 = new byte[keys.Count][];
        for (int k = 0; k < columns.Length; k++)
        {
            columns[k] = TypedColumn.For(keys[k].Class);
            utf8[k] = System.Text.Encoding.UTF8.GetBytes(keys[k].Key);
        }

        // Each dictionary id's key among those asked, -1 for another, -2 not yet matched: per run of the same metadata.
        int[] matched = [];
        ReadOnlySpan<byte> last = default;
        bool[] seen = new bool[keys.Count];
        for (int row = 0; row < rows; row++)
        {
            Array.Clear(seen);
            if (ArenaWords.IsValid(arena, node, row) && ArenaWords.IsValid(arena, value, row))
            {
                ReadOnlySpan<byte> bytes = LiteralReader.ViewAt(valueNode, row);
                if ((bytes[0] & 0x03) == ParquetVariant.BasicObject)
                {
                    ReadOnlySpan<byte> names = LiteralReader.ViewAt(metadataNode, row);
                    VariantDictionary dictionary = VariantDictionary.Read(names);
                    if (!names.SequenceEqual(last) || matched.Length < dictionary.Count)
                    {
                        matched = new int[dictionary.Count];
                        matched.AsSpan().Fill(-2);
                        last = names;
                    }

                    VariantNested fields = VariantNested.Read(bytes);
                    for (int i = 0; i < fields.Count; i++)
                    {
                        int id = fields.IdAt(i);
                        if (matched[id] == -2)
                        {
                            matched[id] = -1;
                            ReadOnlySpan<byte> name = dictionary[id];
                            for (int k = 0; k < keys.Count; k++)
                            {
                                if (name.SequenceEqual(utf8[k]))
                                {
                                    matched[id] = k;
                                    break;
                                }
                            }
                        }

                        int key = matched[id];
                        if (key >= 0 && !seen[key] && OfClass(keys[key].Class, fields.ValueAt(i)))
                        {
                            columns[key].Append(fields.ValueAt(i));
                            seen[key] = true;
                        }
                    }
                }
            }

            for (int k = 0; k < columns.Length; k++)
            {
                if (!seen[k])
                {
                    columns[k].AppendNull();
                }
            }
        }

        int[] built = new int[columns.Length];
        for (int k = 0; k < built.Length; k++)
        {
            built[k] = columns[k].Build(arena, types, rows, out _);
        }

        return built;
    }

    /// <summary>Whether <paramref name="value"/> is of <paramref name="class"/>, from its header byte alone (<see cref="VariantClasses.OfHeader"/>).</summary>
    private static bool OfClass(VariantClass @class, ReadOnlySpan<byte> value) => VariantClasses.OfHeader(value[0]) == @class;

    /// <summary>
    /// One node of the shredding over the rows: its typed column, what of each row it does not take, and
    /// whether the row holds it at all.
    /// </summary>
    private abstract class Group
    {
        /// <summary>What the node does not take of each row: a value of another shape or class, an object's other keys.</summary>
        internal BinaryColumn Values { get; } = new BinaryColumn();

        /// <summary>Whether each row holds the node: the key is in its object.</summary>
        protected Bits Held { get; } = new Bits();

        /// <summary>Whether some row holds the node.</summary>
        internal bool HeldAny => Held.SetCount > 0;

        internal static Group For(VariantShredding plan) => plan.Kind switch
        {
            VariantShreddingKind.Object => new ObjectGroup(plan),
            VariantShreddingKind.List => new ListGroup(plan.Class),
            _ => new PrimitiveGroup(plan.Class),
        };

        /// <summary>A row that does not hold the node.</summary>
        internal abstract void Absent();

        /// <summary>
        /// A row whose node holds <paramref name="value"/>, its keys named by <paramref name="dictionary"/>, the same as every
        /// row of its run of <paramref name="epoch"/>.
        /// </summary>
        internal abstract void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value);

        /// <summary>The node's typed column over <paramref name="rows"/> rows.</summary>
        internal abstract int Typed(CanonicalArena arena, DTypeArena types, int rows, int metadata, out DType dtype);

        /// <summary>
        /// What the node is in its object's struct: its typed column when no row kept a value of its
        /// own, and otherwise a variant of its values and its typed column, valid where the key is
        /// present, over the rows' metadata <paramref name="metadata"/>.
        /// </summary>
        internal int Field(CanonicalArena arena, DTypeArena types, int rows, int metadata, out DType dtype)
        {
            int typed = Typed(arena, types, rows, metadata, out DType typedType);
            if (!Values.HoldsAny)
            {
                dtype = typedType;
                return typed;
            }

            int values = Values.Build(arena, types, types.Binary(Nullability.Nullable), rows);
            dtype = types.Variant(Nullability.Nullable);
            return arena.AddStruct(dtype, rows, Held.Validity(arena, types, rows), [metadata, values, typed]);
        }
    }

    /// <summary>The values of one class in a typed column, any other kept whole.</summary>
    private sealed class PrimitiveGroup(VariantClass @class) : Group
    {
        private readonly TypedColumn _typed = TypedColumn.For(@class);

        /// <summary>Where the values typed are counted block by block; null when they are not.</summary>
        internal Indexes.ChunkBlockBounds.KeyBlocks? Bounds { get; set; }

        internal override void Absent()
        {
            Held.Append(false);
            _typed.AppendNull();
            Values.AppendNull();
        }

        internal override void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value)
        {
            Held.Append(true);
            VariantScalar scalar = VariantScalar.Read(value);
            if (_typed.Takes(scalar.Kind))
            {
                _typed.Append(value);
                Values.AppendNull();
                Bounds?.Note(Held.Count - 1, scalar);
                return;
            }

            _typed.AppendNull();
            Values.Append(value);
        }

        internal override int Typed(CanonicalArena arena, DTypeArena types, int rows, int metadata, out DType dtype) =>
            _typed.Build(arena, types, rows, out dtype);
    }

    /// <summary>Some keys of an object, each a node of its own; the other keys kept in an object of their own.</summary>
    private sealed class ObjectGroup : Group
    {
        private readonly VariantShredding _plan;
        private readonly Group[] _fields;
        private readonly int[] _found;
        private readonly Bits _objects = new Bits();
        private int[] _kept = new int[8];

        // The plan's key of each field id of the dictionary the rows of run _epoch share: rows of one source share their
        // metadata, whose field names a search of the plan per field and per row looked up again.
        private int[] _keyOf = [];
        private int _epoch = -1;

        /// <summary>The node of the plan's key <paramref name="index"/>.</summary>
        internal Group FieldAt(int index) => _fields[index];

        internal ObjectGroup(VariantShredding plan)
        {
            _plan = plan;
            _fields = new Group[plan.Keys.Count];
            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i] = For(plan.Keys[i].Field);
            }

            _found = new int[_fields.Length];
        }

        internal override void Absent()
        {
            Held.Append(false);
            _objects.Append(false);
            foreach (Group field in _fields)
            {
                field.Absent();
            }

            Values.AppendNull();
        }

        internal override void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value)
        {
            Held.Append(true);
            if ((value[0] & 0x03) != ParquetVariant.BasicObject)
            {
                _objects.Append(false);
                foreach (Group field in _fields)
                {
                    field.Absent();
                }

                Values.Append(value);
                return;
            }

            VariantNested fields = VariantNested.Read(value);
            if (_kept.Length < fields.Count)
            {
                _kept = new int[Math.Max(fields.Count, 2 * _kept.Length)];
            }

            // The plan's key of each field id of the run's dictionary, looked up once a run: -2 for an id not looked up yet.
            if (epoch != _epoch)
            {
                _epoch = epoch;
                if (_keyOf.Length < dictionary.Count)
                {
                    _keyOf = new int[Math.Max(dictionary.Count, 2 * _keyOf.Length)];
                }

                _keyOf.AsSpan(0, dictionary.Count).Fill(-2);
            }

            _found.AsSpan().Fill(-1);
            int kept = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                int id = fields.IdAt(i);
                int key;
                if ((uint)id < (uint)dictionary.Count)
                {
                    key = _keyOf[id];
                    if (key == -2)
                    {
                        _keyOf[id] = key = _plan.IndexOf(dictionary[id]);
                    }
                }
                else
                {
                    // An id out of the dictionary: its read refuses it.
                    key = _plan.IndexOf(dictionary[id]);
                }

                if (key >= 0)
                {
                    _found[key] = i;
                }
                else
                {
                    _kept[kept++] = i;
                }
            }

            _objects.Append(true);
            for (int k = 0; k < _fields.Length; k++)
            {
                if (_found[k] >= 0)
                {
                    _fields[k].Present(in dictionary, epoch, fields.ValueAt(_found[k]));
                }
                else
                {
                    _fields[k].Absent();
                }
            }

            if (kept == 0)
            {
                Values.AppendNull();
            }
            else
            {
                Values.AppendObject(fields, _kept.AsSpan(0, kept));
            }
        }

        /// <remarks>
        /// The chunk shreds only the keys its rows hold: a key no row holds would cost its nodes and a
        /// typed column of nulls for nothing, and a chunk's tree is its own, which every reader takes.
        /// When no row holds any, every key is kept, so that the tree is never empty.
        /// </remarks>
        internal override int Typed(CanonicalArena arena, DTypeArena types, int rows, int metadata, out DType dtype)
        {
            int held = 0;
            foreach (Group field in _fields)
            {
                held += field.HeldAny ? 1 : 0;
            }

            int count = held == 0 ? _fields.Length : held;
            int[] nodes = new int[count];
            DType[] fields = new DType[count];
            string[] names = new string[count];
            int at = 0;
            for (int k = 0; k < _fields.Length; k++)
            {
                if (held > 0 && !_fields[k].HeldAny)
                {
                    continue;
                }

                nodes[at] = _fields[k].Field(arena, types, rows, metadata, out fields[at]);
                names[at] = _plan.Keys[k].Name;
                at++;
            }

            dtype = types.Struct(names, fields, Nullability.Nullable);
            return arena.AddStruct(dtype, rows, _objects.Validity(arena, types, rows), nodes);
        }
    }

    /// <summary>A list whose elements are all of one class, in a list of its elements' typed column; any other kept whole.</summary>
    private sealed class ListGroup(VariantClass @class) : Group
    {
        private readonly TypedColumn _elements = TypedColumn.For(@class);
        private readonly Bits _lists = new Bits();
        private int[] _sizes = new int[64];
        private int _rows;

        internal override void Absent()
        {
            Held.Append(false);
            _lists.Append(false);
            Size(0);
            Values.AppendNull();
        }

        internal override void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value)
        {
            Held.Append(true);
            if ((value[0] & 0x03) == ParquetVariant.BasicArray)
            {
                VariantNested elements = VariantNested.Read(value);
                bool fit = true;
                for (int i = 0; i < elements.Count && fit; i++)
                {
                    fit = _elements.Fits(elements.ValueAt(i));
                }

                if (fit)
                {
                    for (int i = 0; i < elements.Count; i++)
                    {
                        _elements.Append(elements.ValueAt(i));
                    }

                    _lists.Append(true);
                    Size(elements.Count);
                    Values.AppendNull();
                    return;
                }
            }

            _lists.Append(false);
            Size(0);
            Values.Append(value);
        }

        internal override int Typed(CanonicalArena arena, DTypeArena types, int rows, int metadata, out DType dtype)
        {
            int elements = _elements.Build(arena, types, _elements.Count, out DType elementType);
            dtype = types.List(elementType, Nullability.Nullable);
            VortexBuffer offsets = arena.AllocateUninitialized(Math.Max(rows * sizeof(int), 1), 8, out Span<byte> into).Slice(0, rows * sizeof(int));
            VortexBuffer sizes = arena.AllocateUninitialized(Math.Max(rows * sizeof(int), 1), 8, out Span<byte> sized).Slice(0, rows * sizeof(int));
            Span<int> starts = MemoryMarshal.Cast<byte, int>(into);
            int at = 0;
            for (int row = 0; row < rows; row++)
            {
                starts[row] = at;
                at += _sizes[row];
            }

            MemoryMarshal.AsBytes(_sizes.AsSpan(0, rows)).CopyTo(sized);
            return arena.AddListView(dtype, rows, _lists.Validity(arena, types, rows), elements, offsets, PType.I32, sizes, PType.I32);
        }

        private void Size(int count)
        {
            if (_rows == _sizes.Length)
            {
                Array.Resize(ref _sizes, 2 * _sizes.Length);
            }

            _sizes[_rows++] = count;
        }
    }

    /// <summary>The typed column of one class, its rows appended one at a time.</summary>
    private abstract class TypedColumn(VariantClass @class)
    {
        protected Bits Valid { get; } = new Bits();

        /// <summary>The rows appended.</summary>
        internal int Count => Valid.Count;

        internal static TypedColumn For(VariantClass @class) => @class switch
        {
            VariantClass.Integer => new IntegerColumn(),
            VariantClass.Double => new DoubleColumn(),
            VariantClass.String => new StringColumn(),
            _ => new BooleanColumn(),
        };

        /// <summary>Whether <paramref name="value"/> is of the column's class.</summary>
        internal bool Fits(ReadOnlySpan<byte> value) => Takes(VariantScalar.Read(value).Kind);

        /// <summary>Whether a value of <paramref name="kind"/> is of the column's class.</summary>
        internal bool Takes(VariantKind kind) => VariantClasses.Of(kind) == @class;

        /// <summary>Appends <paramref name="value"/>, which fits.</summary>
        internal abstract void Append(ReadOnlySpan<byte> value);

        internal abstract void AppendNull();

        internal abstract int Build(CanonicalArena arena, DTypeArena types, int rows, out DType dtype);
    }

    /// <summary>Integers of every width, as 64-bit integers.</summary>
    private sealed class IntegerColumn() : TypedColumn(VariantClass.Integer)
    {
        private long[] _values = new long[64];

        internal override void Append(ReadOnlySpan<byte> value) => Add((long)VariantScalar.Read(value).Integer, true);

        internal override void AppendNull() => Add(0, false);

        internal override int Build(CanonicalArena arena, DTypeArena types, int rows, out DType dtype)
        {
            dtype = types.Primitive(PType.I64, Nullability.Nullable);
            return Fixed(arena, dtype, rows, Valid.Validity(arena, types, rows), PType.I64, MemoryMarshal.AsBytes(_values.AsSpan(0, rows)));
        }

        private void Add(long value, bool valid)
        {
            int row = Valid.Count;
            if (row == _values.Length)
            {
                Array.Resize(ref _values, 2 * _values.Length);
            }

            _values[row] = value;
            Valid.Append(valid);
        }
    }

    /// <summary>64-bit floats.</summary>
    private sealed class DoubleColumn() : TypedColumn(VariantClass.Double)
    {
        private double[] _values = new double[64];

        internal override void Append(ReadOnlySpan<byte> value) => Add(VariantScalar.Read(value).Number, true);

        internal override void AppendNull() => Add(0, false);

        internal override int Build(CanonicalArena arena, DTypeArena types, int rows, out DType dtype)
        {
            dtype = types.Primitive(PType.F64, Nullability.Nullable);
            return Fixed(arena, dtype, rows, Valid.Validity(arena, types, rows), PType.F64, MemoryMarshal.AsBytes(_values.AsSpan(0, rows)));
        }

        private void Add(double value, bool valid)
        {
            int row = Valid.Count;
            if (row == _values.Length)
            {
                Array.Resize(ref _values, 2 * _values.Length);
            }

            _values[row] = value;
            Valid.Append(valid);
        }
    }

    /// <summary>Booleans.</summary>
    private sealed class BooleanColumn() : TypedColumn(VariantClass.Boolean)
    {
        private readonly Bits _values = new Bits();

        internal override void Append(ReadOnlySpan<byte> value)
        {
            _values.Append(VariantScalar.Read(value).Integer != 0);
            Valid.Append(true);
        }

        internal override void AppendNull()
        {
            _values.Append(false);
            Valid.Append(false);
        }

        internal override int Build(CanonicalArena arena, DTypeArena types, int rows, out DType dtype)
        {
            dtype = types.Bool(Nullability.Nullable);
            return arena.AddBool(dtype, rows, Valid.Validity(arena, types, rows), _values.Buffer(arena, rows), 0);
        }
    }

    /// <summary>UTF-8 text.</summary>
    private sealed class StringColumn() : TypedColumn(VariantClass.String)
    {
        private readonly BinaryColumn _values = new BinaryColumn();

        internal override void Append(ReadOnlySpan<byte> value)
        {
            _values.Append(VariantScalar.Read(value).Bytes);
            Valid.Append(true);
        }

        internal override void AppendNull()
        {
            _values.AppendNull();
            Valid.Append(false);
        }

        internal override int Build(CanonicalArena arena, DTypeArena types, int rows, out DType dtype)
        {
            dtype = types.Utf8(Nullability.Nullable);
            return _values.Build(arena, types, dtype, rows);
        }
    }

    /// <summary>A fixed-width column of <paramref name="values"/>, copied into the arena.</summary>
    private static int Fixed(CanonicalArena arena, DType dtype, int rows, Validity validity, PType ptype, ReadOnlySpan<byte> values)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(Math.Max(values.Length, 1), 64, out Span<byte> into).Slice(0, values.Length);
        values.CopyTo(into);
        return arena.AddPrimitive(dtype, rows, validity, ptype, buffer);
    }

    /// <summary>Bits appended one at a time, as a validity or a column of booleans.</summary>
    private sealed class Bits
    {
        private ulong[] _words = new ulong[1];
        private int _set;

        /// <summary>The bits set.</summary>
        internal int SetCount => _set;

        internal int Count { get; private set; }

        internal void Append(bool bit)
        {
            int at = Count;
            if (at >> 6 == _words.Length)
            {
                Array.Resize(ref _words, 2 * _words.Length);
            }

            if (bit)
            {
                _words[at >> 6] |= 1UL << (at & 63);
                _set++;
            }

            Count = at + 1;
        }

        /// <summary>The validity of <paramref name="rows"/> rows these bits say are valid.</summary>
        internal Validity Validity(CanonicalArena arena, DTypeArena types, int rows)
        {
            if (_set == rows)
            {
                return Vorticity.Arrays.Validity.AllValid;
            }

            if (_set == 0)
            {
                return Vorticity.Arrays.Validity.AllInvalid;
            }

            return Vorticity.Arrays.Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Vorticity.Arrays.Validity.NonNullable, Buffer(arena, rows), 0));
        }

        /// <summary>The first <paramref name="rows"/> bits, LSB first, copied into the arena.</summary>
        internal VortexBuffer Buffer(CanonicalArena arena, int rows)
        {
            int bytes = (rows + 7) / 8;
            VortexBuffer buffer = arena.Allocate(Math.Max(bytes, 1), 8, out Span<byte> into).Slice(0, bytes);
            MemoryMarshal.AsBytes(_words.AsSpan()).Slice(0, bytes).CopyTo(into);
            return buffer;
        }
    }

    /// <summary>Byte strings appended one at a time, held end to end until they are copied into the arena as views and one data buffer.</summary>
    private sealed class BinaryColumn
    {
        private readonly Bits _valid = new Bits();
        private byte[] _data = new byte[256];
        private int[] _starts = new int[64];
        private int _used;
        private int _held;

        /// <summary>Whether any row holds a value.</summary>
        internal bool HoldsAny => _held > 0;

        internal void Append(ReadOnlySpan<byte> value)
        {
            Reserve(value.Length);
            value.CopyTo(_data.AsSpan(_used));
            Close(_used + value.Length, true);
        }

        internal void AppendNull() => Close(_used, false);

        /// <summary>Appends the object of the fields <paramref name="kept"/> of <paramref name="fields"/>, in their order, which is their keys'.</summary>
        internal void AppendObject(VariantNested fields, ReadOnlySpan<int> kept)
        {
            int maxId = 0;
            int size = 0;
            foreach (int i in kept)
            {
                maxId = Math.Max(maxId, fields.IdAt(i));
                size = checked(size + fields.ValueAt(i).Length);
            }

            int count = kept.Length;
            int countWidth = count > 0xFF ? 4 : 1;
            int idWidth = ParquetVariant.WidthOf((uint)maxId);
            int offsetWidth = ParquetVariant.WidthOf((uint)size);
            int length = checked(1 + countWidth + (count * idWidth) + ((count + 1) * offsetWidth) + size);
            Reserve(length);
            Span<byte> into = _data.AsSpan(_used, length);
            into[0] = (byte)(((((countWidth == 4 ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicObject);
            int at = 1;
            ParquetVariant.Put(into, ref at, (uint)count, countWidth);
            foreach (int i in kept)
            {
                ParquetVariant.Put(into, ref at, (uint)fields.IdAt(i), idWidth);
            }

            int offset = 0;
            foreach (int i in kept)
            {
                ParquetVariant.Put(into, ref at, (uint)offset, offsetWidth);
                offset += fields.ValueAt(i).Length;
            }

            ParquetVariant.Put(into, ref at, (uint)offset, offsetWidth);
            foreach (int i in kept)
            {
                ReadOnlySpan<byte> value = fields.ValueAt(i);
                value.CopyTo(into[at..]);
                at += value.Length;
            }

            Close(_used + length, true);
        }

        /// <summary>The column of <paramref name="rows"/> rows as a node wearing <paramref name="dtype"/>, its validity's dtype made in <paramref name="types"/>.</summary>
        internal int Build(CanonicalArena arena, DTypeArena types, DType dtype, int rows)
        {
            int bytes = checked(rows * ViewSize);
            VortexBuffer views = arena.Allocate(Math.Max(bytes, 1), 64, out Span<byte> into).Slice(0, bytes);
            for (int row = 0; row < rows; row++)
            {
                int start = _starts[row];
                int length = _starts[row + 1] - start;
                if (length > 0)
                {
                    CanonicalSupport.WriteView(into.Slice(row * ViewSize, ViewSize), _data.AsSpan(start, length), length, 0, start);
                }
            }

            Validity validity = _valid.Validity(arena, types, rows);
            if (_used == 0)
            {
                return arena.AddVarBinView(dtype, rows, validity, views, []);
            }

            VortexBuffer data = arena.AllocateUninitialized(_used, 64, out Span<byte> written).Slice(0, _used);
            _data.AsSpan(0, _used).CopyTo(written);
            return arena.AddVarBinView(dtype, rows, validity, views, [data]);
        }

        private void Reserve(int length)
        {
            if (_data.Length - _used < length)
            {
                Array.Resize(ref _data, Math.Max(checked(_used + length), 2 * _data.Length));
            }
        }

        private void Close(int end, bool valid)
        {
            int row = _valid.Count;
            if (row + 2 > _starts.Length)
            {
                Array.Resize(ref _starts, 2 * _starts.Length);
            }

            _starts[row + 1] = end;
            _used = end;
            _held += valid ? 1 : 0;
            _valid.Append(valid);
        }
    }
}
