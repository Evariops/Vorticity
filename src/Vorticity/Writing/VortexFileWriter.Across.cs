using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
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
        _lanes > 1 && _isTabular && _fieldCount > 1 && _gathering is null && _arrayEncodings.Count > 0 &&
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
    private async ValueTask EmitColumnsAcrossAsync(
        CanonicalArena arena, int rootIndex, long rows, int blocks, CancellationToken cancellationToken)
    {
        int firstBlock = _emittedBlocks;
        ChunkColumns columns = _chunkColumns ??= ChunkColumns.Take(this);
        columns.Prepare(_fieldCount, _arrayEncodings);
        CanonicalNode root = arena.GetNode(rootIndex);
        for (int field = 0; field < _fieldCount; field++)
        {
            // A list column is narrowed on its thread, in its own arena (see ChunkColumns.Compress):
            // its statistics read only the node's kind and rows, which the narrowing keeps.
            int node = root.GetFieldIndex(field);
            columns.Nodes[field] = node;
            columns.Stats[field] = ChunkStatsOf(field, arena, node, rows, firstBlock, blocks);
        }

        WorkFan fan = Fan!;
        try
        {
            columns.Order(fan.Items(_fieldCount));
            fan.Run(columns, _fieldCount, arena);
            ColumnChunksAcross += _fieldCount;
            await WriteColumnBlobsAsync(columns, firstBlock, blocks, cancellationToken).ConfigureAwait(false);
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
        CompressesAcross && _indexes is null && _blockRows > 0 && rows >= _blockRows && Fan is not null;

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
            Span<int> items = fan.Items(_fieldCount * 3);
            int count = IngestItems(arena, items);
            columns.Fuse(items[..count], rows, firstBlock, blocks);
            fan.Run(columns, count, arena, rows);
            _blockFilled = (_blockFilled + rows) % _blockRows;
            BatchesIngestedAcross++;
            ColumnChunksAcross += _fieldCount;
            await WriteColumnBlobsAsync(columns, firstBlock, blocks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            columns.Drop();
        }

        await CloseChunkAsync(rows, blocks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the chunk's blobs in column order, each column's new ids made the file's.</summary>
    private async ValueTask WriteColumnBlobsAsync(ChunkColumns columns, int firstBlock, int blocks, CancellationToken cancellationToken)
    {
        for (int field = 0; field < _fieldCount; field++)
        {
            ArrayBlobWriter.BlobLease blob = columns.Take(field, _arrayEncodings);
            await WriteColumnBlobAsync(field, blob, firstBlock, blocks, cancellationToken).ConfigureAwait(false);
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

        // In an ingest across (see Fuse): per column, its accumulators still to run, and the chunk's
        // rows and blocks its statistics are taken over.
        private int[] _parts = [];
        private bool _fused;
        private int _rows;
        private int _firstBlock;
        private int _blocks;

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
            foreach (string[] fresh in _fresh)
            {
                Array.Clear(fresh);
            }

            _knownCount = 0;
            _writer = null;
            SpareSlots<ChunkColumns>.Give(this);
        }

        /// <summary>Makes room for <paramref name="fields"/> columns and takes the file's ids as the chunk begins.</summary>
        internal void Prepare(int fields, EncodingDictionary file)
        {
            if (_nodes.Length < fields)
            {
                _nodes = new int[fields];
                _stats = new ChunkStats[fields];
                _blobs = new ArrayBlobWriter.BlobLease[fields];
                _fresh = new string[fields][];
                _freshCounts = new int[fields];
                _took = new long[fields];
                for (int field = 0; field < fields; field++)
                {
                    _fresh[field] = new string[4];
                }
            }

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
            if (_parts.Length < _nodes.Length)
            {
                _parts = new int[_nodes.Length];
            }

            for (int i = 0; i < items.Length; i++)
            {
                int entry = items[i];
                _parts[entry >> 3]++;
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
        /// Compresses one column on the thread that claimed it; or, in an ingest across, runs one of
        /// a column's accumulators and compresses the column when it was the last of them.
        /// </summary>
        public void Run(WorkFan fan, int item)
        {
            CanonicalArena chunk = (CanonicalArena)fan.State!;
            if (!_fused)
            {
                Compress(fan.Item(item), chunk);
                return;
            }

            int entry = fan.Item(item);
            int field = entry >> 3;
            VortexFileWriter writer = _writer!;
            writer.IngestPart(field, (ColumnWriter.IngestParts)(entry & 7), chunk, _rows);

            // The decrement orders every accumulator of the column before the compression that
            // reads what they summed, whichever threads ran them.
            if (Interlocked.Decrement(ref _parts[field]) == 0)
            {
                int node = writer._fieldNodes[field];
                _nodes[field] = node;
                _stats[field] = writer.ChunkStatsOf(field, chunk, node, _rows, _firstBlock, _blocks);
                Compress(field, chunk);
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
            try
            {
                lane.Encodings.Reset(_known.AsSpan(0, _knownCount));
                int local = lane.Arena.ReferenceFrom(chunk, _nodes[field]);
                if (writer._mayShareChildren)
                {
                    local = ChunkCompactor.Compact(lane.Arena, local);
                }

                _blobs[field] = ArrayBlobWriter.Write(lane.Blobs, lane.Arena, local, lane.Encodings, writer._compress, _stats[field]);
                KeepFresh(field, lane.Encodings);
            }
            finally
            {
                lane.Give();
            }

            _took[field] = Stopwatch.GetTimestamp() - began;
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
            }

            // A failed ingest across leaves counts behind; the next chunk starts from none.
            _fused = false;
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

        /// <summary>
        /// The threads the column's zstd frames are compressed on, the column's thread included: a fan
        /// of the lane's own, apart from the one the chunk's columns run on, which runs one piece at a
        /// time. A file of few columns then has its costliest one compressed a frame a thread, as a
        /// column written alone is; a fan whose helpers find no thread free does its work on the
        /// caller's.
        /// </summary>
        private WorkFan? _fan;

        private EncodingDictionary _encodings = new EncodingDictionary(ComponentKind.Array, EditionRegistry.Newest);

        internal ArrayBlobWriter.Workspace Blobs => _blobs;

        internal CanonicalArena Arena { get; } = new CanonicalArena();

        internal EncodingDictionary Encodings => _encodings;

        /// <summary>A lane for one column of <paramref name="writer"/>'s, set to its frames, its profile, its zstd level and its edition.</summary>
        internal static CompressionLane Take(VortexFileWriter writer)
        {
            CompressionLane lane = SpareSlots<CompressionLane>.Take() ?? new CompressionLane();
            if (lane._fan?.Lanes != writer._lanes)
            {
                if (lane._fan is { } kept)
                {
                    WorkFan.Return(kept);
                }

                lane._fan = WorkFan.Rent(writer._lanes);
            }

            if (lane._blobs.FrameRows != writer._rowBlock || lane._blobs.SizeFirst != writer._sizeFirst
                || lane._blobs.ZstdLevel != writer._zstdLevel || lane._blobs.Fan != lane._fan)
            {
                lane._blobs.Dispose();
                lane._blobs = new ArrayBlobWriter.Workspace
                {
                    FrameRows = writer._rowBlock, SizeFirst = writer._sizeFirst, ZstdLevel = writer._zstdLevel,
                    OwnTypes = true, Fan = lane._fan,
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
                if (_fan is { } fan)
                {
                    _fan = null;
                    WorkFan.Return(fan);
                }
            }
        }
    }
}
