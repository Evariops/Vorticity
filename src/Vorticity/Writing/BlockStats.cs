using System;
using Vorticity.Expressions;

namespace Vorticity.Writing;

/// <summary>Which of the three accumulators a block's bounds live in.</summary>
internal enum BoundDomain : byte
{
    /// <summary>The column has no scalar bound a zone map can carry.</summary>
    None = 0,

    /// <summary>Signed integers, widened to <see cref="long"/>.</summary>
    Signed = 1,

    /// <summary>Unsigned integers, widened to <see cref="ulong"/>.</summary>
    Unsigned = 2,

    /// <summary>Floats, widened to <see cref="double"/>, NaN excluded.</summary>
    Float = 3,
}

/// <summary>One block's summary: the bounds a reader prunes with, plus its null count.</summary>
/// <remarks>
/// A mutable struct held by reference through <c>ref</c>: it is an accumulator, and copying it per
/// batch would be the allocation this design exists to not make. Nothing stores one by value except
/// the closed-block list, which is where its life ends.
/// </remarks>
internal struct BlockStats
{
    // One pair of words, not three. The three domains are mutually exclusive -- `Domain` says which
    // one a summary is in -- so they share the storage. That is not tidiness: the writer keeps one
    // of these per (column, closed block) until the zone map is written, and one per node of the
    // schema tree, so the struct's own size is multiplied by every block of a whole file.
    private ulong _minBits;
    private ulong _maxBits;

    /// <summary>Rows accumulated into this block so far.</summary>
    /// <remarks>
    /// Also the presence test. A block or a chunk always has rows, so <c>Rows == 0</c> means "no
    /// statistics were computed for this node" -- which is what a child of a cascade gets, since the
    /// ingest pass summarizes the file's columns and not the arrays a scheme invents beneath them.
    /// </remarks>
    internal long Rows;

    /// <summary>How many of them are null.</summary>
    internal long NullCount;

    /// <summary>
    /// For utf8 and binary: the bytes of the valid values, which is the heap a <c>vortex.varbin</c>
    /// form would carry. Zero for every other kind.
    /// </summary>
    internal long TotalBytes;

    /// <summary>
    /// Rows of this block that differ from the row before them in the file, the block's own first
    /// row included; row 0 of the file has no predecessor and is never counted.
    /// </summary>
    /// <remarks>
    /// Counting the first row here rather than separately is what makes a merge a plain sum: a
    /// boundary belongs to the row that starts the new run, and that row is in exactly one block.
    /// <see cref="RunCount"/> turns the sum back into runs.
    /// </remarks>
    internal long RunBoundaries;

    /// <summary>
    /// Whether this summary's first row differs from the row before it, so that a range starting
    /// here knows not to count that boundary.
    /// </summary>
    internal bool FirstRowStartsRun;

    /// <summary>
    /// The step between consecutive rows, when every pair in this summary has the same one.
    /// </summary>
    /// <remarks>
    /// A `long` and not an `Int128`: the difference of two w-bit values lies in (-2^w, 2^w), so for
    /// a column narrower than 64 bits it always fits, and for a 64-bit one a step outside `long`
    /// cannot be constant over three rows -- the values would have to wrap, and a wrapped difference
    /// is not the same number. `ColumnCompressor` never offers a sequence to a column of fewer than
    /// 64 rows, so the two-row case `SequencePlan` guards against is unreachable from here.
    /// </remarks>
    private long _delta;

    /// <summary>Whether a step exists at all: two consecutive values have been seen.</summary>
    internal bool DeltaKnown;

    /// <summary>
    /// Whether something disqualifies the rows from being an arithmetic progression: two different
    /// steps, a null, or a step no wire field can hold.
    /// </summary>
    /// <remarks>
    /// Stated in the negative so that a default summary -- which knows nothing -- is not already
    /// claiming to be a sequence. <see cref="DeltaKnown"/> is what says the claim was ever made.
    /// </remarks>
    internal bool DeltaBroken;

    /// <summary>
    /// The seam: the step from the row before this summary's first row into that row, when the
    /// range opened a block and a row came before it. Kept apart from the block's own steps.
    /// </summary>
    /// <remarks>
    /// Stepping a block from its predecessor's last row as if that row were its own would give a
    /// merge the seam for free, at the price of making the first block of a chunk call itself
    /// broken when the only break is between the two chunks: the chunk it opens is a progression,
    /// the merge says it is not, the chooser trusts the merge over the walk it makes when the steps
    /// are unknown, and bit-packs a column <c>vortex.sequence</c> describes in a handful of bytes.
    /// The seam is recorded here instead, and <see cref="Merge"/> reads it only for a block that is
    /// not the first of the range: the first block's seam is with whatever came before the range,
    /// which is nothing the range describes.
    /// </remarks>
    private long _leading;

    /// <summary>Whether <see cref="_leading"/> holds a step.</summary>
    internal bool LeadingKnown;

    /// <summary>
    /// Whether the seam cannot be a step at all: the row before was null, or the difference does not
    /// fit the wire field.
    /// </summary>
    internal bool LeadingBroken;

    /// <summary>Whether the physical kind has a row equality, so the boundaries mean anything.</summary>
    /// <remarks>
    /// The four <c>IsComparable</c> kinds of <see cref="ColumnCompressor"/>, which are the only ones
    /// run-end is ever offered. A struct or a list leaves this false and the chooser measures
    /// nothing, exactly as it declines to.
    /// </remarks>
    internal bool HasRunBoundaries;

    /// <summary>
    /// Whether this column's canonical form has a min/max a zone map can carry at all.
    /// </summary>
    /// <remarks>
    /// Set by the first batch that contributes a row, and never cleared: a column's kind does not
    /// change between batches, and a block that saw only nulls still belongs to a summarizable
    /// column.
    /// </remarks>
    internal bool IsSummarizable;

    /// <summary>Whether the block held a value that could bound it.</summary>
    /// <remarks>
    /// False for an all-null block and for a float block whose every value is NaN - the two cases
    /// that must say "no bound" rather than invent one.
    /// </remarks>
    internal bool HasBounds;

    /// <summary>
    /// Whether some rows of this block never reached the width histograms, so the block's pair of
    /// them is a partial count and not a summary.
    /// </summary>
    /// <remarks>
    /// A block is fed by as many ranges as the caller's batching gives it, and two of those ranges
    /// can take different paths through the pass: a range whose steps all agree is answered from
    /// its endpoints without a value being read, and a later range of the same block may break the
    /// progression and read every value. The histogram would then hold the second range and not the
    /// first. Merging is a conjunction over the blocks, so one partial block disqualifies the
    /// chunk, and the chooser walks the column itself — the same safe fallback every other absent
    /// statistic takes.
    /// </remarks>
    internal bool WidthsBroken;

    /// <summary>
    /// Whether this block's rows are the elements of list rows whose window does not abut the
    /// window before it, so that no chunk covering the block can be summarized from it.
    /// </summary>
    /// <remarks>
    /// A list's elements are summarized into the parent's blocks, and the merge of those blocks
    /// describes the elements array a chunk writes only when the ranges' windows lie end to end
    /// (<c>ListElements</c>). A block that cannot vouch for that carries this flag, even when
    /// nothing was summarized into it, and <see cref="Merge"/> keeps it from an absent block too:
    /// the chooser must then walk the elements, because a bound or a step read from the wrong
    /// elements would write wrong values rather than cost a pass. It takes one of the spare bytes
    /// the struct's padding already has.
    /// </remarks>
    internal bool Scattered;

    // Order, for the file statistics' is_sorted / is_strict_sorted. Tracked the way the reference
    // implementation computes them: a null sorts below every value, so a sorted nullable column has
    // its nulls first; two equal neighbours -- two values or two nulls -- keep it sorted and make
    // it not strict. A witness is sticky: one value below its predecessor and the column is
    // unsorted for good. What is not tracked claims nothing (OrderUntracked): a NaN, whose place in
    // the reference's order this pass does not reproduce, and the kinds without a scalar order.

    /// <summary>
    /// Whether some range tracked the rows' order at all: a primitive or a string column. A
    /// summary that never did claims nothing, whatever the witnesses say.
    /// </summary>
    internal bool OrderTracked;

    /// <summary>Whether the rows' order cannot be tracked, so neither flag is claimed.</summary>
    internal bool OrderUntracked;

    /// <summary>A row sorted below its predecessor, or a null came after a value.</summary>
    internal bool Unsorted;

    /// <summary>Two consecutive rows were equal, values or nulls.</summary>
    internal bool Repeats;

    /// <summary>
    /// The seam with the block before: whether this block's first row sorts below the previous
    /// block's last row (a break), or equals it (a repeat). Read by <see cref="Merge"/> for a
    /// non-first block, the way <see cref="_leading"/> is.
    /// </summary>
    internal bool OrderSeamBroken;

    /// <summary>The seam's repeat, see <see cref="OrderSeamBroken"/>.</summary>
    internal bool OrderSeamRepeat;

    /// <summary>Which accumulator <see cref="Min"/> and <see cref="Max"/> read.</summary>
    internal BoundDomain Domain;

    /// <summary>
    /// The scheme the chunk covering this block was written as, plus one; <c>0</c> until the chunk
    /// is out. Not folded by <see cref="Merge"/>: it describes the block's chunk, not its rows.
    /// </summary>
    /// <remarks>
    /// The report's ledger, and it costs no byte: the struct ends in a run of one-byte fields after
    /// its last word, and the padding already had room for one more. Keeping the same ledger as a
    /// per-column list of chunks would put a list and its array on every write, which the write
    /// path's allocation ceilings refuse.
    /// </remarks>
    internal byte WrittenScheme;

    /// <summary>
    /// The <c>is_sorted</c> statistic: non-decreasing with the nulls first, or null when the
    /// order was not tracked.
    /// </summary>
    internal readonly bool? IsSorted => OrderTracked && !OrderUntracked ? !Unsorted : null;

    /// <summary>The <c>is_strict_sorted</c> statistic: sorted with no two equal rows.</summary>
    internal readonly bool? IsStrictSorted =>
        OrderTracked && !OrderUntracked ? !Unsorted && !Repeats : null;

    /// <summary>Records what the row before a range says of the range's first row.</summary>
    /// <param name="order">The sign of <c>first - previous</c>, nulls below values.</param>
    /// <param name="startsBlock">Whether the range opens a block, so the seam is the block's.</param>
    internal void NoteOrderSeam(int order, bool startsBlock)
    {
        if (startsBlock)
        {
            OrderSeamBroken = order < 0;
            OrderSeamRepeat = order == 0;
            return;
        }

        if (order < 0)
        {
            Unsorted = true;
        }
        else if (order == 0)
        {
            Repeats = true;
        }
    }

    /// <summary>The smallest non-null, non-NaN value.</summary>
    internal FilterLiteral Min => Domain switch
    {
        BoundDomain.Signed => FilterLiteral.From(unchecked((long)_minBits)),
        BoundDomain.Unsigned => FilterLiteral.From(_minBits),
        BoundDomain.Float => FilterLiteral.From(BitConverter.UInt64BitsToDouble(_minBits)),
        _ => default,
    };

    /// <summary>The largest non-null, non-NaN value.</summary>
    internal FilterLiteral Max => Domain switch
    {
        BoundDomain.Signed => FilterLiteral.From(unchecked((long)_maxBits)),
        BoundDomain.Unsigned => FilterLiteral.From(_maxBits),
        BoundDomain.Float => FilterLiteral.From(BitConverter.UInt64BitsToDouble(_maxBits)),
        _ => default,
    };

    /// <summary>Whether anything was computed for this node.</summary>
    internal readonly bool IsPresent => Rows > 0;

    /// <summary>
    /// A block an append does not read again: what its zone says -- rows, nulls, and
    /// the bounds when they are exact -- and nothing else, so that the zone map can be written
    /// again over it. The order is not tracked: the file statistics answer it for the old rows.
    /// </summary>
    /// <param name="rows">The block's rows.</param>
    /// <param name="nulls">Its null count.</param>
    /// <param name="summarizable">Whether the column carries bounds at all.</param>
    /// <param name="min">The exact minimum, when there is one.</param>
    /// <param name="max">The exact maximum, when there is one.</param>
    /// <param name="scheme">What the block's chunk was written as, plus one; 0 when unknown.</param>
    internal static BlockStats Summary(
        long rows, long nulls, bool summarizable, FilterLiteral? min, FilterLiteral? max, byte scheme)
    {
        BlockStats block = default;
        block.Rows = rows;
        block.NullCount = nulls;
        block.IsSummarizable = summarizable;
        block.WidthsBroken = true;
        block.OrderUntracked = true;
        block.WrittenScheme = scheme;
        if (min is { } low && max is { } high && low.Kind == high.Kind)
        {
            switch (low.Kind)
            {
                case FilterLiteralKind.Signed:
                    block.Domain = BoundDomain.Signed;
                    block._minBits = unchecked((ulong)low.SignedValue);
                    block._maxBits = unchecked((ulong)high.SignedValue);
                    block.HasBounds = true;
                    break;
                case FilterLiteralKind.Unsigned:
                    block.Domain = BoundDomain.Unsigned;
                    block._minBits = low.UnsignedValue;
                    block._maxBits = high.UnsignedValue;
                    block.HasBounds = true;
                    break;
                case FilterLiteralKind.Float:
                    block.Domain = BoundDomain.Float;
                    block._minBits = BitConverter.DoubleToUInt64Bits(low.FloatValue);
                    block._maxBits = BitConverter.DoubleToUInt64Bits(high.FloatValue);
                    block.HasBounds = true;
                    break;
            }
        }

        return block;
    }

    /// <summary>
    /// Runs over the rows this summary covers, exactly as <c>ColumnCompressor</c> would have counted
    /// them by comparing adjacent rows.
    /// </summary>
    /// <remarks>
    /// One run, plus one for every row that starts a new one — minus the summary's own first row,
    /// whose boundary belongs to the range before this one and not to this one. A range that starts
    /// the file has <see cref="FirstRowStartsRun"/> false and loses nothing.
    /// </remarks>
    internal readonly long RunCount => Rows == 0 ? 0 : 1 + RunBoundaries - (FirstRowStartsRun ? 1 : 0);

    /// <summary>
    /// Folds a whole summary in, which is how a chunk is made from the blocks it covers.
    /// </summary>
    /// <remarks>
    /// Exact, which is the property the accumulators are built for: counts add, bounds take the
    /// extreme of the two, and the domain comes from whichever side has one. A chunk is a whole
    /// number of blocks by construction, so this is the only merge the chooser ever needs.
    /// </remarks>
    /// <param name="other">The summary to fold in; a default one is a no-op.</param>
    internal void Merge(in BlockStats other)
    {
        // The one fact an absent summary can carry: its elements cannot be summarized in place.
        Scattered |= other.Scattered;
        if (!other.IsPresent)
        {
            return;
        }

        // The first block folded in decides where the range starts, so its own leading boundary is
        // the one `RunCount` discounts. Every later block's leading boundary is interior to the
        // range and counts.
        bool first = !IsPresent;
        if (first)
        {
            FirstRowStartsRun = other.FirstRowStartsRun;
            HasRunBoundaries = other.HasRunBoundaries;
        }
        else
        {
            HasRunBoundaries &= other.HasRunBoundaries;
        }

        Rows += other.Rows;
        NullCount += other.NullCount;
        TotalBytes += other.TotalBytes;
        RunBoundaries += other.RunBoundaries;
        IsSummarizable |= other.IsSummarizable;
        WidthsBroken |= other.WidthsBroken;

        // Order: the witnesses add up, and a non-first block's seam is a witness of its own. The
        // first block's seam is with whatever came before the range, which is not the range's to
        // judge, so it is carried instead -- as the leading step is, below.
        OrderTracked |= other.OrderTracked;
        OrderUntracked |= other.OrderUntracked;
        Unsorted |= other.Unsorted;
        Repeats |= other.Repeats;
        if (first)
        {
            OrderSeamBroken = other.OrderSeamBroken;
            OrderSeamRepeat = other.OrderSeamRepeat;
        }
        else
        {
            Unsorted |= other.OrderSeamBroken;
            Repeats |= other.OrderSeamRepeat;
        }

        // A progression survives a merge only if both halves are one and they climb by the same
        // step, across the seam included. The seam is `other`'s leading step, and it is a step of
        // this range only when a block of this range came before `other`: the first block's seam
        // is with the chunk before, and a jump there is no jump inside this one.
        if (first)
        {
            _leading = other._leading;
            LeadingKnown = other.LeadingKnown;
            LeadingBroken = other.LeadingBroken;
        }
        else if (other.LeadingBroken)
        {
            DeltaBroken = true;
        }
        else if (other.LeadingKnown)
        {
            if (DeltaKnown && _delta != other._leading)
            {
                DeltaBroken = true;
            }

            _delta = other._leading;
            DeltaKnown = true;
        }

        DeltaBroken |= other.DeltaBroken;
        if (other.DeltaKnown)
        {
            if (DeltaKnown && _delta != other._delta)
            {
                DeltaBroken = true;
            }

            _delta = other._delta;
            DeltaKnown = true;
        }

        if (!other.HasBounds)
        {
            return;
        }

        switch (other.Domain)
        {
            case BoundDomain.Signed:
                MergeSigned(unchecked((long)other._minBits), unchecked((long)other._maxBits));
                return;
            case BoundDomain.Unsigned:
                MergeUnsigned(other._minBits, other._maxBits);
                return;
            default:
                MergeFloat(
                    BitConverter.UInt64BitsToDouble(other._minBits),
                    BitConverter.UInt64BitsToDouble(other._maxBits));
                return;
        }
    }

    /// <summary>The step seen so far; meaningless unless <see cref="DeltaKnown"/>.</summary>
    internal readonly long Delta => _delta;

    /// <summary>The seam's step; meaningless unless <see cref="LeadingKnown"/>.</summary>
    internal readonly long Leading => _leading;

    /// <summary>
    /// Records the step a range walked, the range having already checked that every pair agrees.
    /// </summary>
    /// <remarks>
    /// The check belongs in the caller's loop, on locals: a method call and a field read per row
    /// costs more than the separate walk this folds in.
    /// </remarks>
    /// <param name="delta">The difference between a row and the row before it.</param>
    internal void SetDelta(long delta)
    {
        if (DeltaKnown && _delta != delta)
        {
            DeltaBroken = true;
            return;
        }

        _delta = delta;
        DeltaKnown = true;
    }

    /// <summary>Marks the rows as no progression, whatever the steps so far said.</summary>
    internal void BreakDelta() => DeltaBroken = true;

    /// <summary>Records the seam: the step from the row before the block into its first row.</summary>
    /// <param name="delta">That difference.</param>
    internal void SetLeading(long delta)
    {
        _leading = delta;
        LeadingKnown = true;
    }

    /// <summary>Marks the seam as no step: a null before the block, or a difference too wide.</summary>
    internal void BreakLeading() => LeadingBroken = true;

    /// <summary>Folds one signed bound pair in, widening to the accumulator's domain.</summary>
    /// <param name="min">The smallest value the caller saw.</param>
    /// <param name="max">The largest.</param>
    internal void MergeSigned(long min, long max)
    {
        if (!HasBounds)
        {
            Domain = BoundDomain.Signed;
            HasBounds = true;
            _minBits = unchecked((ulong)min);
            _maxBits = unchecked((ulong)max);
            return;
        }

        if (min < unchecked((long)_minBits))
        {
            _minBits = unchecked((ulong)min);
        }

        if (max > unchecked((long)_maxBits))
        {
            _maxBits = unchecked((ulong)max);
        }
    }

    /// <summary>Folds one unsigned bound pair in.</summary>
    /// <param name="min">The smallest value the caller saw.</param>
    /// <param name="max">The largest.</param>
    internal void MergeUnsigned(ulong min, ulong max)
    {
        if (!HasBounds)
        {
            Domain = BoundDomain.Unsigned;
            HasBounds = true;
            _minBits = min;
            _maxBits = max;
            return;
        }

        if (min < _minBits)
        {
            _minBits = min;
        }

        if (max > _maxBits)
        {
            _maxBits = max;
        }
    }

    /// <summary>Folds one float bound pair in.</summary>
    /// <remarks>
    /// <see cref="Math.Min(double, double)"/> rather than <c>&lt;</c>, and that is not a detail:
    /// <c>-0.0 &lt; +0.0</c> is false, so a raw compare would keep whichever arrived first and make
    /// the bound depend on the batching. The two are distinct values here, and Math.Min/Max are the
    /// functions that order them.
    /// </remarks>
    /// <param name="min">The smallest value the caller saw; never NaN.</param>
    /// <param name="max">The largest; never NaN.</param>
    internal void MergeFloat(double min, double max)
    {
        if (!HasBounds)
        {
            Domain = BoundDomain.Float;
            HasBounds = true;
            _minBits = BitConverter.DoubleToUInt64Bits(min);
            _maxBits = BitConverter.DoubleToUInt64Bits(max);
            return;
        }

        _minBits = BitConverter.DoubleToUInt64Bits(
            Math.Min(BitConverter.UInt64BitsToDouble(_minBits), min));
        _maxBits = BitConverter.DoubleToUInt64Bits(
            Math.Max(BitConverter.UInt64BitsToDouble(_maxBits), max));
    }
}
