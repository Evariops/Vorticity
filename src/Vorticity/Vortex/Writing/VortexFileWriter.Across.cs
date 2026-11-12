using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Editions;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>A chunk's columns compressed side by side on the writer's threads.</summary>
public sealed partial class VortexFileWriter
{
    /// <summary>The columns of the chunk being written, compressed side by side; made with the first chunk that is.</summary>
    private ChunkColumns? _chunkColumns;

    /// <summary>Column chunks compressed on the writer's threads beside the other columns of their chunk.</summary>
    internal int ColumnChunksAcross { get; private set; }

    /// <summary>Column chunks compressed side by side whose blob was renumbered onto ids the file met first in their chunk.</summary>
    internal int ColumnChunksRenumbered { get; private set; }

    /// <summary>Column chunks whose zstd frames were compressed ahead of their trials, on the writer's threads, and kept.</summary>
    internal int ColumnChunksFramedAhead => Volatile.Read(ref _columnChunksFramedAhead);

    private int _columnChunksFramedAhead;

    /// <summary>
    /// The frames ahead of column <paramref name="field"/>, watching its chunk, node <paramref name="node"/> of
    /// <paramref name="arena"/>, written on this thread: its trial notes whether it prices zstd on the column's values,
    /// for the column's next chunk compressed side by side. Null for a writer that compresses none so.
    /// </summary>
    private ZstdPlan.Ahead? Watch(int field, CanonicalArena arena, int node)
    {
        if (_lanes <= 1 || !_isTabular || _fieldCount <= 1)
        {
            return null;
        }

        ChunkColumns columns = _chunkColumns ??= ChunkColumns.Take(this);
        return columns.Watch(field, arena, node);
    }

    /// <summary>The zstd level column <paramref name="field"/>'s frames are compressed at, as its workspace has it; 0 for the default.</summary>
    private int LevelOf(int field) => ZstdLevelOf(field) is var level and not 0 ? level : _zstdLevel;

    /// <summary>
    /// Whether a chunk's columns are compressed side by side on the writer's threads: a tabular file
    /// of several columns written on several lanes, each column chunked with the file, once the
    /// file holds an encoding id.
    /// </summary>
    /// <remarks>
    /// A file's first chunk is compressed on one thread and interns the file's first ids, so that
    /// every chunk after it starts from a dictionary that is not empty (see
    /// <see cref="EmitColumnsAcrossAsync"/>). The tests' probes that watch every plan the chooser
    /// makes ride the write's flow, which the writer's threads do not carry, so a write a probe
    /// watches compresses on its own thread.
    /// </remarks>
    private bool CompressesAcross =>
        _lanes > 1 && _isTabular && _fieldCount > 1 && (_gathering is null || !_mayShareChildren) && _arrayEncodings.Count > 0 &&
        ColumnCompressor.Differential.Value is null && ArrayBlobWriter.ElementsHanded.Value is null;

    /// <summary>
    /// Writes one chunk of every column, each compressed on whichever of the writer's threads takes
    /// it, then the blobs in column order, each column's new encoding ids made the file's in the
    /// order the column met them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A column's blob names its encodings by index, and an index is the order the file met its id
    /// in: compressed side by side, the columns would meet new ids in whatever order the threads ran,
    /// and a file written on several threads would not be the bytes of one written on one. So each
    /// column is written against a dictionary of its own, which starts as a copy of the file's: an
    /// id the file holds keeps its index, and an id new to it takes the next, never 0. Then, in
    /// column order, a column's new ids are interned into the file's in the order the column met
    /// them, which is the order a write on one thread interns them in, and its blob is renumbered in
    /// place onto the file's indices. FlatBuffers leaves out a field at its default, so only an index
    /// that is not 0 can be rewritten in place; the copy guarantees a new id's index is not 0 on
    /// either side, and a column whose ids the file held needs no rewrite at all.
    /// </para>
    /// <para>
    /// A column is read through records of a thread's own arena that view the chunk's: what the
    /// schemes lay out -- a dictionary's values, a run's ends, a constant expanded -- goes there, and
    /// the chunk's arena is only read while the threads run.
    /// </para>
    /// </remarks>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask EmitColumnsAcrossAsync(
        CanonicalArena arena, int rootIndex, long rows, int blocks, CancellationToken cancellationToken)
    {
        int firstBlock = _emittedBlocks;
        ChunkColumns columns = _chunkColumns ??= ChunkColumns.Take(this);
        columns.Prepare(_fieldCount, _arrayEncodings);
        columns.Spread(checked((int)rows), firstBlock, blocks);
        CanonicalNode root = arena.GetNode(rootIndex);
        for (int field = 0; field < _fieldCount; field++)
        {
            // A list column is narrowed on its thread, in its own arena (see ChunkColumns.Compress):
            // its statistics read only the node's kind and rows, which the narrowing keeps. A column
            // with a chunk target of its own gathers on its thread, and its statistics are taken
            // there, over the rows it gathered, once they reach the target.
            int node = root.GetFieldIndex(field);
            columns.Nodes[field] = node;
            if (_gathering?[field] is { } gathering)
            {
                columns.Gather(field, gathering);
            }
            else
            {
                columns.Stats[field] = ChunkStatsOf(field, arena, node, rows, firstBlock, blocks);
            }
        }

        WorkFan fan = Fan!;
        try
        {
            // A column's frames compressed ahead are items before every column's, and its compression waits for them.
            int ahead = columns.PlanAhead(arena, columns.Nodes, withColumns: true);
            Span<int> items = fan.Items(ahead + _fieldCount);
            columns.LayAhead(items[..ahead]);
            columns.Order(items[ahead..]);
            await fan.RunAsync(columns, ahead + _fieldCount, arena).ConfigureAwait(false);
            await WriteColumnBlobsAsync(columns, firstBlock, blocks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            columns.Drop();
        }
    }

    /// <summary>
    /// Writes what every column with a target of its own holds, at a flush or the completion, the
    /// columns compressed side by side and their blobs written in column order, where a write on
    /// one thread puts them.
    /// </summary>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask EmitGatheredAcrossAsync(Gathering?[] gathering, WorkFan fan, CancellationToken cancellationToken)
    {
        ChunkColumns columns = _chunkColumns ??= ChunkColumns.Take(this);
        columns.Prepare(_fieldCount, _arrayEncodings);
        int count = 0;
        for (int field = 0; field < gathering.Length; field++)
        {
            if (gathering[field] is { Rows: > 0 } column)
            {
                columns.Gather(field, column);
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        try
        {
            columns.Flush();
            Span<int> items = fan.Items(count);
            int next = 0;
            for (int field = 0; field < gathering.Length; field++)
            {
                if (columns.GatheredOf(field) is not null)
                {
                    items[next++] = field;
                }
            }

            await fan.RunAsync(columns, count).ConfigureAwait(false);
            await WriteColumnBlobsAsync(columns, 0, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            columns.Drop();
        }
    }

    /// <summary>
    /// Whether a chunk of <paramref name="rows"/> rows cut whole from a batch is ingested and its
    /// columns compressed in one pass across the writer's threads: when its columns are compressed
    /// side by side, and the batch would be ingested across the threads too.
    /// </summary>
    private bool IngestsWithColumns(int rows) =>
        CompressesAcross && _gathering is null && (_indexes is null or { AsksNothing: true } || _indexes.FeedsAcross)
        && _blockRows > 0 && rows >= _blockRows && Fan is not null;

    /// <summary>
    /// Ingests a chunk cut whole from a batch and compresses its columns in one pass across the
    /// writer's threads: each accumulator of each column is an item, as the ingest across makes
    /// them, and the thread that ends a column's last one compresses the column at once, while the
    /// other columns still ingest.
    /// </summary>
    /// <remarks>
    /// The ingest and the compression were two passes, each waiting for its slowest column: the
    /// threads that had ingested the narrow columns waited for the widest one before any column was
    /// compressed. A column's compression reads only its own statistics, done when its last
    /// accumulator is, so it need not wait for the others. The blobs then go out in column order,
    /// as they do from <see cref="EmitColumnsAcrossAsync"/>, and the file is the same bytes.
    /// </remarks>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask IngestAndEmitAcrossAsync(
        CanonicalArena arena, int rootIndex, int rows, CancellationToken cancellationToken)
    {
        int blocks = ChunkBlocks(rows, _blockRows);
        int firstBlock = _emittedBlocks;
        CanonicalNode root = arena.GetNode(rootIndex);
        for (int field = 0; field < _fieldCount; field++)
        {
            _fieldNodes[field] = root.GetFieldIndex(field);
        }

        ChunkColumns columns = _chunkColumns ??= ChunkColumns.Take(this);
        columns.Prepare(_fieldCount, _arrayEncodings);
        WorkFan fan = Fan!;
        try
        {
            // A column's frames compressed ahead are items before every accumulator, and its compression waits for them too.
            int ahead = columns.PlanAhead(arena, _fieldNodes, withColumns: false);
            Span<int> items = fan.Items(ahead + (_fieldCount * 4));
            columns.LayAhead(items[..ahead]);
            Span<int> ingest = items[ahead..];
            int count = FeedItems(ingest, IngestItems(arena, ingest));
            columns.Fuse(ingest[..count], rows, firstBlock, blocks);
            await fan.RunAsync(columns, ahead + count, arena, rows).ConfigureAwait(false);
            _blockFilled = (_blockFilled + rows) % _blockRows;
            BatchesIngestedAcross++;
            await WriteColumnBlobsAsync(columns, firstBlock, blocks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            columns.Drop();
        }

        await CloseChunkAsync(rows, blocks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the chunk's blobs in column order, each column's new ids made the file's: those of the
    /// columns chunked with the file, and of the columns that gather the chunks of their own their
    /// rows reached the target of, which a write on one thread puts in the same places.
    /// </summary>
    private async ValueTask WriteColumnBlobsAsync(ChunkColumns columns, int firstBlock, int blocks, CancellationToken cancellationToken)
    {
        for (int field = 0; field < _fieldCount; field++)
        {
            if (columns.GatheredOf(field) is not { } gathering)
            {
                if (columns.Flushing)
                {
                    continue;
                }

                ColumnChunksAcross++;
                ArrayBlobWriter.BlobLease blob = columns.Take(field, _arrayEncodings);
                await WriteColumnBlobAsync(field, blob, firstBlock, blocks, cancellationToken, columns.TakeBounds(field)).ConfigureAwait(false);
            }
            else if (columns.Wrote(field))
            {
                ColumnChunksAcross++;
                (int first, int count, int rows) = columns.GatheredChunk(field);
                ArrayBlobWriter.BlobLease blob = columns.Take(field, _arrayEncodings);
                await WriteColumnBlobAsync(field, blob, first, count, cancellationToken, columns.TakeBounds(field)).ConfigureAwait(false);
                gathering.ChunkRows.Add(rows);
            }
        }
    }

    /// <summary>Gives back what the side-by-side compression holds, for the next writer; its lanes are already with the process's spares.</summary>
    private void ReleaseColumnsAcross()
    {
        if (_chunkColumns is { } columns)
        {
            _chunkColumns = null;
            columns.Give();
        }
    }

    /// <summary>
    /// The columns of one chunk, compressed on the writer's threads: what each reads, the blob each
    /// wrote and the ids it met, and how long each took the chunk before.
    /// </summary>
    private sealed class ChunkColumns : IFanWork
    {
        /// <summary>The writer whose chunks the columns are, for the one write that holds them.</summary>
        private VortexFileWriter? _writer;

        private int[] _nodes = [];
        private ChunkStats[] _stats = [];
        private ArrayBlobWriter.BlobLease[] _blobs = [];

        /// <summary>The file's ids when the chunk began, in index order: what every column's dictionary starts from.</summary>
        private string[] _known = new string[32];
        private int _knownCount;

        /// <summary>Per column, the ids its blob names that the file did not hold when the chunk began, in the order it met them.</summary>
        private string[][] _fresh = [];
        private int[] _freshCounts = [];

        /// <summary>Per column, the ticks its compression took the last time it ran.</summary>
        private long[] _took = [];

        // Per column, its items still to run before it is compressed: in an ingest across (see Fuse) its accumulators,
        // and its frames compressed ahead (see PlanAhead), with its own item outside an ingest; 0 for a column compressed
        // as its item comes. In an ingest across, the chunk's rows and blocks its statistics are taken over.
        private int[] _parts = [];
        private bool _fused;
        private int _rows;
        private int _firstBlock;
        private int _blocks;

        // In a chunk with columns that gather rows to a target of their own (see Gather): per
        // column, the gathering its thread adds the chunk's rows to, and when the rows reach the
        // target, the blocks and rows of the chunk of its own it then wrote.
        private Gathering?[] _gathered = [];

        // Per column, the block bounds of its shredded keys in this chunk; null for a writer that counts none.
        private Indexes.ChunkBlockBounds?[]? _bounds;
        private int[] _gatheredFirst = [];
        private int[] _gatheredBlocks = [];
        private int[] _gatheredRows = [];

        // Per column, its zstd frames compressed ahead of its trials, kept from one chunk to the next with whether its
        // trial priced zstd (see ZstdPlan.Ahead).
        private ZstdPlan.Ahead?[] _ahead = [];

        /// <summary>
        /// The bits of an item of frames compressed ahead that number it among its column's: the column is above them,
        /// and the item's number is their complement, below zero, apart from every other item's.
        /// </summary>
        private const int AheadBits = 10;

        /// <summary>Per column, the chunk's node the column is read from.</summary>
        internal Span<int> Nodes => _nodes;

        /// <summary>Per column, its ingest statistics over the chunk.</summary>
        internal Span<ChunkStats> Stats => _stats;

        /// <summary>The columns an earlier writer gave back, or new ones, for <paramref name="writer"/>'s chunks.</summary>
        internal static ChunkColumns Take(VortexFileWriter writer)
        {
            ChunkColumns columns = SpareSlots<ChunkColumns>.Take() ?? new ChunkColumns();
            columns._writer = writer;
            return columns;
        }

        /// <summary>Drops what the write left and keeps the columns, their arrays sized, for the next writer.</summary>
        internal void Give()
        {
            Drop();
            Array.Clear(_stats);
            Array.Clear(_took);
            Array.Clear(_known);
            Array.Clear(_gathered);
            if (_bounds is not null)
            {
                Array.Clear(_bounds);
            }

            foreach (string[] fresh in _fresh)
            {
                Array.Clear(fresh);
            }

            foreach (ZstdPlan.Ahead? ahead in _ahead)
            {
                ahead?.Forget();
            }

            _knownCount = 0;
            _writer = null;
            SpareSlots<ChunkColumns>.Give(this);
        }

        /// <summary>The block bounds of column <paramref name="field"/>'s shredded keys in this chunk, given to the caller; null when it counted none.</summary>
        internal Indexes.ChunkBlockBounds? TakeBounds(int field)
        {
            // The slots outlive their writer in the pool: only a writer that counts keys reads them.
            if (_writer?._countedKeys is null || _bounds is null || field >= _bounds.Length)
            {
                return null;
            }

            Indexes.ChunkBlockBounds? bounds = _bounds[field];
            _bounds[field] = null;
            return bounds;
        }

        /// <summary>Makes room for <paramref name="fields"/> columns and takes the file's ids as the chunk begins.</summary>
        internal void Prepare(int fields, EncodingDictionary file)
        {
            // Only a writer that counts shredded keys block by block holds a slot for their bounds.
            if (_writer?._countedKeys is not null && (_bounds is null || _bounds.Length < fields))
            {
                _bounds = new Indexes.ChunkBlockBounds?[fields];
            }

            Size(fields);
            Array.Clear(_gathered, 0, fields);

            int count = file.Count;
            if (_known.Length < count)
            {
                _known = new string[count * 2];
            }

            for (int i = 0; i < count; i++)
            {
                _known[i] = file.Ids[i];
            }

            _knownCount = count;
        }

        /// <summary>Makes room for <paramref name="fields"/> columns, keeping what the columns hold when there is.</summary>
        private void Size(int fields)
        {
            if (_nodes.Length < fields)
            {
                _nodes = new int[fields];
                _parts = new int[fields];
                _ahead = new ZstdPlan.Ahead?[fields];
                _stats = new ChunkStats[fields];
                _blobs = new ArrayBlobWriter.BlobLease[fields];
                _fresh = new string[fields][];
                _freshCounts = new int[fields];
                _took = new long[fields];
                _gathered = new Gathering?[fields];
                _gatheredFirst = new int[fields];
                _gatheredBlocks = new int[fields];
                _gatheredRows = new int[fields];
                for (int field = 0; field < fields; field++)
                {
                    _fresh[field] = new string[4];
                }
            }
        }

        /// <summary>
        /// Plans, for each column of the chunk compressed here rather than gathered, shredded or coded, the zstd frames
        /// it compresses ahead of its trials, of its node in <paramref name="nodes"/>: those of a column whose trial
        /// priced zstd on its values the chunk before (see <see cref="ZstdPlan.Ahead"/>), and which took more than its
        /// share of the writer's threads. Each column's are counted among the items it waits for, with its own item when
        /// <paramref name="withColumns"/>.
        /// </summary>
        /// <remarks>
        /// A chunk takes at least as long as its longest column, and a column that took more than the chunk's columns
        /// together over the threads is the one that keeps the others waiting: its frames, spread, shorten the chunk.
        /// Those of a column within its share would only fill threads the other columns fill, and their rooms, sized
        /// for each frame's worst case, would hold memory for nothing: a file of many like columns compresses none
        /// ahead, and no more columns than threads ever do. The first chunk across reads what each column took on the
        /// writer's thread (<see cref="Took"/>).
        /// </remarks>
        /// <returns>The items the frames take, for <see cref="LayAhead"/> to lay out.</returns>
        internal int PlanAhead(CanonicalArena chunk, ReadOnlySpan<int> nodes, bool withColumns)
        {
            VortexFileWriter writer = _writer!;
            int fields = writer._fieldCount;
            int lanes = writer._lanes;
            if (lanes <= 1 || fields >= 1 << (31 - AheadBits))
            {
                return 0;
            }

            long total = 0;
            for (int field = 0; field < fields; field++)
            {
                total += _took[field];
            }

            int items = 0;
            for (int field = 0; field < fields; field++)
            {
                if (_gathered[field] is not null || writer._shredding?[field] is not null || writer.Codes(field))
                {
                    continue;
                }

                // Within its share, the column is watched alone, its trial noting what it prices.
                ZstdPlan.Ahead ahead = _ahead[field] ??= new ZstdPlan.Ahead();
                bool longest = _took[field] * lanes > total;
                int planned = ahead.Plan(chunk, nodes[field], writer._rowBlock, writer.LevelOf(field), longest ? lanes : 1);
                if (planned > 0)
                {
                    _parts[field] += planned + (withColumns ? 1 : 0);
                    items += planned;
                }
            }

            return items;
        }

        /// <summary>The items of the frames <see cref="PlanAhead"/> planned, each its column above its number, complemented.</summary>
        internal void LayAhead(Span<int> items)
        {
            int at = 0;
            for (int field = 0; at < items.Length; field++)
            {
                if (_ahead[field] is { Ready: true } ahead)
                {
                    for (int item = 0; item < ahead.Items; item++)
                    {
                        items[at++] = ~((field << AheadBits) | item);
                    }
                }
            }
        }

        /// <summary>
        /// The frames ahead of column <paramref name="field"/> set to watch its chunk, node <paramref name="node"/> of
        /// <paramref name="chunk"/>, written on the writer's thread: its trial notes what it prices, for the column's
        /// next chunk compressed here.
        /// </summary>
        internal ZstdPlan.Ahead Watch(int field, CanonicalArena chunk, int node)
        {
            VortexFileWriter writer = _writer!;
            Size(writer._fieldCount);
            ZstdPlan.Ahead ahead = _ahead[field] ??= new ZstdPlan.Ahead();
            ahead.Plan(chunk, node, writer._rowBlock, writer.LevelOf(field), lanes: 1);
            return ahead;
        }

        /// <summary>Notes the ticks column <paramref name="field"/>'s chunk took to compress on the writer's thread, for the columns' order and their frames ahead.</summary>
        internal void Took(int field, long ticks) => _took[field] = ticks;

        /// <summary>
        /// Lays the columns out in <paramref name="order"/>, longest first, by what each took the
        /// chunk before: the writing thread claims the first while the others wake, and the column
        /// that takes longest is not the one left to start last.
        /// </summary>
        internal void Order(Span<int> order)
        {
            for (int c = 0; c < order.Length; c++)
            {
                int at = c;
                while (at > 0 && _took[order[at - 1]] < _took[c])
                {
                    order[at] = order[at - 1];
                    at--;
                }

                order[at] = c;
            }
        }

        /// <summary>
        /// Lays an ingest's items out for <see cref="IngestAndEmitAcrossAsync"/>: those of the column
        /// that took longest the chunk before first, and counts each column's, the last of which
        /// compresses it.
        /// </summary>
        internal void Fuse(Span<int> items, int rows, int firstBlock, int blocks)
        {
            for (int i = 0; i < items.Length; i++)
            {
                // A column's index builders are fed beside its accumulators, and its compression waits for none of them.
                int entry = items[i];
                _parts[entry >> 3] += (entry & 7) == 0 ? 0 : 1;
                int at = i;
                while (at > 0 && _took[items[at - 1] >> 3] < _took[entry >> 3])
                {
                    items[at] = items[at - 1];
                    at--;
                }

                items[at] = entry;
            }

            _fused = true;
            _rows = rows;
            _firstBlock = firstBlock;
            _blocks = blocks;
        }

        /// <summary>
        /// Gives column <paramref name="field"/> of the chunk to its <paramref name="gathering"/>
        /// rather than compressing it: its thread adds the chunk's rows to the gathering, and
        /// compresses what it gathered once that reaches its target.
        /// </summary>
        internal void Gather(int field, Gathering gathering) => _gathered[field] = gathering;

        /// <summary>The gathering column <paramref name="field"/> was given to in this chunk, or null.</summary>
        internal Gathering? GatheredOf(int field) => _gathered[field];

        /// <summary>Whether column <paramref name="field"/> wrote a blob in this chunk: every column but one that only gathered.</summary>
        internal bool Wrote(int field) => _blobs[field].Length > 0;

        /// <summary>The first block, the blocks and the rows of the chunk of its own column <paramref name="field"/> wrote.</summary>
        internal (int FirstBlock, int Blocks, int Rows) GatheredChunk(int field) =>
            (_gatheredFirst[field], _gatheredBlocks[field], _gatheredRows[field]);

        /// <summary>The rows and blocks of the file's chunk, which the columns that gather add to what they hold.</summary>
        internal void Spread(int rows, int firstBlock, int blocks)
        {
            _rows = rows;
            _firstBlock = firstBlock;
            _blocks = blocks;
        }

        /// <summary>
        /// Readies a flush: the columns given to their gatherings (see <see cref="Gather"/>) write
        /// what they hold, whatever their targets, and no other column writes anything, until the run
        /// drops what it held (<see cref="Drop"/>).
        /// </summary>
        internal void Flush() => _flushing = true;

        /// <summary>Whether this run writes what the gatherings hold rather than a chunk of the file.</summary>
        internal bool Flushing => _flushing;

        private bool _flushing;

        /// <summary>
        /// Compresses one column on the thread that claimed it, or adds its rows to its gathering
        /// and compresses what that holds once it reaches its target; or, in an ingest across, runs
        /// one of a column's accumulators and compresses the column when it was the last of them.
        /// </summary>
        public void Run(WorkFan fan, int item)
        {
            CanonicalArena? state = fan.State as CanonicalArena;
            int entry = fan.Item(item);
            if (entry < 0)
            {
                int frames = ~entry;
                int column = frames >> AheadBits;
                _ahead[column]!.Run(frames & ((1 << AheadBits) - 1));
                Counted(column, state!);
                return;
            }

            if (!_fused)
            {
                if (_gathered[entry] is { } gathering)
                {
                    RunGathered(entry, gathering, state);
                }
                else if (_parts[entry] == 0)
                {
                    Compress(entry, state!);
                }
                else
                {
                    Counted(entry, state!);
                }

                return;
            }

            CanonicalArena chunk = state!;
            int field = entry >> 3;
            VortexFileWriter writer = _writer!;
            if ((entry & 7) == 0)
            {
                writer.FeedPart(field, chunk, _rows);
                return;
            }

            writer.IngestPart(field, (ColumnWriter.IngestParts)(entry & 7), chunk, _rows);
            Counted(field, chunk);
        }

        /// <summary>
        /// Counts one of the items column <paramref name="field"/> waits for done, and compresses the column after the
        /// last: its accumulators in an ingest across, and its frames compressed ahead.
        /// </summary>
        /// <remarks>
        /// The decrement orders every item of the column before the compression that reads what they summed and
        /// compressed, whichever threads ran them. A column's own item outside an ingest counts 1 until it runs, so that
        /// a column outside one reads 0 only when it has no item to wait for.
        /// </remarks>
        private void Counted(int field, CanonicalArena chunk)
        {
            if (Interlocked.Decrement(ref _parts[field]) != 0)
            {
                return;
            }

            if (_fused)
            {
                VortexFileWriter writer = _writer!;
                int node = writer._fieldNodes[field];
                _nodes[field] = node;
                _stats[field] = writer.ChunkStatsOf(field, chunk, node, _rows, _firstBlock, _blocks);
            }

            Compress(field, chunk);
        }

        /// <summary>
        /// Adds the chunk's rows of column <paramref name="field"/> to its gathering, and once that
        /// reaches its target, compresses what it gathered as a chunk of the column's own, as one
        /// thread writing the file does, and lets the gathered rows go.
        /// </summary>
        private void RunGathered(int field, Gathering gathering, CanonicalArena? chunk)
        {
            VortexFileWriter writer = _writer!;
            if (!_flushing)
            {
                writer.Gather(gathering, chunk!, _nodes[field], _rows, _blocks);
                if (!gathering.Due)
                {
                    return;
                }
            }

            ScanContext context = gathering.Context!;
            int rows = checked((int)gathering.Rows);
            int node = gathering.Nodes.Count == 1
                ? gathering.Nodes[0]
                : CanonicalConcat.Concat(
                    context.Decode, writer._isTabular ? writer._schema.GetField(field) : writer._schema, rows,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gathering.Nodes));
            _gatheredFirst[field] = gathering.FirstBlock;
            _gatheredBlocks[field] = gathering.Blocks;
            _gatheredRows[field] = rows;
            gathering.Nodes.Clear();
            gathering.Rows = 0;
            gathering.Cut = gathering.Bytes;
            gathering.Bytes = 0;
            gathering.Blocks = 0;
            try
            {
                _nodes[field] = node;
                _stats[field] = writer.ChunkStatsOf(field, context.Canonical, node, rows, _gatheredFirst[field], _gatheredBlocks[field]);
                Compress(field, context.Canonical);
            }
            finally
            {
                context.Canonical.Reset();
            }
        }

        /// <summary>
        /// Compresses column <paramref name="field"/> of <paramref name="chunk"/> in a lane of its own:
        /// through records of the lane's arena that view the chunk's, narrowed there when a list may
        /// share a batch's elements, since what the blob writer is handed is what lands in the file.
        /// </summary>
        private void Compress(int field, CanonicalArena chunk)
        {
            long began = Stopwatch.GetTimestamp();
            VortexFileWriter writer = _writer!;
            CompressionLane lane = CompressionLane.Take(writer);

            // What the column's frames compressed ahead took counts as its own, every item done.
            ZstdPlan.Ahead? ahead = _ahead[field];
            long framed = ahead is { Ready: true } ? ahead.Ticks : 0;
            try
            {
                lane.Encodings.Reset(_known.AsSpan(0, _knownCount));
                int local = lane.Arena.ReferenceFrom(chunk, _nodes[field]);
                if (writer._mayShareChildren)
                {
                    local = ChunkCompactor.Compact(lane.Arena, local);
                }

                // A shredded variant's chunk is its residual and its typed columns, which the
                // statistics of its rows as they came do not describe; a column written against an
                // object dictionary's, its codes.
                ChunkStats stats = _stats[field];
                Types.Variants.VariantShredding? plan = writer._shredding?[field];
                if (plan is null && writer.Codes(field))
                {
                    local = writer.Coded(field, lane.Arena, local);
                    stats = default;
                }

                lane.Blobs.ColumnLevel = writer.ZstdLevelOf(field);
                lane.Blobs.Ahead = ahead;
                if (plan is not null)
                {
                    Indexes.ChunkBlockBounds? bounds = writer.BoundsFor(field);
                    _blobs[field] = VariantShredder.Write(lane.Blobs, lane.Arena, local, plan, bounds, lane.Encodings, writer._compress);
                    if (bounds is not null)
                    {
                        _bounds![field] = bounds;
                    }
                }
                else
                {
                    _blobs[field] = ArrayBlobWriter.Write(lane.Blobs, lane.Arena, local, lane.Encodings, writer._compress, stats);
                }
                KeepFresh(field, lane.Encodings);
                if (ahead is { Taken: true })
                {
                    Interlocked.Increment(ref writer._columnChunksFramedAhead);
                }
            }
            finally
            {
                lane.Blobs.ColumnLevel = 0;
                lane.Blobs.Ahead = null;
                ahead?.Finish();
                lane.Give();
            }

            _took[field] = Stopwatch.GetTimestamp() - began + framed;
        }

        /// <summary>Keeps, as the column's, the ids <paramref name="encodings"/> took past the file's.</summary>
        private void KeepFresh(int field, EncodingDictionary encodings)
        {
            int count = encodings.Count - _knownCount;
            string[] fresh = _fresh[field];
            if (fresh.Length < count)
            {
                fresh = _fresh[field] = new string[count * 2];
            }

            for (int i = 0; i < count; i++)
            {
                fresh[i] = encodings.Ids[_knownCount + i];
            }

            _freshCounts[field] = count;
        }

        /// <summary>
        /// The blob of column <paramref name="field"/>, its new ids interned into
        /// <paramref name="file"/> in the order the column met them and its nodes renumbered onto
        /// the file's indices; the caller owns it.
        /// </summary>
        internal ArrayBlobWriter.BlobLease Take(int field, EncodingDictionary file)
        {
            ArrayBlobWriter.BlobLease blob = _blobs[field];
            _blobs[field] = default;
            int fresh = _freshCounts[field];
            if (fresh == 0)
            {
                return blob;
            }

            int count = _knownCount + fresh;
            Span<ushort> map = count <= 128 ? stackalloc ushort[128] : new ushort[count];
            bool same = true;
            for (int i = 0; i < _knownCount; i++)
            {
                map[i] = (ushort)i;
            }

            string[] ids = _fresh[field];
            for (int i = 0; i < fresh; i++)
            {
                ushort index = file.Intern(ids[i]);
                map[_knownCount + i] = index;
                same &= index == _knownCount + i;
            }

            if (!same)
            {
                ArrayBlobWriter.Renumber(in blob, map[..count]);
                _writer!.ColumnChunksRenumbered++;
            }

            return blob;
        }

        /// <summary>Gives back every blob no column has taken: a chunk a failure stopped.</summary>
        internal void Drop()
        {
            for (int field = 0; field < _blobs.Length; field++)
            {
                _blobs[field].Dispose();
                _blobs[field] = default;
                _ahead[field]?.Finish();
            }

            // A failed ingest across leaves counts behind; the next chunk starts from none. A flush ends
            // with its run: left set, the next run, this writer's or the next one's that takes the
            // columns from the spares, would write none of its chunk's columns.
            _fused = false;
            _flushing = false;
            Array.Clear(_parts);
        }
    }

    /// <summary>
    /// What a thread compresses a column in: a blob workspace with a dtype arena of its own, an arena
    /// for what the schemes lay out, and the column's own dictionary of encoding ids; taken from the
    /// process's spares for one column and given back after it.
    /// </summary>
    private sealed class CompressionLane
    {
        private ArrayBlobWriter.Workspace _blobs = new ArrayBlobWriter.Workspace { OwnTypes = true };

        private EncodingDictionary _encodings = new EncodingDictionary(ComponentKind.Array, EditionRegistry.Newest);

        internal ArrayBlobWriter.Workspace Blobs => _blobs;

        internal CanonicalArena Arena { get; } = new CanonicalArena();

        internal EncodingDictionary Encodings => _encodings;

        /// <summary>A lane for one column of <paramref name="writer"/>'s, set to its frames, its profile, its zstd level and its edition.</summary>
        internal static CompressionLane Take(VortexFileWriter writer)
        {
            CompressionLane lane = SpareSlots<CompressionLane>.Take() ?? new CompressionLane();
            if (lane._blobs.FrameRows != writer._rowBlock || lane._blobs.GranuleRows != writer.GranuleReadRows
                || lane._blobs.SizeFirst != writer._sizeFirst || lane._blobs.ZstdLevel != writer._zstdLevel)
            {
                lane._blobs.Dispose();
                lane._blobs = new ArrayBlobWriter.Workspace
                {
                    FrameRows = writer._rowBlock, GranuleRows = writer.GranuleReadRows, SizeFirst = writer._sizeFirst,
                    ZstdLevel = writer._zstdLevel, OwnTypes = true,
                };
            }

            if (lane._encodings.Target != writer._arrayEncodings.Target)
            {
                lane._encodings = new EncodingDictionary(ComponentKind.Array, writer._arrayEncodings.Target);
            }

            return lane;
        }

        /// <summary>
        /// Empties the lane and keeps it for the next column, on any thread and of any writer, or
        /// releases it when the spares are full: its arena's blocks go back to the shared pool and
        /// its compressor to the process's, whose bounds govern what idle memory is kept.
        /// </summary>
        internal void Give()
        {
            Arena.Reset();
            _encodings.Clear();
            _blobs.ReturnCompressors();
            if (!SpareSlots<CompressionLane>.Give(this))
            {
                _blobs.Dispose();
            }
        }
    }
}
