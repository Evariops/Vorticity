using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// The per-column state the writer carries from the first batch to the footer, accumulated as rows
/// arrive in block coordinates counted from row 0 of the file. The writer keeps a tree of these,
/// one node per physical leaf — a struct field, a list's elements child, an extension's storage —
/// and not per top-level field, since a root field is often a struct whose leaves would otherwise
/// go unmeasured. A list's elements are not row-aligned with their parent yet still use its blocks:
/// block <c>i</c> of the elements covers the elements of parent rows <c>[8192·i, 8192·(i+1))</c>,
/// and a range whose window does not abut the one before it leaves its block scattered, so a chunk
/// covering it measures its elements itself.
/// </summary>
internal sealed class ColumnWriter
{
    /// <summary>One summary per closed block, in block order, until the zone map is written.</summary>
    private readonly AppendList<BlockStats> _closed = new AppendList<BlockStats>();

    /// <summary>
    /// One pair of width histograms per closed block, parallel to <see cref="_closed"/>. Per block
    /// rather than a running total because a block closes when its last row is ingested while its
    /// chunk is emitted when enough rows are pending, so a running accumulator would mix the next
    /// chunk's blocks into this one's. An entry is nulled and its buffer returned once the chunk is
    /// written, leaving one buffer per block in transit.
    /// </summary>
    private readonly AppendList<int[]?> _widths = new AppendList<int[]?>();

    /// <summary>
    /// The row-aligned children, created on the first <see cref="Accumulate"/> and therefore always
    /// before the first <see cref="CloseBlock"/>, which keeps every node's closed-block list the
    /// same length as its parent's. Never reordered: the shape is the schema's and cannot change.
    /// </summary>
    private ColumnWriter[]? _children;

    private BlockStats _open;

    private int[]? _openWidths;

    /// <summary>
    /// The chunk's running distinct table, created on the first range of a comparable column and
    /// reused, reset, for every chunk after.
    /// </summary>
    private DistinctTable? _table;

    internal bool EditionAllowsDictionary { get; init; } = true;

    /// <summary>
    /// The byte limit of the bounded string extremes this column's zones carry, or 0 for none. The
    /// backing field is null when the feature is off, so a column writer costs nothing for it.
    /// </summary>
    internal int StringBoundBytes
    {
        get => _stringZones?.Limit ?? 0;
        init => _stringZones = value > 0 ? new StringZones(value) : null;
    }

    private StringZones? _stringZones;

    /// <summary>
    /// Every block's string extremes, or <see langword="null"/> when a block lacks them — the zone
    /// map then carries none rather than describe some zones and invent the others.
    /// </summary>
    internal IReadOnlyList<ZoneString>? StringZones => _stringZones?.All(_closed.Count);

    /// <summary>
    /// Whether the table has a consumer on the chunk being ingested, so it should run at all: only
    /// on a chunk whose remembered plan is a dictionary that held. A column's first chunk walks
    /// instead, because the walk abandons early on columns where a dictionary loses and the table
    /// cannot, not knowing the budget.
    /// </summary>
    internal bool DictionaryLive => EditionAllowsDictionary && _live;

    private bool _live;

    /// <summary>
    /// Counted only while bit-packing is the remembered plan that held, on the table's rule and for
    /// the table's reason.
    /// </summary>
    private bool _widthsLive;

    /// <summary>
    /// Per closed block, in block order: how many distinct values and how many heap bytes the table
    /// held when that block closed — the two numbers that bound a chunk ending there. By the time a
    /// chunk closes the table has probed rows beyond it, but since codes are handed out in order of
    /// first appearance, the chunk's rows use exactly the codes below the count at close.
    /// </summary>
    private readonly AppendList<(int Distinct, long Heap)> _tableAtClose = new AppendList<(int Distinct, long Heap)>();

    /// <summary>
    /// The column's last row, kept because run continuity is a question about the previous row and
    /// that row's arena is recycled as soon as the next batch is decoded. It outlives a block close
    /// too: a block boundary is not a run boundary.
    /// </summary>
    private readonly PreviousRow _previous = new PreviousRow();

    /// <summary>The closed blocks, in order. Zone <c>z</c> is entry <c>z</c>.</summary>
    internal IReadOnlyList<BlockStats> Blocks => _closed;

    /// <summary>
    /// The statistics of <paramref name="count"/> blocks from <paramref name="first"/>: what a
    /// chunk covering them is, a sum and never an approximation since a chunk is a whole number of
    /// blocks. An out-of-range request returns an absent summary rather than throwing, so that the
    /// chooser measures the column itself and a plumbing slip costs a pass, never a wrong plan.
    /// </summary>
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
    /// Folds rows <c>[start, start + count)</c> of <paramref name="nodeIndex"/> into the open
    /// block. The caller has cut the range at the block boundary.
    /// </summary>
    internal void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (count <= 0)
        {
            // A list range whose rows name no element: nothing to summarize, but the subtree still
            // has to exist before the block closes, so that every node closes as many blocks as its
            // parent.
            Shape(arena, nodeIndex);
            return;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        if (_openWidths is null && _widthsLive
            && node.Kind == CanonicalKind.Primitive && node.PType.IsInteger())
        {
            // Rented on the first integer range of the block and not before, so that a schema of a
            // thousand string columns rents nothing, and only while bit-packing is the remembered
            // plan: an integer column whose plan is never a bit-packing would otherwise pay a
            // leading-zero count and two increments per row for a candidate nobody prices.
            _openWidths = ArrayPool<int>.Shared.Rent(BitPackWidths.Length);
            _openWidths.AsSpan(0, BitPackWidths.Length).Clear();
        }

        Span<int> widths =
            _openWidths is null ? default : _openWidths.AsSpan(0, BitPackWidths.Length);
        BlockStatsPass.Accumulate(arena, nodeIndex, start, count, ref _open, _previous, widths);
        if (_stringZones is not null && node.Kind == CanonicalKind.VarBinView)
        {
            _stringZones.Accumulate(arena, node, start, count);
        }

        // The distinct table runs on the same rows right after the statistics pass has loaded them,
        // so the probe pays its hash and its compare and none of its loads.
        if (DictionaryLive)
        {
            _table ??= DistinctTable.For(node);
            _table?.Probe(arena, node, start, count);
        }

        switch (node.Kind)
        {
            case CanonicalKind.Struct:
            {
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

            case CanonicalKind.ListView:
            {
                // The window of elements the rows name goes into this block, whatever its own
                // count, when it abuts the window before it; otherwise the block is scattered and
                // summarizes nothing.
                int elements = node.ElementsIndex;
                ColumnWriter child = Children(1)[0];
                if (ListElements.Contiguous(
                        node, arena.GetNode(elements).Length, start, count, _previous, out int from, out int length))
                {
                    child.Accumulate(arena, elements, from, length);
                    return;
                }

                child.Shape(arena, elements);
                child.Scatter();
                return;
            }

            case CanonicalKind.FixedSizeList:
            {
                // Row r's elements are [r·size, (r+1)·size), always in order.
                long size = node.FixedSize;
                Children(1)[0].Accumulate(
                    arena, node.ElementsIndex, checked((int)(start * size)), checked((int)(count * size)));
                return;
            }

            default:
                return;
        }
    }

    /// <summary>
    /// Creates the nodes under this one that <paramref name="nodeIndex"/>'s shape calls for,
    /// summarizing nothing. A node created a block late would close one block fewer than its
    /// parent, and every block after it would be summarized under the wrong number.
    /// </summary>
    private void Shape(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        switch (node.Kind)
        {
            case CanonicalKind.Struct:
            {
                ColumnWriter[] children = Children(node.FieldCount);
                for (int i = 0; i < children.Length; i++)
                {
                    children[i].Shape(arena, node.GetFieldIndex(i));
                }

                return;
            }

            case CanonicalKind.Extension:
                Children(1)[0].Shape(arena, node.StorageIndex);
                return;

            case CanonicalKind.ListView or CanonicalKind.FixedSizeList:
                Children(1)[0].Shape(arena, node.ElementsIndex);
                return;

            default:
                return;
        }
    }

    private void Scatter()
    {
        _open.Scattered = true;
        ColumnWriter[]? children = _children;
        if (children is null)
        {
            return;
        }

        for (int i = 0; i < children.Length; i++)
        {
            children[i].Scatter();
        }
    }

    /// <summary>
    /// The elements' node over a chunk, when the chunk's elements can be summarized from its
    /// blocks; <see langword="null"/> when this is not a list, when a block is scattered, or when
    /// the blocks name another number of elements than the chunk writes.
    /// </summary>
    internal ColumnWriter? ElementsOver(int first, int count, long elements)
    {
        if (Field(0) is not { } child || _children!.Length != 1)
        {
            return null;
        }

        BlockStats merged = child.Chunk(first, count);
        if (merged.Scattered)
        {
            return null;
        }

        // A chunk whose lists are all empty has no element block present, and nothing to price.
        return merged.Rows == elements ? child : null;
    }

    /// <summary>
    /// Sums the width histograms of <paramref name="count"/> blocks from <paramref name="first"/>
    /// into <paramref name="destination"/>, which must hold <see cref="BitPackWidths.Length"/>.
    /// All or nothing, because a partial histogram is a wrong one: a range in which any block lacks
    /// its widths answers false, and the chooser walks the column instead.
    /// </summary>
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
    /// Gives back the per-block scratch of a chunk that has been written, all the way down. The
    /// entries are nulled rather than removed so that a block's index stays its index.
    /// </summary>
    internal void ReleaseChunk(int first, int count)
    {
        for (int i = first; i < first + count && i < _widths.Count; i++)
        {
            ref int[]? widths = ref _widths.At(i);
            if (widths is not null)
            {
                ArrayPool<int>.Shared.Return(widths);
                widths = null;
            }
        }

        // The table forgets the chunk here and the tail comes back through `Reprobe`: an emitted
        // value is new again in the next chunk, so nothing survives a chunk except the buffers.
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

    internal DistinctTable? Table => _table;

    /// <summary>
    /// What the last chunk of this column was encoded as, what the chooser predicted it would cost,
    /// and what the encoder actually produced. <paramref name="Pinned"/> is the same mechanism with
    /// the tolerance set to infinity, so a pinned memory holds whatever the bytes did.
    /// </summary>
    internal readonly record struct PlanMemory(ColumnScheme Scheme, long Predicted, long Actual, bool Pinned = false)
    {
        /// <summary>Five per cent, as a ratio of the prediction.</summary>
        private const long TolerancePercent = 5;

        /// <summary>
        /// Whether the prediction held, so the next chunk may reuse the plan without pricing the
        /// alternatives. Both sides are buffer bytes with framing excluded, so a scheme that
        /// produces no buffer at all — a progression — holds trivially.
        /// </summary>
        internal bool WithinTolerance =>
            Pinned || (Predicted == 0
                ? Actual == 0
                : Math.Abs(Actual - Predicted) * 100 <= Predicted * TolerancePercent);
    }

    /// <summary>The memory of the last chunk written, or none for the first.</summary>
    internal PlanMemory? Memory { get; private set; }

    private int _plansPriced;
    private int _plansHeld;

    /// <summary>
    /// What the chunk covering <paramref name="block"/> was written as, or
    /// <see langword="null"/> when that block's chunk is not out (or never reached this node).
    /// <paramref name="block"/> is counted from row 0 of the file.
    /// </summary>
    internal ColumnScheme? SchemeAt(int block) =>
        (uint)block < (uint)_closed.Count && _closed[block].WrittenScheme != 0
            ? (ColumnScheme)(_closed[block].WrittenScheme - 1)
            : null;

    /// <summary>Chunks that had a remembered plan to consult.</summary>
    internal int PlansPriced => _plansPriced;

    /// <summary>Of those, the ones whose remembered plan held and was kept.</summary>
    internal int PlansHeld => _plansHeld;

    private long _widthsServed;

    internal void NoteWidthsServed() => _widthsServed++;

    /// <summary>
    /// Chunks of this column and its children whose bit-packing was priced from the ingested width
    /// histograms rather than from a walk.
    /// </summary>
    internal long WidthsServed
    {
        get
        {
            long served = _widthsServed;
            ColumnWriter[]? children = _children;
            if (children is not null)
            {
                for (int i = 0; i < children.Length; i++)
                {
                    served += children[i].WidthsServed;
                }
            }

            return served;
        }
    }

    /// <summary>
    /// Records what the chunk just written was encoded as, against what it was priced at, and
    /// decides from that which ingest state the next chunk runs. A plan that was never priced
    /// leaves no memory, since a memory predicting zero cannot hold.
    /// </summary>
    internal void Remember(in ColumnPlan plan, long actualBytes, int firstBlock, int blockCount)
    {
        // A progression predicts zero buffer bytes and is priced all the same; anything else
        // predicting zero was never priced at all.
        bool priced = plan.PredictedBytes > 0 || plan.Scheme == ColumnScheme.Sequence;

        // What each block's chunk became, in a byte the closed block already had spare. The hit is
        // counted against the memory the chunk was chosen under, which is the one being replaced.
        byte written = (byte)(plan.Scheme + 1);
        for (int block = Math.Max(firstBlock, 0); block < firstBlock + blockCount && block < _closed.Count; block++)
        {
            _closed.At(block).WrittenScheme = written;
        }

        if (Memory is { } previous)
        {
            _plansPriced++;
            if (previous.WithinTolerance && previous.Scheme == plan.Scheme)
            {
                _plansHeld++;
            }
        }

        // A pinned memory is not replaced: the caller's scheme is offered to every chunk, and a
        // chunk written as something else does not withdraw it.
        if (Memory is not { Pinned: true })
        {
            Memory = priced ? new PlanMemory(plan.Scheme, plan.PredictedBytes, actualBytes) : null;
        }

        // The table's consumer for the next chunk, decided here and nowhere else, so that the probe
        // is paid exactly where its answer is used.
        _live = Memory is { WithinTolerance: true, Scheme: ColumnScheme.Dict };
        // The ingest histograms are the raw and the zigzag widths. A frame of reference wants the
        // framed widths, which are the raw ones exactly when the reference is zero and something
        // the pack has to walk for otherwise, so counting them there would pay twice.
        _widthsLive = Memory is { WithinTolerance: true, Scheme: ColumnScheme.BitPacked }
            && plan.BitPack is { } packed
            && (packed.Transform == BitPackTransform.ZigZag || packed.Reference == 0);
    }

    /// <summary>What the table held when block <paramref name="block"/> closed, or <c>(-1, 0)</c>.</summary>
    internal (int Distinct, long Heap) TableAtClose(int block) =>
        (uint)block < (uint)_tableAtClose.Count ? _tableAtClose[block] : (-1, 0);

    /// <summary>
    /// Probes rows <c>[start, start + count)</c> into the table only — the statistics already hold
    /// them — all the way down the tree. This is for the carried tail and nothing else: those rows
    /// were probed under the emitted chunk's codes, so in the new chunk their values are new, while
    /// their block statistics are untouched because the open block is still the open block.
    /// </summary>
    internal void Reprobe(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (DictionaryLive)
        {
            // A table plan memory has just turned back on has no table yet for this column; the
            // carried tail is the first thing it must see, so it is created here as well.
            _table ??= DistinctTable.For(node);
            _table?.Probe(arena, node, start, count);
        }

        // The carried rows are the open block, ingested before the plan that wants widths held, so
        // the open block has no widths yet. Counting them here makes the block whole and lets the
        // chunk it opens read its histograms. Only when the carry is the whole block: a count over
        // part of it would be a wrong histogram rather than a missing one.
        if (_widthsLive && _openWidths is null && _open.IsPresent && count == _open.Rows
            && node.Kind == CanonicalKind.Primitive && node.PType.IsInteger())
        {
            _openWidths = ArrayPool<int>.Shared.Rent(BitPackWidths.Length);
            Span<int> widths = _openWidths.AsSpan(0, BitPackWidths.Length);
            widths.Clear();
            BlockStatsPass.Widths(arena, nodeIndex, start, count, widths);
            _open.WidthsBroken = false;
        }

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

            case CanonicalKind.ListView:
            {
                int elements = node.ElementsIndex;
                ListElements.Window(node, arena.GetNode(elements).Length, start, count, out int from, out int length);
                if (length > 0)
                {
                    Children(1)[0].Reprobe(arena, elements, from, length);
                }

                return;
            }

            case CanonicalKind.FixedSizeList:
            {
                long size = node.FixedSize;
                if (size > 0)
                {
                    Children(1)[0].Reprobe(
                        arena, node.ElementsIndex, checked((int)(start * size)), checked((int)(count * size)));
                }

                return;
            }

            default:
                return;
        }
    }

    /// <summary>
    /// Child <paramref name="index"/>, or <see langword="null"/> when this column has no children —
    /// which the caller reads as "measure it yourself".
    /// </summary>
    internal ColumnWriter? Field(int index)
    {
        ColumnWriter[]? children = _children;
        return children is not null && (uint)index < (uint)children.Length ? children[index] : null;
    }

    /// <summary>
    /// Seals the open block and starts the next one, all the way down. Called when the block's last
    /// row has been seen, which is the moment its statistics are final; a block with no rows is
    /// never closed, since the caller only closes a boundary it has crossed.
    /// </summary>
    internal void CloseBlock()
    {
        // A block whose widths are partial is stored as absent rather than as a wrong count, so the
        // chooser measures the column itself.
        if (_open.WidthsBroken && _openWidths is not null)
        {
            ArrayPool<int>.Shared.Return(_openWidths);
            _openWidths = null;
        }

        _closed.Add(_open);
        _widths.Add(_openWidths);
        _stringZones?.Close();

        // A dead table records nothing, so that the chooser reads "no table" rather than a stale
        // count.
        _tableAtClose.Add(
            _table is null || !DictionaryLive ? (-1, 0) : (_table.Distinct, _table.HeapBytes));
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

    /// <summary>
    /// Takes an existing file's blocks as closed, for an append: the zone map is written again over
    /// them, and the chunks already out keep their block numbers. <paramref name="strings"/> is the
    /// old zones' string bounds, already cut to <see cref="StringBoundBytes"/>.
    /// </summary>
    internal void Seed(IReadOnlyList<BlockStats> blocks, IReadOnlyList<ZoneString?>? strings = null)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            _closed.Add(blocks[i]);
            _widths.Add(null);
            _tableAtClose.Add((-1, 0));
            _stringZones?.Seed(strings?[i]);
        }

        _seeded += blocks.Count;
    }

    /// <summary>
    /// Starts this column, and the child columns the seed names, from what an appended file's last
    /// chunk was written as: a memory that held, and the ingest state its scheme reads, exactly as
    /// <see cref="Remember"/> would have left them.
    /// </summary>
    internal void SeedPlan(PlanSeed seed)
    {
        if (seed.Scheme is { } scheme)
        {
            Memory = new PlanMemory(scheme, 1, 1);
            _live = scheme == ColumnScheme.Dict;
            _widthsLive = scheme == ColumnScheme.BitPacked && seed.WidthsServe;
        }

        PlanSeed?[] fields = seed.Fields;
        if (fields.Length == 0)
        {
            return;
        }

        ColumnWriter[] children = Children(fields.Length);
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] is { } field)
            {
                children[i].SeedPlan(field);
            }
        }
    }

    /// <summary>
    /// Pins the scheme this column is written with, whatever its chunks cost. Must be set before
    /// the first batch, because the ingest state the scheme reads starts with it: a pinned
    /// dictionary needs the distinct table live on the column's first rows.
    /// </summary>
    internal void Pin(ColumnScheme scheme)
    {
        // A pin is not a measurement, so it carries no numbers; `Pinned` is what sets the tolerance
        // they would otherwise fail to infinity.
        Memory = new PlanMemory(scheme, Predicted: 0, Actual: -1, Pinned: true);
        _live = scheme == ColumnScheme.Dict;
        _widthsLive = scheme == ColumnScheme.BitPacked;
    }

    /// <summary>Child <paramref name="index"/>, creating the nodes on the first batch.</summary>
    internal ColumnWriter Descend(int index, int count) => Children(count)[index];

    /// <summary>Blocks taken from an existing file, which a child created later is given too.</summary>
    private int _seeded;

    private ColumnWriter[] Children(int count)
    {
        ColumnWriter[]? children = _children;
        if (children is not null && children.Length == count)
        {
            return children;
        }

        // A shape that disagrees with the first batch's is not a state this can be in, the schema
        // being the file's, but growing rather than throwing keeps a surprise costing a pass
        // instead of a write: the nodes created late have fewer closed blocks than their parent,
        // so the chooser sees an uncovered range and measures the column itself.
        ColumnWriter[] grown = new ColumnWriter[count];
        for (int i = 0; i < count; i++)
        {
            if (children is not null && i < children.Length)
            {
                grown[i] = children[i];
                continue;
            }

            // A child of an appended column starts with its parent's old blocks, as absent
            // summaries, so every node's closed list keeps the same length.
            grown[i] = new ColumnWriter();
            if (_seeded > 0)
            {
                grown[i].Seed(new BlockStats[_seeded]);
            }
        }

        _children = grown;
        return grown;
    }
}
