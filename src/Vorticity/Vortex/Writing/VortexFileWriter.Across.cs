using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Editions;
using Vorticity.Execution;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// A chunk of the file taken to emission: the columns that gather take its rows side by side on the writer's threads, and
/// every column whose chunk is due leaves for an emission of its own (<see cref="StartEmission"/>), in column order.
/// </summary>
public sealed partial class VortexFileWriter
{
    /// <summary>The columns that gather a chunk of the file side by side; made with the first chunk that is.</summary>
    private ChunkColumns? _chunkColumns;

    /// <summary>Column chunks a pump of the session's core compressed, emitted by a task of their own on more than one lane.</summary>
    internal int ColumnChunksAcross { get; private set; }

    /// <summary>Column chunks emitted apart whose blob was renumbered onto ids the file met first as they landed.</summary>
    internal int ColumnChunksRenumbered { get; private set; }

    /// <summary>Column chunks whose zstd frames were compressed as items of their emission, ahead of their trials, and kept.</summary>
    internal int ColumnChunksFramedAhead => Volatile.Read(ref _columnChunksFramedAhead);

    private int _columnChunksFramedAhead;

    /// <summary>The zstd level column <paramref name="field"/>'s frames are compressed at, as its workspace has it; 0 for the default.</summary>
    private int LevelOf(int field) => ZstdLevelOf(field) is var level and not 0 ? level : _zstdLevel;

    /// <summary>
    /// Whether the columns that gather take a chunk of the file side by side on the writer's threads: a tabular file of
    /// several columns written on several lanes whose chunks are emitted apart (<see cref="EmitsApart"/>).
    /// </summary>
    private bool CompressesAcross => _lanes > 1 && _isTabular && _fieldCount > 1 && (_gathering is null || !_mayShareChildren) && EmitsApart;

    /// <summary>
    /// Whether a probe of the tests watches the plans the chooser makes (<see cref="Probes"/>): a chunk is then compressed on
    /// the writer's flow, one at a time, which changes where, and not what, it writes.
    /// </summary>
    private bool ProbesWatch => _probes is not null;

    /// <summary>
    /// Takes one chunk of the file, node <paramref name="rootIndex"/> of <paramref name="arena"/>, to emission: the columns
    /// that gather add its rows to what they hold, across the writer's threads when they may, and each column whose chunk
    /// is due, by its target, the ceiling or the file's chunk, leaves for an emission of its own, in column order, behind
    /// the column's chunks before it in emission, the oldest of which lands now when as many as are in emission at once
    /// already are.
    /// </summary>
    /// <remarks>
    /// The chunk's columns were compressed side by side here, the next chunk's first block waiting for the slowest: an
    /// emission compresses its chunk on a free lane while the blocks after it go through, and lands at a point of the rows'
    /// own order, the column's chunk after next for a column that gathers, the next for one chunked with the file, so that
    /// the file is the same bytes at any degree.
    /// </remarks>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask EmitApartAsync(CanonicalArena arena, int rootIndex, long rows, int blocks, CancellationToken cancellationToken)
    {
        int firstBlock = _emittedBlocks;
        ChunkColumns? columns = null;
        if (_gathering is not null && !_gatheredInIngest && CompressesAcross && Fan is { } fan)
        {
            // The rows of the columns that gather copied side by side, each due one taken from its gathering in its item.
            columns = _chunkColumns ??= ChunkColumns.Take(this);
            columns.Prepare(_fieldCount);
            columns.Spread(checked((int)rows), blocks);
            CanonicalNode root = arena.GetNode(rootIndex);
            Span<int> items = fan.Items(_fieldCount);
            int count = 0;
            for (int field = 0; field < _fieldCount; field++)
            {
                columns.Nodes[field] = root.GetFieldIndex(field);
                if (_gathering[field] is { } gathering)
                {
                    columns.Gather(field, gathering);
                    items[count++] = field;
                }
            }

            try
            {
                await fan.RunAsync(columns, count, arena).ConfigureAwait(false);
            }
            catch
            {
                columns.Drop();
                throw;
            }
        }

        try
        {
            for (int field = 0; field < _fieldCount; field++)
            {
                int node = _isTabular ? arena.GetNode(rootIndex).GetFieldIndex(field) : rootIndex;
                if (_gathering?[field] is { } gathering)
                {
                    ColumnEmission? detached;
                    if (columns is not null)
                    {
                        detached = columns.TakeDetached(field);
                    }
                    else
                    {
                        if (!_gatheredInIngest)
                        {
                            Gather(field, gathering, arena, node, rows, blocks);
                        }

                        detached = DueOrOver(gathering) ? Detach(field, gathering) : null;
                    }

                    if (detached is not null)
                    {
                        // One more chunk compressing only once the writer's share holds it: those before end first.
                        await LandEmissionsAsync(field, cancellationToken, keep: EmissionDepth - 1).ConfigureAwait(false);
                        await RoomAsync(0).ConfigureAwait(false);
                        StartEmission(detached);
                    }

                    continue;
                }

                // A column chunked with the file: its chunk before lands, and this one leaves.
                await LandEmissionsAsync(field, cancellationToken).ConfigureAwait(false);
                await RoomAsync(0).ConfigureAwait(false);
                StartEmission(DetachChunk(field, arena, node, checked((int)rows), firstBlock, blocks));
            }

            // A writer whose indexes are judged against their columns' bytes as the chunk closes, the payloads the chunk
            // closed written after the verdict, writes the chunk of its columns chunked with the file before it closes: its
            // columns compressed at once on the lanes, the verdict reading what it read when they were compressed here.
            if (_indexes is { AsksNothing: false })
            {
                for (int field = 0; field < _fieldCount; field++)
                {
                    if (_gathering?[field] is null)
                    {
                        await LandEmissionsAsync(field, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            columns?.Drop();
        }
    }

    /// <summary>Gives back what the side-by-side gathering holds, for the next writer; its lanes are already with the process's spares.</summary>
    private void ReleaseColumnsAcross()
    {
        if (_chunkColumns is { } columns)
        {
            _chunkColumns = null;
            columns.Give();
        }
    }

    /// <summary>
    /// The columns that gather of one chunk of the file, on the writer's threads: what each reads, the gathering it adds the
    /// chunk's rows to, and the chunk it took from that gathering once due.
    /// </summary>
    private sealed class ChunkColumns : IBatchWork
    {
        /// <summary>The writer whose chunks the columns are, for the one write that holds them.</summary>
        private VortexFileWriter? _writer;

        private int[] _nodes = [];

        // The chunk's rows and blocks, which the columns that gather add to what they hold.
        private int _rows;
        private int _blocks;

        // Per column, the gathering its thread adds the chunk's rows to; null for a column chunked with the file.
        private Gathering?[] _gathered = [];

        // Per column, the chunk it took from its gathering to emission (VortexFileWriter.StartEmission), its target reached in
        // this chunk of the file; null for none.
        private ColumnEmission?[] _detached = [];

        /// <summary>Per column, the chunk's node the column is read from.</summary>
        internal Span<int> Nodes => _nodes;

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
            Array.Clear(_gathered);
            _writer = null;
            SpareSlots<ChunkColumns>.Give(this);
        }

        /// <summary>Makes room for <paramref name="fields"/> columns.</summary>
        internal void Prepare(int fields)
        {
            if (_nodes.Length < fields)
            {
                _nodes = new int[fields];
                _gathered = new Gathering?[fields];
                _detached = new ColumnEmission?[fields];
            }

            Array.Clear(_gathered, 0, fields);
        }

        /// <summary>The rows and blocks of the file's chunk, which the columns that gather add to what they hold.</summary>
        internal void Spread(int rows, int blocks)
        {
            _rows = rows;
            _blocks = blocks;
        }

        /// <summary>
        /// Gives column <paramref name="field"/> of the chunk to its <paramref name="gathering"/>: its thread adds the chunk's
        /// rows to the gathering, and takes what it gathered as a chunk of its own once that is due.
        /// </summary>
        internal void Gather(int field, Gathering gathering) => _gathered[field] = gathering;

        /// <summary>The chunk column <paramref name="field"/> took from its gathering in this chunk to emission, given to the caller; null for none.</summary>
        internal ColumnEmission? TakeDetached(int field)
        {
            ColumnEmission? chunk = field < _detached.Length ? _detached[field] : null;
            if (chunk is not null)
            {
                _detached[field] = null;
            }

            return chunk;
        }

        /// <summary>
        /// Adds the chunk's rows of the column item <paramref name="item"/> names to its gathering, and once that is due takes
        /// what it gathered as a chunk of its own, for an emission: the gathering and the column's ingest state alone.
        /// </summary>
        public void Run(CoreBatch fan, int item)
        {
            int field = fan.Item(item);
            Gathering gathering = _gathered[field]!;
            VortexFileWriter writer = _writer!;
            writer.Gather(field, gathering, (CanonicalArena)fan.State!, _nodes[field], _rows, _blocks);
            if (writer.DueOrOver(gathering))
            {
                _detached[field] = writer.Detach(field, gathering);
            }
        }

        /// <summary>Gives back a chunk taken to emission that a failure kept from starting: its rows, and what it counted.</summary>
        internal void Drop()
        {
            for (int field = 0; field < _detached.Length; field++)
            {
                if (_detached[field] is { } detached && _writer is { } writer)
                {
                    writer.Release(detached);
                    writer.Uncount(detached);
                    writer.ReturnTaken(detached);
                }

                _detached[field] = null;
            }
        }
    }

    /// <summary>
    /// What a thread compresses a column in: a blob workspace with a dtype arena of its own, an arena
    /// for what the schemes lay out, and the column's own dictionary of encoding ids; taken from the
    /// process's spares for one column and given back after it. Kept between writers, its arenas count under the memory the
    /// process keeps, and under the writer's account while a writer holds it.
    /// </summary>
    private sealed class CompressionLane : IDisposable
    {
        private ArrayBlobWriter.Workspace _blobs = new ArrayBlobWriter.Workspace { OwnTypes = true, Account = MemoryAccount.Kept };

        private EncodingDictionary _encodings = new EncodingDictionary(ComponentKind.Array, EditionRegistry.Newest);

        internal ArrayBlobWriter.Workspace Blobs => _blobs;

        internal CanonicalArena Arena { get; } = new CanonicalArena(64, MemoryAccount.Kept);

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
                    ZstdLevel = writer._zstdLevel, OwnTypes = true, Account = writer.Account,
                };
            }

            if (lane._encodings.Target != writer._arrayEncodings.Target)
            {
                lane._encodings = new EncodingDictionary(ComponentKind.Array, writer._arrayEncodings.Target);
            }

            lane._blobs.Account = writer.Account;
            lane._blobs.Probes = writer._probes;
            lane.Arena.Account = writer.Account;
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
            Arena.Account = MemoryAccount.Kept;
            _blobs.Account = MemoryAccount.Kept;
            _blobs.Probes = null;
            _encodings.Clear();
            _blobs.ReturnCompressors();
            if (!SpareSlots<CompressionLane>.Give(this))
            {
                Dispose();
            }
        }

        /// <summary>Releases what the lane's workspace holds, when the spares are full or the registry lets the lane go.</summary>
        public void Dispose() => _blobs.Dispose();
    }
}
