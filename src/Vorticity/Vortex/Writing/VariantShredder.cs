using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
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
    /// What a writer holds of a shredded variant column, about, in its chunk's target bytes of values: the values
    /// gathered, once; what the shredder takes of them, three times at most, a row and a value of eight bytes or a row,
    /// a length and the bytes for each value written in four bytes or more; the typed column laid out and its blob, one.
    /// </summary>
    internal const int HeldPerTarget = 5;

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
            _root.Attach(_replay);
        }

        // What the last row read took, node by node, for the rows that repeat its value to take again without reading it.
        private readonly Replay _replay = new Replay();

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
            Begin(arena, bounds);
            Rows rows = Prepare(arena, node);
            TakeMore(in rows, 0);
            _arena = arena;
            _node = node;
            _metadata = rows.Metadata;
            _rows = rows.Count;
        }

        /// <summary>
        /// A variant column's rows made ready to take (<see cref="Prepare"/>): its arena, its node, its metadata and its
        /// values in canonical form, and its rows. Taking them only reads the arena.
        /// </summary>
        internal readonly record struct Rows(CanonicalArena Arena, int Node, int Metadata, int Value, int Count);

        /// <summary>
        /// The variant column <paramref name="node"/> of <paramref name="arena"/> made ready to take: its metadata and its values
        /// in canonical form, which may add nodes to the arena, on the thread that owns it.
        /// </summary>
        internal static Rows Prepare(CanonicalArena arena, int node)
        {
            CanonicalNode variant = arena.GetNode(node);
            return new Rows(
                arena, node, ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0)), ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(1)), variant.Length);
        }

        /// <summary>
        /// Begins a chunk taken in parts (<see cref="TakeMore"/>), what each node takes carved from <paramref name="store"/>,
        /// which lasts as long as the chunk: nothing taken, the keys <paramref name="bounds"/> asks for counted block by block.
        /// </summary>
        internal void Begin(CanonicalArena store, Indexes.ChunkBlockBounds? bounds)
        {
            _root.Clear(store);
            if (_root is ObjectGroup keys)
            {
                keys.Count(bounds);
            }

            _epoch = -1;
            Taken = 0;
        }

        /// <summary>The rows of the chunk taken since it began (<see cref="Begin"/>), every part's.</summary>
        internal int Taken { get; private set; }

        // The run of rows of the same metadata the last row taken was of, numbered across the parts: an object node looks its
        // keys up once a run, by its number, which a part starting its own from nothing would give to another dictionary.
        private int _epoch = -1;

        /// <summary>
        /// Takes what each node holds of <paramref name="rows"/>, as the chunk's rows from <paramref name="offset"/> on, after
        /// the rows taken before, which they follow: a part of a chunk taken block by block, as a whole chunk taken at once
        /// would have taken its rows. Reads the part's arena alone, which may go once it is taken.
        /// </summary>
        /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
        internal void TakeMore(in Rows rows, int offset)
        {
            CanonicalArena arena = rows.Arena;
            int node = rows.Node;
            int value = rows.Value;
            CanonicalNode metadataNode = arena.GetNode(rows.Metadata);
            CanonicalNode valueNode = arena.GetNode(value);

            // Rows in a run of the same metadata share its dictionary, which each object node looks its keys up in once: a run
            // a source's rows, which the slice's sort keeps together.
            ReadOnlySpan<byte> previous = default;
            int epoch = _epoch;
            bool started = false;

            // A row whose value is the last row read's, under the same metadata, takes again what that row took, node by
            // node, without reading it: a metric's series repeats its attributes row after row, which were read again each, a
            // fifth of a merge of metric records (2026-11-16). The rows that do so one after the other take it together, each
            // node the run at once (Replay.Again): a consolidation's series holds a run a row of each of its inputs long, and
            // each row's nodes taking it again one by one took half of the shredding.
            ReadOnlySpan<byte> lastValue = default;
            int lastEpoch = -1;
            int runStart = 0;
            int runCount = 0;

            // The views and data buffers resolved once, and the validities read only where some row is null: a node's accessors
            // look its record up at every call, four a row here, which the take of a run's rows paid for each of them.
            ViewValues values = new ViewValues(valueNode);
            ViewValues metadata = new ViewValues(metadataNode);
            bool everyRow = arena.GetNode(node).Validity.IsAllValid;
            bool everyValue = valueNode.Validity.IsAllValid;
            for (int at = 0; at < rows.Count; at++)
            {
                if (!everyRow && !ArenaWords.IsValid(arena, node, at))
                {
                    continue;
                }

                int row = offset + at;
                ReadOnlySpan<byte> bytes = everyValue || ArenaWords.IsValid(arena, value, at) ? values.At(at) : VariantEncoding.NullValue;
                ReadOnlySpan<byte> metadataBytes = metadata.At(at);
                if (!started || !metadataBytes.SequenceEqual(previous))
                {
                    started = true;
                    epoch++;
                    previous = metadataBytes;
                }

                if (epoch == lastEpoch && bytes.SequenceEqual(lastValue))
                {
                    if (runCount > 0 && runStart + runCount != row)
                    {
                        _replay.Again(lastValue, runStart, runCount);
                        runCount = 0;
                    }

                    runStart = runCount == 0 ? row : runStart;
                    runCount++;
                    continue;
                }

                if (runCount > 0)
                {
                    _replay.Again(lastValue, runStart, runCount);
                    runCount = 0;
                }

                VariantDictionary dictionary = VariantDictionary.Read(metadataBytes);
                _replay.Begin();
                _root.Present(in dictionary, epoch, bytes, row);
                lastEpoch = _replay.Whole ? epoch : -1;
                lastValue = bytes;
            }

            if (runCount > 0)
            {
                _replay.Again(lastValue, runStart, runCount);
            }

            _epoch = epoch;
            Taken = offset + rows.Count;
        }

        /// <summary>
        /// Settles a chunk taken in parts on its rows whole, node <paramref name="node"/> of <paramref name="arena"/>, once every
        /// part is taken: what its blob's frames and its rows' metadata are written from.
        /// </summary>
        /// <remarks>
        /// Rows gathered thin keep a run of rows' metadata once (<c>VortexFileWriter.Thin</c>), which the encoders of its leaf
        /// size by what its rows name (<see cref="CanonicalRecord.SharesBytes"/>), the same leaf as a chunk gathered whole
        /// writes. Copied again whole here, the metadata of a consolidation of metric records' chunks took 4.3 % of its
        /// processor (2026-11-18).
        /// </remarks>
        internal void Settle(CanonicalArena arena, int node)
        {
            CanonicalNode variant = arena.GetNode(node);
            _arena = arena;
            _node = node;
            _rows = variant.Length;
            _metadata = ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0));
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

        // The leaves of the chunk taken, in the order its blob comes to them (PrepareLeaves), each written apart (WriteLeaf),
        // kept from one chunk to the next; the first of them not yet listed; and while the blob is written from them
        // (WriteGrafted), the next to graft.
        private readonly List<LeafJob> _jobs = [];
        private int _jobCount;
        private int _cursor = -1;

        /// <summary>
        /// Lists the leaves of the chunk taken, in the order its blob comes to them as <see cref="Write"/> writes it: each
        /// variant node's metadata and values and each typed column, with the validity encoding the blob writes each under,
        /// a variant's typed tree's under <paramref name="compress"/>, the rest under <paramref name="encodesValidity"/>. The
        /// rows' metadata, which every variant node names, is written once a validity encoding. Their count.
        /// </summary>
        internal int PrepareLeaves(bool encodesValidity, bool compress)
        {
            _jobCount = 0;
            _metadataJobs = default;
            _listCompress = compress;
            ListVariant(_root, encodesValidity);
            return _jobCount;
        }

        // The leaf of the rows' metadata written under each validity encoding, which the others name again; and the validity
        // encoding of a variant's typed tree, as the blob writer sets it there (ArrayBlobWriter.WriteShredded).
        private (LeafJob? Plain, LeafJob? Encoded) _metadataJobs;
        private bool _listCompress;

        /// <summary>A variant node's leaves: its metadata and values under <paramref name="validity"/>, its typed tree under the blob's compression.</summary>
        private void ListVariant(Group group, bool validity)
        {
            LeafJob metadata = List(LeafKind.Metadata, null, validity);
            LeafJob? first = validity ? _metadataJobs.Encoded : _metadataJobs.Plain;
            if (first is null)
            {
                _metadataJobs = validity ? (_metadataJobs.Plain, metadata) : (metadata, _metadataJobs.Encoded);
            }
            else
            {
                metadata.Same = first;
            }

            List(LeafKind.Values, group, validity);
            ListTyped(group, _listCompress);
        }

        /// <summary>A typed node's leaves, under <paramref name="validity"/>: its column, or each field of its object's in order (<see cref="WriteTyped"/>, <see cref="WriteField"/>).</summary>
        private void ListTyped(Group group, bool validity)
        {
            if (group is not ObjectGroup objects)
            {
                List(LeafKind.Typed, group, validity);
                return;
            }

            int count = objects.WrittenCount;
            for (int i = 0; i < count; i++)
            {
                Group field = objects.Written(i);
                if (field.Values.HoldsAny)
                {
                    ListVariant(field, validity);
                }
                else
                {
                    ListTyped(field, validity);
                }
            }
        }

        private LeafJob List(LeafKind kind, Group? group, bool validity)
        {
            if (_jobCount == _jobs.Count)
            {
                _jobs.Add(new LeafJob());
            }

            LeafJob job = _jobs[_jobCount++];
            job.Kind = kind;
            job.Group = group;
            job.Validity = validity;
            job.Same = null;
            job.Done = false;
            return job;
        }

        /// <summary>
        /// Writes leaf <paramref name="index"/> of those <see cref="PrepareLeaves"/> listed, apart, in <paramref name="work"/>
        /// and against <paramref name="encodings"/>, emptied first, as <see cref="Write"/> would write it in the blob: on any
        /// thread, beside the other leaves, each reading only what its node took of the chunk. What it wrote is kept, its
        /// buffers its own, for <see cref="WriteGrafted"/> to graft.
        /// </summary>
        internal void WriteLeafApart(int index, ArrayBlobWriter.Workspace work, EncodingDictionary encodings, bool compress)
        {
            LeafJob job = _jobs[index];
            if (job.Same is not null)
            {
                return;
            }

            // A dtype arena takes no two writers: the leaf's node is made in one of its own.
            DTypeArena types = new DTypeArena();
            CanonicalArena leaves = work.Leaves;
            bool encodes = work.EncodesValidity;
            encodings.Clear();
            work.Builder.Clear();
            work.EncodesValidity = job.Validity;
            try
            {
                int node = job.Kind switch
                {
                    LeafKind.Metadata => leaves.ReferenceFrom(_arena, _metadata),
                    LeafKind.Values => job.Group!.Values.Build(leaves, types, types.Binary(Nullability.Nullable), _rows),
                    _ => ((Leaf)job.Group!).Typed(leaves, types, _rows),
                };
                int written = ArrayBlobWriter.WriteChild(work, leaves, node, encodings, compress);

                // Its buffers its own, past the leaves' arena emptied below, and its tree as the builder finished it.
                work.Keep(0);
                job.Take(work, written, encodings);
            }
            finally
            {
                work.EncodesValidity = encodes;
                work.Discard();
                leaves.ResetKeepingBlocks();
            }
        }

        /// <summary>
        /// <see cref="Write"/>, each leaf grafted where the blob comes to it from what <see cref="WriteLeafApart"/> wrote of it apart:
        /// its tree written again node by node in the blob's builder, its buffers past the blob's, its ids the dictionary's in
        /// the order the blob meets them; the same bytes as the chunk written as one blob.
        /// </summary>
        /// <exception cref="InvalidOperationException">A leaf the blob comes to was not listed there, or not written.</exception>
        internal ArrayBlobWriter.BlobLease WriteGrafted(ArrayBlobWriter.Workspace blob, EncodingDictionary encodings, bool compress)
        {
            _cursor = 0;
            try
            {
                return Write(blob, encodings, compress);
            }
            finally
            {
                _cursor = -1;
                ReleaseLeaves();
            }
        }

        /// <summary>Gives back what the leaves written apart still hold: their trees, and the buffers no blob took.</summary>
        internal void ReleaseLeaves()
        {
            for (int i = 0; i < _jobCount; i++)
            {
                _jobs[i].Release();
            }
        }

        /// <summary>The leaf the blob comes to next, as <see cref="PrepareLeaves"/> listed it: grafted into <paramref name="blob"/>.</summary>
        private int Graft(ArrayBlobWriter.Workspace blob, EncodingDictionary encodings, LeafKind kind, Group? group)
        {
            LeafJob job = _cursor < _jobCount ? _jobs[_cursor++] : throw new InvalidOperationException("The blob came to a leaf no list holds.");
            if (job.Kind != kind || !ReferenceEquals(job.Group, group))
            {
                throw new InvalidOperationException("The leaves were listed in another order than the blob comes to them.");
            }

            LeafJob made = job.Same ?? job;
            if (!made.Done)
            {
                throw new InvalidOperationException("A leaf the blob comes to was not written.");
            }

            // Its buffers past the blob's: the leaf's own the first time, the blob's then, copies of them each time the rows'
            // metadata is named again, which comes after.
            int shift = blob.Buffers.Count;
            for (int i = 0; i < made.Buffers.Count; i++)
            {
                blob.Buffers.Add(job.Same is null ? made.Buffers[i] : Copied(made.Buffers[i]));
            }

            made.Taken = true;

            int budget = VortexLimits.MaxFlatBufferTables;
            FlatBufferTable root = FlatBufferTable.Root(made.Tree.AsSpan(0, made.TreeLength), ref budget);
            return Regraft(blob.Builder, encodings, root, made.Ids, shift, depth: 1);
        }

        /// <summary>A pending buffer of bytes of its own, copied from <paramref name="pending"/>'s: a view or zeros as they are.</summary>
        private static ArrayBlobWriter.PendingBuffer Copied(ArrayBlobWriter.PendingBuffer pending)
        {
            if (pending.Bytes is null || pending.Length == 0)
            {
                return pending;
            }

            Block<byte> bytes = Blocks.Rent<byte>(pending.Length);
            pending.CopyTo(bytes.AsSpan(0, pending.Length));
            return new ArrayBlobWriter.PendingBuffer(bytes, pending.Length, pending.AlignmentExponent, rented: true);
        }

        /// <summary>
        /// Writes <paramref name="node"/>, a node of a leaf's tree, and its children before it, in <paramref name="builder"/>,
        /// as the blob writer wrote it: its encoding the id <paramref name="ids"/> names interned in
        /// <paramref name="encodings"/>, its buffers <paramref name="shift"/> on; its offset.
        /// </summary>
        private static int Regraft(FlatBufferBuilder builder, EncodingDictionary encodings, FlatBufferTable node, string[] ids, int shift, int depth)
        {
            VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Array");
            FlatBufferVector children = node.GetVector(SchemaFieldIds.ArrayNodeChildren);
            int count = children.Count;
            Block<int> rented = count > 16 ? Blocks.Rent<int>(count) : default;
            Span<int> offsets = count > 16 ? rented.AsSpan() : stackalloc int[16];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    offsets[i] = Regraft(builder, encodings, children.GetTable(i), ids, shift, depth + 1);
                }

                ushort encoding = encodings.Intern(ids[node.GetUInt16(SchemaFieldIds.ArrayNodeEncoding)]);
                ReadOnlySpan<ushort> buffers = node.TryGetStructVector(SchemaFieldIds.ArrayNodeBuffers, out ReadOnlySpan<ushort> held) ? held : default;
                Span<ushort> moved = buffers.Length <= 16 ? stackalloc ushort[16] : new ushort[buffers.Length];
                for (int i = 0; i < buffers.Length; i++)
                {
                    moved[i] = checked((ushort)(buffers[i] + shift));
                }

                return ArrayWriter.WriteNode(
                    builder, encoding, node.GetByteVector(SchemaFieldIds.ArrayNodeMetadata), offsets[..count], moved[..buffers.Length], statsOffset: 0);
            }
            finally
            {
                Blocks.Return(rented);
            }
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
                0 => _cursor >= 0 ? Graft(blob, encodings, LeafKind.Metadata, null) : ArrayBlobWriter.WriteChild(blob, _arena, _metadata, encodings, compress),
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
            if (_cursor >= 0)
            {
                return Graft(blob, encodings, values ? LeafKind.Values : LeafKind.Typed, group);
            }

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

    /// <summary>What a leaf of a shredded chunk's blob is: the rows' metadata, a node's values, or a node's typed column.</summary>
    private enum LeafKind : byte
    {
        Metadata,
        Values,
        Typed,
    }

    /// <summary>
    /// A leaf of a shredded chunk's blob written apart (<see cref="Shredding.WriteLeafApart"/>): which leaf, the validity encoding
    /// the blob writes it under, and once written its tree as the builder finished it, its buffers, which are its own until a
    /// blob takes them, and the ids its tree names by index.
    /// </summary>
    private sealed class LeafJob
    {
        internal LeafKind Kind;
        internal Group? Group;
        internal bool Validity;

        /// <summary>The leaf written under the same validity encoding of which this one is a copy: the rows' metadata, named again.</summary>
        internal LeafJob? Same;

        internal bool Done;
        internal Block<byte> Tree;
        internal int TreeLength;
        internal string[] Ids = [];

        /// <summary>Whether a blob took its buffers, which are the blob's to give back from then on.</summary>
        internal bool Taken;

        internal List<ArrayBlobWriter.PendingBuffer> Buffers { get; } = [];

        /// <summary>Takes what <paramref name="work"/> wrote of the leaf, its tree at <paramref name="written"/>, against <paramref name="encodings"/>.</summary>
        internal void Take(ArrayBlobWriter.Workspace work, int written, EncodingDictionary encodings)
        {
            ReadOnlySpan<byte> tree = work.Builder.Finish(written);
            if (Tree.Length < tree.Length)
            {
                Blocks.Return(Tree);
                Tree = Blocks.Rent<byte>(tree.Length);
            }

            tree.CopyTo(Tree.AsSpan());
            TreeLength = tree.Length;
            Buffers.Clear();
            Buffers.AddRange(work.Buffers);
            work.Buffers.Clear();
            Ids = [.. encodings.Ids];
            Taken = false;
            Done = true;
        }

        /// <summary>Gives back the buffers no blob took, and its tree's block.</summary>
        internal void Release()
        {
            for (int i = 0; !Taken && i < Buffers.Count; i++)
            {
                if (Buffers[i].Rented)
                {
                    Blocks.ReturnBytes(Buffers[i].Bytes);
                }
            }

            Buffers.Clear();
            Blocks.Return(Tree);
            Tree = default;
            TreeLength = 0;
            Taken = false;
            Done = false;
        }
    }

    /// <summary>
    /// The nodes a row reached as it was read, in order, each with where its value lies in the row's: what a row that repeats
    /// the value takes again, node by node, each node its own part alone (<see cref="Group.Again(ReadOnlySpan{byte}, int)"/>). A row whose object kept
    /// keys of its own beside the shredded ones is not replayed: they are read from its fields, which a replay does not read.
    /// </summary>
    private sealed class Replay
    {
        private Group[] _groups = new Group[16];
        private int[] _offsets = new int[16];
        private int[] _lengths = new int[16];
        private int _count;

        /// <summary>Whether the row read last may be replayed.</summary>
        internal bool Whole { get; private set; }

        /// <summary>A row is read: what the row before reached is forgotten.</summary>
        internal void Begin()
        {
            _count = 0;
            Whole = true;
        }

        /// <summary><paramref name="group"/> took the value of <paramref name="length"/> bytes at <paramref name="offset"/> of the row's.</summary>
        internal void Note(Group group, int offset, int length)
        {
            if (_count == _groups.Length)
            {
                Array.Resize(ref _groups, 2 * _count);
                Array.Resize(ref _offsets, 2 * _count);
                Array.Resize(ref _lengths, 2 * _count);
            }

            _groups[_count] = group;
            _offsets[_count] = offset;
            _lengths[_count] = length;
            _count++;
        }

        /// <summary>The row read takes what a replay cannot take again.</summary>
        internal void Break() => Whole = false;

        /// <summary>Row <paramref name="row"/>, whose value <paramref name="value"/> is the last row read's, takes what it took.</summary>
        internal void Again(ReadOnlySpan<byte> value, int row)
        {
            for (int i = 0; i < _count; i++)
            {
                _groups[i].Again(value.Slice(_offsets[i], _lengths[i]), row);
            }
        }

        /// <summary>
        /// The <paramref name="count"/> rows from <paramref name="row"/> on, which follow each other and whose value
        /// <paramref name="value"/> is the last row read's, take what it took, each node the run at once.
        /// </summary>
        internal void Again(ReadOnlySpan<byte> value, int row, int count)
        {
            for (int i = 0; i < _count; i++)
            {
                _groups[i].Again(value.Slice(_offsets[i], _lengths[i]), row, count);
            }
        }
    }

    private abstract class Group
    {
        /// <summary>What the node does not take of the rows that hold it: a value of another shape or class, an object's other keys.</summary>
        internal BinaryValues Values { get; } = new BinaryValues();

        /// <summary>The last row the node was handed, -1 before the first.</summary>
        internal int Last { get; private set; } = -1;

        /// <summary>Where the node notes the rows it takes, for the rows that repeat their values; null for none.</summary>
        private protected Replay? Record { get; private set; }

        /// <summary>Has the node, and every node below it, note the values it takes in <paramref name="record"/>.</summary>
        internal virtual void Attach(Replay record) => Record = record;

        /// <summary>
        /// Takes at row <paramref name="row"/> its own part of <paramref name="value"/>, the value it took at the row read last,
        /// which this row repeats: a leaf what it took then, an object its row, its keys taking theirs by their own notes.
        /// </summary>
        internal virtual void Again(ReadOnlySpan<byte> value, int row)
        {
            Last = row;
            Take(default, -1, value, row, 0);
        }

        /// <summary>
        /// Takes at the <paramref name="count"/> rows from <paramref name="row"/> on, which follow each other and repeat the
        /// row read last, its own part of <paramref name="value"/> again: a row at a time, but where a node takes a run at once.
        /// </summary>
        internal virtual void Again(ReadOnlySpan<byte> value, int row, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Again(value, row + i);
            }
        }

        /// <summary>Notes <paramref name="row"/> as the last the node took, as <see cref="Present"/> does.</summary>
        private protected void Mark(int row) => Last = row;

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
        /// Row <paramref name="row"/>, whose node holds <paramref name="value"/>, <paramref name="offset"/> bytes into the row's
        /// value, its keys named by <paramref name="dictionary"/>, the same as every row of its run of <paramref name="epoch"/>;
        /// rows come in order.
        /// </summary>
        internal void Present(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset = 0)
        {
            Last = row;
            Record?.Note(this, offset, value.Length);
            Take(in dictionary, epoch, value, row, offset);
        }

        /// <summary>Takes what the node holds of row <paramref name="row"/>, as <see cref="Present"/> says.</summary>
        protected abstract void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset);

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

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset)
        {
            VariantScalar scalar = VariantScalar.Read(value);
            if (_typed.Takes(scalar.Kind))
            {
                _typed.Add(row, in scalar);
                Bounds?.Note(row, scalar);
                _typedLast = true;
                return;
            }

            Values.Add(row, value);
            _typedLast = false;
        }

        // Whether the value taken last went to the typed column, which a row that repeats it takes again there.
        private bool _typedLast;

        /// <summary>The value taken last again, not read: the typed column's last value, or the value kept whole.</summary>
        internal override void Again(ReadOnlySpan<byte> value, int row)
        {
            Mark(row);
            if (!_typedLast)
            {
                Values.Add(row, value);
                return;
            }

            _typed.Again(row);
            Bounds?.Again(row, value);
        }

        /// <summary>The run takes the typed column's last value again at once, and its blocks count it; a value kept whole a row at a time.</summary>
        internal override void Again(ReadOnlySpan<byte> value, int row, int count)
        {
            if (!_typedLast)
            {
                base.Again(value, row, count);
                return;
            }

            Mark(row + count - 1);
            _typed.Again(row, count);
            Bounds?.Again(row, count, value);
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

        internal override void Attach(Replay record)
        {
            base.Attach(record);
            foreach (Group field in _fields)
            {
                field.Attach(record);
            }
        }

        /// <summary>The row's object takes its row again, its keys theirs by their own notes; any other value its value.</summary>
        internal override void Again(ReadOnlySpan<byte> value, int row)
        {
            Mark(row);
            if ((value[0] & 0x03) != ParquetVariant.BasicObject)
            {
                Values.Add(row, value);
                return;
            }

            _objects.Add(row);
        }

        /// <summary>The run's objects take their rows at once; any other value a row at a time.</summary>
        internal override void Again(ReadOnlySpan<byte> value, int row, int count)
        {
            if ((value[0] & 0x03) != ParquetVariant.BasicObject)
            {
                base.Again(value, row, count);
                return;
            }

            Mark(row + count - 1);
            _objects.AddSequence(row, count);
        }

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset)
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
                        ReadOnlySpan<byte> held = fields.ValueAt(i);
                        int at = offset + (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(value), ref MemoryMarshal.GetReference(held));
                        field.Present(in dictionary, epoch, held, row, at);
                    }
                }
                else
                {
                    _kept[kept++] = i;
                }
            }

            // The keys kept apart are read from the row's fields, which a row that repeats it would not read.
            if (kept > 0)
            {
                Record?.Break();
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

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset)
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

        /// <summary>Takes again at row <paramref name="row"/> the value taken last, which the row repeats.</summary>
        internal void Again(int row)
        {
            Rows?.Add(row);
            Count++;
            Repeat();
        }

        /// <summary>Takes again at the <paramref name="count"/> rows from <paramref name="row"/> on the value taken last, which they repeat.</summary>
        internal void Again(int row, int count)
        {
            Rows?.AddSequence(row, count);
            Count += count;
            Repeat(count);
        }

        /// <summary>Takes the value taken last again.</summary>
        protected abstract void Repeat();

        /// <summary>Takes the value taken last <paramref name="count"/> times again.</summary>
        protected abstract void Repeat(int count);

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

        protected override void Repeat() => _values.Add(_values.Last);

        protected override void Repeat(int count) => _values.AddRepeated(_values.Last, count);

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

        protected override void Repeat() => _values.Add(_values.Last);

        protected override void Repeat(int count) => _values.AddRepeated(_values.Last, count);

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

        protected override void Repeat() => _values.Add(_values.Last);

        protected override void Repeat(int count) => _values.AddRepeated(_values.Last, count);

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

        // The length of the text taken last, which a row that repeats it takes again.
        private int _lastLength;

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _lengths.Clear(arena);
            _bytes.Clear(arena);
        }

        internal override DType Type(DTypeArena types) => types.Utf8(Nullability.Nullable);

        protected override void Value(in VariantScalar scalar)
        {
            ReadOnlySpan<byte> text = scalar.Bytes;
            _lengths.Add(text.Length);
            _bytes.AddRange(text);
            _lastLength = text.Length;
        }

        // A repeat's length is complemented, which tells Binary that it views the bytes of the value it repeats, kept once.
        protected override void Repeat() => _lengths.Add(~_lastLength);

        protected override void Repeat(int count) => _lengths.AddRepeated(~_lastLength, count);

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
            Block<byte> rented = default;
            Span<byte> into = length <= 512 ? stackalloc byte[512] : (rented = Blocks.Rent<byte>(length));
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
                Blocks.Return(rented);
            }
        }

        /// <summary>The column of <paramref name="rows"/> rows as a node wearing <paramref name="dtype"/>, its validity's dtype made in <paramref name="types"/>.</summary>
        internal int Build(CanonicalArena arena, DTypeArena types, DType dtype, int rows) =>
            Binary(arena, dtype, rows, RowsValidity(arena, types, rows, _rows), _rows, _lengths, _bytes);
    }

    /// <summary>
    /// A column of byte strings over <paramref name="rows"/> rows, as views and one data buffer: each of
    /// <paramref name="lengths"/>' strings, end to end in <paramref name="bytes"/>, at its row of <paramref name="at"/>,
    /// or at the next row when <paramref name="at"/> is null. A length complemented is a repeat of the string before, whose
    /// bytes are not there again: its view names the bytes of the string it repeats.
    /// </summary>
    /// <remarks>
    /// A series' attribute repeats row after row, and each row viewing its own copy of the bytes, every search for the
    /// column's runs compared the bytes of each row with the row before's: 362 M rows of the 828 M a consolidation of metric
    /// records searched, 1.9 s of its processor (2026-11-18). Its rows view one copy now, and their views are equal. The
    /// column keeps a repeat's bytes once and stands for every row's bytes (<see cref="CanonicalRecord.SharesBytes"/>), as
    /// its buffer held them before, so that its encoders choose what they chose. The views are zero past an inline value's
    /// bytes, as
    /// <see cref="CanonicalSupport.WriteView(Span{byte}, ReadOnlySpan{byte}, int, int, int)"/> writes them into zeroed room.
    /// </remarks>
    private static int Binary(
        CanonicalArena arena, DType dtype, int rows, Validity validity, Appended<int>? at, Appended<int> lengths, Appended<byte> bytes)
    {
        int size = checked(rows * ViewSize);
        VortexBuffer views = arena.Allocate(Math.Max(size, 1), 64, out Span<byte> into).Slice(0, size);
        int used = bytes.Count;
        if (used == 0)
        {
            return arena.AddVarBinView(dtype, rows, validity, views, [], padded: true);
        }

        VortexBuffer data = arena.AllocateUninitialized(used, 64, out Span<byte> written).Slice(0, used);
        written = written[..used];
        bytes.CopyTo(written);
        Span<Int128> laid = MemoryMarshal.Cast<byte, Int128>(into)[..rows];
        int start = 0;

        // A repeat's view is the one written for the string it repeats, kept as its two words: stored again, not composed.
        Int128 view = default;
        long repeated = 0;

        // The rows and the lengths, appended a value at a time to lists that grow alike, are read a block of both at a time,
        // and a run of repeats on rows that follow each other is laid at once. Read a value at a time, a row's view at a
        // time, the leaves of a consolidation of metric records took 7.3 % of its processor (2026-11-18).
        if (at is null || Aligned(at, lengths))
        {
            int first = 0;
            for (int block = 0; block < lengths.Blocks; block++)
            {
                ReadOnlySpan<int> counts = lengths.Block(block);
                ReadOnlySpan<int> tos = at is null ? default : at.Block(block);
                int k = 0;
                while (k < counts.Length)
                {
                    int count = counts[k];
                    int end = k + 1;
                    if (count >= 0)
                    {
                        view = Composed(written, start, count);
                        start += count;
                    }
                    else
                    {
                        repeated += ~count;
                    }

                    // The repeats after it, which view what it views.
                    while (end < counts.Length && counts[end] < 0)
                    {
                        repeated += ~counts[end];
                        end++;
                    }

                    int from = at is null ? first + k : tos[k];
                    int last = at is null ? first + end - 1 : tos[end - 1];
                    if ((uint)from >= (uint)rows || (uint)last >= (uint)rows)
                    {
                        throw new ArgumentOutOfRangeException(nameof(at), "A string's row lies past the column's.");
                    }

                    // Rows that follow each other take the view at once: the rows a node takes values at only rise, so that
                    // the run's are consecutive when its last is as far from its first as the run is long.
                    if (last - from == end - 1 - k)
                    {
                        laid.Slice(from, end - k).Fill(view);
                    }
                    else
                    {
                        for (int j = k; j < end; j++)
                        {
                            int to = tos[j];
                            if ((uint)to >= (uint)rows)
                            {
                                throw new ArgumentOutOfRangeException(nameof(at), "A string's row lies past the column's.");
                            }

                            laid[to] = view;
                        }
                    }

                    k = end;
                }

                first += counts.Length;
            }
        }
        else
        {
            Reading<int> row = new Reading<int>(at);
            Reading<int> length = new Reading<int>(lengths);
            for (int i = 0; i < lengths.Count; i++)
            {
                int to = row.Next();
                int count = length.Next();
                if (count >= 0)
                {
                    view = Composed(written, start, count);
                    start += count;
                }
                else
                {
                    repeated += ~count;
                }

                if ((uint)to >= (uint)rows)
                {
                    throw new ArgumentOutOfRangeException(nameof(at), "A string's row lies past the column's.");
                }

                laid[to] = view;
            }
        }

        return arena.AddVarBinView(dtype, rows, validity, views, [data], padded: true, repeated > 0 ? used + repeated : -1);
    }

    /// <summary>The view of the <paramref name="count"/> bytes of <paramref name="written"/> from <paramref name="start"/> on, zero past an inline value's bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int128 Composed(Span<byte> written, int start, int count)
    {
        Int128 view = default;
        if (count > 0)
        {
            Span<byte> composed = MemoryMarshal.AsBytes(new Span<Int128>(ref view));
            CanonicalSupport.WriteView(composed, written.Slice(start, count), count, 0, start);
        }

        return view;
    }

    /// <summary>Whether <paramref name="rows"/> and <paramref name="values"/> hold as many values a block, appended to alike.</summary>
    private static bool Aligned(Appended<int> rows, Appended<int> values)
    {
        if (rows.Blocks != values.Blocks || rows.Count != values.Count)
        {
            return false;
        }

        for (int block = 0; block < rows.Blocks; block++)
        {
            if (rows.Block(block).Length != values.Block(block).Length)
            {
                return false;
            }
        }

        return true;
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

        /// <summary>Appends <paramref name="value"/> <paramref name="count"/> times, a block's room at a time.</summary>
        internal void AddRepeated(T value, int count)
        {
            while (count > 0)
            {
                if (_left == 0)
                {
                    Grow();
                }

                int take = Math.Min(_left, count);
                new Span<T>(_next, take).Fill(value);
                _next += take;
                _left -= take;
                Count += take;
                count -= take;
            }
        }

        /// <summary>
        /// Appends <paramref name="values"/> <paramref name="count"/> times: as many copies as a block's room holds written once
        /// and doubled, a copy that crosses into the next block appended alone.
        /// </summary>
        /// <remarks>
        /// A text a run of rows repeats, appended a row at a time, was 57 % of the take of a consolidation of metric records'
        /// variant columns, whose series repeat their attributes over every input's row. Tiled, and the rows' views read once
        /// a take, the take of the widest of them went from 0.79 ms a block to 0.40 at degree 14 (2026-11-18).
        /// </remarks>
        internal void AddTiled(ReadOnlySpan<T> values, int count)
        {
            int width = values.Length;
            if (width == 0)
            {
                return;
            }

            while (count > 0)
            {
                if (_left < width)
                {
                    AddRange(values);
                    count--;
                    continue;
                }

                int fit = Math.Min(_left / width, count);
                Span<T> into = new Span<T>(_next, fit * width);
                values.CopyTo(into);
                int filled = width;
                while (filled < into.Length)
                {
                    int chunk = Math.Min(filled, into.Length - filled);
                    into[..chunk].CopyTo(into.Slice(filled, chunk));
                    filled += chunk;
                }

                _next += into.Length;
                _left -= into.Length;
                Count += into.Length;
                count -= fit;
            }
        }

        /// <summary>Appends the <paramref name="count"/> integers from <paramref name="first"/> on, of a list of integers: rows that follow each other.</summary>
        internal void AddSequence(int first, int count)
        {
            System.Diagnostics.Debug.Assert(typeof(T) == typeof(int), "A sequence is of rows.");
            while (count > 0)
            {
                if (_left == 0)
                {
                    Grow();
                }

                int take = Math.Min(_left, count);
                int* into = (int*)_next;
                for (int i = 0; i < take; i++)
                {
                    into[i] = first + i;
                }

                _next += take;
                _left -= take;
                Count += take;
                first += take;
                count -= take;
            }
        }

        /// <summary>The value appended last; meaningless before one.</summary>
        internal T Last => *(_next - 1);

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
