using System;
using System.Buffers;
using System.Runtime.CompilerServices;
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
/// <para>
/// The pass over the rows takes each node's values at the rows that hold it and nothing at the others, so what it
/// holds is the values whatever the keys. A typed column is laid out over the chunk's rows only when the blob comes
/// to it, in an arena emptied before the next one: a chunk of a million rows whose objects each held a few of
/// hundreds of keys laid every key's column out at once, sixteen bytes a row a key of text, gigabytes a chunk.
/// </para>
/// </remarks>
internal static class VariantShredder
{
    private const int ViewSize = 16;

    /// <summary>
    /// Writes <paramref name="node"/>, a <c>Struct{metadata, value}</c> wearing a variant dtype, shredded as
    /// <paramref name="plan"/> says, as one blob: the bytes <see cref="ArrayBlobWriter.Write"/> writes of the node
    /// <see cref="Shred"/> makes, each typed column laid out in the workspace's leaves while the blob writes it. What
    /// the rows hold is taken into <paramref name="arena"/>, which keeps it until it is reset.
    /// </summary>
    /// <param name="blob">The workspace the blob is assembled in.</param>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The chunk's variant column.</param>
    /// <param name="plan">The shredding.</param>
    /// <param name="bounds">Where the chunk's shredded keys are counted block by block; null when none is.</param>
    /// <param name="encodings">The file's array-encoding dictionary, extended as needed.</param>
    /// <param name="compress">Whether the compressor picks each column's encoding.</param>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal static ArrayBlobWriter.BlobLease Write(
        ArrayBlobWriter.Workspace blob, CanonicalArena arena, int node, VariantShredding plan, Indexes.ChunkBlockBounds? bounds,
        EncodingDictionary encodings, bool compress)
    {
        Shredding shredding = blob.Shredding(plan);
        shredding.Take(arena, node, bounds);
        return shredding.Write(blob, encodings, compress);
    }

    /// <summary>
    /// Shreds <paramref name="node"/>, a <c>Struct{metadata, value}</c> wearing a variant dtype, as
    /// <paramref name="plan"/> says: a node of three fields, the rows' metadata, the residual and the
    /// shredded tree, wearing the same dtype and validity, which the blob writer writes as a
    /// <c>vortex.variant</c>. Every typed column is laid out in <paramref name="arena"/>, whose blob is the one
    /// <see cref="Write"/> writes a column at a time. The tree's dtypes are made in <paramref name="types"/>.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal static int Shred(CanonicalArena arena, DTypeArena types, int node, VariantShredding plan, Indexes.ChunkBlockBounds? bounds = null)
    {
        Shredding shredding = new Shredding(plan);
        shredding.Take(arena, node, bounds);
        return shredding.Whole(types);
    }

    /// <summary>
    /// The nodes of a shredding over a chunk's rows, which a workspace keeps from one chunk to the next of the same
    /// plan: what each node takes of the rows, then the tree, laid out whole or written a column at a time.
    /// </summary>
    internal sealed class Shredding
    {
        private readonly Group _root;

        // The chunk the nodes took: the arena that holds it, what they took of it and the frames of the tree; its
        // node, its rows' metadata and its rows; the dtypes of its tree.
        private CanonicalArena _arena = null!;
        private int _node;
        private int _metadata;
        private int _rows;
        private DTypeArena _types = null!;

        internal Shredding(VariantShredding plan)
        {
            Plan = plan;
            _root = Group.For(plan);
        }

        /// <summary>The shredding the nodes are of.</summary>
        internal VariantShredding Plan { get; }

        /// <summary>
        /// Takes what each node holds of the rows of <paramref name="node"/>, a <c>Struct{metadata, value}</c> wearing
        /// a variant dtype, into <paramref name="arena"/>: its values at the rows that hold it, the keys
        /// <paramref name="bounds"/> asks for counted block by block as they are typed.
        /// </summary>
        /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
        internal void Take(CanonicalArena arena, int node, Indexes.ChunkBlockBounds? bounds)
        {
            CanonicalNode variant = arena.GetNode(node);
            int rows = variant.Length;
            int metadata = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0));
            int value = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(1));
            CanonicalNode metadataNode = arena.GetNode(metadata);
            CanonicalNode valueNode = arena.GetNode(value);
            _arena = arena;
            _node = node;
            _metadata = metadata;
            _rows = rows;
            _root.Clear(arena);
            if (_root is ObjectGroup keys)
            {
                keys.Count(bounds);
            }

            // Rows in a run of the same metadata share its dictionary, which each object node looks its keys up in once: a run
            // a source's rows, which the slice's sort keeps together.
            ReadOnlySpan<byte> previous = default;
            int epoch = -1;
            for (int row = 0; row < rows; row++)
            {
                if (!ArenaWords.IsValid(arena, node, row))
                {
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
                _root.Present(in dictionary, epoch, bytes, row);
            }
        }

        /// <summary>The chunk taken, shredded, every typed column laid out in its arena; the tree's dtypes made in <paramref name="types"/>.</summary>
        internal int Whole(DTypeArena types)
        {
            _types = types;
            CanonicalNode variant = _arena.GetNode(_node);
            int tree = Laid(_root);
            int residual = _root.Values.Build(_arena, types, types.Binary(Nullability.Nullable), _rows);
            return _arena.AddStruct(variant.DType, _rows, variant.Validity, [_metadata, residual, tree]);
        }

        /// <summary>
        /// The chunk taken, shredded and written as one blob: each typed column laid out in the workspace's leaves when the
        /// blob comes to it, the buffers the blob took of it kept, and the leaves emptied for the next.
        /// </summary>
        internal ArrayBlobWriter.BlobLease Write(ArrayBlobWriter.Workspace blob, EncodingDictionary encodings, bool compress)
        {
            DTypeArena types = _types = new DTypeArena();
            CanonicalNode variant = _arena.GetNode(_node);
            int frame = _arena.AddStruct(variant.DType, _rows, variant.Validity, []);
            blob.Builder.Clear();
            try
            {
                int root = ArrayBlobWriter.WriteShredded(
                    blob, _arena, frame, encodings, compress, residualNullable: true, _root.TypedType(types), new Children(this, _root, variant: true));
                return ArrayBlobWriter.Assemble(blob, root);
            }
            finally
            {
                blob.Discard();
                blob.Leaves.Reset();
            }
        }

        /// <summary><paramref name="group"/>'s typed node laid out whole: an object's struct of its fields, any other its typed column.</summary>
        private int Laid(Group group)
        {
            if (group is not ObjectGroup objects)
            {
                return ((Leaf)group).Typed(_arena, _types, _rows);
            }

            int count = objects.WrittenCount;
            int[] fields = new int[count];
            for (int i = 0; i < count; i++)
            {
                fields[i] = LaidField(objects.Written(i));
            }

            return _arena.AddStruct(objects.TypedType(_types), _rows, objects.ObjectsValidity(_arena, _types, _rows), fields);
        }

        /// <summary>
        /// <paramref name="group"/> laid out whole as a field of its object: its typed node when it kept no value of its
        /// own, and otherwise the variant of those values and its typed node, valid where the key is present.
        /// </summary>
        private int LaidField(Group group)
        {
            int typed = Laid(group);
            if (!group.Values.HoldsAny)
            {
                return typed;
            }

            int values = group.Values.Build(_arena, _types, _types.Binary(Nullability.Nullable), _rows);
            return _arena.AddStruct(_types.Variant(Nullability.Nullable), _rows, group.HeldValidity(_arena, _types, _rows), [_metadata, values, typed]);
        }

        /// <summary>Child <paramref name="child"/> of the variant of <paramref name="group"/>'s values: the rows' metadata, the values, the typed node.</summary>
        private int WriteVariantChild(ArrayBlobWriter.Workspace blob, Group group, int child, EncodingDictionary encodings, bool compress) =>
            child switch
            {
                0 => ArrayBlobWriter.WriteChild(blob, _arena, _metadata, encodings, compress),
                1 => WriteLeaf(blob, group, values: true, encodings, compress),
                _ => WriteTyped(blob, group, encodings, compress),
            };

        /// <summary>
        /// Writes <paramref name="group"/> as a field of its object: its typed node, or the variant of the values it kept
        /// of its own and its typed node.
        /// </summary>
        private int WriteField(ArrayBlobWriter.Workspace blob, Group group, EncodingDictionary encodings, bool compress)
        {
            if (!group.Values.HoldsAny)
            {
                return WriteTyped(blob, group, encodings, compress);
            }

            int frame = _arena.AddStruct(_types.Variant(Nullability.Nullable), _rows, group.HeldValidity(_arena, _types, _rows), []);
            return ArrayBlobWriter.WriteShredded(
                blob, _arena, frame, encodings, compress, residualNullable: true, group.TypedType(_types), new Children(this, group, variant: true));
        }

        /// <summary>Writes <paramref name="group"/>'s typed node: an object's struct a field at a time, any other's typed column.</summary>
        private int WriteTyped(ArrayBlobWriter.Workspace blob, Group group, EncodingDictionary encodings, bool compress)
        {
            if (group is not ObjectGroup objects)
            {
                return WriteLeaf(blob, group, values: false, encodings, compress);
            }

            int frame = _arena.AddStruct(objects.TypedType(_types), _rows, objects.ObjectsValidity(_arena, _types, _rows), []);
            return ArrayBlobWriter.WriteStruct(
                blob, _arena, frame, encodings, compress, objects.WrittenCount, new Children(this, objects, variant: false));
        }

        /// <summary>
        /// Lays <paramref name="group"/>'s typed column, or its values, out over the chunk's rows in the workspace's leaves,
        /// writes it, keeps the buffers the blob took of it, and empties the leaves.
        /// </summary>
        private int WriteLeaf(ArrayBlobWriter.Workspace blob, Group group, bool values, EncodingDictionary encodings, bool compress)
        {
            CanonicalArena leaves = blob.Leaves;
            int first = blob.Buffers.Count;
            try
            {
                int node = values
                    ? group.Values.Build(leaves, _types, _types.Binary(Nullability.Nullable), _rows)
                    : ((Leaf)group).Typed(leaves, _types, _rows);
                int written = ArrayBlobWriter.WriteChild(blob, leaves, node, encodings, compress);
                blob.Keep(first);
                return written;
            }
            finally
            {
                leaves.ResetKeepingBlocks();
            }
        }

        /// <summary>
        /// The children of a node of the tree, each written when the blob comes to it: a variant's metadata, values and
        /// typed node, or an object's fields.
        /// </summary>
        private readonly struct Children(Shredding shredding, Group group, bool variant) : ArrayBlobWriter.IChildren
        {
            public int Write(ArrayBlobWriter.Workspace blob, int child, EncodingDictionary encodings, bool compress) =>
                variant
                    ? shredding.WriteVariantChild(blob, group, child, encodings, compress)
                    : shredding.WriteField(blob, ((ObjectGroup)group).Written(child), encodings, compress);
        }
    }

    /// <summary>
    /// One node of the shredding over a chunk's rows: what of each row that holds it the node does not take, its typed
    /// values, and the rows that hold it.
    /// </summary>
    private abstract class Group
    {
        /// <summary>What the node does not take of the rows that hold it: a value of another shape or class, an object's other keys.</summary>
        internal BinaryValues Values { get; } = new BinaryValues();

        /// <summary>The last row the node was handed, -1 before the first.</summary>
        internal int Last { get; private set; } = -1;

        internal static Group For(VariantShredding plan) => plan.Kind switch
        {
            VariantShreddingKind.Object => new ObjectGroup(plan),
            VariantShreddingKind.List => new ListGroup(plan.Class),
            _ => new PrimitiveGroup(plan.Class),
        };

        /// <summary>Whether some row holds the node.</summary>
        internal abstract bool HeldAny { get; }

        /// <summary>Empties the node for a chunk whose rows it takes into <paramref name="arena"/>.</summary>
        internal virtual void Clear(CanonicalArena arena)
        {
            Values.Clear(arena);
            Last = -1;
        }

        /// <summary>
        /// Row <paramref name="row"/>, whose node holds <paramref name="value"/>, its keys named by <paramref name="dictionary"/>,
        /// the same as every row of its run of <paramref name="epoch"/>; rows come in order.
        /// </summary>
        internal void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row)
        {
            Last = row;
            Take(in dictionary, epoch, value, row);
        }

        /// <summary>Takes what the node holds of row <paramref name="row"/>, as <see cref="Present"/> says.</summary>
        protected abstract void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row);

        /// <summary>The dtype of the node's typed node.</summary>
        internal abstract DType TypedType(DTypeArena types);

        /// <summary>The dtype of the node in its object's struct: a variant when it kept values of its own, its typed node's otherwise.</summary>
        internal DType FieldType(DTypeArena types) => Values.HoldsAny ? types.Variant(Nullability.Nullable) : TypedType(types);

        /// <summary>The validity of the rows that hold the node, over <paramref name="rows"/> rows.</summary>
        internal abstract Validity HeldValidity(CanonicalArena arena, DTypeArena types, int rows);
    }

    /// <summary>A node whose typed node is a column, laid out whole when the blob comes to it.</summary>
    private abstract class Leaf : Group
    {
        /// <summary>The node's typed column over <paramref name="rows"/> rows, laid out in <paramref name="arena"/>.</summary>
        internal abstract int Typed(CanonicalArena arena, DTypeArena types, int rows);
    }

    /// <summary>The values of one class in a typed column, any other kept whole.</summary>
    private sealed class PrimitiveGroup(VariantClass @class) : Leaf
    {
        private readonly TypedValues _typed = TypedValues.For(@class, dense: false);

        /// <summary>Where the values typed are counted block by block; null when they are not.</summary>
        internal Indexes.ChunkBlockBounds.KeyBlocks? Bounds { get; set; }

        internal override bool HeldAny => _typed.Count > 0 || Values.HoldsAny;

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _typed.Clear(arena);
        }

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row)
        {
            VariantScalar scalar = VariantScalar.Read(value);
            if (_typed.Takes(scalar.Kind))
            {
                _typed.Add(row, in scalar);
                Bounds?.Note(row, scalar);
                return;
            }

            Values.Add(row, value);
        }

        internal override DType TypedType(DTypeArena types) => _typed.Type(types);

        internal override Validity HeldValidity(CanonicalArena arena, DTypeArena types, int rows) =>
            RowsValidity(arena, types, rows, _typed.Rows!, Values.Rows);

        internal override int Typed(CanonicalArena arena, DTypeArena types, int rows) => _typed.Build(arena, types, rows);
    }

    /// <summary>Some keys of an object, each a node of its own; the other keys kept in an object of their own.</summary>
    private sealed class ObjectGroup : Group
    {
        private readonly VariantShredding _plan;
        private readonly Group[] _fields;
        private readonly Appended<int> _objects = new Appended<int>();
        private int[] _kept = new int[8];

        // The plan's key of each field id of the dictionary the rows of run _epoch share: rows of one source share their
        // metadata, whose field names a search of the plan per field and per row looked up again.
        private int[] _keyOf = [];
        private int _epoch = -1;

        // The fields the chunk's struct holds, by index among the plan's keys, -1 for their count until the chunk's rows
        // are in; and its dtype once made.
        private int[] _written = [];
        private int _writtenCount = -1;
        private DType? _type;

        internal ObjectGroup(VariantShredding plan)
        {
            _plan = plan;
            _fields = new Group[plan.Keys.Count];
            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i] = For(plan.Keys[i].Field);
            }
        }

        internal override bool HeldAny => _objects.Count > 0 || Values.HoldsAny;

        /// <remarks>
        /// The chunk shreds only the keys its rows hold: a key no row holds would cost its nodes and a
        /// typed column of nulls for nothing, and a chunk's tree is its own, which every reader takes.
        /// When no row holds any, every key is kept, so that the tree is never empty.
        /// </remarks>
        internal int WrittenCount
        {
            get
            {
                Settle();
                return _writtenCount;
            }
        }

        /// <summary>The node of the struct's field <paramref name="index"/>.</summary>
        internal Group Written(int index)
        {
            Settle();
            return _fields[_written[index]];
        }

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _objects.Clear(arena);
            _epoch = -1;
            _writtenCount = -1;
            _type = null;
            foreach (Group field in _fields)
            {
                field.Clear(arena);
            }
        }

        /// <summary>Counts the values of the keys <paramref name="bounds"/> asks for block by block as they are typed, and no other key's.</summary>
        internal void Count(Indexes.ChunkBlockBounds? bounds)
        {
            foreach (Group field in _fields)
            {
                if (field is PrimitiveGroup typed)
                {
                    typed.Bounds = null;
                }
            }

            for (int i = 0; bounds is not null && i < bounds.Keys.Count; i++)
            {
                Indexes.ChunkBlockBounds.KeyBlocks key = bounds.Keys[i];
                if (_fields[key.Index] is PrimitiveGroup typed)
                {
                    typed.Bounds = key;
                }
            }
        }

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row)
        {
            if ((value[0] & 0x03) != ParquetVariant.BasicObject)
            {
                Values.Add(row, value);
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

            _objects.Add(row);
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

                // A key an object holds twice keeps its first.
                if (key >= 0)
                {
                    Group field = _fields[key];
                    if (field.Last < row)
                    {
                        field.Present(in dictionary, epoch, fields.ValueAt(i), row);
                    }
                }
                else
                {
                    _kept[kept++] = i;
                }
            }

            if (kept > 0)
            {
                Values.AddObject(row, fields, _kept.AsSpan(0, kept));
            }
        }

        internal override DType TypedType(DTypeArena types)
        {
            if (_type is { } made)
            {
                return made;
            }

            int count = WrittenCount;
            DType[] fields = new DType[count];
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = _plan.Keys[_written[i]].Name;
                fields[i] = _fields[_written[i]].FieldType(types);
            }

            DType type = types.Struct(names, fields, Nullability.Nullable);
            _type = type;
            return type;
        }

        /// <summary>The validity of the rows whose value is an object, over <paramref name="rows"/> rows.</summary>
        internal Validity ObjectsValidity(CanonicalArena arena, DTypeArena types, int rows) => RowsValidity(arena, types, rows, _objects);

        internal override Validity HeldValidity(CanonicalArena arena, DTypeArena types, int rows) =>
            RowsValidity(arena, types, rows, _objects, Values.Rows);

        private void Settle()
        {
            if (_writtenCount >= 0)
            {
                return;
            }

            if (_written.Length < _fields.Length)
            {
                _written = new int[_fields.Length];
            }

            int count = 0;
            for (int k = 0; k < _fields.Length; k++)
            {
                if (_fields[k].HeldAny)
                {
                    _written[count++] = k;
                }
            }

            if (count == 0)
            {
                for (int k = 0; k < _fields.Length; k++)
                {
                    _written[k] = k;
                }

                count = _fields.Length;
            }

            _writtenCount = count;
        }
    }

    /// <summary>A list whose elements are all of one class, in a list of its elements' typed column; any other kept whole.</summary>
    private sealed class ListGroup(VariantClass @class) : Leaf
    {
        private readonly TypedValues _elements = TypedValues.For(@class, dense: true);
        private readonly Appended<int> _lists = new Appended<int>();
        private readonly Appended<int> _sizes = new Appended<int>();

        internal override bool HeldAny => _lists.Count > 0 || Values.HoldsAny;

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _elements.Clear(arena);
            _lists.Clear(arena);
            _sizes.Clear(arena);
        }

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row)
        {
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
                        VariantScalar element = VariantScalar.Read(elements.ValueAt(i));
                        _elements.Add(0, in element);
                    }

                    _lists.Add(row);
                    _sizes.Add(elements.Count);
                    return;
                }
            }

            Values.Add(row, value);
        }

        internal override DType TypedType(DTypeArena types) => types.List(_elements.Type(types), Nullability.Nullable);

        internal override Validity HeldValidity(CanonicalArena arena, DTypeArena types, int rows) =>
            RowsValidity(arena, types, rows, _lists, Values.Rows);

        internal override int Typed(CanonicalArena arena, DTypeArena types, int rows)
        {
            int elements = _elements.Build(arena, types, _elements.Count);
            DType dtype = TypedType(types);
            VortexBuffer offsets = arena.AllocateUninitialized(Math.Max(rows * sizeof(int), 1), 8, out Span<byte> into).Slice(0, rows * sizeof(int));
            VortexBuffer sizes = arena.Allocate(Math.Max(rows * sizeof(int), 1), 8, out Span<byte> sized).Slice(0, rows * sizeof(int));
            Span<int> starts = MemoryMarshal.Cast<byte, int>(into)[..rows];
            Span<int> counts = MemoryMarshal.Cast<byte, int>(sized)[..rows];
            Reading<int> at = new Reading<int>(_lists);
            Reading<int> size = new Reading<int>(_sizes);
            for (int i = 0; i < _lists.Count; i++)
            {
                counts[at.Next()] = size.Next();
            }

            int start = 0;
            for (int row = 0; row < rows; row++)
            {
                starts[row] = start;
                start += counts[row];
            }

            return arena.AddListView(dtype, rows, RowsValidity(arena, types, rows, _lists), elements, offsets, PType.I32, sizes, PType.I32);
        }
    }

    /// <summary>
    /// The values of one class a node typed, at the rows that hold one, laid out over the chunk's rows once they are
    /// in: null at every other row. Or a list's elements, each valid, one after the other.
    /// </summary>
    private abstract class TypedValues(VariantClass @class, bool dense)
    {
        /// <summary>The rows that hold a value, in order; null for elements, which are rows of their own.</summary>
        internal Appended<int>? Rows { get; } = dense ? null : new Appended<int>();

        /// <summary>The values taken.</summary>
        internal int Count { get; private set; }

        internal static TypedValues For(VariantClass @class, bool dense) => @class switch
        {
            VariantClass.Integer => new IntegerValues(dense),
            VariantClass.Double => new DoubleValues(dense),
            VariantClass.String => new StringValues(dense),
            _ => new BooleanValues(dense),
        };

        /// <summary>Whether <paramref name="value"/> is of the column's class.</summary>
        internal bool Fits(ReadOnlySpan<byte> value) => Takes(VariantScalar.Read(value).Kind);

        /// <summary>Whether a value of <paramref name="kind"/> is of the column's class.</summary>
        internal bool Takes(VariantKind kind) => VariantClasses.Of(kind) == @class;

        internal virtual void Clear(CanonicalArena arena)
        {
            Rows?.Clear(arena);
            Count = 0;
        }

        /// <summary>Takes <paramref name="scalar"/>, a value of the column's class, at row <paramref name="row"/>, which elements ignore.</summary>
        internal void Add(int row, in VariantScalar scalar)
        {
            Rows?.Add(row);
            Count++;
            Value(in scalar);
        }

        /// <summary>The dtype of the column.</summary>
        internal abstract DType Type(DTypeArena types);

        /// <summary>The column over <paramref name="rows"/> rows, laid out in <paramref name="arena"/>, its dtypes made in <paramref name="types"/>.</summary>
        internal int Build(CanonicalArena arena, DTypeArena types, int rows) =>
            Lay(arena, Type(types), rows, Rows is null ? Validity.AllValid : RowsValidity(arena, types, rows, Rows));

        /// <summary>Takes the value <paramref name="scalar"/>.</summary>
        protected abstract void Value(in VariantScalar scalar);

        /// <summary>The column of <paramref name="dtype"/> and <paramref name="validity"/> over <paramref name="rows"/> rows, each value at its row.</summary>
        protected abstract int Lay(CanonicalArena arena, DType dtype, int rows, Validity validity);
    }

    /// <summary>Integers of every width, as 64-bit integers.</summary>
    private sealed class IntegerValues(bool dense) : TypedValues(VariantClass.Integer, dense)
    {
        private readonly Appended<long> _values = new Appended<long>();

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _values.Clear(arena);
        }

        internal override DType Type(DTypeArena types) => types.Primitive(PType.I64, Nullability.Nullable);

        protected override void Value(in VariantScalar scalar) => _values.Add((long)scalar.Integer);

        protected override int Lay(CanonicalArena arena, DType dtype, int rows, Validity validity) =>
            arena.AddPrimitive(dtype, rows, validity, PType.I64, Fixed(arena, rows, Rows, _values));
    }

    /// <summary>64-bit floats.</summary>
    private sealed class DoubleValues(bool dense) : TypedValues(VariantClass.Double, dense)
    {
        private readonly Appended<double> _values = new Appended<double>();

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _values.Clear(arena);
        }

        internal override DType Type(DTypeArena types) => types.Primitive(PType.F64, Nullability.Nullable);

        protected override void Value(in VariantScalar scalar) => _values.Add(scalar.Number);

        protected override int Lay(CanonicalArena arena, DType dtype, int rows, Validity validity) =>
            arena.AddPrimitive(dtype, rows, validity, PType.F64, Fixed(arena, rows, Rows, _values));
    }

    /// <summary>Booleans.</summary>
    private sealed class BooleanValues(bool dense) : TypedValues(VariantClass.Boolean, dense)
    {
        private readonly Appended<byte> _values = new Appended<byte>();

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _values.Clear(arena);
        }

        internal override DType Type(DTypeArena types) => types.Bool(Nullability.Nullable);

        protected override void Value(in VariantScalar scalar) => _values.Add(scalar.Integer != 0 ? (byte)1 : (byte)0);

        protected override int Lay(CanonicalArena arena, DType dtype, int rows, Validity validity)
        {
            int bytes = (rows + 7) / 8;
            VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 8, out Span<byte> into).Slice(0, bytes);
            Reading<byte> values = new Reading<byte>(_values);
            Reading<int> at = Rows is null ? default : new Reading<int>(Rows);
            for (int i = 0; i < Count; i++)
            {
                int row = Rows is null ? i : at.Next();
                if (values.Next() != 0)
                {
                    into[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            return arena.AddBool(dtype, rows, validity, bits, 0);
        }
    }

    /// <summary>UTF-8 text.</summary>
    private sealed class StringValues(bool dense) : TypedValues(VariantClass.String, dense)
    {
        private readonly Appended<int> _lengths = new Appended<int>();
        private readonly Appended<byte> _bytes = new Appended<byte>();

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _lengths.Clear(arena);
            _bytes.Clear(arena);
        }

        internal override DType Type(DTypeArena types) => types.Utf8(Nullability.Nullable);

        protected override void Value(in VariantScalar scalar)
        {
            _lengths.Add(scalar.Bytes.Length);
            _bytes.AddRange(scalar.Bytes);
        }

        protected override int Lay(CanonicalArena arena, DType dtype, int rows, Validity validity) =>
            Binary(arena, dtype, rows, validity, Rows, _lengths, _bytes);
    }

    /// <summary>Byte strings taken at the rows that hold one, laid out over the chunk's rows as views and one data buffer.</summary>
    private sealed class BinaryValues
    {
        private readonly Appended<int> _rows = new Appended<int>();
        private readonly Appended<int> _lengths = new Appended<int>();
        private readonly Appended<byte> _bytes = new Appended<byte>();

        /// <summary>Whether any row holds a value.</summary>
        internal bool HoldsAny => _rows.Count > 0;

        /// <summary>The rows that hold a value, in order.</summary>
        internal Appended<int> Rows => _rows;

        internal void Clear(CanonicalArena arena)
        {
            _rows.Clear(arena);
            _lengths.Clear(arena);
            _bytes.Clear(arena);
        }

        /// <summary>Takes <paramref name="value"/> at row <paramref name="row"/>.</summary>
        internal void Add(int row, ReadOnlySpan<byte> value)
        {
            _rows.Add(row);
            _lengths.Add(value.Length);
            _bytes.AddRange(value);
        }

        /// <summary>
        /// Takes at row <paramref name="row"/> the object of the fields <paramref name="kept"/> of <paramref name="fields"/>,
        /// in their order, which is their keys'.
        /// </summary>
        internal void AddObject(int row, VariantNested fields, ReadOnlySpan<int> kept)
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
            byte[]? rented = null;
            Span<byte> into = length <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(length));
            into = into[..length];
            try
            {
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

                Add(row, into);
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        /// <summary>The column of <paramref name="rows"/> rows as a node wearing <paramref name="dtype"/>, its validity's dtype made in <paramref name="types"/>.</summary>
        internal int Build(CanonicalArena arena, DTypeArena types, DType dtype, int rows) =>
            Binary(arena, dtype, rows, RowsValidity(arena, types, rows, _rows), _rows, _lengths, _bytes);
    }

    /// <summary>
    /// A column of byte strings over <paramref name="rows"/> rows, as views and one data buffer: each of
    /// <paramref name="lengths"/>' strings, end to end in <paramref name="bytes"/>, at its row of <paramref name="at"/>,
    /// or at the next row when <paramref name="at"/> is null.
    /// </summary>
    private static int Binary(
        CanonicalArena arena, DType dtype, int rows, Validity validity, Appended<int>? at, Appended<int> lengths, Appended<byte> bytes)
    {
        int size = checked(rows * ViewSize);
        VortexBuffer views = arena.Allocate(Math.Max(size, 1), 64, out Span<byte> into).Slice(0, size);
        int used = bytes.Count;
        if (used == 0)
        {
            return arena.AddVarBinView(dtype, rows, validity, views, []);
        }

        VortexBuffer data = arena.AllocateUninitialized(used, 64, out Span<byte> written).Slice(0, used);
        written = written[..used];
        bytes.CopyTo(written);
        Reading<int> row = at is null ? default : new Reading<int>(at);
        Reading<int> length = new Reading<int>(lengths);
        int start = 0;
        for (int i = 0; i < lengths.Count; i++)
        {
            int to = at is null ? i : row.Next();
            int count = length.Next();
            if (count > 0)
            {
                CanonicalSupport.WriteView(into.Slice(to * ViewSize, ViewSize), written.Slice(start, count), count, 0, start);
            }

            start += count;
        }

        return arena.AddVarBinView(dtype, rows, validity, views, [data]);
    }

    /// <summary>
    /// A fixed-width buffer of <paramref name="rows"/> values laid out in <paramref name="arena"/>: each of
    /// <paramref name="values"/> at its row of <paramref name="at"/>, zero at every other, or one after the other when
    /// <paramref name="at"/> is null.
    /// </summary>
    private static VortexBuffer Fixed<T>(CanonicalArena arena, int rows, Appended<int>? at, Appended<T> values)
        where T : unmanaged
    {
        int bytes = checked(rows * Unsafe.SizeOf<T>());
        if (at is null)
        {
            VortexBuffer copied = arena.AllocateUninitialized(Math.Max(bytes, 1), 64, out Span<byte> into).Slice(0, bytes);
            values.CopyTo(MemoryMarshal.Cast<byte, T>(into[..bytes]));
            return copied;
        }

        VortexBuffer buffer = arena.Allocate(Math.Max(bytes, 1), 64, out Span<byte> zeroed).Slice(0, bytes);
        Span<T> laid = MemoryMarshal.Cast<byte, T>(zeroed[..bytes]);
        Reading<int> row = new Reading<int>(at);
        Reading<T> value = new Reading<T>(values);
        for (int i = 0; i < values.Count; i++)
        {
            laid[row.Next()] = value.Next();
        }

        return buffer;
    }

    /// <summary>
    /// The validity of <paramref name="rows"/> rows valid at those of <paramref name="at"/>, and at those of
    /// <paramref name="also"/> when given, each in order: every row valid, none, or the bits of those that are.
    /// </summary>
    private static Validity RowsValidity(CanonicalArena arena, DTypeArena types, int rows, Appended<int> at, Appended<int>? also = null)
    {
        bool union = also is { Count: > 0 };
        if (!union && at.Count == rows)
        {
            return Validity.AllValid;
        }

        if (!union && at.Count == 0)
        {
            return Validity.AllInvalid;
        }

        int bytes = (rows + 7) / 8;
        VortexBuffer buffer = arena.Allocate(Math.Max(bytes, 1), 8, out Span<byte> into).Slice(0, bytes);
        Set(into, at);
        if (union)
        {
            // The rows of the two may meet, an object's with kept keys: the bits say how many rows are valid.
            Set(into, also!);
            if (BitmapKernels.CountSet(into[..bytes], 0, rows) == rows)
            {
                return Validity.AllValid;
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, buffer, 0));

        static void Set(Span<byte> bits, Appended<int> valid)
        {
            for (int b = 0; b < valid.Blocks; b++)
            {
                foreach (int row in valid.Block(b))
                {
                    bits[row >> 3] |= (byte)(1 << (row & 7));
                }
            }
        }
    }

    /// <summary>
    /// Values appended one at a time into blocks carved from a chunk's arena, each four times then twice the last up to
    /// a megabyte, and read back in order: what a node takes of the rows, however many they turn out to be, with no
    /// array grown by copying. The blocks are the arena's, given back when it is reset.
    /// </summary>
    private sealed unsafe class Appended<T>
        where T : unmanaged
    {
        private const int FirstBytes = 256;
        private const int LargestBytes = 1 << 20;

        private CanonicalArena _arena = null!;
        private VortexBuffer[] _blocks = new VortexBuffer[4];
        private int _blockCount;
        private T* _next;
        private int _left;

        /// <summary>The values appended.</summary>
        internal int Count { get; private set; }

        /// <summary>The blocks that hold the values.</summary>
        internal int Blocks => _blockCount;

        /// <summary>Empties the list for values carved from <paramref name="arena"/>.</summary>
        internal void Clear(CanonicalArena arena)
        {
            _arena = arena;
            _blockCount = 0;
            _next = null;
            _left = 0;
            Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Add(T value)
        {
            if (_left == 0)
            {
                Grow();
            }

            *_next++ = value;
            _left--;
            Count++;
        }

        internal void AddRange(ReadOnlySpan<T> values)
        {
            while (!values.IsEmpty)
            {
                if (_left == 0)
                {
                    Grow();
                }

                int take = Math.Min(_left, values.Length);
                values[..take].CopyTo(new Span<T>(_next, take));
                _next += take;
                _left -= take;
                Count += take;
                values = values[take..];
            }
        }

        /// <summary>The values of block <paramref name="index"/>, the last one's as far as it is filled.</summary>
        internal ReadOnlySpan<T> Block(int index)
        {
            ReadOnlySpan<T> block = _blocks[index].Cast<T>();
            return index == _blockCount - 1 ? block[..(block.Length - _left)] : block;
        }

        /// <summary>Copies the values, in order, into <paramref name="destination"/>.</summary>
        internal void CopyTo(Span<T> destination)
        {
            int at = 0;
            for (int i = 0; i < _blockCount; i++)
            {
                ReadOnlySpan<T> block = Block(i);
                block.CopyTo(destination[at..]);
                at += block.Length;
            }
        }

        private void Grow()
        {
            int last = _blockCount == 0 ? 0 : _blocks[_blockCount - 1].Length;
            int bytes = last == 0 ? FirstBytes : last < 1024 ? last * 4 : Math.Min(last * 2, LargestBytes);
            VortexBuffer block = _arena.AllocateUninitialized(bytes, 8, out Span<byte> into).Slice(0, bytes);
            if (_blockCount == _blocks.Length)
            {
                Array.Resize(ref _blocks, 2 * _blocks.Length);
            }

            _blocks[_blockCount++] = block;
            _next = (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(into));
            _left = bytes / sizeof(T);
        }
    }

    /// <summary>Reads an <see cref="Appended{T}"/> back in order, a value at a time.</summary>
    private ref struct Reading<T>(Appended<T> from)
        where T : unmanaged
    {
        private ReadOnlySpan<T> _block;
        private int _at;
        private int _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Next()
        {
            if (_at == _block.Length)
            {
                _block = from.Block(_next++);
                _at = 0;
            }

            return _block[_at++];
        }
    }
}
