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
    // By column, its chunks taken to emission and not yet written, oldest first, at most EmissionDepth of them; what Held
    // counts them at, the bytes their columns counted them at when they reached their targets; and the block bounds a
    // shredded column's chunks count into, free for the next, by column.
    private List<ColumnEmission>?[]? _emissions;
    private long _emittingBytes;
    private List<ChunkBlockBounds>?[]? _emissionBounds;

    // By column whose emissions follow one another, one with a memory of its plans, the last one started, which the next
    // waits for; and its zstd frames, compressed as items of its emission and kept from one chunk to the next.
    private ColumnEmission?[]? _lastEmission;
    private ZstdPlan.Ahead?[]? _emissionFrames;

    // The leaves of the shredded chunks the writer's own flow writes, reused from one chunk to the next.
    private LeafWork? _leafWork;

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
        internal ScanContext? Context;
        internal int Node;
        internal int Rows;
        internal int FirstBlock;
        internal int Blocks;
        internal long Cut;
        internal long Counted;
        internal long Held;
        internal string[] Known = [];
        internal int KnownCount;
        internal string[] Fresh = [];
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

        /// <summary>Its rows taken block by block on the writer's line (<see cref="ShredChain"/>), every row of it; null when they are taken here.</summary>
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
                Shredding!.WriteLeafApart(item, lane.Blobs, lane.Encodings, writer._compress);
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
                writer._emissionFrames![chunk.Field]!.Run(item);
            }
            catch
            {
                Volatile.Write(ref chunk.FramesFailed, true);
                throw;
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
        int leaves = shredding.PrepareLeaves(blobs.EncodesValidity, _compress);
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
    private ColumnEmission Detach(int field, Gathering gathering)
    {
        ScanContext context = gathering.Context!;
        int rows = checked((int)gathering.Rows);
        int node = gathering.Nodes.Count == 1
            ? gathering.Nodes[0]
            : CanonicalConcat.Concat(
                context.Decode, ChunkType(field), rows, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gathering.Nodes));
        ColumnEmission chunk = new ColumnEmission(this)
        {
            Field = field,
            Context = context,
            Node = node,
            Rows = rows,
            FirstBlock = gathering.FirstBlock,
            Blocks = gathering.Blocks,
            Cut = gathering.Bytes,
            Counted = (long)(gathering.Bytes * gathering.Rate),
        };

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

        // A shredded chunk's rows taken block by block on the line, when they are exactly the chunk's.
        chunk.Taken = _chains is { Length: > 0 } chains ? chains[field]?.Hand(gathering.Generation, gathering.Rows) : null;
        gathering.Generation++;
        gathering.Context = null;
        gathering.Nodes.Clear();
        gathering.Rows = 0;
        gathering.Held = 0;
        gathering.Bytes = 0;
        gathering.Blocks = 0;
        return chunk;
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
        _emittingBytes += chunk.Counted;
        ChunksEmittedApart++;
        int known = _arrayEncodings.Count;
        chunk.Known = new string[known];
        for (int i = 0; i < known; i++)
        {
            chunk.Known[i] = _arrayEncodings.Ids[i];
        }

        chunk.KnownCount = known;
        if (_shredding?[field] is not null)
        {
            chunk.Bounds = chunk.Taken is { } taken ? taken.Bounds : EmissionBoundsFor(field);
        }

        if (_lanes <= 1)
        {
            Compress(chunk);
            return;
        }

        chunk.Offered = true;
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

        List<ChunkBlockBounds> free = (_emissionBounds ??= new List<ChunkBlockBounds>?[_fieldCount])[field] ??= [];
        if (free.Count == 0)
        {
            return new ChunkBlockBounds(keys, _blockRows, _variantTextBound).Cleared();
        }

        ChunkBlockBounds bounds = free[^1];
        free.RemoveAt(free.Count - 1);
        return bounds.Cleared();
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
        ScanContext context = chunk.Context!;
        bool apart = false;
        try
        {
            lane.Encodings.Reset(chunk.Known.AsSpan(0, chunk.KnownCount));
            int local = lane.Arena.ReferenceFrom(context.Canonical, chunk.Node);
            if (_mayShareChildren)
            {
                local = ChunkCompactor.Compact(lane.Arena, local);
            }

            lane.Blobs.ColumnLevel = ZstdLevelOf(chunk.Field);
            if (_lanes <= 1)
            {
                chunk.Blob = VariantShredder.Write(
                    lane.Blobs, lane.Arena, local, _shredding![chunk.Field]!, chunk.Bounds, lane.Encodings, _compress);
                NoteFresh(chunk, lane);
                return;
            }

            // Its rows taken on the line block by block, or here at once.
            VariantShredder.Shredding shredding;
            if (chunk.Taken is { } taken)
            {
                shredding = taken.Shredding;
                shredding.Settle(lane.Arena, local);
                Interlocked.Increment(ref _chunksTakenOnLine);
            }
            else
            {
                shredding = lane.Blobs.Shredding(_shredding![chunk.Field]!);
                shredding.Take(lane.Arena, local, chunk.Bounds);
            }

            int leaves = shredding.PrepareLeaves(lane.Blobs.EncodesValidity, _compress);
            chunk.Lane = lane;
            chunk.Shredding = shredding;
            chunk.Batch = CoreBatch.Rent(_core, _lanes);
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
            ScanContext context = chunk.Context!;
            lane.Encodings.Reset(chunk.Known.AsSpan(0, chunk.KnownCount));
            int local = lane.Arena.ReferenceFrom(context.Canonical, chunk.Node);
            if (_mayShareChildren)
            {
                local = ChunkCompactor.Compact(lane.Arena, local);
            }

            lane.Blobs.ColumnLevel = ZstdLevelOf(chunk.Field);
            ZstdPlan.Ahead? frames = Codes(chunk.Field)
                ? null
                : (_emissionFrames ??= new ZstdPlan.Ahead?[_fieldCount])[chunk.Field] ??= new ZstdPlan.Ahead();
            int items = frames?.Plan(context.Canonical, chunk.Node, _rowBlock, LevelOf(chunk.Field), _lanes) ?? 0;
            if (items > 0)
            {
                chunk.Lane = lane;
                chunk.Local = local;
                chunk.FramesLeft = items;
                chunk.FramesFailed = false;
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
    }

    /// <summary>Writes <paramref name="chunk"/>'s column, node <paramref name="local"/> of <paramref name="lane"/>'s arena, into its blob.</summary>
    private void WriteColumn(ColumnEmission chunk, CompressionLane lane, int local)
    {
        // A column of an object dictionary's codes gathered its codes, which the statistics of its values do not describe.
        ChunkStats stats = chunk.Stats is { } frozen && !Codes(chunk.Field) ? new ChunkStats(frozen, this) : default;
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
        chunk.Fresh = new string[fresh];
        for (int i = 0; i < fresh; i++)
        {
            chunk.Fresh[i] = lane.Encodings.Ids[chunk.KnownCount + i];
        }
    }

    /// <summary>
    /// Waits for <paramref name="chunk"/>'s emission: its unit, run here when no pump took it yet, then the piece it goes on
    /// in, a shredded chunk's blob grafted from its leaves. What failed it is thrown, its piece let go.
    /// </summary>
    private async ValueTask EmittedAsync(ColumnEmission chunk)
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

    /// <summary>Lets <paramref name="chunk"/>'s rows go, once: its context back to the process's, its tables back to its column.</summary>
    private void Release(ColumnEmission chunk)
    {
        chunk.Stats?.GiveTables();
        if (chunk.Context is not { } context)
        {
            return;
        }

        chunk.Context = null;
        TransitContexts.Return(context);
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
        _emittingBytes -= chunk.Counted;
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
        if (chunk.Fresh.Length > 0)
        {
            int count = chunk.KnownCount + chunk.Fresh.Length;
            ushort[] map = new ushort[count];
            bool same = true;
            for (int i = 0; i < chunk.KnownCount; i++)
            {
                map[i] = (ushort)i;
            }

            for (int i = 0; i < chunk.Fresh.Length; i++)
            {
                ushort index = _arrayEncodings.Intern(chunk.Fresh[i]);
                map[chunk.KnownCount + i] = index;
                same &= index == chunk.KnownCount + i;
            }

            if (!same)
            {
                ArrayBlobWriter.Renumber(in blob, map);
                ColumnChunksRenumbered++;
            }
        }

        Gathering gathering = _gathering![field]!;
        gathering.Cut = chunk.Cut;
        await WriteColumnBlobAsync(field, blob, chunk.FirstBlock, chunk.Blocks, cancellationToken, chunk.Bounds, released: true).ConfigureAwait(false);
        gathering.ChunkRows.Add(chunk.Rows);
        if (chunk.Bounds is { } bounds)
        {
            _emissionBounds![field]!.Add(bounds);
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

        _emittingBytes = 0;
        if (_lastEmission is { } last)
        {
            Array.Clear(last);
        }
    }
}
