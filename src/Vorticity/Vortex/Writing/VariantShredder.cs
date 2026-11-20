using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
    internal sealed class Shredding : CanonicalArena.ITakenRows
    {
        private readonly Group _root;

        // The chunk the nodes took: the arena that holds it, what they took of it and the frames of the tree; its
        // node, its rows' metadata and its rows; the dtypes of its tree.
        private CanonicalArena _arena = null!;
        private int _node;
        private int _metadata;
        private int _rows;
        private DTypeArena _types = null!;

        /// <summary>
        /// A shredding of <paramref name="plan"/>. With <paramref name="ranged"/>, one whose rows another shredding of the plan
        /// takes ranges of once they are all in (<see cref="Index"/>, <see cref="AppendRange"/>): its nodes keep what a range
        /// of their rows is found from.
        /// </summary>
        internal Shredding(VariantShredding plan, bool ranged = false)
        {
            Plan = plan;
            _planKeys = KeysOf(plan);
            _root = Group.For(plan);
            _root.Attach(_replay);
            _ranged = ranged;
            if (ranged)
            {
                _root.Range();
            }
        }

        // Whether the nodes keep what a range of their rows is found from, the arena they carve it from, and whether it is
        // built over the rows taken.
        private readonly bool _ranged;
        private CanonicalArena? _store;

        /// <summary>
        /// Who lent the shredding to a decode, for the rows of a window of a job's input, and takes it back once the arena
        /// the window lies in is reset (<see cref="VariantTake.Rent"/>); null for a shredding nobody lends.
        /// </summary>
        internal VariantTake? Lender { get; init; }

        void CanonicalArena.ITakenRows.Forgotten() => Lender?.Return(this);

        void CanonicalArena.ITakenRows.Rebuild(
            int first, int count, ReadOnlySpan<byte> views, ReadOnlySpan<byte> metadataViews, ReadOnlySpan<VortexBuffer> metadataBuffers,
            Span<byte> into, int origin) =>
            Rebuild(first, count, views, metadataViews, metadataBuffers, into, origin);

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
            int value = variant.GetFieldIndex(1);

            // Values not put back together stay so: what took their rows holds them, and no row of them is read here.
            bool held = arena.GetNode(value).Kind == CanonicalKind.Shredded && arena.PutBack(value) < 0;
            return new Rows(
                arena, node, ArrayBlobWriter.Materialize(arena, variant.GetFieldIndex(0)), held ? value : ArrayBlobWriter.Materialize(arena, value), variant.Length);
        }

        /// <summary>
        /// Begins a chunk taken in parts (<see cref="TakeMore"/>), what each node takes carved from <paramref name="store"/>,
        /// which lasts as long as the chunk: nothing taken, the keys <paramref name="bounds"/> asks for counted block by block.
        /// With <paramref name="counted"/>, a block read alone whose paths a census counts from what it took
        /// (<see cref="Count"/>): the runs of its rows that repeat the row before them are kept for it.
        /// </summary>
        internal void Begin(CanonicalArena store, Indexes.ChunkBlockBounds? bounds, bool counted = false)
        {
            _keepsRuns = counted;
            _runCount = 0;
            _store = store;
            Indexed = false;
            _reshaped = false;
            _replay.Clear();
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

            // A row whose value and metadata views are the row before's, which was taken, repeats its bytes: taken with its run
            // without reading them. A block's rows view a value that repeats the row before's once, and comparing each repeat's
            // bytes with the last value's was a third of the take of a consolidation of metric records (2026-11-18).
            ReadOnlySpan<Int128> valueViews = everyValue ? MemoryMarshal.Cast<byte, Int128>(valueNode.Views.Span) : default;
            ReadOnlySpan<Int128> metadataViews = everyValue ? MemoryMarshal.Cast<byte, Int128>(metadataNode.Views.Span) : default;
            int taken = -2;
            for (int at = 0; at < rows.Count; at++)
            {
                if (!everyRow && !ArenaWords.IsValid(arena, node, at))
                {
                    continue;
                }

                int row = offset + at;
                if (taken == at - 1 && epoch == lastEpoch && everyValue
                    && valueViews[at] == valueViews[at - 1] && metadataViews[at] == metadataViews[at - 1])
                {
                    if (runCount > 0 && runStart + runCount != row)
                    {
                        Again(lastValue, runStart, runCount);
                        runCount = 0;
                    }

                    runStart = runCount == 0 ? row : runStart;
                    runCount++;
                    taken = at;
                    continue;
                }

                taken = at;
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
                        Again(lastValue, runStart, runCount);
                        runCount = 0;
                    }

                    runStart = runCount == 0 ? row : runStart;
                    runCount++;
                    continue;
                }

                if (runCount > 0)
                {
                    Again(lastValue, runStart, runCount);
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
                Again(lastValue, runStart, runCount);
            }

            _epoch = epoch;
            Taken = offset + rows.Count;
        }

        // For a chunk that keeps them (Begin), the runs of rows that took again what the row read before them took, a first
        // row and a count each, in the rows' order: what a census counts a run at once from (Count).
        private int[] _runs = [];
        private int _runCount;
        private bool _keepsRuns;

        /// <summary>The <paramref name="count"/> rows from <paramref name="row"/> on take again what the row read last took, of value <paramref name="value"/>.</summary>
        private void Again(ReadOnlySpan<byte> value, int row, int count)
        {
            _replay.Again(value, row, count);
            if (!_keepsRuns)
            {
                return;
            }

            if (2 * (_runCount + 1) > _runs.Length)
            {
                Array.Resize(ref _runs, Math.Max(64, 2 * _runs.Length));
            }

            _runs[2 * _runCount] = row;
            _runs[(2 * _runCount) + 1] = count;
            _runCount++;
        }

        /// <summary>
        /// Takes what <paramref name="block"/>, a shredding of the same plan begun for a block's rows alone, took of them, as
        /// the chunk's rows from <paramref name="offset"/> on, after the rows taken before, which they follow: what
        /// <see cref="TakeMore"/> takes of the same rows there, node by node, and no row read. The keys the chunk counts block
        /// by block are counted from what the block typed.
        /// </summary>
        /// <remarks>
        /// A column's blocks are taken one after the other, each after the one before, since each adds to the chunk's nodes:
        /// reading the rows there, the take of a consolidation of log records' attributes was 4.4 s of work in a row a range
        /// of its job, under a write pass of 6.4 s (2026-11-19). A block read alone, beside anything, leaves the chain the
        /// copy of what it took.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="block"/> is of another plan.</exception>
        internal void Append(Shredding block, int offset)
        {
            if (!ReferenceEquals(block.Plan, Plan))
            {
                throw new ArgumentException("A block's shredding is added to a chunk's of the same plan.", nameof(block));
            }

            _root.Append(block._root, offset, block.Runs);
            Taken = offset + block.Taken;
        }

        /// <summary>
        /// Takes what each node holds of the rows of <paramref name="group"/>, the group another shredding holds them in, a
        /// struct of the rows' <c>metadata</c>, the <c>value</c> that shredding left of each and its <c>typed_value</c>
        /// (<c>ShreddedTree.Group</c>), as the chunk's rows from <paramref name="offset"/> on, after the rows taken before:
        /// what <see cref="TakeMore"/> takes of the same rows put back together, and no row put back together. A key both
        /// shreddings type in one class is taken as its column lies, and what the other left of a row is read as a row's
        /// value is, alone. False, and nothing taken, for a group that is not taken so (<see cref="ObjectGroup.Projects"/>).
        /// </summary>
        /// <remarks>
        /// The texts are taken whole, each its bytes: a column tells nothing of a row that repeats the one before, which a
        /// read of the rows finds and takes again at once.
        /// </remarks>
        /// <exception cref="VortexFormatException">What the other shredding left of a row does not decode.</exception>
        internal bool TakeGroup(CanonicalArena arena, int group, int offset)
        {
            CanonicalNode node = arena.GetNode(group);
            int metadata = -1;
            int value = -1;
            int typed = -1;
            for (int f = 0; node.Kind == CanonicalKind.Struct && f < node.FieldCount; f++)
            {
                ReadOnlySpan<byte> name = node.DType.GetFieldNameUtf8(f);
                if (name.SequenceEqual("metadata"u8))
                {
                    metadata = node.GetFieldIndex(f);
                }
                else if (name.SequenceEqual("value"u8))
                {
                    value = node.GetFieldIndex(f);
                }
                else if (name.SequenceEqual("typed_value"u8))
                {
                    typed = node.GetFieldIndex(f);
                }
            }

            if (_root is not ObjectGroup root || metadata < 0 || typed < 0)
            {
                return false;
            }

            typed = ArrayBlobWriter.Materialize(arena, typed);
            if (!root.Projects(arena, typed))
            {
                return false;
            }

            // The rows the column holds, those of them whose value is an object, and those the other shredding left a value of.
            int rows = node.Length;
            int words = (rows + 63) >> 6;
            Block<ulong> masks = Blocks.Rent<ulong>(Math.Max(2 * words, 1));
            try
            {
                Span<ulong> held = masks.AsSpan(0, words);
                Span<ulong> left = masks.AsSpan(words, words);
                ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, group);
                ReadOnlySpan<ulong> objects = ArenaWords.Validity(arena, typed);
                int residual = value < 0 ? -1 : ArrayBlobWriter.Materialize(arena, value);
                ReadOnlySpan<ulong> kept = residual < 0 ? default : ArenaWords.Validity(arena, residual);
                for (int w = 0; w < words; w++)
                {
                    ulong column = valid.IsEmpty ? ulong.MaxValue : valid[w];
                    if (w == words - 1 && (rows & 63) != 0)
                    {
                        column &= (1UL << (rows & 63)) - 1;
                    }

                    held[w] = column & (objects.IsEmpty ? ulong.MaxValue : objects[w]);

                    // Read alone: the value left of a row, and a row neither holds, the variant null.
                    left[w] = column & (residual < 0 ? ~held[w] : (kept.IsEmpty ? ulong.MaxValue : kept[w]) | ~held[w]);
                }

                root.TakeProjected(arena, typed, held, rows, offset);

                ViewValues metadataValues = new ViewValues(arena.GetNode(ArrayBlobWriter.Materialize(arena, metadata)));
                ViewValues values = residual < 0 ? default : new ViewValues(arena.GetNode(residual));
                ReadOnlySpan<byte> previous = default;
                int epoch = _epoch;
                bool started = false;
                for (int w = 0; w < words; w++)
                {
                    for (ulong bits = left[w]; bits != 0; bits &= bits - 1)
                    {
                        int at = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bool holds = residual >= 0 && (kept.IsEmpty || ((kept[at >> 6] >> (at & 63)) & 1) != 0);
                        ReadOnlySpan<byte> bytes = holds ? values.At(at) : VariantEncoding.NullValue;
                        if (((held[at >> 6] >> (at & 63)) & 1) == 0)
                        {
                            // No object: the value whole, as the row's value read is kept. An object left whole there, which
                            // no shredding of ours writes, would be read key by key: kept whole, the rows are not those read.
                            _reshaped |= !bytes.IsEmpty && (bytes[0] & 0x03) == ParquetVariant.BasicObject;
                            root.Values.Add(offset + at, bytes);
                            continue;
                        }

                        ReadOnlySpan<byte> metadataBytes = metadataValues.At(at);
                        if (!started || !metadataBytes.SequenceEqual(previous))
                        {
                            started = true;
                            epoch++;
                            previous = metadataBytes;
                        }

                        VariantDictionary dictionary = VariantDictionary.Read(metadataBytes);
                        _replay.Begin();
                        root.TakeKept(in dictionary, epoch, bytes, offset + at);
                    }
                }

                _epoch = epoch;
                Taken = offset + rows;
                return true;
            }
            finally
            {
                Blocks.Return(masks);
            }
        }

        // Whether a row taken as another shredding holds it (TakeGroup) was taken otherwise than its rows read would be.
        private bool _reshaped;

        /// <summary>
        /// Whether the rows taken as another shredding holds them (<see cref="TakeGroup"/>) are put back together, to the
        /// byte, as that shredding puts them back together (<see cref="Rebuild"/>): an object at the top whose every key
        /// took values of no keys of their own, each in the form its class is put back in, no key dropped for being held
        /// twice, and no object left whole where the other shredding types none.
        /// </summary>
        /// <remarks>
        /// What a typed column does not keep of the bytes it read is kept beside it or tells here. An integer read in fewer
        /// than eight bytes keeps its width (<c>IntegerValues</c>), since a job that types a key its inputs left in their
        /// rows' values meets them at every merge. A short text read in the long form, which no writer of ours writes,
        /// tells here.
        /// </remarks>
        internal bool Exact => !_reshaped && !_replay.Dropped && _root is ObjectGroup root && root.Exact;

        /// <summary>
        /// Writes the values of the rows from <paramref name="first"/> on, <paramref name="count"/> of the rows taken, that
        /// are objects, as the shredding they were taken from puts them back together (<see cref="ObjectGroup.Rebuild"/>):
        /// each where its view of <paramref name="views"/> says it lies, <paramref name="origin"/> bytes before
        /// <paramref name="into"/>. Any range of a shredding made ready for ranges (<see cref="Index"/>), found at once; of
        /// another, searched for.
        /// </summary>
        /// <exception cref="InvalidOperationException">The shredding is not <see cref="Exact"/>, or a row does not take the bytes it was sized at.</exception>
        internal void Rebuild(
            int first, int count, ReadOnlySpan<byte> views, ReadOnlySpan<byte> metadataViews, ReadOnlySpan<VortexBuffer> metadataBuffers,
            Span<byte> into, int origin)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(first);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Taken - first);
            if (!Exact || _root is not ObjectGroup root)
            {
                throw new InvalidOperationException("Only a shredding that kept every byte of the rows it took puts them back together.");
            }

            root.Rebuild(first, count, Indexed, views, metadataViews, metadataBuffers, into, origin);
        }

        /// <summary>Whether ranges of the rows taken may be taken by another shredding of the plan (<see cref="Index"/>).</summary>
        internal bool Indexed { get; private set; }

        /// <summary>
        /// Makes the rows taken ready for another shredding of the plan to take ranges of them (<see cref="AppendRange"/>),
        /// once they are all in: where each node holds each row, and where each text's bytes start, carved from the chunk's
        /// store, after which the shredding only is read, by any thread. False, and no range taken of it, for a shredding
        /// not made for it, or whose rows took again what the row before them took, a text's bytes then kept once for
        /// several rows, or one that dropped a key's second value.
        /// </summary>
        internal bool Index()
        {
            Indexed = _ranged && _store is { } store && !_replay.Dropped && _root.Index(store, Taken);
            return Indexed;
        }

        /// <summary>
        /// Takes what <paramref name="source"/>, a shredding of the same plan made ready for it (<see cref="Index"/>), took of
        /// its <paramref name="count"/> rows from <paramref name="first"/> on, as the chunk's rows from <paramref name="at"/>
        /// on, after the rows taken before, which they follow: what <see cref="TakeMore"/> takes of the same rows there, node
        /// by node, and no row read. Each text is taken whole, a row that repeats the row before's among them, and no key is
        /// counted block by block: a block read alone takes ranges, which a chunk then takes whole (<see cref="Append"/>).
        /// </summary>
        /// <remarks>
        /// A merge hands its writer runs of its inputs' rows, a block's from several batches: where the scan of an input
        /// took a batch's rows as its file's shredding holds them (<see cref="TakeGroup"/>), the block takes its runs of them
        /// here, and no row of it is read.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="source"/> is of another plan, or not made ready.</exception>
        internal void AppendRange(Shredding source, int first, int count, int at)
        {
            if (!ReferenceEquals(source.Plan, Plan) || !source.Indexed)
            {
                throw new ArgumentException("A range is taken of a shredding of the same plan, made ready for it.", nameof(source));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(first);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, source.Taken - first);
            _root.AppendRange(source._root, first, count, at - first);
            Taken = at + count;
        }

        /// <summary>The runs kept (<see cref="Begin"/>), a first row and a count each; none for a chunk that keeps none.</summary>
        private ReadOnlySpan<int> Runs => _keepsRuns ? _runs.AsSpan(0, 2 * _runCount) : default;

        /// <summary>
        /// Counts in <paramref name="census"/> the paths of <paramref name="rows"/>, a block's rows read alone, numbered from
        /// its first (<see cref="Begin"/>, then <see cref="TakeMore"/> at 0), from what each node took of them: what the
        /// census counts of the rows read one after the other (<see cref="PathCensus.Add(in PathCensus.Counting)"/>), their
        /// values holding <paramref name="valueBytes"/> bytes. False, and nothing counted, where the two would differ: an
        /// object held a key the shredding takes twice, whose second value it dropped, or the census is so near its most keys
        /// that which ones it counts depends on the order it meets them in.
        /// </summary>
        /// <remarks>
        /// A value the shredding typed is counted from its node, with no row read: the census read every row's object again,
        /// field by field, 8 % of the processor of a consolidation of log records, in a row from a block to the next
        /// (2026-11-19). What the nodes kept whole is read as it was, a key's value of another class and an object's other
        /// keys.
        /// </remarks>
        internal bool Count(PathCensus census, in Rows rows, long valueBytes)
        {
            if (!_keepsRuns || _replay.Dropped || !census.HasRoomFor(_planKeys + _root.KeptKeys))
            {
                return false;
            }

            long first = census.BeginTaken(rows.Count, valueBytes);
            Counting counting = new Counting(census, new ViewValues(rows.Arena.GetNode(rows.Metadata)), first, Runs);
            _root.Count(in counting, census.Root, depth: 1);
            return true;
        }

        /// <summary>
        /// Whether the values of key <paramref name="key"/> of <paramref name="plan"/>'s object that <paramref name="read"/>
        /// takes are those its typed column holds, a float kept whole beside doubles aside: a key shredded as values of one
        /// class, read in that class, whose column over a block is written from what the block's read took of it
        /// (<see cref="KeyColumn"/>).
        /// </summary>
        internal static bool Reads(VariantShredding plan, int key, VariantRead read) =>
            plan.Kind == VariantShreddingKind.Object && (uint)key < (uint)plan.Keys.Count
            && plan.Keys[key].Field is { Kind: VariantShreddingKind.Primitive } field
            && read == field.Class switch
            {
                VariantClass.Integer => VariantRead.AsInt64,
                VariantClass.Double => VariantRead.AsDouble,
                VariantClass.String => VariantRead.AsString,
                _ => VariantRead.AsBool,
            };

        /// <summary>Whether a row's object held a key the shredding takes twice, whose second value it dropped.</summary>
        internal bool Dropped => _replay.Dropped;

        /// <summary>
        /// The column of key <paramref name="key"/> of the plan's object over the <paramref name="rows"/> rows taken, a block's
        /// read alone, read as <paramref name="read"/> (<see cref="Reads"/>), written into <paramref name="into"/>: the values
        /// a read of the rows' keys writes of it (<see cref="VariantFieldExpr.KeyReads.Write"/>), from what the key's node
        /// took and no row read. A column of texts views the bytes the node took. The keys no row holds share
        /// <paramref name="absent"/>, as there.
        /// </summary>
        /// <remarks>
        /// The keys a variant column shreds are read for their indexes a block at a time, a pass over every row's object that
        /// notes where each key lies, then each key's values read from its notes: 20.5 billion of the 210 billion cycles of a
        /// consolidation of log records, beside the take that had typed the same values (2026-11-19).
        /// </remarks>
        internal int KeyColumn(CanonicalArena into, int key, VariantRead read, int rows, ref VortexBuffer absent) =>
            ((PrimitiveGroup)((ObjectGroup)_root).Field(key)).Column(into, read, rows, ref absent, Runs);

        // The keys the plan names, every depth together: the most a count of what the nodes typed may add to a census.
        private readonly int _planKeys;

        private static int KeysOf(VariantShredding plan)
        {
            int keys = plan.Keys.Count;
            foreach (ShreddedKey key in plan.Keys)
            {
                keys += KeysOf(key.Field);
            }

            return keys;
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

        /// <summary>Whether a row of the chunk held a key the shredding takes twice, whose second value was dropped: what the nodes took is then not all the rows hold.</summary>
        internal bool Dropped { get; private set; }

        /// <summary>A key's second value in a row's object is dropped.</summary>
        internal void Drop() => Dropped = true;

        /// <summary>A chunk begins: nothing dropped.</summary>
        internal void Clear() => Dropped = false;

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

        /// <summary>
        /// Takes what <paramref name="other"/>, this node of another shredding of the plan, took of its rows, each
        /// <paramref name="offset"/> rows on, after what this one took: the values it kept whole here, a node's own in its
        /// override. <paramref name="runs"/> are the runs of the other's rows that took again what the row before them took,
        /// when it kept them (<see cref="Counting.Runs"/>).
        /// </summary>
        internal virtual void Append(Group other, int offset, ReadOnlySpan<int> runs)
        {
            Values.Append(other.Values, offset);
            if (other.Last >= 0)
            {
                Last = offset + other.Last;
            }
        }

        /// <summary>Has the node, and every node below it, keep what a range of its rows is found from (<see cref="Shredding.Index"/>).</summary>
        internal virtual void Range() => Values.Range();

        /// <summary>
        /// Makes what the node took of a chunk's <paramref name="rows"/> rows ready for ranges of them to be taken
        /// (<see cref="AppendRange"/>), carved from <paramref name="arena"/>: the values it kept whole here, a node's own in
        /// its override. False for a node no range is taken of.
        /// </summary>
        internal virtual bool Index(CanonicalArena arena, int rows) => Values.Index(arena, rows);

        /// <summary>
        /// Takes what <paramref name="other"/>, this node of another shredding of the plan made ready for it
        /// (<see cref="Index"/>), took of its <paramref name="count"/> rows from <paramref name="first"/> on, each
        /// <paramref name="shift"/> rows on, after what this one took: the values it kept whole here, a node's own in its
        /// override.
        /// </summary>
        internal virtual void AppendRange(Group other, int first, int count, int shift)
        {
            if (Values.AppendRange(other.Values, first, count, shift) > 0)
            {
                Last = Math.Max(Last, Values.Rows.Last);
            }
        }

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

        /// <summary>
        /// Takes the values of <paramref name="values"/>, the column of a key's values whole in another shredding of the same
        /// rows, the one column of the key's group <paramref name="pair"/> there, at the rows <paramref name="held"/> selects
        /// that hold one, as the chunk's rows from <paramref name="offset"/> on: what the node takes of each as the key's
        /// value read in its row's object, a value at a time and none noted for a row to take again. A node whose values
        /// name no key of their own, which reads none here.
        /// </summary>
        /// <exception cref="VortexFormatException">A value does not decode.</exception>
        internal void TakeValues(CanonicalArena arena, int pair, int values, ReadOnlySpan<ulong> held, int rows, int offset)
        {
            ReadOnlySpan<ulong> there = ArenaWords.Validity(arena, pair);
            ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, values);
            ViewValues read = new ViewValues(arena.GetNode(values));
            int words = (rows + 63) >> 6;
            for (int w = 0; w < words; w++)
            {
                ulong bits = held[w] & (there.IsEmpty ? ulong.MaxValue : there[w]) & (valid.IsEmpty ? ulong.MaxValue : valid[w]);
                for (; bits != 0; bits &= bits - 1)
                {
                    int at = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                    Last = offset + at;
                    Take(default, -1, read.At(at), offset + at, 0);
                }
            }
        }

        /// <summary>The dtype of the node's typed node.</summary>
        internal abstract DType TypedType(DTypeArena types);

        /// <summary>The dtype of the node in its object's struct: a variant when it kept values of its own, its typed node's otherwise.</summary>
        internal DType FieldType(DTypeArena types) => Values.HoldsAny ? types.Variant(Nullability.Nullable) : TypedType(types);

        /// <summary>The validity of the rows that hold the node, over <paramref name="rows"/> rows.</summary>
        internal abstract Validity HeldValidity(CanonicalArena arena, DTypeArena types, int rows);

        /// <summary>
        /// Counts at <paramref name="count"/>, the node's path in a census, what the node took of a block's rows, as the
        /// census counts the rows' values read one after the other: each value at its row, in its class, the rows of one
        /// class in their order; and below it what each of its nodes took. <paramref name="depth"/> is the node's, 1 for
        /// the rows' own value.
        /// </summary>
        internal abstract void Count(in Counting counting, PathCensus.PathCount count, int depth);

        /// <summary>The most keys the values the node and every node below it kept whole name (<see cref="BinaryValues.Keys"/>).</summary>
        internal virtual long KeptKeys => Values.Keys;
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
            _reformed = false;
        }

        internal override void Range()
        {
            base.Range();
            _typed.Range();
        }

        protected override void Take(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset)
        {
            // A value of the column's class whose bytes are all there is typed from its header as it lies: read whole into a
            // scalar of every type's fields first, the values of a consolidation of log records' attributes took a fifth of
            // their take (2026-11-19).
            if (!value.IsEmpty && _typed.Plain(row, value, Bounds))
            {
                _typedLast = true;
                return;
            }

            VariantScalar scalar = VariantScalar.Read(value);
            if (_typed.Takes(scalar.Kind))
            {
                // A text typed here and not from its header is one in the long form: put back in the short one when it is
                // short enough for it, other bytes than those read.
                _reformed |= scalar.Kind == VariantKind.String && scalar.Bytes.Length <= ParquetVariant.MaxShortString;
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

        // Whether a value typed was read in a form its class does not put it back in (Put): a short text in the long form.
        private bool _reformed;

        /// <summary>Whether every value the node typed is put back in the bytes it was read in (<see cref="PutTyped"/>).</summary>
        internal bool Exact => !_reformed;

        /// <summary>Sets <paramref name="at"/> on what the node took of the rows from <paramref name="first"/> to <paramref name="end"/>.</summary>
        internal void Seek(int first, int end, bool indexed, ref FieldAt at)
        {
            _typed.Seek(first, end, indexed, ref at);
            Values.Seek(first, end, indexed, ref at.KeptRows, ref at.KeptLengths, ref at.KeptBytes, out at.Kept);
        }

        /// <summary>
        /// For each row of the range <paramref name="at"/> is set on that holds a value of the node, counts it at the row
        /// after it in <paramref name="starts"/>, or, with <paramref name="defined"/>, lays there the node's
        /// <paramref name="key"/> doubled, and one more for a value kept whole, at the row's place in
        /// <paramref name="starts"/>, which moves past it.
        /// </summary>
        internal void List(in FieldAt at, int first, Span<int> starts, Span<int> defined, int key)
        {
            Cursor rows = at.TypedRows;
            for (int left = at.Typed; left > 0; left--)
            {
                int row = _typed.Rows!.Next(ref rows) - first;
                if (defined.IsEmpty)
                {
                    starts[row + 1]++;
                }
                else
                {
                    defined[starts[row]++] = key << 1;
                }
            }

            rows = at.KeptRows;
            for (int left = at.Kept; left > 0; left--)
            {
                int row = Values.Rows.Next(ref rows) - first;
                if (defined.IsEmpty)
                {
                    starts[row + 1]++;
                }
                else
                {
                    defined[starts[row]++] = (key << 1) | 1;
                }
            }
        }

        /// <summary>Writes the typed value at <paramref name="at"/>, which moves past it, as a variant value; the bytes written.</summary>
        internal int PutTyped(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing) => _typed.Put(ref at, into, ref crossing);

        /// <summary>Writes the value kept whole at <paramref name="at"/>, which moves past it, as it was read; the bytes written.</summary>
        internal int PutKept(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing)
        {
            ReadOnlySpan<byte> value = Values.Next(ref at.KeptLengths, ref at.KeptBytes, ref crossing);
            value.CopyTo(into);
            return value.Length;
        }

        /// <summary>Whether <paramref name="column"/>, a typed column of another shredding, holds values of this node's class in the form its typed column takes them.</summary>
        internal bool Holds(CanonicalNode column) => _typed.Holds(column);

        /// <summary>
        /// Takes the values of <paramref name="column"/>, a typed column of this node's class in another shredding of the same
        /// rows, at the rows <paramref name="held"/> selects that hold one, as the chunk's rows from <paramref name="offset"/>
        /// on: what the node would type of those rows' values read one by one.
        /// </summary>
        internal void TakeColumn(CanonicalArena arena, int column, ReadOnlySpan<ulong> held, int rows, int offset)
        {
            _typed.TakeColumn(arena, column, held, rows, offset);
            _typedLast = true;
        }

        /// <summary>
        /// The values typed and the values kept whole, each at its row, in the rows' order: a float kept whole is read as a
        /// double is, and its rows lie among the doubles'.
        /// </summary>
        internal override void Count(in Counting counting, PathCensus.PathCount count, int depth)
        {
            Appended<int> typedRows = _typed.Rows!;
            int typed = _typed.Count;
            int kept = Values.Count;
            Cursor typedAt = default;
            Cursor keptAt = default;
            Cursor keptLengths = default;
            Cursor keptBytes = default;
            byte[]? crossing = null;
            TypedCursor values = default;
            RunsAt typedRuns = new RunsAt(counting.Runs);
            RunsAt keptRuns = new RunsAt(counting.Runs);
            int t = typed > 0 ? typedRows.Next(ref typedAt) : int.MaxValue;
            int k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
            while (typed > 0 || kept > 0)
            {
                // A run of rows that took the value before them again is counted at once, and what they hold passed over.
                if (t < k)
                {
                    int times = typedRuns.Times(t);
                    _typed.CountNext(in counting, count, counting.First + t, times, ref values);
                    typedRows.Skip(ref typedAt, times - 1);
                    typed -= times;
                    t = typed > 0 ? typedRows.Next(ref typedAt) : int.MaxValue;
                }
                else
                {
                    int times = keptRuns.Times(k);
                    ReadOnlySpan<byte> value = Values.Next(ref keptLengths, ref keptBytes, ref crossing);
                    counting.Kept(count, k, value, depth, times);
                    Values.Skip(ref keptLengths, ref keptBytes, times - 1, value.Length);
                    Values.Rows.Skip(ref keptAt, times - 1);
                    kept -= times;
                    k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
                }
            }
        }

        /// <summary>
        /// The column of the key over <paramref name="rows"/> rows, read as <paramref name="read"/>, the read of the typed
        /// column's class: each value typed at its row, and of the values kept whole those the read takes, a float beside
        /// doubles.
        /// </summary>
        internal int Column(CanonicalArena into, VariantRead read, int rows, ref VortexBuffer absent, ReadOnlySpan<int> runs)
        {
            if (_typed.Count == 0 && Values.Count == 0)
            {
                return VariantFieldExpr.Output.Absent(into, read, rows, ReadOnlySpan<VortexBuffer>.Empty, ref absent);
            }

            VariantFieldExpr.Output output = new VariantFieldExpr.Output(into, read, rows);
            VortexBuffer data = _typed.Scatter(ref output, into, runs);
            int kept = Values.Count;
            Cursor keptAt = default;
            Cursor keptLengths = default;
            Cursor keptBytes = default;
            byte[]? crossing = null;
            for (int i = 0; i < kept; i++)
            {
                int row = Values.Rows.Next(ref keptAt);
                output.Write(row, Values.Next(ref keptLengths, ref keptBytes, ref crossing), default, default);
            }

            return data.Length > 0 ? output.Finish(into, [data]) : output.Finish(into, ReadOnlySpan<VortexBuffer>.Empty);
        }

        /// <summary>The other's typed values after these, counted in the chunk's blocks as a take counts them, and the values it kept whole.</summary>
        internal override void Append(Group other, int offset, ReadOnlySpan<int> runs)
        {
            PrimitiveGroup from = (PrimitiveGroup)other;
            base.Append(other, offset, runs);
            if (Bounds is { } bounds)
            {
                from._typed.CountInto(bounds, offset, runs);
            }

            _typed.Append(from._typed, offset);
            _typedLast = from._typedLast;
        }

        internal override bool Index(CanonicalArena arena, int rows) => base.Index(arena, rows) && _typed.Index(arena, rows);

        /// <summary>
        /// The other's typed values of the rows after these, and the values it kept whole of them. No row takes again what a
        /// range's last took, which follows a row read, and a block that takes ranges counts no key block by block.
        /// </summary>
        internal override void AppendRange(Group other, int first, int count, int shift)
        {
            System.Diagnostics.Debug.Assert(Bounds is null, "A block that takes ranges counts no key.");
            PrimitiveGroup from = (PrimitiveGroup)other;
            base.AppendRange(other, first, count, shift);
            if (_typed.AppendRange(from._typed, first, count, shift) > 0)
            {
                Mark(Math.Max(Last, _typed.Rows!.Last));
            }
        }

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

        /// <summary>The node of the plan's key <paramref name="index"/>.</summary>
        internal Group Field(int index) => _fields[index];

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

        /// <summary>
        /// The objects, each at its row, the keys the shredding left of one counted as its fields, and the values that are
        /// no object, kept whole; then each key the rows hold, by its own node.
        /// </summary>
        internal override void Count(in Counting counting, PathCensus.PathCount count, int depth)
        {
            int objects = _objects.Count;
            int kept = Values.Count;
            Cursor objectsAt = default;
            Cursor keptAt = default;
            Cursor keptLengths = default;
            Cursor keptBytes = default;
            byte[]? crossing = null;
            RunsAt objectRuns = new RunsAt(counting.Runs);
            RunsAt keptRuns = new RunsAt(counting.Runs);
            int o = objects > 0 ? _objects.Next(ref objectsAt) : int.MaxValue;
            int k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
            while (objects > 0 || kept > 0)
            {
                if (o < k)
                {
                    int times = objectRuns.Times(o);
                    count.TakenObject(counting.Census, counting.First + o, times);
                    _objects.Skip(ref objectsAt, times - 1);
                    objects -= times;
                    o = objects > 0 ? _objects.Next(ref objectsAt) : int.MaxValue;
                    continue;
                }

                ReadOnlySpan<byte> value = Values.Next(ref keptLengths, ref keptBytes, ref crossing);
                if (k < o)
                {
                    int times = keptRuns.Times(k);
                    counting.Kept(count, k, value, depth, times);
                    Values.Skip(ref keptLengths, ref keptBytes, times - 1, value.Length);
                    Values.Rows.Skip(ref keptAt, times - 1);
                    kept -= times;
                }
                else
                {
                    // The row's object, and the keys kept of it in an object of their own: a row read, which no row takes again.
                    count.TakenObject(counting.Census, counting.First + o, 1);
                    VariantDictionary dictionary = counting.Census.Enter(counting.Metadata.At(k));
                    count.TakenFields(counting.Census, counting.First + k, in dictionary, value, depth);
                    o = --objects > 0 ? _objects.Next(ref objectsAt) : int.MaxValue;
                    kept--;
                }

                k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
            }

            for (int i = 0; i < _fields.Length; i++)
            {
                if (_fields[i].HeldAny && count.Key(counting.Census, _plan.Keys[i].Utf8) is { } key)
                {
                    _fields[i].Count(in counting, key, depth + 1);
                }
            }
        }

        internal override long KeptKeys
        {
            get
            {
                long keys = Values.Keys;
                foreach (Group node in _fields)
                {
                    keys += node.KeptKeys;
                }

                return keys;
            }
        }

        /// <summary>The other's objects' rows after these, and each key what the other's took.</summary>
        internal override void Append(Group other, int offset, ReadOnlySpan<int> runs)
        {
            ObjectGroup from = (ObjectGroup)other;
            base.Append(other, offset, runs);
            _objects.AddShifted(from._objects, offset);
            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i].Append(from._fields[i], offset, runs);
            }
        }

        // Where the objects' rows hold each row of the chunk, once made ready for ranges of them (Index).
        private Ranks _ranks;

        internal override void Range()
        {
            base.Range();
            foreach (Group field in _fields)
            {
                field.Range();
            }
        }

        internal override bool Index(CanonicalArena arena, int rows)
        {
            if (!base.Index(arena, rows))
            {
                return false;
            }

            _ranks.Build(arena, _objects, rows);
            foreach (Group field in _fields)
            {
                if (!field.Index(arena, rows))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The other's objects' rows of the range after these, and each key what the other's took of it: none where the range holds no object, whose keys hold nothing then.</summary>
        internal override void AppendRange(Group other, int first, int count, int shift)
        {
            ObjectGroup from = (ObjectGroup)other;
            base.AppendRange(other, first, count, shift);
            int start = from._ranks.Before(first);
            int objects = from._ranks.Before(first + count) - start;
            if (objects == 0)
            {
                return;
            }

            AddRowsOf(_objects, from._objects, start, objects, first, count, shift);
            Mark(Math.Max(Last, _objects.Last));
            for (int i = 0; i < _fields.Length; i++)
            {
                if (from._fields[i].HeldAny)
                {
                    _fields[i].AppendRange(from._fields[i], first, count, shift);
                }
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

            _objects.Add(row);
            Fields(in dictionary, epoch, value, row, offset);
        }

        /// <summary>
        /// Takes the keys of <paramref name="kept"/>, the object of the keys another shredding left of the object at row
        /// <paramref name="row"/>, which <see cref="TakeProjected"/> took as an object: each key the plan names by its node, the
        /// others kept in an object of their own, as the keys of the row's whole object are taken.
        /// </summary>
        /// <exception cref="VortexFormatException"><paramref name="kept"/> is no object.</exception>
        internal void TakeKept(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> kept, int row)
        {
            if ((kept[0] & 0x03) != ParquetVariant.BasicObject)
            {
                throw new VortexFormatException("A shredded object's residual is not an object.");
            }

            Fields(in dictionary, epoch, kept, row, 0);
        }

        /// <summary>
        /// Whether what the struct <paramref name="typed"/> of <paramref name="arena"/> holds, another shredding's typed
        /// object, a group a key, is taken as it lies (<see cref="TakeProjected"/>): every key of it one the plan types as a
        /// column of the class of the key's column there, none that kept values of its own beside it, or an object or a list
        /// of its own.
        /// </summary>
        internal bool Projects(CanonicalArena arena, int typed)
        {
            CanonicalNode node = arena.GetNode(typed);
            if (node.Kind != CanonicalKind.Struct)
            {
                return false;
            }

            for (int f = 0; f < node.FieldCount; f++)
            {
                int key = _plan.IndexOf(node.DType.GetFieldNameUtf8(f));
                CanonicalNode pair = arena.GetNode(node.GetFieldIndex(f));
                if (key < 0 || pair.Kind != CanonicalKind.Struct || pair.FieldCount != 1)
                {
                    return false;
                }

                CanonicalNode held = arena.GetNode(ArrayBlobWriter.Materialize(arena, pair.GetFieldIndex(0)));
                bool taken = Valued(pair)
                    ? _fields[key] is not ObjectGroup && held.Kind == CanonicalKind.VarBinView
                    : pair.DType.GetFieldNameUtf8(0).SequenceEqual("typed_value"u8) && _fields[key] is PrimitiveGroup column && column.Holds(held);
                if (!taken)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether <paramref name="pair"/>, a key's group of one column in another shredding, holds the key's values whole: a key that shredding types in no one class.</summary>
        private static bool Valued(CanonicalNode pair) => pair.DType.GetFieldNameUtf8(0).SequenceEqual("value"u8);

        /// <summary>
        /// Takes what the struct <paramref name="typed"/> holds of the rows <paramref name="held"/> selects, a bit a row, the
        /// rows whose value is an object, as the chunk's rows from <paramref name="offset"/> on: each an object, and each key's
        /// column the values of the rows that hold one, with no value read from a row (<see cref="Projects"/>).
        /// </summary>
        internal void TakeProjected(CanonicalArena arena, int typed, ReadOnlySpan<ulong> held, int rows, int offset)
        {
            AddRows(_objects, held, rows, offset);
            CanonicalNode node = arena.GetNode(typed);
            for (int f = 0; f < node.FieldCount; f++)
            {
                int key = _plan.IndexOf(node.DType.GetFieldNameUtf8(f));
                int pair = node.GetFieldIndex(f);
                int column = ArrayBlobWriter.Materialize(arena, arena.GetNode(pair).GetFieldIndex(0));
                if (Valued(arena.GetNode(pair)))
                {
                    _fields[key].TakeValues(arena, pair, column, held, rows, offset);
                }
                else
                {
                    ((PrimitiveGroup)_fields[key]).TakeColumn(arena, column, held, rows, offset);
                }
            }
        }

        /// <summary>
        /// Whether the rows the node took are put back together in the bytes they were read in (<see cref="Rebuild"/>):
        /// every key a column of values of no keys of their own, each typed value in the form its class is put back in.
        /// </summary>
        internal bool Exact
        {
            get
            {
                foreach (Group node in _fields)
                {
                    if (node is PrimitiveGroup typed ? !typed.Exact : node.HeldAny)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Writes the objects of the rows from <paramref name="first"/> on, <paramref name="count"/> of them, as putting
        /// them back together from the shredding they were taken from writes them
        /// (<c>ShreddedVariant.WriteSlice</c>): each key the node's keys hold of a row and each key kept apart of it, in
        /// the order of their names, under the ids the row's metadata gives them. A row's object goes where its view of
        /// <paramref name="views"/>, the rows' sixteen bytes each, says it lies, <paramref name="origin"/> bytes before
        /// <paramref name="into"/>, in the bytes it says it takes. A row that is no object is left alone, and so is one
        /// whose view holds its value, which is read past.
        /// </summary>
        /// <param name="first">The first row, among the rows taken.</param>
        /// <param name="count">The rows.</param>
        /// <param name="indexed">Whether the node is made ready for ranges (<see cref="Index"/>): a range is then found at once, and searched for otherwise.</param>
        /// <param name="views">The rows' views, sized as the rows put back together are (<c>ShreddedVariant.HollowSlice</c>).</param>
        /// <param name="metadataViews">The views of the rows' metadata.</param>
        /// <param name="metadataBuffers">The buffers those views name.</param>
        /// <param name="into">Where the objects are written.</param>
        /// <param name="origin">The offset, in the buffer the views name, <paramref name="into"/> starts at.</param>
        /// <exception cref="InvalidOperationException">A row put back together does not take the bytes it was sized at: the node is not <see cref="Exact"/>.</exception>
        /// <exception cref="VortexFormatException">A row's metadata does not name a key the row holds.</exception>
        internal void Rebuild(
            int first, int count, bool indexed, ReadOnlySpan<byte> views, ReadOnlySpan<byte> metadataViews,
            ReadOnlySpan<VortexBuffer> metadataBuffers, Span<byte> into, int origin)
        {
            int end = first + count;
            int keys = _fields.Length;

            // The id of each key of the plan in the metadata of the row being put back, looked up when a row first holds
            // the key under that metadata, and the number of the metadata it was looked up in. Its own for the call: the
            // rows a shredding took are put back by several readers at once, which only read the shredding.
            Block<int> looked = Blocks.RentCleared<int>(2 * Math.Max(keys, 1));
            int stamp = 0;
            Block<FieldAt> cursors = Blocks.Rent<FieldAt>(Math.Max(keys, 1));
            Block<int> listed = Blocks.RentCleared<int>(count + 1);
            Block<int> laid = default;
            Block<int> merged = Blocks.Rent<int>(3 * 64);
            byte[]? crossing = null;
            byte[]? keptCrossing = null;
            Span<byte> inline = stackalloc byte[CanonicalSupport.MaxInlineViewLength];
            try
            {
                // The keys each row holds, in the order of their names: counted a key at a time, then laid row after row.
                Span<FieldAt> at = cursors.AsSpan(0, keys);
                Span<int> starts = listed.AsSpan(0, count + 1);
                int held = 0;
                for (int k = 0; k < keys; k++)
                {
                    at[k] = default;
                    if (_fields[k] is PrimitiveGroup { HeldAny: true } field)
                    {
                        field.Seek(first, end, indexed, ref at[k]);
                        field.List(in at[k], first, starts, default, k);
                        held += at[k].Typed + at[k].Kept;
                    }
                }

                for (int i = 1; i <= count; i++)
                {
                    starts[i] += starts[i - 1];
                }

                laid = Blocks.Rent<int>(Math.Max(held, 1));
                Span<int> defined = laid.AsSpan(0, held);
                for (int k = 0; held > 0 && k < keys; k++)
                {
                    if (_fields[k] is PrimitiveGroup { HeldAny: true } field)
                    {
                        field.List(in at[k], first, starts, defined, k);
                    }
                }

                for (int i = count; i > 0; i--)
                {
                    starts[i] = starts[i - 1];
                }

                starts[0] = 0;

                // The rows that are objects, and what the node kept of each row: the keys kept apart of an object, or the
                // value of a row that is none.
                Cursor objectsAt = default;
                int objectsStart = Below(_objects, in _ranks, indexed, first);
                int objectsLeft = Below(_objects, in _ranks, indexed, end) - objectsStart;
                _objects.Skip(ref objectsAt, objectsStart);
                Cursor keptRows = default;
                Cursor keptLengths = default;
                Cursor keptBytes = default;
                Values.Seek(first, end, indexed, ref keptRows, ref keptLengths, ref keptBytes, out int keptLeft);
                int nextObject = objectsLeft > 0 ? _objects.Next(ref objectsAt) : int.MaxValue;
                int nextKept = keptLeft > 0 ? Values.Rows.Next(ref keptRows) : int.MaxValue;

                VariantDictionary dictionary = default;
                ulong lastLow = 0;
                ulong lastHigh = 0;
                bool seen = false;
                for (int i = 0; i < count; i++)
                {
                    int row = first + i;
                    ReadOnlySpan<byte> kept = default;
                    if (nextKept == row)
                    {
                        kept = Values.Next(ref keptLengths, ref keptBytes, ref keptCrossing);
                        nextKept = --keptLeft > 0 ? Values.Rows.Next(ref keptRows) : int.MaxValue;
                    }

                    if (nextObject != row)
                    {
                        continue;
                    }

                    nextObject = --objectsLeft > 0 ? _objects.Next(ref objectsAt) : int.MaxValue;

                    // The row's metadata, read again only when its view is not the last row's.
                    ReadOnlySpan<byte> metadataView = metadataViews.Slice(i * ViewSize, ViewSize);
                    ulong low = MemoryMarshal.Read<ulong>(metadataView);
                    ulong high = MemoryMarshal.Read<ulong>(metadataView[sizeof(ulong)..]);
                    if (!seen || low != lastLow || high != lastHigh)
                    {
                        dictionary = VariantDictionary.Read(BlockStatsPass.Value(metadataBuffers, metadataViews, i));
                        (lastLow, lastHigh, seen) = (low, high, true);
                        stamp++;
                    }

                    ReadOnlySpan<byte> view = views.Slice(i * ViewSize, ViewSize);
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view);
                    ReadOnlySpan<int> fields = defined[starts[i]..starts[i + 1]];
                    VariantNested others = kept.IsEmpty ? default : VariantNested.Read(kept);
                    int total = fields.Length + others.Count;
                    if (merged.Length < 3 * (total + 1))
                    {
                        Blocks.Return(merged);
                        merged = default;
                        merged = Blocks.Rent<int>(3 * (total + 1));
                    }

                    try
                    {
                        // A value its view holds names no place: written apart, for what its keys took to be read past.
                        Span<byte> target = length <= CanonicalSupport.MaxInlineViewLength
                            ? inline[..length]
                            : into.Slice(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view[12..]) - origin, length);
                        Write(
                            target, fields, others, in dictionary, at, merged.AsSpan(0, 3 * (total + 1)), looked.AsSpan(0, keys),
                            looked.AsSpan(Math.Max(keys, 1), keys), stamp, ref crossing);
                    }
                    catch (ArgumentException)
                    {
                        // A value past the bytes the row was sized at.
                        throw NotAsSized(row, length);
                    }
                }
            }
            finally
            {
                Blocks.Return(merged);
                Blocks.Return(laid);
                Blocks.Return(listed);
                Blocks.Return(cursors);
                Blocks.Return(looked);
            }
        }

        /// <summary>
        /// Writes into <paramref name="target"/>, the bytes a row's object takes, the object of the row's
        /// <paramref name="fields"/>, each the plan's key doubled and one more for a value kept whole
        /// (<see cref="PrimitiveGroup.List"/>), and of <paramref name="others"/>, the keys kept apart of the row, merged
        /// in the order of their names. <paramref name="looked"/> holds the id of each key of the plan looked up in the
        /// metadata numbered <paramref name="stamp"/>, where <paramref name="stamps"/> says so.
        /// </summary>
        private void Write(
            Span<byte> target, ReadOnlySpan<int> fields, VariantNested others, scoped in VariantDictionary dictionary,
            Span<FieldAt> at, Span<int> room, Span<int> looked, Span<int> stamps, int stamp, scoped ref byte[]? crossing)
        {
            int total = fields.Length + others.Count;
            Span<int> order = room[..total];
            Span<int> ids = room.Slice(total, total);
            Span<int> offsets = room.Slice(2 * total, total + 1);

            // Each field's source, a key of the plan's or the complement of a key kept apart, and its id.
            uint largest = 0;
            for (int n = 0, f = 0, o = 0; n < total; n++)
            {
                bool named = o == others.Count
                    || (f < fields.Length && _plan.Keys[fields[f] >> 1].Utf8.AsSpan().SequenceCompareTo(dictionary[others.IdAt(o)]) < 0);
                if (named)
                {
                    int key = fields[f] >> 1;
                    if (stamps[key] != stamp)
                    {
                        looked[key] = dictionary.Find(_plan.Keys[key].Utf8);
                        stamps[key] = stamp;
                    }

                    if (looked[key] < 0)
                    {
                        throw new VortexFormatException($"The variant has a field '{_plan.Keys[key].Name}' its row's metadata does not name.");
                    }

                    ids[n] = looked[key];
                    order[n] = fields[f++];
                }
                else
                {
                    ids[n] = others.IdAt(o);
                    order[n] = ~o++;
                }

                largest = Math.Max(largest, (uint)ids[n]);
            }

            // The header's widths: the offsets' is the one under which the values take what the header leaves of the row.
            int length = target.Length;
            int countWidth = total > byte.MaxValue ? 4 : 1;
            int idWidth = ParquetVariant.WidthOf(largest);
            int named0 = 1 + countWidth + (total * idWidth);
            int offsetWidth = 1;
            for (; offsetWidth <= 4; offsetWidth++)
            {
                int size = length - named0 - ((total + 1) * offsetWidth);
                if (size >= 0 && ParquetVariant.WidthOf((uint)size) == offsetWidth)
                {
                    break;
                }
            }

            if (offsetWidth > 4)
            {
                throw new ArgumentException("No width of offsets fits the row's bytes.", nameof(target));
            }

            int header = named0 + ((total + 1) * offsetWidth);
            int written = header;
            for (int n = 0; n < total; n++)
            {
                offsets[n] = written - header;
                int source = order[n];
                if (source < 0)
                {
                    ReadOnlySpan<byte> value = others.ValueAt(~source);
                    value.CopyTo(target[written..]);
                    written += value.Length;
                    continue;
                }

                PrimitiveGroup field = (PrimitiveGroup)_fields[source >> 1];
                written += (source & 1) == 0
                    ? field.PutTyped(ref at[source >> 1], target[written..], ref crossing)
                    : field.PutKept(ref at[source >> 1], target[written..], ref crossing);
            }

            if (written != length)
            {
                throw new ArgumentException("The row's values do not fill the row's bytes.", nameof(target));
            }

            offsets[total] = written - header;
            target[0] = (byte)(((((countWidth == 4 ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicObject);
            int put = 1;
            ParquetVariant.Put(target, ref put, (uint)total, countWidth);
            for (int n = 0; n < total; n++)
            {
                ParquetVariant.Put(target, ref put, (uint)ids[n], idWidth);
            }

            for (int n = 0; n <= total; n++)
            {
                ParquetVariant.Put(target, ref put, (uint)offsets[n], offsetWidth);
            }
        }

        private static InvalidOperationException NotAsSized(int row, int length) =>
            new($"Row {row} put back together from what a shredding took of it does not take the {length} bytes it was sized at.");

        /// <summary>The keys of <paramref name="value"/>, an object at row <paramref name="row"/>: each key the plan names taken by its node, the others kept in an object of their own.</summary>
        private void Fields(in VariantDictionary dictionary, int epoch, ReadOnlySpan<byte> value, int row, int offset)
        {
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
                    else
                    {
                        Record?.Drop();
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

        /// <summary>The lists typed, each at its row by its elements' class, and the values kept whole, a list whose elements do not all fit among them, in the rows' order.</summary>
        internal override void Count(in Counting counting, PathCensus.PathCount count, int depth)
        {
            int lists = _lists.Count;
            int kept = Values.Count;
            Cursor listsAt = default;
            Cursor sizesAt = default;
            Cursor keptAt = default;
            Cursor keptLengths = default;
            Cursor keptBytes = default;
            byte[]? crossing = null;
            RunsAt listRuns = new RunsAt(counting.Runs);
            RunsAt keptRuns = new RunsAt(counting.Runs);
            int l = lists > 0 ? _lists.Next(ref listsAt) : int.MaxValue;
            int k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
            while (lists > 0 || kept > 0)
            {
                if (l < k)
                {
                    int times = listRuns.Times(l);
                    count.TakenList(counting.Census, counting.First + l, _sizes.Next(ref sizesAt), @class, times);
                    _sizes.Skip(ref sizesAt, times - 1);
                    _lists.Skip(ref listsAt, times - 1);
                    lists -= times;
                    l = lists > 0 ? _lists.Next(ref listsAt) : int.MaxValue;
                }
                else
                {
                    int times = keptRuns.Times(k);
                    ReadOnlySpan<byte> value = Values.Next(ref keptLengths, ref keptBytes, ref crossing);
                    counting.Kept(count, k, value, depth, times);
                    Values.Skip(ref keptLengths, ref keptBytes, times - 1, value.Length);
                    Values.Rows.Skip(ref keptAt, times - 1);
                    kept -= times;
                    k = kept > 0 ? Values.Rows.Next(ref keptAt) : int.MaxValue;
                }
            }
        }

        /// <summary>The other's lists after these: their rows, their sizes and their elements, which are rows of their own.</summary>
        internal override void Append(Group other, int offset, ReadOnlySpan<int> runs)
        {
            ListGroup from = (ListGroup)other;
            base.Append(other, offset, runs);
            _elements.Append(from._elements, 0);
            _lists.AddShifted(from._lists, offset);
            _sizes.AddAll(from._sizes);
        }

        // Where the lists' rows hold each row of the chunk, and where each list's elements start among the elements, once
        // made ready for ranges of them (Index).
        private Ranks _ranks;
        private Starts _starts;

        internal override bool Index(CanonicalArena arena, int rows)
        {
            _ranks.Build(arena, _lists, rows);
            return base.Index(arena, rows) && _starts.Build(arena, _sizes) && _elements.Index(arena, rows);
        }

        /// <summary>The other's lists of the range after these: their rows, their sizes and their elements.</summary>
        internal override void AppendRange(Group other, int first, int count, int shift)
        {
            ListGroup from = (ListGroup)other;
            base.AppendRange(other, first, count, shift);
            int start = from._ranks.Before(first);
            int lists = from._ranks.Before(first + count) - start;
            if (lists == 0)
            {
                return;
            }

            AddRowsOf(_lists, from._lists, start, lists, first, count, shift);
            Mark(Math.Max(Last, _lists.Last));
            _sizes.AddFrom(from._sizes, start, lists);
            int element = from._starts.Before(start);
            _elements.AppendSome(from._elements, element, from._starts.Before(start + lists) - element);
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

        /// <summary>
        /// Takes <paramref name="value"/> at row <paramref name="row"/> from its header as it lies, when it is a value of the
        /// column's class the encoding writes in one piece and its bytes are all there, counted in <paramref name="bounds"/>
        /// as <see cref="Indexes.ChunkBlockBounds.KeyBlocks.Note"/> counts it read; false, and nothing taken, for any other
        /// value, which is read whole.
        /// </summary>
        internal abstract bool Plain(int row, ReadOnlySpan<byte> value, Indexes.ChunkBlockBounds.KeyBlocks? bounds);

        /// <summary>A value is taken at row <paramref name="row"/>: its row, and one value more.</summary>
        protected void Took(int row)
        {
            Rows?.Add(row);
            Count++;
        }

        /// <summary>Whether <paramref name="column"/> is a column of this class's values as a typed column of a shredding holds them.</summary>
        internal abstract bool Holds(CanonicalNode column);

        /// <summary>
        /// Takes the values of <paramref name="column"/>, a column of this class (<see cref="Holds"/>) over
        /// <paramref name="rows"/> rows, at the rows <paramref name="held"/> selects that hold one, a bit a row, each at its
        /// row <paramref name="offset"/> rows on: the rows that follow each other at once.
        /// </summary>
        internal void TakeColumn(CanonicalArena arena, int column, ReadOnlySpan<ulong> held, int rows, int offset)
        {
            ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, column);
            ColumnValues values = new ColumnValues(arena.GetNode(column));
            int words = (rows + 63) >> 6;
            for (int w = 0; w < words; w++)
            {
                ulong bits = held[w] & (valid.IsEmpty ? ulong.MaxValue : valid[w]);
                int first = w << 6;
                while (bits != 0)
                {
                    int start = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    int length = bits == ulong.MaxValue ? 64 : System.Numerics.BitOperations.TrailingZeroCount(~(bits >> start));
                    Rows?.AddSequence(offset + first + start, length);
                    Count += length;
                    Range(in values, first + start, length);
                    bits = length == 64 ? 0 : bits & ~(((1UL << length) - 1) << start);
                }
            }
        }

        /// <summary>Takes the <paramref name="count"/> values of <paramref name="column"/> from row <paramref name="first"/> on, one after the other.</summary>
        protected abstract void Range(in ColumnValues column, int first, int count);

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

        /// <summary>
        /// Takes the values <paramref name="other"/>, a column of the same class, took, after these, each at its row
        /// <paramref name="offset"/> rows on.
        /// </summary>
        internal void Append(TypedValues other, int offset)
        {
            Rows?.AddShifted(other.Rows!, offset);
            Count += other.Count;
            All(other);
        }

        /// <summary>Takes every value of <paramref name="other"/>, of the same class, after these.</summary>
        protected abstract void All(TypedValues other);

        // Where the rows hold each row of the chunk, once made ready for ranges of them (Index).
        private Ranks _ranks;

        /// <summary>
        /// Makes the values taken of a chunk's <paramref name="rows"/> rows ready for ranges of them to be taken
        /// (<see cref="AppendRange"/>), carved from <paramref name="arena"/>. False for values no range is taken of: texts some
        /// of which repeat the one before, their bytes kept once.
        /// </summary>
        internal bool Index(CanonicalArena arena, int rows)
        {
            if (Rows is { } held)
            {
                _ranks.Build(arena, held, rows);
            }

            return IndexValues(arena);
        }

        /// <summary>Makes the values themselves ready for a range of them to be taken, by their place among them (<see cref="Some"/>).</summary>
        protected virtual bool IndexValues(CanonicalArena arena) => true;

        /// <summary>Has the values keep what putting a range of their rows back together reads beside them (<see cref="Put"/>), before any is taken.</summary>
        internal virtual void Range()
        {
        }

        /// <summary>
        /// Sets <paramref name="at"/> on the values taken at the rows from <paramref name="first"/> to <paramref name="end"/>,
        /// the first of them next: found by the rows' ranks when the values are made ready for ranges
        /// (<paramref name="indexed"/>), counted otherwise.
        /// </summary>
        internal void Seek(int first, int end, bool indexed, ref FieldAt at)
        {
            int start = Below(Rows!, in _ranks, indexed, first);
            at.Typed = Below(Rows!, in _ranks, indexed, end) - start;
            at.TypedRows = default;
            Rows!.Skip(ref at.TypedRows, start);
            SeekValues(start, indexed, ref at);
        }

        /// <summary>Sets <paramref name="at"/> on the value at place <paramref name="start"/> among the values.</summary>
        protected abstract void SeekValues(int start, bool indexed, ref FieldAt at);

        /// <summary>
        /// Writes the value at <paramref name="at"/>, which moves past it, into <paramref name="into"/> as a variant value in
        /// the form putting a typed column's rows back together writes it in (<c>ShreddedVariant.WritePrimitive</c>); the
        /// bytes written.
        /// </summary>
        internal abstract int Put(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing);

        /// <summary>
        /// Takes the values <paramref name="other"/>, a column of the same class made ready for it (<see cref="Index"/>), took
        /// at its <paramref name="count"/> rows from <paramref name="first"/> on, after these, each at its row
        /// <paramref name="shift"/> rows on; returns how many.
        /// </summary>
        internal int AppendRange(TypedValues other, int first, int count, int shift)
        {
            int start = other._ranks.Before(first);
            int values = other._ranks.Before(first + count) - start;
            if (values > 0)
            {
                AddRowsOf(Rows!, other.Rows!, start, values, first, count, shift);
                AppendSome(other, start, values);
            }

            return values;
        }

        /// <summary>Takes the <paramref name="count"/> values of <paramref name="other"/>, of the same class and made ready for it, from its value <paramref name="from"/> on, after these: a list's elements, which are rows of their own.</summary>
        internal void AppendSome(TypedValues other, int from, int count)
        {
            Count += count;
            Some(other, from, count);
        }

        /// <summary>Takes the <paramref name="count"/> values of <paramref name="other"/>, of the same class, from its value <paramref name="from"/> on, after these.</summary>
        protected abstract void Some(TypedValues other, int from, int count);

        /// <summary>
        /// Counts the values taken in <paramref name="bounds"/>, each at its row <paramref name="offset"/> rows on, as a take
        /// that counts a chunk's blocks counts them as it types them.
        /// </summary>
        /// <remarks>
        /// A run of rows of <paramref name="runs"/> holds the value before it again, once each (<see cref="Counting.Runs"/>):
        /// counted a block's rows at once. A value at a time, the rows of a consolidation of metric records' series, some
        /// thirty to a run, took 5 billion cycles more than their take had (2026-11-19).
        /// </remarks>
        internal abstract void CountInto(Indexes.ChunkBlockBounds.KeyBlocks bounds, int offset, ReadOnlySpan<int> runs);

        /// <summary>
        /// Writes each value taken at its row of <paramref name="output"/>, a column of the values' class. A column of texts
        /// views their bytes in the buffer returned, which the column is finished over: the block that holds them when one
        /// does, a copy in <paramref name="into"/> otherwise; none for any other class, or for texts all empty.
        /// </summary>
        /// <remarks>A run of rows of <paramref name="runs"/> takes the value before it at once (<see cref="Counting.Runs"/>).</remarks>
        internal abstract VortexBuffer Scatter(ref VariantFieldExpr.Output output, CanonicalArena into, ReadOnlySpan<int> runs);

        /// <summary>
        /// Counts at <paramref name="count"/> the value after the one counted last, the first from a cursor at its default,
        /// at each of the <paramref name="times"/> rows from <paramref name="row"/> of the census's on: the value once, and the
        /// same once more for each row after the first, which the cursor passes over.
        /// </summary>
        internal abstract void CountNext(in Counting counting, PathCensus.PathCount count, long row, int times, ref TypedCursor cursor);

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

        // The bytes each value was read in, one a value, from the first read in fewer than eight on (Noted): what putting
        // the rows back together writes it in again (Put). Kept by values ranges of which are taken (Range), and by those
        // that take such ranges; made with the first value noted, so that values no range is taken of hold none.
        private Appended<byte>? _widths;
        private bool _keepsWidths;

        private bool Noted => _widths is { Count: > 0 };

        internal override void Clear(CanonicalArena arena)
        {
            base.Clear(arena);
            _values.Clear(arena);
            _widths?.Clear(arena);
        }

        internal override void Range() => _keepsWidths = true;

        internal override DType Type(DTypeArena types) => types.Primitive(PType.I64, Nullability.Nullable);

        protected override void Value(in VariantScalar scalar)
        {
            _values.Add((long)scalar.Integer);
            if (Noted)
            {
                _widths!.Add(sizeof(long));
            }
        }

        /// <summary>The widths, made in the arena the values are carved from when none was noted since it was set.</summary>
        private Appended<byte> Widths()
        {
            if (_widths is null)
            {
                _widths = new Appended<byte>();
                _widths.Clear(_values.Arena);
            }

            return _widths;
        }

        /// <summary>
        /// Notes that the value about to be taken was read in <paramref name="width"/> bytes: nothing until one is read in
        /// fewer than eight, the values before it then noted at eight.
        /// </summary>
        private void Width(int width)
        {
            Appended<byte>? widths = _widths;
            if (widths is not { Count: > 0 })
            {
                if (width == sizeof(long))
                {
                    return;
                }

                widths = Widths();
                widths.AddRepeated(sizeof(long), Count);
            }

            widths.Add((byte)width);
        }

        internal override bool Plain(int row, ReadOnlySpan<byte> value, Indexes.ChunkBlockBounds.KeyBlocks? bounds)
        {
            byte header = value[0];
            if ((header & 0x03) != ParquetVariant.BasicPrimitive)
            {
                return false;
            }

            long integer;
            switch (header >> 2)
            {
                case 3 when value.Length >= 2:
                    integer = (sbyte)value[1];
                    break;
                case 4 when value.Length >= 3:
                    integer = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(value[1..]);
                    break;
                case 5 when value.Length >= 5:
                    integer = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value[1..]);
                    break;
                case 6 when value.Length >= 9:
                    integer = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(value[1..]);
                    break;
                default:
                    return false;
            }

            if (_keepsWidths)
            {
                // Types 3 to 6 are integers of one, two, four and eight bytes.
                Width(1 << ((header >> 2) - 3));
            }

            Took(row);
            _values.Add(integer);
            bounds?.Integer(row, integer);
            return true;
        }

        internal override bool Holds(CanonicalNode column) => column.Kind == CanonicalKind.Primitive && column.PType == PType.I64;

        protected override void Range(in ColumnValues column, int first, int count)
        {
            _values.AddRange(MemoryMarshal.Cast<byte, long>(column.Fixed).Slice(first, count));
            if (Noted)
            {
                _widths!.AddRepeated(sizeof(long), count);
            }
        }

        protected override void All(TypedValues other)
        {
            _values.AddAll(((IntegerValues)other)._values);
            if (Noted)
            {
                _widths!.AddRepeated(sizeof(long), other.Count);
            }
        }

        /// <summary>The other's values of the range, and the bytes each was read in when either notes them: <see cref="TypedValues.Count"/> counts the range already.</summary>
        protected override void Some(TypedValues other, int from, int count)
        {
            IntegerValues source = (IntegerValues)other;
            _values.AddFrom(source._values, from, count);
            if (source.Noted)
            {
                Appended<byte> widths = Widths();
                if (widths.Count == 0)
                {
                    widths.AddRepeated(sizeof(long), Count - count);
                }

                widths.AddFrom(source._widths!, from, count);
            }
            else if (Noted)
            {
                _widths!.AddRepeated(sizeof(long), count);
            }
        }

        protected override void SeekValues(int start, bool indexed, ref FieldAt at)
        {
            at.Values = default;
            _values.Skip(ref at.Values, start);
            at.Widths = default;
            if (Noted)
            {
                _widths!.Skip(ref at.Widths, start);
            }
        }

        internal override int Put(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing)
        {
            long value = _values.Next(ref at.Values);
            switch (Noted ? _widths!.Next(ref at.Widths) : sizeof(long))
            {
                case 1:
                    into[0] = ParquetVariant.Primitive(3);
                    into[1] = unchecked((byte)value);
                    return 2;
                case 2:
                    into[0] = ParquetVariant.Primitive(4);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(into[1..], unchecked((short)value));
                    return 3;
                case 4:
                    into[0] = ParquetVariant.Primitive(5);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(into[1..], unchecked((int)value));
                    return 5;
                default:
                    into[0] = ParquetVariant.Primitive(6);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(into[1..], value);
                    return 9;
            }
        }

        internal override void CountNext(in Counting counting, PathCensus.PathCount count, long row, int times, ref TypedCursor cursor)
        {
            count.TakenInteger(counting.Census, row, _values.Next(ref cursor.Values), times);
            _values.Skip(ref cursor.Values, times - 1);
        }

        internal override VortexBuffer Scatter(ref VariantFieldExpr.Output output, CanonicalArena into, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor valuesAt = default;
            RunsAt at = new RunsAt(runs);
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int times = at.Times(row);
                output.Put(row, _values.Next(ref valuesAt), times);
                Rows.Skip(ref rowsAt, times - 1);
                _values.Skip(ref valuesAt, times - 1);
                left -= times;
            }

            return default;
        }

        internal override void CountInto(Indexes.ChunkBlockBounds.KeyBlocks bounds, int offset, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor valuesAt = default;
            RunsAt at = new RunsAt(runs);
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int times = at.Times(row);
                bounds.Integer(offset + row, _values.Next(ref valuesAt), times);
                Rows.Skip(ref rowsAt, times - 1);
                _values.Skip(ref valuesAt, times - 1);
                left -= times;
            }
        }

        protected override void Repeat()
        {
            _values.Add(_values.Last);
            if (Noted)
            {
                _widths!.Add(_widths.Last);
            }
        }

        protected override void Repeat(int count)
        {
            _values.AddRepeated(_values.Last, count);
            if (Noted)
            {
                _widths!.AddRepeated(_widths.Last, count);
            }
        }

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

        /// <summary>A double alone: a float, which a question reads as one, is of no typed column's class.</summary>
        internal override bool Plain(int row, ReadOnlySpan<byte> value, Indexes.ChunkBlockBounds.KeyBlocks? bounds)
        {
            // No chunk counts doubles block by block: one that did would count the value read.
            if (bounds is not null || value[0] != ParquetVariant.Primitive(7) || value.Length < 9)
            {
                return false;
            }

            Took(row);
            _values.Add(System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(value[1..]));
            return true;
        }

        internal override bool Holds(CanonicalNode column) => column.Kind == CanonicalKind.Primitive && column.PType == PType.F64;

        protected override void SeekValues(int start, bool indexed, ref FieldAt at)
        {
            at.Values = default;
            _values.Skip(ref at.Values, start);
        }

        internal override int Put(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing)
        {
            into[0] = ParquetVariant.Primitive(7);
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(into[1..], _values.Next(ref at.Values));
            return 9;
        }

        protected override void Range(in ColumnValues column, int first, int count) =>
            _values.AddRange(MemoryMarshal.Cast<byte, double>(column.Fixed).Slice(first, count));

        protected override void All(TypedValues other) => _values.AddAll(((DoubleValues)other)._values);

        protected override void Some(TypedValues other, int from, int count) => _values.AddFrom(((DoubleValues)other)._values, from, count);

        internal override void CountNext(in Counting counting, PathCensus.PathCount count, long row, int times, ref TypedCursor cursor)
        {
            count.TakenDouble(counting.Census, row, _values.Next(ref cursor.Values), times);
            _values.Skip(ref cursor.Values, times - 1);
        }

        internal override VortexBuffer Scatter(ref VariantFieldExpr.Output output, CanonicalArena into, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor valuesAt = default;
            RunsAt at = new RunsAt(runs);
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int times = at.Times(row);
                output.Put(row, _values.Next(ref valuesAt), times);
                Rows.Skip(ref rowsAt, times - 1);
                _values.Skip(ref valuesAt, times - 1);
                left -= times;
            }

            return default;
        }

        /// <summary>A chunk counts no doubles block by block (<see cref="Indexes.ChunkBlockBounds.Counted"/>).</summary>
        internal override void CountInto(Indexes.ChunkBlockBounds.KeyBlocks bounds, int offset, ReadOnlySpan<int> runs)
        {
        }

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

        internal override bool Plain(int row, ReadOnlySpan<byte> value, Indexes.ChunkBlockBounds.KeyBlocks? bounds)
        {
            byte header = value[0];
            if (header != ParquetVariant.Primitive(1) && header != ParquetVariant.Primitive(2))
            {
                return false;
            }

            bool held = header == ParquetVariant.Primitive(1);
            Took(row);
            _values.Add(held ? (byte)1 : (byte)0);
            bounds?.Boolean(row, held);
            return true;
        }

        internal override bool Holds(CanonicalNode column) => column.Kind == CanonicalKind.Bool;

        protected override void SeekValues(int start, bool indexed, ref FieldAt at)
        {
            at.Values = default;
            _values.Skip(ref at.Values, start);
        }

        internal override int Put(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing)
        {
            into[0] = ParquetVariant.Primitive(_values.Next(ref at.Values) != 0 ? 1 : 2);
            return 1;
        }

        protected override void Range(in ColumnValues column, int first, int count)
        {
            for (int row = first; row < first + count; row++)
            {
                int bit = column.BitOffset + row;
                _values.Add((byte)((column.Bits[bit >> 3] >> (bit & 7)) & 1));
            }
        }

        protected override void All(TypedValues other) => _values.AddAll(((BooleanValues)other)._values);

        protected override void Some(TypedValues other, int from, int count) => _values.AddFrom(((BooleanValues)other)._values, from, count);

        internal override void CountNext(in Counting counting, PathCensus.PathCount count, long row, int times, ref TypedCursor cursor)
        {
            count.TakenBoolean(counting.Census, row, _values.Next(ref cursor.Values) != 0, times);
            _values.Skip(ref cursor.Values, times - 1);
        }

        internal override VortexBuffer Scatter(ref VariantFieldExpr.Output output, CanonicalArena into, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor valuesAt = default;
            RunsAt at = new RunsAt(runs);
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int times = at.Times(row);
                output.Put(row, _values.Next(ref valuesAt) != 0, times);
                Rows.Skip(ref rowsAt, times - 1);
                _values.Skip(ref valuesAt, times - 1);
                left -= times;
            }

            return default;
        }

        internal override void CountInto(Indexes.ChunkBlockBounds.KeyBlocks bounds, int offset, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor valuesAt = default;
            RunsAt at = new RunsAt(runs);
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int times = at.Times(row);
                bounds.Boolean(offset + row, _values.Next(ref valuesAt) != 0, times);
                Rows.Skip(ref rowsAt, times - 1);
                _values.Skip(ref valuesAt, times - 1);
                left -= times;
            }
        }

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

        /// <summary>A short string, its length in its header: the string primitive of a longer text is read whole.</summary>
        internal override bool Plain(int row, ReadOnlySpan<byte> value, Indexes.ChunkBlockBounds.KeyBlocks? bounds)
        {
            byte header = value[0];
            int length = header >> 2;
            if ((header & 0x03) != ParquetVariant.BasicShortString || value.Length <= length)
            {
                return false;
            }

            ReadOnlySpan<byte> text = value.Slice(1, length);
            Took(row);
            _lengths.Add(length);
            _bytes.AddRange(text);
            _lastLength = length;
            bounds?.String(row, text);
            return true;
        }

        internal override bool Holds(CanonicalNode column) => column.Kind == CanonicalKind.VarBinView && column.DType.Kind == DTypeKind.Utf8;

        protected override void SeekValues(int start, bool indexed, ref FieldAt at)
        {
            at.Lengths = default;
            _lengths.Skip(ref at.Lengths, start);
            at.Bytes = default;
            _bytes.Skip(ref at.Bytes, start == 0 ? 0 : indexed ? _starts.Before(start) : _lengths.Sum(0, start));
        }

        /// <exception cref="InvalidOperationException">The text repeats the one before, its bytes kept once: no range is taken of such texts.</exception>
        internal override int Put(ref FieldAt at, Span<byte> into, scoped ref byte[]? crossing)
        {
            int length = _lengths.Next(ref at.Lengths);
            if (length < 0)
            {
                throw new InvalidOperationException("A text that repeats the one before is not put back from its place among the texts.");
            }

            ReadOnlySpan<byte> text = _bytes.Next(ref at.Bytes, length, ref crossing);
            if (length <= ParquetVariant.MaxShortString)
            {
                into[0] = (byte)((length << 2) | ParquetVariant.BasicShortString);
                text.CopyTo(into[1..]);
                return 1 + length;
            }

            into[0] = ParquetVariant.Primitive(16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(into[1..], (uint)length);
            text.CopyTo(into[5..]);
            return 5 + length;
        }

        /// <summary>Each text whole, its bytes its own: a column says nothing of the row that repeats the one before.</summary>
        protected override void Range(in ColumnValues column, int first, int count)
        {
            for (int row = first; row < first + count; row++)
            {
                ReadOnlySpan<byte> text = column.Views.At(row);
                _lengths.Add(text.Length);
                _bytes.AddRange(text);
                _lastLength = text.Length;
            }
        }

        protected override void All(TypedValues other)
        {
            StringValues from = (StringValues)other;
            _lengths.AddAll(from._lengths);
            _bytes.AddAll(from._bytes);
            _lastLength = from._lastLength;
        }

        // Where each text's bytes start among the bytes, once made ready for ranges of them (Index).
        private Starts _starts;

        /// <summary>False for texts some of which repeat the one before: a range of them would start on bytes it does not hold.</summary>
        protected override bool IndexValues(CanonicalArena arena) => _starts.Build(arena, _lengths);

        protected override void Some(TypedValues other, int from, int count)
        {
            StringValues source = (StringValues)other;
            _lengths.AddFrom(source._lengths, from, count);
            int start = source._starts.Before(from);
            _bytes.AddFrom(source._bytes, start, source._starts.Before(from + count) - start);
            _lastLength = _lengths.Last;
        }

        /// <summary>The next text, a repeat the text it repeats, whose bytes are there once and which the cursor keeps.</summary>
        internal override void CountNext(in Counting counting, PathCensus.PathCount count, long row, int times, ref TypedCursor cursor)
        {
            int length = _lengths.Next(ref cursor.Lengths);
            if (length >= 0)
            {
                cursor.Last = _bytes.Next(ref cursor.Values, length, ref cursor.Crossing);
            }

            // The rows after the first repeat the text, each a length of its own and no bytes.
            count.TakenString(counting.Census, row, cursor.Last, times);
            _lengths.Skip(ref cursor.Lengths, times - 1);
        }

        /// <summary>Each text's view at its row, a repeat the view of the text it repeats, over the bytes taken: where they lie when one block holds them, a copy otherwise.</summary>
        internal override VortexBuffer Scatter(ref VariantFieldExpr.Output output, CanonicalArena into, ReadOnlySpan<int> runs)
        {
            VortexBuffer data = default;
            ReadOnlySpan<byte> written = default;
            if (_bytes.Blocks == 1)
            {
                data = _bytes.Held(0);
                written = data.Span;
            }
            else if (_bytes.Count > 0)
            {
                data = into.AllocateUninitialized(_bytes.Count, 1, out Span<byte> copy).Slice(0, _bytes.Count);
                _bytes.CopyTo(copy);
                written = copy[.._bytes.Count];
            }

            Cursor rowsAt = default;
            Cursor lengthsAt = default;
            RunsAt at = new RunsAt(runs);
            Int128 view = default;
            int start = 0;
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int count = _lengths.Next(ref lengthsAt);
                if (count >= 0)
                {
                    view = Composed(written, start, count);
                    start += count;
                }

                // The rows of a run repeat the text before them, each a length of its own and no bytes.
                int times = at.Times(row);
                output.Put(row, view, times);
                Rows.Skip(ref rowsAt, times - 1);
                _lengths.Skip(ref lengthsAt, times - 1);
                left -= times;
            }

            return data;
        }

        /// <summary>Each text counted at its row, a repeat as the text it repeats, whose bytes are there once.</summary>
        internal override void CountInto(Indexes.ChunkBlockBounds.KeyBlocks bounds, int offset, ReadOnlySpan<int> runs)
        {
            Cursor rowsAt = default;
            Cursor lengthsAt = default;
            RunsAt at = new RunsAt(runs);
            Texts texts = new Texts(_bytes);
            ReadOnlySpan<byte> last = default;
            for (int left = Count; left > 0;)
            {
                int row = Rows!.Next(ref rowsAt);
                int count = _lengths.Next(ref lengthsAt);
                int times = at.Times(row);
                if (count >= 0)
                {
                    last = texts.Next(count);
                    bounds.String(offset + row, last, times);
                }
                else if (times > 1)
                {
                    // A run of rows that repeat the text before them: counted a block's rows at once.
                    bounds.String(offset + row, last, times);
                }
                else
                {
                    bounds.StringAgain(offset + row, last);
                }

                Rows.Skip(ref rowsAt, times - 1);
                _lengths.Skip(ref lengthsAt, times - 1);
                left -= times;
            }
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

        // For values ranges of which are taken (Range): the most keys each names (Keys), which a range sums; and, once made
        // ready (Index), where the rows hold each row of the chunk and where each value's bytes start among the bytes.
        private Appended<int>? _keysOf;
        private Ranks _ranks;
        private Starts _starts;

        internal void Clear(CanonicalArena arena)
        {
            _rows.Clear(arena);
            _lengths.Clear(arena);
            _bytes.Clear(arena);
            _keysOf?.Clear(arena);
            Keys = 0;
        }

        /// <summary>Has the values keep what a range of them is found from (<see cref="Index"/>), before any is taken.</summary>
        internal void Range() => _keysOf = new Appended<int>();

        /// <summary>Makes the values taken of a chunk's <paramref name="rows"/> rows ready for ranges of them to be taken (<see cref="AppendRange"/>), carved from <paramref name="arena"/>.</summary>
        internal bool Index(CanonicalArena arena, int rows)
        {
            _ranks.Build(arena, _rows, rows);
            return _keysOf is not null && _starts.Build(arena, _lengths);
        }

        /// <summary>
        /// Takes the values <paramref name="other"/>, made ready for it (<see cref="Index"/>), took at its
        /// <paramref name="count"/> rows from <paramref name="first"/> on, after these, each at its row <paramref name="shift"/>
        /// rows on; returns how many.
        /// </summary>
        internal int AppendRange(BinaryValues other, int first, int count, int shift)
        {
            int start = other._ranks.Before(first);
            int values = other._ranks.Before(first + count) - start;
            if (values == 0)
            {
                return 0;
            }

            AddRowsOf(_rows, other._rows, start, values, first, count, shift);
            _lengths.AddFrom(other._lengths, start, values);
            int from = other._starts.Before(start);
            _bytes.AddFrom(other._bytes, from, other._starts.Before(start + values) - from);
            _keysOf?.AddFrom(other._keysOf!, start, values);
            Keys += other._keysOf!.Sum(start, values);
            return values;
        }

        /// <summary>Takes <paramref name="value"/>, a value whole, at row <paramref name="row"/>.</summary>
        internal void Add(int row, ReadOnlySpan<byte> value)
        {
            Put(row, value, Nested(value));
        }

        /// <summary>
        /// Sets the cursors on the values taken at the rows from <paramref name="first"/> to <paramref name="end"/>, the
        /// first of them next (<see cref="Next"/>), <paramref name="left"/> of them: found by the rows' ranks when the values
        /// are made ready for ranges (<paramref name="indexed"/>), counted otherwise.
        /// </summary>
        internal void Seek(int first, int end, bool indexed, ref Cursor rows, ref Cursor lengths, ref Cursor bytes, out int left)
        {
            int start = Below(_rows, in _ranks, indexed, first);
            left = Below(_rows, in _ranks, indexed, end) - start;
            rows = default;
            _rows.Skip(ref rows, start);
            lengths = default;
            _lengths.Skip(ref lengths, start);
            bytes = default;
            _bytes.Skip(ref bytes, start == 0 ? 0 : indexed ? _starts.Before(start) : _lengths.Sum(0, start));
        }

        private void Put(int row, ReadOnlySpan<byte> value, long keys)
        {
            _rows.Add(row);
            _lengths.Add(value.Length);
            _bytes.AddRange(value);
            _keysOf?.Add(checked((int)keys));
            Keys += keys;
        }

        /// <summary>Takes the values <paramref name="other"/> took, after these, each at its row <paramref name="offset"/> rows on.</summary>
        internal void Append(BinaryValues other, int offset)
        {
            System.Diagnostics.Debug.Assert(_keysOf is null, "A chunk that takes blocks whole is not one ranges are taken of.");
            _rows.AddShifted(other._rows, offset);
            _lengths.AddAll(other._lengths);
            _bytes.AddAll(other._bytes);
            Keys += other.Keys;
        }

        /// <summary>The values taken.</summary>
        internal int Count => _rows.Count;

        /// <summary>
        /// The most keys the values taken name, every depth together: a key for each field of an object of keys kept, and
        /// for an object or a list kept whole or held by such a field, a key for every three of its bytes, an id, an offset
        /// and a byte of value at least. What a census that counts them may meet for the first time.
        /// </summary>
        internal long Keys { get; private set; }

        /// <summary>The most keys <paramref name="value"/> names below it: none for a scalar, one for every three bytes of an object or a list.</summary>
        private static long Nested(ReadOnlySpan<byte> value) =>
            !value.IsEmpty && (value[0] & 0x03) is ParquetVariant.BasicObject or ParquetVariant.BasicArray ? value.Length / 3 : 0;

        /// <summary>
        /// The value after the one read last, the first from cursors at their default: where it lies when a block holds it
        /// whole, and otherwise a copy in <paramref name="crossing"/>, which lasts until the next value read so.
        /// </summary>
        internal ReadOnlySpan<byte> Next(scoped ref Cursor lengths, scoped ref Cursor bytes, scoped ref byte[]? crossing) =>
            _bytes.Next(ref bytes, _lengths.Next(ref lengths), ref crossing);

        /// <summary>Moves the cursors past the <paramref name="count"/> values after the one read last, each of <paramref name="length"/> bytes: the rows that repeat it.</summary>
        internal void Skip(ref Cursor lengths, ref Cursor bytes, long count, int length)
        {
            _lengths.Skip(ref lengths, count);
            _bytes.Skip(ref bytes, count * length);
        }

        /// <summary>
        /// Takes at row <paramref name="row"/> the object of the fields <paramref name="kept"/> of <paramref name="fields"/>,
        /// in their order, which is their keys'.
        /// </summary>
        internal void AddObject(int row, VariantNested fields, ReadOnlySpan<int> kept)
        {
            int maxId = 0;
            int size = 0;
            long keys = 0;
            foreach (int i in kept)
            {
                ReadOnlySpan<byte> held = fields.ValueAt(i);
                maxId = Math.Max(maxId, fields.IdAt(i));
                size = checked(size + held.Length);
                keys += 1 + Nested(held);
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

                Put(row, into, keys);
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
    private static Int128 Composed(ReadOnlySpan<byte> written, int start, int count)
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

        /// <summary>The arena the values are carved from (<see cref="Clear"/>).</summary>
        internal CanonicalArena Arena => _arena;

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

                // Eight rows a step, two vectors of four: the runs a consolidation's take replays are a series' rows, two
                // thousand on average, each of a node's rows written one at a time before (2026-11-18).
                int take = Math.Min(_left, count);
                int* into = (int*)_next;
                int i = 0;
                if (take >= 8)
                {
                    Vector128<int> low = Vector128.Create(first, first + 1, first + 2, first + 3);
                    Vector128<int> high = low + Vector128.Create(4);
                    Vector128<int> step = Vector128.Create(8);
                    for (; i <= take - 8; i += 8)
                    {
                        low.Store(into + i);
                        high.Store(into + i + 4);
                        low += step;
                        high += step;
                    }
                }

                for (; i < take; i++)
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

        /// <summary>Appends every value of <paramref name="other"/>, another list, in order.</summary>
        internal void AddAll(Appended<T> other)
        {
            for (int block = 0; block < other.Blocks; block++)
            {
                AddRange(other.Block(block));
            }
        }

        /// <summary>Appends every row of <paramref name="other"/>, another list of rows, in order, each <paramref name="shift"/> rows on.</summary>
        internal void AddShifted(Appended<T> other, int shift)
        {
            System.Diagnostics.Debug.Assert(typeof(T) == typeof(int), "Rows are shifted.");
            if (shift == 0)
            {
                AddAll(other);
                return;
            }

            for (int block = 0; block < other.Blocks; block++)
            {
                AddShifted(MemoryMarshal.Cast<T, int>(other.Block(block)), shift);
            }
        }

        /// <summary>Appends <paramref name="rows"/>, each <paramref name="shift"/> rows on, to a list of rows.</summary>
        private void AddShifted(ReadOnlySpan<int> rows, int shift)
        {
            Vector128<int> by = Vector128.Create(shift);
            while (!rows.IsEmpty)
            {
                if (_left == 0)
                {
                    Grow();
                }

                int take = Math.Min(_left, rows.Length);
                int* into = (int*)_next;
                ref int from = ref MemoryMarshal.GetReference(rows);
                int i = 0;
                for (; i <= take - Vector128<int>.Count; i += Vector128<int>.Count)
                {
                    (Vector128.LoadUnsafe(ref from, (nuint)i) + by).Store(into + i);
                }

                for (; i < take; i++)
                {
                    into[i] = rows[i] + shift;
                }

                _next += take;
                _left -= take;
                Count += take;
                rows = rows[take..];
            }
        }

        /// <summary>Appends the <paramref name="count"/> values of <paramref name="other"/>, another list, from its value <paramref name="from"/> on.</summary>
        internal void AddFrom(Appended<T> other, int from, int count)
        {
            for (int b = 0; b < other._blockCount && count > 0; b++)
            {
                ReadOnlySpan<T> block = other.Block(b);
                if (from >= block.Length)
                {
                    from -= block.Length;
                    continue;
                }

                int take = Math.Min(block.Length - from, count);
                AddRange(block.Slice(from, take));
                from = 0;
                count -= take;
            }
        }

        /// <summary>
        /// Appends the <paramref name="count"/> rows of <paramref name="other"/>, another list of rows, from its row
        /// <paramref name="from"/> on, each <paramref name="shift"/> rows on.
        /// </summary>
        internal void AddShiftedFrom(Appended<T> other, int from, int count, int shift)
        {
            System.Diagnostics.Debug.Assert(typeof(T) == typeof(int), "Rows are shifted.");
            for (int b = 0; b < other._blockCount && count > 0; b++)
            {
                ReadOnlySpan<int> block = MemoryMarshal.Cast<T, int>(other.Block(b));
                if (from >= block.Length)
                {
                    from -= block.Length;
                    continue;
                }

                int take = Math.Min(block.Length - from, count);
                AddShifted(block.Slice(from, take), shift);
                from = 0;
                count -= take;
            }
        }

        /// <summary>The sum of the <paramref name="count"/> values from value <paramref name="from"/> on, of a list of counts.</summary>
        internal long Sum(int from, int count)
        {
            System.Diagnostics.Debug.Assert(typeof(T) == typeof(int), "Counts are summed.");
            long sum = 0;
            for (int b = 0; b < _blockCount && count > 0; b++)
            {
                ReadOnlySpan<int> block = MemoryMarshal.Cast<T, int>(Block(b));
                if (from >= block.Length)
                {
                    from -= block.Length;
                    continue;
                }

                int take = Math.Min(block.Length - from, count);
                foreach (int value in block.Slice(from, take))
                {
                    sum += value;
                }

                from = 0;
                count -= take;
            }

            return sum;
        }

        /// <summary>Moves <paramref name="cursor"/> past the <paramref name="count"/> values after the one read last.</summary>
        internal void Skip(ref Cursor cursor, long count)
        {
            while (count > 0)
            {
                int left = Block(cursor.Block).Length - cursor.At;
                if (left == 0)
                {
                    cursor.Block++;
                    cursor.At = 0;
                    continue;
                }

                int take = (int)Math.Min(left, count);
                cursor.At += take;
                count -= take;
            }
        }

        /// <summary>The value at <paramref name="cursor"/>, which moves past it: the values read back in order from a cursor at its default.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Next(ref Cursor cursor)
        {
            ReadOnlySpan<T> block = Block(cursor.Block);
            if (cursor.At == block.Length)
            {
                block = Block(++cursor.Block);
                cursor.At = 0;
            }

            return block[cursor.At++];
        }

        /// <summary>
        /// The <paramref name="count"/> values from <paramref name="cursor"/> on, which moves past them: where they lie when a
        /// block holds them all, and otherwise a copy in <paramref name="crossing"/>, which lasts until the next values a block
        /// does not hold whole.
        /// </summary>
        internal ReadOnlySpan<T> Next(scoped ref Cursor cursor, int count, scoped ref T[]? crossing)
        {
            if (count == 0)
            {
                return default;
            }

            ReadOnlySpan<T> block = Block(cursor.Block);
            if (cursor.At == block.Length)
            {
                block = Block(++cursor.Block);
                cursor.At = 0;
            }

            if (block.Length - cursor.At >= count)
            {
                ReadOnlySpan<T> held = block.Slice(cursor.At, count);
                cursor.At += count;
                return held;
            }

            if (crossing is null || crossing.Length < count)
            {
                crossing = new T[Math.Max(count, 256)];
            }

            int copied = 0;
            while (copied < count)
            {
                if (cursor.At == block.Length)
                {
                    block = Block(++cursor.Block);
                    cursor.At = 0;
                }

                int take = Math.Min(count - copied, block.Length - cursor.At);
                block.Slice(cursor.At, take).CopyTo(crossing.AsSpan(copied));
                copied += take;
                cursor.At += take;
            }

            return crossing.AsSpan(0, count);
        }

        /// <summary>The value appended last; meaningless before one.</summary>
        internal T Last => *(_next - 1);

        /// <summary>The buffer of block <paramref name="index"/>, the last one's as far as it is filled: what a view of its values names.</summary>
        internal VortexBuffer Held(int index)
        {
            VortexBuffer block = _blocks[index];
            return index == _blockCount - 1 ? block.Slice(0, block.Length - (_left * sizeof(T))) : block;
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

    /// <summary>
    /// A typed column of another shredding, resolved once for the rows a node takes of it (<see cref="TypedValues.TakeColumn"/>):
    /// its fixed-width values, a boolean's bits and where they start, or a text's views and their bytes.
    /// </summary>
    private readonly ref struct ColumnValues
    {
        internal readonly ReadOnlySpan<byte> Fixed;
        internal readonly ReadOnlySpan<byte> Bits;
        internal readonly int BitOffset;
        internal readonly ViewValues Views;

        internal ColumnValues(CanonicalNode column)
        {
            switch (column.Kind)
            {
                case CanonicalKind.Primitive:
                    Fixed = column.Values.Span;
                    break;
                case CanonicalKind.Bool:
                    Bits = column.Bits.Span;
                    BitOffset = column.BitOffset;
                    break;
                default:
                    Views = new ViewValues(column);
                    break;
            }
        }
    }

    /// <summary>Appends to <paramref name="list"/> the rows <paramref name="held"/> selects, a bit a row, each <paramref name="offset"/> rows on: those that follow each other at once.</summary>
    private static void AddRows(Appended<int> list, ReadOnlySpan<ulong> held, int rows, int offset)
    {
        int words = (rows + 63) >> 6;
        for (int w = 0; w < words; w++)
        {
            ulong bits = held[w];
            int first = w << 6;
            while (bits != 0)
            {
                int start = System.Numerics.BitOperations.TrailingZeroCount(bits);
                int length = bits == ulong.MaxValue ? 64 : System.Numerics.BitOperations.TrailingZeroCount(~(bits >> start));
                list.AddSequence(offset + first + start, length);
                bits = length == 64 ? 0 : bits & ~(((1UL << length) - 1) << start);
            }
        }
    }

    /// <summary>
    /// Appends to <paramref name="list"/> the <paramref name="held"/> rows of <paramref name="from"/>, another list of rows,
    /// from its row <paramref name="start"/> on, which are those it holds of the <paramref name="count"/> rows from
    /// <paramref name="first"/> on, each <paramref name="shift"/> rows on: the range whole at once when it holds every row of it.
    /// </summary>
    private static void AddRowsOf(Appended<int> list, Appended<int> from, int start, int held, int first, int count, int shift)
    {
        if (held == count)
        {
            list.AddSequence(first + shift, count);
        }
        else
        {
            list.AddShiftedFrom(from, start, held, shift);
        }
    }

    /// <summary>
    /// Where a list of rising rows holds each row of a chunk, told at any row once built (<see cref="Shredding.Index"/>):
    /// a bit a row and the rows held before each word of them, and neither for a list that holds every row or none.
    /// </summary>
    /// <remarks>
    /// A block takes of a batch's shredding as many ranges as the merge handed it runs of the batch, a row or two long where
    /// its inputs' keys interleave closely: a search of the rows for each end of each range, of each node, would cost a row
    /// read whole.
    /// </remarks>
    private struct Ranks
    {
        private VortexBuffer _bits;
        private VortexBuffer _before;
        private int _rows;
        private int _held;

        /// <summary>Builds it for <paramref name="held"/>, rising rows below <paramref name="rows"/>, in <paramref name="arena"/>.</summary>
        internal void Build(CanonicalArena arena, Appended<int> held, int rows)
        {
            _rows = rows;
            _held = held.Count;
            _bits = default;
            _before = default;
            if (_held == 0 || _held == rows)
            {
                return;
            }

            // A word more than the rows fill, for the rows held below the chunk's end.
            int words = (rows >> 6) + 1;
            _bits = arena.Allocate(words * sizeof(ulong), 8, out Span<byte> zeroed).Slice(0, words * sizeof(ulong));
            _before = arena.AllocateUninitialized(words * sizeof(int), 8, out Span<byte> counted).Slice(0, words * sizeof(int));
            Span<ulong> bits = MemoryMarshal.Cast<byte, ulong>(zeroed)[..words];
            for (int b = 0; b < held.Blocks; b++)
            {
                foreach (int row in held.Block(b))
                {
                    bits[row >> 6] |= 1UL << (row & 63);
                }
            }

            Span<int> before = MemoryMarshal.Cast<byte, int>(counted)[..words];
            int sum = 0;
            for (int w = 0; w < words; w++)
            {
                before[w] = sum;
                sum += System.Numerics.BitOperations.PopCount(bits[w]);
            }
        }

        /// <summary>How many of the rows held lie below <paramref name="row"/>, at most the chunk's rows: the place among them of the first one from it on.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly int Before(int row)
        {
            if (_held == 0)
            {
                return 0;
            }

            if (_held == _rows)
            {
                return row;
            }

            int word = row >> 6;
            ulong below = _bits.Cast<ulong>()[word] & ((1UL << (row & 63)) - 1);
            return _before.Cast<int>()[word] + System.Numerics.BitOperations.PopCount(below);
        }
    }

    /// <summary>
    /// The sum of the counts before each of a list of counts, and of them all, told at any place once built
    /// (<see cref="Shredding.Index"/>): where a text's bytes start among its list's bytes, or a list's elements among the
    /// elements.
    /// </summary>
    private struct Starts
    {
        private VortexBuffer _held;

        /// <summary>Builds it for <paramref name="counts"/> in <paramref name="arena"/>. False when a count is complemented, a repeat that holds no bytes of its own.</summary>
        internal bool Build(CanonicalArena arena, Appended<int> counts)
        {
            _held = default;
            if (counts.Count == 0)
            {
                return true;
            }

            int bytes = checked((counts.Count + 1) * sizeof(int));
            _held = arena.AllocateUninitialized(bytes, 8, out Span<byte> into).Slice(0, bytes);
            Span<int> starts = MemoryMarshal.Cast<byte, int>(into)[..(counts.Count + 1)];
            int sum = 0;
            int at = 0;
            int signs = 0;
            for (int b = 0; b < counts.Blocks; b++)
            {
                foreach (int count in counts.Block(b))
                {
                    starts[at++] = sum;
                    sum += count;
                    signs |= count;
                }
            }

            starts[at] = sum;
            return signs >= 0;
        }

        /// <summary>The sum of the counts before the one at <paramref name="index"/>, at most the list's count.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly int Before(int index) => _held.Cast<int>()[index];
    }

    /// <summary>Where a list of values is read back from, a value or a few at a time (<see cref="Appended{T}.Next(ref Cursor)"/>): its first value at its default.</summary>
    private struct Cursor
    {
        internal int Block;
        internal int At;
    }

    /// <summary>
    /// Where what one key's node took of a range of rows is read back from, as the rows' values are put back together
    /// (<see cref="ObjectGroup.Rebuild"/>): the values it typed, and those it kept whole, each with how many the range
    /// still holds.
    /// </summary>
    private struct FieldAt
    {
        internal Cursor TypedRows;
        internal int Typed;
        internal Cursor Values;
        internal Cursor Lengths;
        internal Cursor Bytes;
        internal Cursor Widths;
        internal Cursor KeptRows;
        internal int Kept;
        internal Cursor KeptLengths;
        internal Cursor KeptBytes;
    }

    /// <summary>
    /// How many of <paramref name="held"/>, rising rows, lie below <paramref name="row"/>: told by
    /// <paramref name="ranks"/> when they are built over the rows (<paramref name="indexed"/>), searched block by block
    /// otherwise.
    /// </summary>
    private static int Below(Appended<int> held, in Ranks ranks, bool indexed, int row)
    {
        if (indexed)
        {
            return ranks.Before(row);
        }

        int below = 0;
        for (int b = 0; row > 0 && b < held.Blocks; b++)
        {
            ReadOnlySpan<int> block = held.Block(b);
            if (block.Length > 0 && block[^1] < row)
            {
                below += block.Length;
                continue;
            }

            int low = 0;
            int high = block.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (block[middle] < row)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return below + low;
        }

        return below;
    }

    /// <summary>
    /// Where a typed column's values are read back from, one at a time (<see cref="TypedValues.CountNext"/>): its values, a
    /// text's bytes, its lengths, and the text read last, which the texts after it may repeat.
    /// </summary>
    private ref struct TypedCursor
    {
        internal Cursor Values;
        internal Cursor Lengths;
        internal byte[]? Crossing;
        internal ReadOnlySpan<byte> Last;
    }

    /// <summary>
    /// A census counting what a shredding took of a block (<see cref="Shredding.Count"/>): the census, the block's rows'
    /// metadata, which the keys of a value kept whole are named by, and the block's first row among the census's.
    /// </summary>
    private readonly ref struct Counting(PathCensus census, ViewValues metadata, long first, ReadOnlySpan<int> runs)
    {
        internal readonly PathCensus Census = census;
        internal readonly ViewValues Metadata = metadata;
        internal readonly long First = first;

        /// <summary>
        /// The runs of rows that took again what the row read before them took (<see cref="Replay"/>), a first row and a
        /// count each, in the rows' order: a node that took the row read holds, right after what it took of it, the same
        /// once more for each row of each run that follows it.
        /// </summary>
        internal readonly ReadOnlySpan<int> Runs = runs;

        /// <summary>
        /// Counts at <paramref name="count"/> the value <paramref name="value"/> kept whole at each of the
        /// <paramref name="times"/> rows from <paramref name="row"/> of the block on, its row's dictionary read when it is an
        /// object, whose keys it names.
        /// </summary>
        internal void Kept(PathCensus.PathCount count, int row, ReadOnlySpan<byte> value, int depth, int times)
        {
            VariantDictionary dictionary = (value[0] & 0x03) == ParquetVariant.BasicObject ? Census.Enter(Metadata.At(row)) : default;
            count.TakenValue(Census, First + row, in dictionary, value, depth, times);
        }
    }

    /// <summary>
    /// The runs of a block (<see cref="Counting.Runs"/>) walked beside what one node took, whose rows rise: how many rows
    /// take what the node holds at a row.
    /// </summary>
    private ref struct RunsAt(ReadOnlySpan<int> runs)
    {
        private readonly ReadOnlySpan<int> _runs = runs;
        private int _at;

        /// <summary>
        /// The rows that hold what the node holds at <paramref name="row"/>, from it on: those of the run that starts there,
        /// which the node holds the same for once each right after, or the row alone.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Times(int row)
        {
            while (_at < _runs.Length && _runs[_at] < row)
            {
                _at += 2;
            }

            return _at < _runs.Length && _runs[_at] == row ? _runs[_at + 1] : 1;
        }
    }

    /// <summary>
    /// Reads the texts appended end to end to a list of bytes back in order, each of a length its caller knows: where it
    /// lies when a block holds it whole, and otherwise a copy, which lasts until the next text a block does not hold whole.
    /// </summary>
    private ref struct Texts(Appended<byte> from)
    {
        private ReadOnlySpan<byte> _block;
        private int _at;
        private int _next;
        private byte[]? _crossing;

        internal ReadOnlySpan<byte> Next(int count)
        {
            if (count == 0)
            {
                return default;
            }

            if (_at == _block.Length)
            {
                _block = from.Block(_next++);
                _at = 0;
            }

            if (_block.Length - _at >= count)
            {
                ReadOnlySpan<byte> text = _block.Slice(_at, count);
                _at += count;
                return text;
            }

            if (_crossing is null || _crossing.Length < count)
            {
                _crossing = new byte[Math.Max(count, 256)];
            }

            int copied = 0;
            while (copied < count)
            {
                if (_at == _block.Length)
                {
                    _block = from.Block(_next++);
                    _at = 0;
                }

                int take = Math.Min(count - copied, _block.Length - _at);
                _block.Slice(_at, take).CopyTo(_crossing.AsSpan(copied));
                copied += take;
                _at += take;
            }

            return _crossing.AsSpan(0, count);
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
