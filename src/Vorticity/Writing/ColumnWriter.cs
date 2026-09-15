// One column's state across the whole file - docs/11-write-strategy.md §3.0.
//
// The writer keeps a TREE of these, one per column the file summarizes, and each is a state machine
// fed by every WriteAsync rather than by every emission. That inversion is the point of stage 1:
// what a column knows about itself now accumulates as the rows arrive, in block coordinates counted
// from row 0 of the file, instead of being recomputed from whatever chunk the rows happened to land
// in.
//
// WHAT IT HOLDS TODAY is the open block's partial and the closed blocks' summaries - a few hundred
// bytes plus ~200 per block, which is what §3.7 budgets for it. The stages after this one add the
// chooser's plan memory (§3.4.3), the running distinct table (§3.2.2) and the index builders
// (docs/10-indexes.md §7) to the same object, which is why it is a class with a growing state and
// not a tuple.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>The per-column state the writer carries from the first batch to the footer.</summary>
/// <remarks>
/// A TREE, ONE NODE PER PHYSICAL COLUMN — docs/11-write-strategy.md §3.0, "one per physical leaf (a
/// struct field, a list's elements child, an extension's storage)". A file's root field is very
/// often not a column at all: `encodings/variant` is one `variant` dtype whose canonical form is a
/// two-field struct of varbinviews, and `table_wide` is a struct of structs. Summarizing only the
/// root fields left every one of those leaves unmeasured, so the chooser fell back to walking them —
/// which is what WRITE-ARCHITECTURE.md §1 charges `variant` 62 % for.
/// <para>
/// A STRUCT'S FIELDS AND AN EXTENSION'S STORAGE ARE ROW-ALIGNED with their parent, so a child's
/// blocks are the parent's blocks and the row range passes straight down. A LIST'S ELEMENTS ARE NOT:
/// §3.2.4 defines their blocks as the parent's ROWS, found through the offsets, and that is a later
/// stage — until then a list's elements child has no node here and the chooser measures it itself.
/// </para>
/// </remarks>
internal sealed class ColumnWriter
{
    /// <summary>One summary per closed block, in block order, until the zone map is written.</summary>
    private readonly List<BlockStats> _closed = [];

    /// <summary>
    /// One pair of width histograms per closed block, parallel to <see cref="_closed"/>, held only
    /// until the chunk covering the block has been emitted.
    /// </summary>
    /// <remarks>
    /// WHY PER BLOCK AND NOT A RUNNING TOTAL, which is what this looked like it should be: a block
    /// closes when its last row is INGESTED, and the chunk that carries it is emitted when enough
    /// rows are PENDING — two different moments. A batch can close several blocks that the writer
    /// then holds in transit, so a single running accumulator would already be mixing the next
    /// chunk's blocks into this one's by the time the chooser asked.
    /// <para>
    /// The list stays index-aligned with <see cref="_closed"/> forever, but an entry is nulled and
    /// its buffer returned the moment <see cref="ReleaseChunk"/> says the chunk is written, so what
    /// is actually held is one buffer per block IN TRANSIT — bounded by
    /// <c>DataBlockTargetBytes</c>, which is one chunk. That is the "block scratch" row of
    /// docs/11-write-strategy.md §3.7 and not the "~200 B per closed block" one, which is what the
    /// 520 bytes of a histogram pair would have blown through by a factor of nine.
    /// </para>
    /// </remarks>
    private readonly List<int[]?> _widths = [];

    /// <summary>
    /// The row-aligned children, created the first time a batch shows them; never reordered.
    /// </summary>
    /// <remarks>
    /// Created on the first <see cref="Accumulate"/> and therefore always before the first
    /// <see cref="CloseBlock"/>, which is what keeps every node's closed-block list the same length
    /// as its parent's. The shape cannot change between batches: it is the schema's, and the schema
    /// is fixed for the file.
    /// </remarks>
    private ColumnWriter[]? _children;

    /// <summary>The block in progress; every batch that covers part of it folds into this one.</summary>
    private BlockStats _open;

    /// <summary>The open block's width histograms, rented on its first integer range.</summary>
    private int[]? _openWidths;

    /// <summary>
    /// The chunk's running distinct table (docs/11-write-strategy.md §3.2.2), created on the first
    /// range of a comparable column and reused, reset, for every chunk after.
    /// </summary>
    private DistinctTable? _table;

    /// <summary>
    /// Whether a dictionary has a consumer, so the table should run at all. The writer sets it from
    /// the edition; §3.2.2's plan memory will narrow it per chunk.
    /// </summary>
    internal bool DictionaryLive { get; set; } = true;

    /// <summary>
    /// Per closed block, in block order: how many distinct values and how many heap bytes the table
    /// held when that block closed — the two numbers that bound a chunk ending there.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT MAKES THE TAIL HARMLESS. A chunk closes at a block boundary, and the table has
    /// by then probed rows beyond it. Because codes are handed out in order of first appearance,
    /// the codes used by the chunk's rows are exactly <c>[0, distinct-at-close)</c>, and the heap
    /// bytes those entries own are exactly <c>heap-at-close</c>: the entries above are the tail's
    /// and are not part of this chunk. Recorded at every close so that whichever block ends the
    /// chunk, the answer is one lookup.
    /// </remarks>
    private readonly List<(int Distinct, long Heap)> _tableAtClose = [];

    /// <summary>
    /// The column's last row, which outlives both the batch it came in and the block it fell in.
    /// </summary>
    /// <remarks>
    /// Run continuity is the reason: whether row 8 192 starts a run is a question about row 8 191,
    /// and that row's arena was recycled the moment the next batch was decoded
    /// (docs/11-write-strategy.md §3.1). It survives <see cref="CloseBlock"/> for the same reason —
    /// a block boundary is not a run boundary.
    /// </remarks>
    private readonly PreviousRow _previous = new PreviousRow();

    /// <summary>The closed blocks, in order. Zone <c>z</c> is entry <c>z</c>.</summary>
    internal IReadOnlyList<BlockStats> Blocks => _closed;

    /// <summary>
    /// The statistics of <paramref name="count"/> blocks starting at <paramref name="first"/>: what
    /// a chunk covering them is, for the chooser.
    /// </summary>
    /// <remarks>
    /// A chunk is a whole number of blocks (docs/11-write-strategy.md §3.1), so this is a sum and
    /// never an approximation. An out-of-range request returns an absent summary rather than
    /// throwing: a chooser with no statistics measures the column itself, which is exactly what it
    /// did before this existed, so a plumbing slip costs a pass and never a wrong plan.
    /// </remarks>
    /// <param name="first">The first block of the chunk.</param>
    /// <param name="count">How many blocks it covers.</param>
    /// <returns>The merged summary, or a default one when the range is not fully closed.</returns>
    internal BlockStats Chunk(int first, int count)
    {
        if (first < 0 || count <= 0 || first + count > _closed.Count)
        {
            return default;
        }

        BlockStats merged = default;
        for (int i = 0; i < count; i++)
        {
            merged.Merge(_closed[first + i]);
        }

        return merged;
    }

    /// <summary>
    /// Folds rows <c>[start, start + count)</c> of <paramref name="nodeIndex"/> into the open block.
    /// </summary>
    /// <param name="arena">The arena holding the batch.</param>
    /// <param name="nodeIndex">This column inside the batch.</param>
    /// <param name="start">First row of the range, inside the batch.</param>
    /// <param name="count">How many rows; the caller has cut the range at the block boundary.</param>
    internal void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (_openWidths is null && node.Kind == CanonicalKind.Primitive && node.PType.IsInteger())
        {
            // Rented on the first integer range of the block and not before: a schema of a thousand
            // string columns rents nothing, which is what keeps this out of the per-column budget.
            _openWidths = ArrayPool<int>.Shared.Rent(BitPackWidths.Length);
            _openWidths.AsSpan(0, BitPackWidths.Length).Clear();
        }

        Span<int> widths =
            _openWidths is null ? default : _openWidths.AsSpan(0, BitPackWidths.Length);
        BlockStatsPass.Accumulate(arena, nodeIndex, start, count, ref _open, _previous, widths);

        // THE DISTINCT TABLE RUNS ON THE SAME ROWS, RIGHT AFTER: the statistics pass has just loaded
        // them, so the probe pays its hash and its compare and none of its loads. Created on the
        // first range of a comparable column, and only while a dictionary has a consumer.
        if (DictionaryLive)
        {
            _table ??= DistinctTable.For(node);
            _table?.Probe(arena, node, start, count);
        }

        switch (node.Kind)
        {
            case CanonicalKind.Struct:
            {
                // A variant wears a struct's canonical form and is descended for the same reason:
                // its two fields are the columns, and they are row-aligned with it.
                ColumnWriter[] children = Children(node.FieldCount);
                for (int i = 0; i < children.Length; i++)
                {
                    children[i].Accumulate(arena, node.GetFieldIndex(i), start, count);
                }

                return;
            }

            case CanonicalKind.Extension:
                Children(1)[0].Accumulate(arena, node.StorageIndex, start, count);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Sums the width histograms of <paramref name="count"/> blocks from <paramref name="first"/>
    /// into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// ALL OR NOTHING, because a partial histogram is a wrong one: a chunk whose blocks do not every
    /// one carry their widths — a range the progression short-circuit answered, a block already
    /// released, a column that is not an integer primitive — answers false, and the chooser walks
    /// the column as it did before this existed.
    /// </remarks>
    /// <param name="first">The chunk's first block.</param>
    /// <param name="count">How many blocks it covers.</param>
    /// <param name="destination">Receives the sum; must hold <see cref="BitPackWidths.Length"/>.</param>
    /// <returns>Whether every block in the range had its widths.</returns>
    internal bool Widths(int first, int count, Span<int> destination)
    {
        if (first < 0 || count <= 0 || first + count > _widths.Count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (_widths[first + i] is null)
            {
                return false;
            }
        }

        destination.Clear();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<int> block = _widths[first + i].AsSpan(0, BitPackWidths.Length);
            for (int w = 0; w < BitPackWidths.Length; w++)
            {
                destination[w] += block[w];
            }
        }

        return true;
    }

    /// <summary>
    /// Gives back the per-block scratch of a chunk that has been written, all the way down.
    /// </summary>
    /// <remarks>
    /// This is the other half of the lifetime <see cref="_widths"/> documents. The entries are
    /// nulled rather than removed so that a block's index stays its index; what is freed is the
    /// buffer, which is the part that has a size.
    /// </remarks>
    /// <param name="first">The chunk's first block.</param>
    /// <param name="count">How many blocks it covers.</param>
    internal void ReleaseChunk(int first, int count)
    {
        for (int i = first; i < first + count && i < _widths.Count; i++)
        {
            int[]? widths = _widths[i];
            if (widths is not null)
            {
                ArrayPool<int>.Shared.Return(widths);
                _widths[i] = null;
            }
        }

        // THE TABLE FORGETS THE CHUNK HERE, AND THE TAIL COMES BACK THROUGH `Reprobe`. Every entry
        // it held is either emitted -- and an emitted value is new again in the next chunk -- or
        // belongs to rows the writer is about to carry over and hand back. Nothing survives a chunk
        // except the buffers.
        _table?.Reset();

        ColumnWriter[]? children = _children;
        if (children is null)
        {
            return;
        }

        for (int i = 0; i < children.Length; i++)
        {
            children[i].ReleaseChunk(first, count);
        }
    }

    /// <summary>The chunk's distinct table, or <see langword="null"/> when none runs here.</summary>
    internal DistinctTable? Table => _table;

    /// <summary>
    /// What the last chunk of this column was encoded as, what the chooser predicted it would cost,
    /// and what the encoder actually produced — docs/11-write-strategy.md §3.4.3.
    /// </summary>
    /// <param name="Scheme">The plan's scheme.</param>
    /// <param name="Predicted">The chooser's bytes for it.</param>
    /// <param name="Actual">The buffer bytes the encoder wrote for it.</param>
    internal readonly record struct PlanMemory(ColumnScheme Scheme, long Predicted, long Actual)
    {
        /// <summary>§3.4.3's <c>plan_tolerance</c>: five per cent, as a ratio of the prediction.</summary>
        private const long TolerancePercent = 5;

        /// <summary>
        /// Whether the prediction held: the actual bytes are within the tolerance of it, so the
        /// next chunk may reuse the plan without pricing the alternatives.
        /// </summary>
        internal bool WithinTolerance =>
            Predicted > 0 && Math.Abs(Actual - Predicted) * 100 <= Predicted * TolerancePercent;
    }

    /// <summary>The memory of the last chunk written, or none for the first.</summary>
    internal PlanMemory? Memory { get; private set; }

    /// <summary>
    /// Records what the chunk just written was encoded as, against what it was priced at.
    /// </summary>
    /// <remarks>
    /// STORED, NOT YET CONSULTED: this commit lays the accounting down and proves it costs nothing
    /// — every byte identical — before the next one lets the chooser short-circuit on it and the
    /// table go dead on it. A plan that was never priced (a child a scheme invented, the reference
    /// chooser's) leaves no memory, because a memory whose prediction is zero can never hold.
    /// </remarks>
    /// <param name="plan">The plan the encoder just wrote.</param>
    /// <param name="actualBytes">The buffer bytes it produced, this column's subtree included.</param>
    internal void Remember(in ColumnPlan plan, long actualBytes)
    {
        Memory = plan.PredictedBytes > 0
            ? new PlanMemory(plan.Scheme, plan.PredictedBytes, actualBytes)
            : null;
    }

    /// <summary>
    /// What the table held when block <paramref name="block"/> closed: the distinct count and the
    /// heap bytes, which bound a chunk ending at that block. <c>(-1, 0)</c> when unknown.
    /// </summary>
    /// <param name="block">The block, counted from row 0 of the file.</param>
    internal (int Distinct, long Heap) TableAtClose(int block) =>
        (uint)block < (uint)_tableAtClose.Count ? _tableAtClose[block] : (-1, 0);

    /// <summary>
    /// Probes rows <c>[start, start + count)</c> into the table only — the statistics already hold
    /// them — all the way down the tree.
    /// </summary>
    /// <remarks>
    /// FOR THE CARRIED TAIL, AND FOR NOTHING ELSE. After a chunk is emitted, the rows the writer
    /// carries into the next one were probed under the OLD chunk's codes, some of them against
    /// entries that have just been written out; in the new chunk those values are new. Their block
    /// statistics are untouched — the open block is still the open block — so only the table sees
    /// them again, in the order the next chunk will hold them, which is first.
    /// </remarks>
    /// <param name="arena">The arena holding the carried rows.</param>
    /// <param name="nodeIndex">This column inside them.</param>
    /// <param name="start">First row of the range, inside the node.</param>
    /// <param name="count">How many rows.</param>
    internal void Reprobe(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        _table?.Probe(arena, node, start, count);

        switch (node.Kind)
        {
            case CanonicalKind.Struct:
            {
                ColumnWriter[] children = Children(node.FieldCount);
                for (int i = 0; i < children.Length; i++)
                {
                    children[i].Reprobe(arena, node.GetFieldIndex(i), start, count);
                }

                return;
            }

            case CanonicalKind.Extension:
                Children(1)[0].Reprobe(arena, node.StorageIndex, start, count);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Child <paramref name="index"/>, or <see langword="null"/> when this column has no children —
    /// which the caller reads as "measure it yourself".
    /// </summary>
    /// <param name="index">The field index, in the writer's own order.</param>
    internal ColumnWriter? Field(int index)
    {
        ColumnWriter[]? children = _children;
        return children is not null && (uint)index < (uint)children.Length ? children[index] : null;
    }

    /// <summary>Seals the open block and starts the next one, all the way down.</summary>
    /// <remarks>
    /// Called when the block's last row has been seen, which is the moment its statistics are final
    /// - and, from stage 9 on, the moment its Bloom filter is built from the hash buffer
    /// (docs/10-indexes.md §4.3). A block with no rows is never closed: the caller only closes a
    /// boundary it has crossed.
    /// </remarks>
    internal void CloseBlock()
    {
        // A block whose widths are partial is stored as ABSENT rather than as a wrong count: the
        // buffer goes straight back to the pool and the chooser measures the column itself.
        if (_open.WidthsBroken && _openWidths is not null)
        {
            ArrayPool<int>.Shared.Return(_openWidths);
            _openWidths = null;
        }

        _closed.Add(_open);
        _widths.Add(_openWidths);
        _tableAtClose.Add(_table is null ? (-1, 0) : (_table.Distinct, _table.HeapBytes));
        _open = default;
        _openWidths = null;

        ColumnWriter[]? children = _children;
        if (children is null)
        {
            return;
        }

        for (int i = 0; i < children.Length; i++)
        {
            children[i].CloseBlock();
        }
    }

    private ColumnWriter[] Children(int count)
    {
        ColumnWriter[]? children = _children;
        if (children is not null && children.Length == count)
        {
            return children;
        }

        // A shape that disagrees with the first batch's is not a state this can be in -- the schema
        // is the file's -- but growing rather than throwing keeps a surprise costing a pass instead
        // of a write: the nodes created late have fewer closed blocks than their parent, `Chunk`
        // sees the range is not covered, and the chooser measures the column itself.
        ColumnWriter[] grown = new ColumnWriter[count];
        for (int i = 0; i < count; i++)
        {
            grown[i] = children is not null && i < children.Length ? children[i] : new ColumnWriter();
        }

        _children = grown;
        return grown;
    }
}
