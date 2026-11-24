using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Execution;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// The chunks of the columns that gather, each emitted by a task of its own on the core while the blocks after it go
/// through, and written into the file at a point of the rows' own order.
/// </summary>
public sealed partial class VortexFileWriter
{
    // By column, its chunks taken to emission and not yet written, oldest first, at most EmissionDepth of them; and the block
    // bounds a shredded column's chunks count into, free for the next, by column.
    private List<ColumnEmission>?[]? _emissions;
    private List<ChunkBlockBounds>?[]? _emissionBounds;

    // By column whose emissions follow one another, one with a memory of its plans, the last one started, which the next
    // waits for; and its zstd frames, compressed as items of its emission and kept from one chunk to the next.
    private ColumnEmission?[]? _lastEmission;
    private ZstdPlan.Ahead?[]? _emissionFrames;

    // The leaves of the shredded chunks the writer's own flow writes, reused from one chunk to the next.
    private LeafWork? _leafWork;

    // By column, an emission landed, for its next chunk: a column holds as many as it has in emission at once, made once.
    private ColumnEmission?[]? _spareEmissions;

    // The transit context the chunk being emitted lies in (EmitBlockAsync), and, once a column's emission takes records
    // over its buffers, what keeps it until the last of them lets it go.
    private ScanContext? _sharingFrom;
    private SharedRows? _sharing;

    /// <summary>
    /// A transit context whose buffers the emissions of a chunk of the file read through records of their own, given back
    /// to the process's once the writer and every one of them let it go: the next chunk's rows go into another.
    /// </summary>
    private sealed class SharedRows(ScanContext context)
    {
        // The writer's hold, and one per emission.
        private int _holds = 1;

        internal ScanContext Context => context;

        internal void Hold() => Interlocked.Increment(ref _holds);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _holds) == 0)
            {
                TransitContexts.Return(context);
            }
        }
    }

    /// <summary>An emission for a chunk of column <paramref name="field"/>: the one the column's last landed left, or new.</summary>
    private ColumnEmission TakeEmission(int field)
    {
        if (_spareEmissions?[field] is { } spare)
        {
            _spareEmissions[field] = null;
            return spare;
        }

        return new ColumnEmission(this);
    }

    /// <summary>Keeps <paramref name="chunk"/>, landed, for its column's next chunk, emptied.</summary>
    private void Spare(ColumnEmission chunk)
    {
        chunk.Arena = null;
        chunk.Owned = null;
        chunk.Shared = null;
        chunk.Inline = false;
        chunk.Stats = null;
        chunk.Bounds = null;
        chunk.Blob = default;
        chunk.Offered = false;
        chunk.Taken = null;
        chunk.Then = null;
        chunk.FreshCount = 0;
        chunk.Cut = 0;
        chunk.Held = 0;
        (_spareEmissions ??= new ColumnEmission?[_fieldCount])[chunk.Field] ??= chunk;
    }

    /// <summary>
    /// Takes column <paramref name="field"/>'s share of a chunk of the file, node <paramref name="node"/> of
    /// <paramref name="arena"/>, <paramref name="rows"/> rows over <paramref name="blocks"/> blocks from
    /// <paramref name="firstBlock"/>, as a chunk to emit apart, with what its ingest knew of it: on more than one lane, through
    /// records of its own over the transit's buffers, which the writer keeps until the emission lets them go, or a copy of
    /// rows the caller lent; on one, read where it lies, compressed as it leaves.
    /// </summary>
    private ColumnEmission DetachChunk(int field, CanonicalArena arena, int node, int rows, int firstBlock, int blocks)
    {
        ColumnEmission chunk = TakeEmission(field);
        chunk.Field = field;
        chunk.Rows = rows;
        chunk.FirstBlock = firstBlock;
        chunk.Blocks = blocks;

        // A chunk of the file is the column's chunk as its writer sizes them: its plan is the column's.
        chunk.Measures = true;

        // A column written against an object dictionary goes as its codes, which the statistics of its values do not describe.
        if (Codes(field))
        {
            node = Coded(field, arena, node);
        }

        chunk.Stats = _columns[field].Freeze(firstBlock, blocks);
        if (_shredding?[field] is null && !Codes(field))
        {
            Tally(new ChunkStats(chunk.Stats, this), arena, node, rows);
        }

        if (_lanes <= 1 || ProbesWatch)
        {
            chunk.Arena = arena;
            chunk.Node = node;
            chunk.Inline = true;
            return chunk;
        }

        ScanContext own = TransitContexts.Rent(Account);
        if (_sharingFrom is { } from && ReferenceEquals(arena, from.Canonical))
        {
            SharedRows shared = _sharing ??= new SharedRows(from);
            shared.Hold();
            chunk.Shared = shared;
            chunk.Node = own.Canonical.ReferenceFrom(arena, node);
        }
        else
        {
            chunk.Node = own.Canonical.CopyFrom(arena, node);
        }

        chunk.Arena = own.Canonical;
        chunk.Owned = own;
        return chunk;
    }

    /// <summary>
    /// Ends the emission of a chunk of the file from transit context <paramref name="from"/> (<see cref="_sharingFrom"/>):
    /// true when an emission took records over its buffers, which the writer then lets go of, the emissions giving it back
    /// to the process's once done; false when it is still the writer's to reset.
    /// </summary>
    private bool EndSharing(ScanContext from)
    {
        _sharingFrom = null;
        if (_sharing is not { } shared || !ReferenceEquals(shared.Context, from))
        {
            _sharing = null;
            return false;
        }

        _sharing = null;
        for (int slot = 0; _transit is not null && slot < _transit.Length; slot++)
        {
            if (ReferenceEquals(_transit[slot], from))
            {
                _transit[slot] = null;
            }
        }

        shared.Release();
        return true;
    }

    /// <summary>
    /// The chunks of a column in emission at once: two, so that the column's next chunk is taken while the one before is
    /// still compressed. One at a time, a chunk of a log's attributes took 139 ms to compress, the column gathered the next
    /// in 80, and the writer waited 1.9 s of a tier merge's 8.4 for the chunk before it, 32 times (2026-11-16).
    /// </summary>
    private const int EmissionDepth = 2;

    /// <summary>What a column's emission that ended leaves for the one after it (<see cref="ColumnEmission.Then"/>).</summary>
    private static readonly ColumnEmission ChainEnded = new ColumnEmission(null!);

    /// <summary>The chunks of the columns that gather emitted by tasks of their own (<see cref="StartEmission"/>).</summary>
    internal int ChunksEmittedApart { get; private set; }

    /// <summary>The shredded chunks whose leaves were written apart on the core and grafted into their blob, emitted apart or not.</summary>
    internal int ChunksGrafted { get; private set; }

    /// <summary>Of <see cref="ChunksEmittedApart"/>, the shredded ones whose rows the writer's line took block by block (<see cref="ShredChain"/>).</summary>
    internal int ChunksTakenOnLine => Volatile.Read(ref _chunksTakenOnLine);

    private int _chunksTakenOnLine;

    /// <summary>
    /// Of <see cref="ChunksTakenOnLine"/>, those some of whose rows were gathered thin (<see cref="Thin"/>), counted by their
    /// columns' chains, once the writer is done.
    /// </summary>
    internal int ChunksGatheredThin
    {
        get
        {
            int count = 0;
            foreach (ShredChain? chain in _chains ?? [])
            {
                count += chain?.ChunksThin ?? 0;
            }

            return count;
        }
    }

    /// <summary>
    /// Of <see cref="ChunksTakenOnLine"/>, those whose chain broke, their rows past the break taken as they were compressed,
    /// counted by their columns' chains, once the writer is done.
    /// </summary>
    internal int ChunksTakenInPart
    {
        get
        {
            int count = 0;
            foreach (ShredChain? chain in _chains ?? [])
            {
                count += chain?.ChunksInPart ?? 0;
            }

            return count;
        }
    }

    /// <summary>
    /// Whether the chunk of a column that gathers, due, is emitted by a task of its own: once the file holds an encoding id.
    /// An emission meets the file's ids as they stand and is renumbered onto them when it lands, and an id new to it at
    /// index 0 is left out of its tree as the default, where no renumbering reaches it; the file's first ids are met by a
    /// chunk written on the writer's flow.
    /// </summary>
    private bool EmitsApart => _arrayEncodings.Count > 0;

    /// <summary>
    /// A chunk of a column that gathers, taken from its gathering as it was due (<see cref="Detach"/>) and emitted by a task
    /// of its own (<see cref="StartEmission"/>): its rows, in a context of their own until compressed, where it lies among
    /// the file's blocks, what its ingest knew of it, the canonical bytes its segment measures the rate against, what
    /// <see cref="Held"/> counts it at; and once compressed, its blob, the ids it met past the file's when it began, and a
    /// shredded column's block bounds.
    /// </summary>
    private sealed class ColumnEmission(VortexFileWriter writer) : CoreUnit
    {
        internal int Field;

        /// <summary>
        /// The arena its rows are read from, at <see cref="Node"/>; null once let go. Its own context when it has one
        /// (<see cref="Owned"/>), records over the transit's buffers that <see cref="Shared"/> keeps, or, compressed on the
        /// writer's flow as it leaves (<see cref="Inline"/>), the arena the chunk lies in.
        /// </summary>
        internal CanonicalArena? Arena;

        /// <inheritdoc cref="Arena"/>
        internal ScanContext? Owned;

        /// <inheritdoc cref="Arena"/>
        internal SharedRows? Shared;

        /// <summary>Whether it is compressed on the writer's flow as it leaves rather than by a unit of the core.</summary>
        internal bool Inline;

        /// <summary>Whether its segment measures its column's rate as it lands (<see cref="Gathering.Measures"/>).</summary>
        internal bool Measures;

        internal int Node;
        internal int Rows;
        internal int FirstBlock;
        internal int Blocks;
        internal long Cut;
        internal long Held;

        /// <summary>What the writer reserved for the copy it took as it left (<see cref="RoomAsync"/>), until it is emitted.</summary>
        internal long Raised;
        internal string[] Known = [];
        internal int KnownCount;
        internal string[] Fresh = [];
        internal int FreshCount;
        internal ChunkBlockBounds? Bounds;
        internal ArrayBlobWriter.BlobLease Blob;

        /// <summary>What the column's ingest knew of the chunk as it was taken (<see cref="ColumnWriter.Freeze"/>); null once landed.</summary>
        internal ChunkSnapshot? Stats;

        /// <summary>Whether it was handed to the core, to be waited for before it is written: offered, or due to be by the emission before it.</summary>
        internal bool Offered;

        /// <summary>
        /// While it goes on in a piece of the core past its unit: the lane a shredded chunk's rows were taken in and its blob is
        /// grafted in, or a column's lane its frames' last item writes it in; the shredding; the piece and what it completes.
        /// </summary>
        internal CompressionLane? Lane;

        /// <inheritdoc cref="Lane"/>
        internal VariantShredder.Shredding? Shredding;

        /// <inheritdoc cref="Lane"/>
        internal CoreBatch? Batch;

        /// <inheritdoc cref="Lane"/>
        internal Task? Piece;

        /// <summary>The items of a shredded chunk's piece, each a leaf of its blob written apart; made with its first.</summary>
        internal LeafWork? Work;

        /// <summary>
        /// The items of a column's piece, each a run of its zstd frames, the last to end writing the column (<see cref="WriteFramed"/>):
        /// what they read, the column's node in its lane, how many are left, and whether one failed.
        /// </summary>
        internal FrameWork? Frames;

        /// <inheritdoc cref="Frames"/>
        internal int Local;

        /// <inheritdoc cref="Frames"/>
        internal int FramesLeft;

        /// <inheritdoc cref="Frames"/>
        internal bool FramesFailed;

        /// <summary>What failed the first of the frames' items to fail, thrown by the last to end once the column ended.</summary>
        internal Exception? FramesError;

        /// <summary>
        /// Its first rows taken block by block on the writer's line (<see cref="ShredChain"/>), up to where its chain broke,
        /// the rest after them (<see cref="TakenChunk.Rest"/>); null when they are taken here.
        /// </summary>
        internal TakenChunk? Taken;

        /// <summary>
        /// The column's next emission, which waits for this one's memory of its plans: null while none waits and this one
        /// runs, <see cref="ChainEnded"/> once it ended, which then offers the one waiting.
        /// </summary>
        internal ColumnEmission? Then;

        protected override void Execute() => writer.Compress(this);
    }

    /// <summary>
    /// A leaf of a shredded chunk of column <see cref="Field"/> written apart (<see cref="VariantShredder.Shredding.WriteLeafApart"/>),
    /// an item of the piece the chunk's leaves run as: in a lane of its own, taken for the leaf and given back after it.
    /// </summary>
    private sealed class LeafWork(VortexFileWriter writer) : IBatchWork
    {
        /// <summary>The shredding whose leaves the piece writes.</summary>
        internal VariantShredder.Shredding? Shredding;

        /// <summary>The column the chunk is of, whose zstd level its leaves compress at.</summary>
        internal int Field;

        public void Run(CoreBatch batch, int item)
        {
            CompressionLane lane = CompressionLane.Take(writer);
            try
            {
                lane.Blobs.ColumnLevel = writer.ZstdLevelOf(Field);
                Shredding!.WriteLeafApart(item, lane.Blobs, lane.Encodings, writer._compress, writer.KeepsBlocks);
            }
            finally
            {
                lane.Blobs.ColumnLevel = 0;
                lane.Give();
            }
        }
    }

    /// <summary>
    /// A run of the zstd frames of a column's chunk, an item of its emission's piece (<see cref="ZstdPlan.Ahead.Run"/>): the
    /// item that ends last writes the column, its trial taking the frames.
    /// </summary>
    private sealed class FrameWork(VortexFileWriter writer, ColumnEmission chunk) : IBatchWork
    {
        public void Run(CoreBatch batch, int item)
        {
            try
            {
                // Past a failed item, the frames are of a column that will not be written: the item only counts itself out.
                if (!Volatile.Read(ref chunk.FramesFailed))
                {
                    writer._emissionFrames![chunk.Field]!.Run(item);
                }
            }
            catch (Exception error)
            {
                // Kept, not thrown: an item that throws makes the piece leave the items not claimed yet, which never counted
                // themselves out, so that the column was never ended and its lane never given back.
                Interlocked.CompareExchange(ref chunk.FramesError, error, null);
                Volatile.Write(ref chunk.FramesFailed, true);
            }
            finally
            {
                if (Interlocked.Decrement(ref chunk.FramesLeft) == 0)
                {
                    writer.WriteFramed(chunk);
                }
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="shredding"/>'s chunk of column <paramref name="field"/>, its rows taken, as one blob in
    /// <paramref name="blobs"/> against <paramref name="encodings"/>: its leaves written apart as items of a piece of the
    /// core, the caller's flow running its share, then the blob grafted from them, the bytes of the blob written in one go.
    /// </summary>
    private async ValueTask<ArrayBlobWriter.BlobLease> WriteLeavesAsync(
        VariantShredder.Shredding shredding, int field, ArrayBlobWriter.Workspace blobs, EncodingDictionary encodings, CoreBatch fan)
    {
        int leaves = shredding.PrepareLeaves(blobs.EncodesValidity, _compress, encodings.Target);
        LeafWork work = _leafWork ??= new LeafWork(this);
        work.Shredding = shredding;
        work.Field = field;
        try
        {
            await fan.RunAsync(work, leaves).ConfigureAwait(false);
        }
        catch
        {
            shredding.ReleaseLeaves();
            throw;
        }
        finally
        {
            work.Shredding = null;
        }

        ChunksGrafted++;
        return shredding.WriteGrafted(blobs, encodings, _compress);
    }

    /// <summary>
    /// Takes what column <paramref name="field"/> gathered, its target reached, as a chunk to emit apart
    /// (<see cref="StartEmission"/>), with what its ingest knew of it: the column gathers again in a context of its own.
    /// Touches the column's gathering and ingest state alone, so that the column's item of a chunk across the writer's
    /// threads takes it.
    /// </summary>
    private ColumnEmission Detach(int field, Gathering gathering, bool due = true, long raised = 0)
    {
        ScanContext context = gathering.Context!;
        int rows = checked((int)gathering.Rows);

        // A shredded chunk's first rows taken block by block on the line, up to where its chain broke, if it did.
        ShredChain? chain = _chains is { Length: > 0 } chains ? chains[field] : null;
        TakenChunk? taken = chain?.Hand(gathering.Generation, gathering.Rows);
        if (chain is not null)
        {
            chain.ChunksThin += gathering.ThinNodes > 0 ? 1 : 0;
            chain.ChunksInPart += taken is not null && taken.Rows < rows ? 1 : 0;
        }

        int rest = -1;
        int node = taken is not null || gathering.ThinNodes > 0
            ? TakenNode(field, gathering, context, rows, taken, out rest)
            : gathering.Nodes.Count == 1
            ? gathering.Nodes[0]
            : CanonicalConcat.Concat(
                context.Decode, ChunkType(field), rows, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gathering.Nodes));
        ColumnEmission chunk = TakeEmission(field);
        chunk.Field = field;
        chunk.Arena = context.Canonical;
        chunk.Owned = context;
        chunk.Inline = ProbesWatch;
        chunk.Node = node;
        chunk.Rows = rows;
        chunk.FirstBlock = gathering.FirstBlock;
        chunk.Blocks = gathering.Blocks;
        chunk.Cut = gathering.Bytes;
        chunk.Measures = gathering.Measures(due);

        // What its ingest knew of it, read by its emission while the column ingests the blocks after it, and counted as a
        // chunk compressed on the writer's flow counts it.
        chunk.Stats = _columns[field].Freeze(gathering.FirstBlock, gathering.Blocks);
        if (_shredding?[field] is null && !Codes(field))
        {
            Tally(new ChunkStats(chunk.Stats, this), context.Canonical, node, rows);
        }

        // What it holds counts toward the writer's ceiling until it is written (Uncount), at a point of the rows' own order, so
        // that the ceiling decides the same whatever the lanes' pace. Left off the ceiling, the chunks compressing ahead took
        // the writer past its price, and a consolidation was refused twice and narrowed to 8 inputs; written at a fixed
        // block half a chunk after this one, once (2026-11-17).
        chunk.Held = gathering.Held;
        chunk.Raised = raised;

        if (taken is not null)
        {
            taken.Rest = rest;
        }

        chunk.Taken = taken;
        gathering.Generation++;
        gathering.Context = null;
        gathering.Nodes.Clear();
        gathering.ThinNodes = 0;
        gathering.Rows = 0;
        gathering.Held = 0;
        gathering.Bytes = 0;
        gathering.Blocks = 0;
        return chunk;
    }

    /// <summary>
    /// The node of a shredded chunk whose first rows its chain took on the line, <paramref name="taken"/>: every row's
    /// metadata and validity, each of its gathered nodes thinned (<see cref="Thin"/>); and in <paramref name="rest"/> the
    /// rows past those taken, whole, which its compression takes (<see cref="Compress"/>), -1 for none.
    /// </summary>
    /// <exception cref="InvalidOperationException">A node gathered thin lies past the rows taken.</exception>
    private int TakenNode(int field, Gathering gathering, ScanContext context, int rows, TakenChunk? taken, out int rest)
    {
        CanonicalArena arena = context.Canonical;
        Span<int> nodes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gathering.Nodes);
        int took = taken?.Rows ?? 0;

        // The node row `took` falls in, and where that node begins.
        int at = 0;
        int start = 0;
        while (at < nodes.Length && start + arena.GetNode(nodes[at]).Length <= took)
        {
            start += arena.GetNode(nodes[at]).Length;
            at++;
        }

        if (gathering.ThinNodes > at)
        {
            throw new InvalidOperationException(
                $"Column {field} gathered {gathering.ThinNodes} nodes thin, past the {took} rows of {rows} its chain took.");
        }

        // The rest whole, the node it begins in cut where it begins, then given back its rows to be thinned with the others.
        rest = -1;
        if (took < rows)
        {
            int first = nodes[at];
            int length = arena.GetNode(first).Length;
            if (took > start)
            {
                nodes[at] = Layouts.CanonicalSlice.Slice(context.Decode, first, took - start, length - (took - start));
            }

            rest = nodes.Length - at == 1 ? nodes[at] : CanonicalConcat.Concat(context.Decode, ChunkType(field), rows - took, nodes[at..]);
            nodes[at] = first;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = Thin(arena, arena, nodes[i]);
        }

        return nodes.Length == 1 ? nodes[0] : CanonicalConcat.Concat(context.Decode, ChunkType(field), rows, nodes);
    }

    /// <summary>
    /// Emits <paramref name="chunk"/>, taken from its column's gathering (<see cref="Detach"/>), as a unit of the core: a pump
    /// compresses it while the blocks after it go through, or the writer's flow does as it writes the chunk; inline on one
    /// lane. Its blob is written when its column's chunk after next is due, or at a flush, the end of a group or the
    /// completion (<see cref="LandEmissionsAsync(int, CancellationToken, int)"/>), the column's chunks before it first. A
    /// column with a memory of its plans emits its chunks one after another, each offered as the one before ends; a
    /// shredded column's, which has none, at once.
    /// </summary>
    /// <remarks>
    /// A chunk compressed on the writer's flow kept every block after it waiting: a shredded variant's, 19.9 s of a backlog's
    /// 31.5 s of writing at degree 14. The blob is written at the same place in the file whatever lane compressed it and
    /// whenever it ended, and the plan a column's chunk is chosen under is the one its chunk before left: the file is the
    /// same at any degree. Compressed against the file's ids as they stand, it makes those it met past them the file's in
    /// the order it met them when it lands, as a chunk across the writer's threads does.
    /// </remarks>
    private void StartEmission(ColumnEmission chunk)
    {
        int field = chunk.Field;
        ((_emissions ??= new List<ColumnEmission>?[_fieldCount])[field] ??= []).Add(chunk);
        ChunksEmittedApart++;
        int known = _arrayEncodings.Count;
        if (chunk.Known.Length < known)
        {
            chunk.Known = new string[Math.Max(known, chunk.Known.Length * 2)];
        }

        for (int i = 0; i < known; i++)
        {
            chunk.Known[i] = _arrayEncodings.Ids[i];
        }

        chunk.KnownCount = known;
        if (_shredding?[field] is not null)
        {
            chunk.Bounds = chunk.Taken is { } taken ? taken.Bounds : EmissionBoundsFor(field);
        }

        if (_lanes <= 1 || chunk.Inline)
        {
            chunk.Inline = true;
            Compress(chunk);
            CloseRaised(chunk);
            return;
        }

        chunk.Offered = true;
        ColumnChunksAcross++;
        if (_shredding?[field] is null)
        {
            // After the column's emission before, which offers it as it ends; at once when it did.
            ColumnEmission?[] last = _lastEmission ??= new ColumnEmission?[_fieldCount];
            ColumnEmission? previous = last[field];
            last[field] = chunk;
            if (previous is not null && Interlocked.CompareExchange(ref previous.Then, chunk, null) is null)
            {
                return;
            }
        }

        chunk.Offer(_core, Flow.Write);
    }

    /// <summary>
    /// <see cref="BoundsFor"/> for a shredded chunk emitted apart, which counts into bounds of its own while the column's
    /// other chunks in emission count into theirs: one of the column's free ones, or new; given back once its blob is written.
    /// </summary>
    private ChunkBlockBounds? EmissionBoundsFor(int field)
    {
        if (_countedKeys?[field] is not { } keys)
        {
            return null;
        }

        List<ChunkBlockBounds> free = FreeBounds(field);
        lock (free)
        {
            if (free.Count > 0)
            {
                ChunkBlockBounds bounds = free[^1];
                free.RemoveAt(free.Count - 1);
                return bounds.Cleared();
            }
        }

        return new ChunkBlockBounds(keys, _blockRows, _variantTextBound).Cleared();
    }

    /// <summary>
    /// The block bounds column <paramref name="field"/>'s chunks landed gave back, for the next to count into: taken by an
    /// emission as it starts, or by the chain that takes its rows on the line (<see cref="ShredChain"/>), in the line's
    /// prologues; given back as a chunk lands. Under its own lock.
    /// </summary>
    private List<ChunkBlockBounds> FreeBounds(int field)
    {
        List<ChunkBlockBounds>?[] all = _emissionBounds ?? Interlocked.CompareExchange(ref _emissionBounds, new List<ChunkBlockBounds>?[_fieldCount], null) ?? _emissionBounds;
        return all[field] ?? Interlocked.CompareExchange(ref all[field], [], null) ?? all[field]!;
    }

    /// <summary>
    /// Compresses <paramref name="chunk"/> into its blob, on whichever thread runs it, and lets its rows go. On more than one
    /// lane, a shredded chunk's rows are taken here, or were on the line, and each leaf of its blob, the rows' metadata, a
    /// key's values or its typed column, is written apart as an item of a piece of the core, beside the others, the writer's
    /// flow awaiting the piece as the chunk lands, then grafting the blob from them (<see cref="EmittedAsync"/>).
    /// </summary>
    /// <remarks>
    /// A shredded chunk wrote its leaves one after another, hundreds of keys of a log's attributes, close to a core of serial
    /// work a column (<c>PLAN-COMPACTION-SPEED.md</c>). The blob grafted is the bytes of the blob written in one go.
    /// </remarks>
    private void Compress(ColumnEmission chunk)
    {
        if (_shredding?[chunk.Field] is null)
        {
            CompressColumn(chunk);
            return;
        }

        CompressionLane lane = CompressionLane.Take(this);
        bool apart = false;
        try
        {
            lane.Encodings.Reset(chunk.Known.AsSpan(0, chunk.KnownCount));
            int local = lane.Arena.ReferenceFrom(chunk.Arena!, chunk.Node);
            if (_mayShareChildren)
            {
                local = ChunkCompactor.Compact(lane.Arena, local);
            }

            lane.Blobs.ColumnLevel = ZstdLevelOf(chunk.Field);

            // Its rows taken on the line block by block, the rest after them here, or every row here at once.
            VariantShredder.Shredding shredding;
            if (chunk.Taken is { } taken)
            {
                shredding = taken.Shredding;
                shredding.Settle(lane.Arena, local);
                if (taken.Rest >= 0)
                {
                    int rest = lane.Arena.ReferenceFrom(chunk.Arena!, taken.Rest);
                    if (_mayShareChildren)
                    {
                        rest = ChunkCompactor.Compact(lane.Arena, rest);
                    }

                    VariantShredder.Shredding.Rows more = VariantShredder.Shredding.Prepare(lane.Arena, rest);
                    shredding.TakeMore(in more, taken.Rows);
                }

                Interlocked.Increment(ref _chunksTakenOnLine);
            }
            else
            {
                shredding = lane.Blobs.Shredding(_shredding![chunk.Field]!);
                shredding.Take(lane.Arena, local, chunk.Bounds);
            }

            if (chunk.Inline)
            {
                chunk.Blob = shredding.Write(lane.Blobs, lane.Encodings, _compress);
                NoteFresh(chunk, lane);
                return;
            }

            int leaves = shredding.PrepareLeaves(lane.Blobs.EncodesValidity, _compress, lane.Encodings.Target);
            chunk.Lane = lane;
            chunk.Shredding = shredding;
            chunk.Batch = CoreBatch.Rent(_core, LeavesAtOnce(chunk, shredding, leaves));
            apart = true;

            // The piece is awaited, and the blob grafted, by the writer's flow as the chunk lands (EmittedAsync): its value
            // task taken here as a task, once, a task a chunk of many blocks.
            LeafWork work = chunk.Work ??= new LeafWork(this);
            work.Shredding = shredding;
            work.Field = chunk.Field;
            chunk.Piece = chunk.Batch.RunAsync(work, leaves).AsTask();
        }
        catch
        {
            if (apart)
            {
                DropPiece(chunk);
            }

            throw;
        }
        finally
        {
            if (!apart)
            {
                lane.Blobs.ColumnLevel = 0;
                lane.Give();
                Release(chunk);
                ReturnTaken(chunk);
            }
        }
    }

    /// <summary>
    /// How many of the <paramref name="leaves"/> of a shredded chunk are written at once: as many as the writer has lanes,
    /// for a writer on its own. A job's writer reserves first what the leaves it writes at once lay out, the largest of
    /// them each at its own size (<see cref="VariantShredder.Shredding.LargestLeaves"/>): as many leaves never lay out
    /// more than as many of the largest. Without waiting, a lane's way, it writes at once as many as the job admitted
    /// and the budget held: one at least, which is its least, the others taking their turn in the piece. What it
    /// reserved goes back as the chunk's emission ends (<see cref="CloseRaised"/>).
    /// </summary>
    /// <remarks>
    /// Offered all at once to fourteen lanes, the leaves of a chunk of a log's attributes laid out 84 MB at the same time,
    /// in 17 blocks, of the 339 MiB a consolidation's writer held for a share of 269 (2026-11-21). Each was then reserved
    /// at what the largest of them lays out, a bound and not its size, until 2026-11-22. The leaves are grafted in the
    /// order the blob comes to them, whenever each was written: the chunk's bytes are the same at any width.
    /// </remarks>
    private int LeavesAtOnce(ColumnEmission chunk, VariantShredder.Shredding shredding, int leaves)
    {
        if (_room is not { } room || Account.Reserve is not { } reserve)
        {
            return _lanes;
        }

        int most = Math.Min(Math.Min(_lanes, MostLeavesReserved), Math.Max(leaves, 1));
        Span<long> largest = stackalloc long[MostLeavesReserved];
        int listed = shredding.LargestLeaves(largest[..most]);
        int width = 0;
        long raised = 0;
        while (width < most)
        {
            // The leaves past those listed lay out nothing, and take a lane without a reservation.
            long each = width < listed ? largest[width] : 0;
            if (each != 0 && !(room.Admits(each) && reserve.TryRaise(each)))
            {
                break;
            }

            raised += each;
            width++;
        }

        chunk.Raised += raised;
        return Math.Max(width, 1);
    }

    /// <summary>The most leaves of a chunk a job's writer reserves for and writes at once (<see cref="LeavesAtOnce"/>): the lanes of a large machine.</summary>
    private const int MostLeavesReserved = 64;

    /// <summary>
    /// Compresses <paramref name="chunk"/> of a column that is not shredded, under what its ingest knew of it: its zstd frames
    /// watched for its trial, and compressed beside it as items of a piece of the core when the column's last trial priced
    /// zstd on its values, the item that ends last then writing the column (<see cref="WriteFramed"/>). The column's next
    /// emission is offered once this one ended, whatever became of it.
    /// </summary>
    private void CompressColumn(ColumnEmission chunk)
    {
        CompressionLane? lane = null;
        bool apart = false;
        try
        {
            lane = CompressionLane.Take(this);
            CanonicalArena rows = chunk.Arena!;
            lane.Encodings.Reset(chunk.Known.AsSpan(0, chunk.KnownCount));
            int local = lane.Arena.ReferenceFrom(rows, chunk.Node);
            if (_mayShareChildren)
            {
                local = ChunkCompactor.Compact(lane.Arena, local);
            }

            lane.Blobs.ColumnLevel = ZstdLevelOf(chunk.Field);
            ZstdPlan.Ahead? frames = Codes(chunk.Field)
                ? null
                : (_emissionFrames ??= new ZstdPlan.Ahead?[_fieldCount])[chunk.Field] ??= new ZstdPlan.Ahead();
            int items = frames?.Plan(rows, chunk.Node, _rowBlock, LevelOf(chunk.Field), chunk.Inline ? 1 : _lanes) ?? 0;
            if (items > 0)
            {
                chunk.Lane = lane;
                chunk.Local = local;
                chunk.FramesLeft = items;
                chunk.FramesFailed = false;
                chunk.FramesError = null;
                chunk.Batch = CoreBatch.Rent(_core, _lanes);
                apart = true;
                FrameWork work = chunk.Frames ??= new FrameWork(this, chunk);
                chunk.Piece = chunk.Batch.RunAsync(work, items).AsTask();
                return;
            }

            WriteColumn(chunk, lane, local);
        }
        finally
        {
            if (!apart)
            {
                EndColumn(chunk, lane);
            }
        }
    }

    /// <summary>Writes <paramref name="chunk"/>'s column once its frames' items ended, in the lane its unit prepared; nothing more when one failed.</summary>
    private void WriteFramed(ColumnEmission chunk)
    {
        CompressionLane lane = chunk.Lane!;
        chunk.Lane = null;
        try
        {
            if (!Volatile.Read(ref chunk.FramesFailed))
            {
                WriteColumn(chunk, lane, chunk.Local);
            }
        }
        finally
        {
            EndColumn(chunk, lane);
        }

        // The column ended, what failed a frame fails the piece, as the item that failed threw it before.
        if (Interlocked.Exchange(ref chunk.FramesError, null) is { } error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }
    }

    /// <summary>Writes <paramref name="chunk"/>'s column, node <paramref name="local"/> of <paramref name="lane"/>'s arena, into its blob.</summary>
    private void WriteColumn(ColumnEmission chunk, CompressionLane lane, int local)
    {
        // A column of an object dictionary's codes gathered its codes, which the statistics of its values do not describe.
        ChunkStats stats = chunk.Stats is { } frozen && !Codes(chunk.Field) ? new ChunkStats(frozen, this, keeps: !chunk.Measures) : default;
        ZstdPlan.Ahead? frames = _emissionFrames?[chunk.Field];
        lane.Blobs.Ahead = frames;
        try
        {
            chunk.Blob = ArrayBlobWriter.Write(lane.Blobs, lane.Arena, local, lane.Encodings, _compress, stats);
            NoteFresh(chunk, lane);
            if (frames is { Taken: true })
            {
                Interlocked.Increment(ref _columnChunksFramedAhead);
            }
        }
        finally
        {
            lane.Blobs.Ahead = null;
        }
    }

    /// <summary>Lets go of what <paramref name="chunk"/>'s column held to compress, then offers the column's next emission.</summary>
    private void EndColumn(ColumnEmission chunk, CompressionLane? lane)
    {
        try
        {
            if (lane is not null)
            {
                lane.Blobs.ColumnLevel = 0;
                lane.Give();
            }

            _emissionFrames?[chunk.Field]?.Finish();
            Release(chunk);
        }
        finally
        {
            if (Interlocked.Exchange(ref chunk.Then, ChainEnded) is { } then)
            {
                then.Offer(_core, Flow.Write);
            }
        }
    }

    /// <summary>
    /// The blob of <paramref name="chunk"/> grafted from its leaves written apart, its lane given back and its rows let go:
    /// once every leaf is written, the piece awaited (<see cref="EmittedAsync"/>).
    /// </summary>
    private void Graft(ColumnEmission chunk)
    {
        CompressionLane lane = chunk.Lane!;
        try
        {
            chunk.Blob = chunk.Shredding!.WriteGrafted(lane.Blobs, lane.Encodings, _compress);
            NoteFresh(chunk, lane);
            ChunksGrafted++;
        }
        finally
        {
            DropPiece(chunk);
        }
    }

    /// <summary>
    /// Lets go of what <paramref name="chunk"/>'s piece holds, done or given up: the piece, and a shredded chunk's leaves, lane
    /// and rows; a column's were let go as its frames' last item ended.
    /// </summary>
    private void DropPiece(ColumnEmission chunk)
    {
        if (chunk.Batch is { } batch)
        {
            chunk.Batch = null;
            CoreBatch.Return(batch);
        }

        chunk.Piece = null;
        if (chunk.Lane is not { } lane)
        {
            ReturnTaken(chunk);
            return;
        }

        chunk.Shredding?.ReleaseLeaves();
        if (chunk.Work is { } work)
        {
            work.Shredding = null;
        }

        chunk.Shredding = null;
        chunk.Lane = null;
        lane.Blobs.ColumnLevel = 0;
        lane.Give();
        Release(chunk);
        ReturnTaken(chunk);
    }

    /// <summary>Gives back what <paramref name="chunk"/>'s rows taken on the line held, once: written or given up.</summary>
    private void ReturnTaken(ColumnEmission chunk)
    {
        if (chunk.Taken is { } taken)
        {
            chunk.Taken = null;
            _chains![chunk.Field]!.Return(taken);
        }
    }

    /// <summary>Notes the ids <paramref name="chunk"/>'s blob met past the file's as it began, which its landing makes the file's.</summary>
    private static void NoteFresh(ColumnEmission chunk, CompressionLane lane)
    {
        int fresh = lane.Encodings.Count - chunk.KnownCount;
        if (chunk.Fresh.Length < fresh)
        {
            chunk.Fresh = new string[fresh];
        }

        for (int i = 0; i < fresh; i++)
        {
            chunk.Fresh[i] = lane.Encodings.Ids[chunk.KnownCount + i];
        }

        chunk.FreshCount = fresh;
    }

    /// <summary>
    /// Waits for <paramref name="chunk"/>'s emission: its unit, run here when no pump took it yet, then the piece it goes on
    /// in, a shredded chunk's blob grafted from its leaves. What failed it is thrown, its piece let go.
    /// </summary>
    private async ValueTask EmittedAsync(ColumnEmission chunk)
    {
        try
        {
            if (chunk.Offered)
            {
                chunk.Offered = false;
                await chunk.RunOrWaitAsync(_core).ConfigureAwait(false);
            }

            if (chunk.Piece is not { } piece)
            {
                return;
            }

            try
            {
                await piece.ConfigureAwait(false);
            }
            catch
            {
                DropPiece(chunk);
                throw;
            }

            if (chunk.Shredding is not null)
            {
                Graft(chunk);
            }
            else
            {
                DropPiece(chunk);
            }
        }
        finally
        {
            // Its copy is taken, or it never will be: what was reserved for it and no block took goes back.
            CloseRaised(chunk);
        }
    }

    /// <summary>Gives back what was reserved for <paramref name="chunk"/>'s copy and no block took, once.</summary>
    private void CloseRaised(ColumnEmission chunk)
    {
        if (chunk.Raised > 0)
        {
            Account.Reserve?.Close(chunk.Raised);
            chunk.Raised = 0;
        }
    }

    /// <summary>
    /// Makes room for <paramref name="bytes"/> the writer is about to take, a block's copy or a chunk's as it leaves for
    /// its emission, on the flow that drives the writer, the one place where it can wait: while the work the writer is
    /// part of does not admit them past what the writer holds (<see cref="Writing.WriteRoom.Admits"/>), the chunks
    /// in emission end, the oldest first, each column's in their order. The writer then reserves the bytes
    /// (<see cref="Raise"/>), so that what the work beside it reserves meanwhile leaves them.
    /// </summary>
    /// <remarks>
    /// A wait changes nothing of the file: a chunk's blob is written at a point of the rows' own order
    /// (<see cref="LandEmissionAsync"/>), whenever its emission ended. Once nothing is left in emission the writer goes on
    /// all the same, on what it holds at least; and what a lane of it takes past what was reserved, a shredding's copies,
    /// an encoder's output, is asked of the budget as it is taken, the next wait here making up for it.
    /// </remarks>
    private ValueTask RoomAsync(long bytes) =>
        _room is { } room && !room.Admits(bytes) ? EmissionsEndAsync(room, bytes) : ValueTask.CompletedTask;

    /// <summary>
    /// Waits for the chunks in emission to end, none written before its place in the file: what a work short of room asks
    /// of its writer once its blocks in flight landed (<see cref="LandAsync"/>), for the rows and the workspaces their
    /// compression holds.
    /// </summary>
    internal ValueTask EmissionsEndAsync() =>
        _emissions is null ? ValueTask.CompletedTask : EmissionsEndAsync(null, 0);

    /// <summary>
    /// <see cref="RoomAsync"/>, once the work does not admit <paramref name="bytes"/>: the chunks in emission end until it
    /// does, then the work gives back what it started ahead of the writer (<see cref="Writing.WriteRoom.MakeRoomAsync"/>).
    /// Without a <paramref name="room"/>, every chunk in emission ends.
    /// </summary>
    private async ValueTask EmissionsEndAsync(Writing.WriteRoom? room, long bytes)
    {
        for (int field = 0; _emissions is not null && field < _emissions.Length; field++)
        {
            // By index: an emission that ends offers its column's next one, and takes none off the list.
            for (int i = 0; _emissions[field] is { } chunks && i < chunks.Count; i++)
            {
                await EmittedAsync(chunks[i]).ConfigureAwait(false);
                if (room is not null && room.Admits(bytes))
                {
                    return;
                }
            }
        }

        if (room is not null)
        {
            await room.MakeRoomAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a column's chunk copies as it leaves for its emission (<see cref="Detach"/>): its nodes into one, what the
    /// column gathered once more; nothing for a chunk of one node, or for one gathered thin, its rows taken by its chain.
    /// </summary>
    private static long CopiedLeaving(Gathering gathering) =>
        gathering.Nodes.Count > 1 && gathering.ThinNodes == 0 ? gathering.Held : 0;

    /// <summary>
    /// Reserves <paramref name="bytes"/> the writer is about to take, when its account reserves
    /// (<see cref="Buffers.WorkAccount.Reserve"/>) and the budget holds them now: what it reserved, to give back once the copy is
    /// taken (<see cref="MemoryReserve.Close"/>); 0 for none, the copy then asking the budget as it is taken.
    /// </summary>
    private long Raise(long bytes) =>
        bytes > 0 && Account.Reserve is { } reserve && reserve.TryRaise(bytes) ? bytes : 0;

    /// <summary>
    /// Lets <paramref name="chunk"/>'s rows go, once: its context back to the process's, its hold on the transit it reads
    /// through let go, its tables back to its column.
    /// </summary>
    private void Release(ColumnEmission chunk)
    {
        chunk.Stats?.GiveTables();
        chunk.Arena = null;
        if (chunk.Owned is { } context)
        {
            chunk.Owned = null;
            TransitContexts.Return(context);
        }

        if (chunk.Shared is { } shared)
        {
            chunk.Shared = null;
            shared.Release();
        }
    }

    /// <summary>Takes what <paramref name="chunk"/> held off the writer's ceiling, once: as it is written, or given up.</summary>
    private void Uncount(ColumnEmission chunk)
    {
        Interlocked.Add(ref _gatheredHeld, -chunk.Held);
        chunk.Held = 0;
    }

    /// <summary>
    /// Writes column <paramref name="field"/>'s chunks in emission, oldest first, but the last <paramref name="keep"/>: each
    /// once emitted, its new ids made the file's in the order it met them and its tree renumbered onto the file's indices,
    /// what its emission decided handed back to its column; nothing when none waits. A column whose next chunk is due keeps
    /// <see cref="EmissionDepth"/> less one of them in emission, a flush or the end none.
    /// </summary>
    private async ValueTask LandEmissionsAsync(int field, CancellationToken cancellationToken, int keep = 0)
    {
        while (_emissions?[field] is { } chunks && chunks.Count > keep)
        {
            ColumnEmission chunk = chunks[0];
            chunks.RemoveAt(0);
            await LandEmissionAsync(field, chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes <paramref name="chunk"/>, column <paramref name="field"/>'s oldest chunk in emission (<see cref="LandEmissionsAsync(int, CancellationToken, int)"/>).</summary>
    private async ValueTask LandEmissionAsync(int field, ColumnEmission chunk, CancellationToken cancellationToken)
    {
        Uncount(chunk);
        try
        {
            await EmittedAsync(chunk).ConfigureAwait(false);
        }
        catch
        {
            chunk.Blob.Dispose();
            throw;
        }

        ArrayBlobWriter.BlobLease blob = chunk.Blob;
        if (chunk.FreshCount > 0)
        {
            int count = chunk.KnownCount + chunk.FreshCount;
            Span<ushort> map = count <= 128 ? stackalloc ushort[128] : new ushort[count];
            bool same = true;
            for (int i = 0; i < chunk.KnownCount; i++)
            {
                map[i] = (ushort)i;
            }

            for (int i = 0; i < chunk.FreshCount; i++)
            {
                ushort index = _arrayEncodings.Intern(chunk.Fresh[i]);
                map[chunk.KnownCount + i] = index;
                same &= index == chunk.KnownCount + i;
            }

            if (!same)
            {
                ArrayBlobWriter.Renumber(in blob, map[..count]);
                ColumnChunksRenumbered++;
            }
        }

        // A column that gathers measures its rate on the chunk; one chunked with the file has none to measure.
        Gathering? gathering = _gathering?[field];
        if (gathering is not null)
        {
            gathering.Cut = chunk.Measures ? chunk.Cut : 0;
        }

        await WriteColumnBlobAsync(field, blob, chunk.FirstBlock, chunk.Blocks, cancellationToken, chunk.Bounds, released: true).ConfigureAwait(false);
        gathering?.ChunkRows.Add(chunk.Rows);

        if (chunk.Bounds is { } bounds)
        {
            // Those a chain made for the rows it took on the line too: whoever made them, the column's next chunk counts into them.
            List<ChunkBlockBounds> free = FreeBounds(field);
            lock (free)
            {
                free.Add(bounds);
            }
        }

        // What its emission decided for the column's ingest takes effect here, at a point of the rows' own order.
        if (chunk.Stats is { } stats)
        {
            chunk.Stats = null;
            _columns[field].Land(stats);
        }

        if (_lastEmission?[field] == chunk)
        {
            _lastEmission[field] = null;
        }

        Spare(chunk);
    }

    /// <summary>Writes every column's chunks in emission, in the columns' order.</summary>
    private async ValueTask LandEmissionsAsync(CancellationToken cancellationToken)
    {
        for (int field = 0; _emissions is not null && field < _emissions.Length; field++)
        {
            await LandEmissionsAsync(field, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for every chunk in emission whatever becomes of it, and gives its blob back: the writer is given up.</summary>
    private async ValueTask SettleEmissionsAsync()
    {
        for (int field = 0; _emissions is not null && field < _emissions.Length; field++)
        {
            if (_emissions[field] is not { } chunks)
            {
                continue;
            }

            // Oldest first: a column's emission offers the one after it as it ends.
            foreach (ColumnEmission chunk in chunks)
            {
                try
                {
                    await EmittedAsync(chunk).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Given up with the writer: its failure has no one to report to.
                }

                DropPiece(chunk);
                Release(chunk);
                Uncount(chunk);
                chunk.Blob.Dispose();
                chunk.Stats = null;
            }

            chunks.Clear();
        }

        if (_lastEmission is { } last)
        {
            Array.Clear(last);
        }
    }
}
