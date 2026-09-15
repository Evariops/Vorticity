// What one block of one column is, in the only terms the writer needs - docs/11-write-strategy.md
// §3.2, stage 1 of its §8.
//
// A BLOCK IS NOT A CHUNK, and that distinction is the whole point. A chunk is an emission unit whose
// size the caller's batching and the byte target decide together; a block is `RowBlockSize` rows
// counted from row 0 OF THE FILE, so it is the same range whatever the batching. The zone map wants
// the second and was given the first, which is why five corpus files lose their zone map today: a
// ragged chunking has no single zone length to declare (WRITE-ARCHITECTURE.md §3.7). Summarizing per
// block removes the question.
//
// MERGEABLE, BECAUSE A BATCH STRADDLES BLOCKS. 8 131 rows handed over at a time never line up with
// 8 192, so what the pass produces is a PARTIAL that the next batch continues: min, max and the
// counts merge exactly, and `Merge` is the proof that nothing here needs the rows a second time.
//
// The bounds live in the domain the column is read in - signed, unsigned or float - rather than in a
// FilterLiteral, so accumulating one value costs a compare and not a construction; the literal is
// built once per block, when the zone map asks for it.
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
    // ONE PAIR OF WORDS, NOT THREE. The three domains are mutually exclusive -- `Domain` says which
    // one a summary is in -- so they share the storage and `BlockStats` goes from 88 bytes to 56.
    // That is not tidiness: the writer keeps one of these per (column, closed block) until the zone
    // map is written, which at a million rows is 123 per column, and one per NODE of the schema tree
    // since the column writers became a tree.
    private ulong _minBits;
    private ulong _maxBits;

    /// <summary>Rows accumulated into this block so far.</summary>
    /// <remarks>
    /// ALSO THE PRESENCE TEST. A block or a chunk always has rows, so <c>Rows == 0</c> means "no
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
    /// Rows of this block that differ from the row before them IN THE FILE, the block's own first
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
    /// cannot be CONSTANT over three rows -- the values would have to wrap, and a wrapped difference
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
    /// Stated in the NEGATIVE so that a default summary -- which knows nothing -- is not already
    /// claiming to be a sequence. <see cref="DeltaKnown"/> is what says the claim was ever made.
    /// </remarks>
    internal bool DeltaBroken;

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
    /// docs/08-semantics.md §2 says must say "no bound" rather than invent one.
    /// </remarks>
    internal bool HasBounds;

    /// <summary>
    /// Whether some rows of this block never reached the width histograms, so the block's pair of
    /// them is a partial count and not a summary.
    /// </summary>
    /// <remarks>
    /// A BLOCK IS FED BY AS MANY RANGES AS THE CALLER'S BATCHING GIVES IT, and two of those ranges
    /// can take different paths through the pass: a range whose steps all agree is answered from
    /// its endpoints without a value being read (§3.2's progression short-circuit), and a later
    /// range of the same block may break the progression and read every value. The histogram would
    /// then hold the second range and not the first. Merging is an AND over the blocks, so one
    /// partial block disqualifies the chunk, and the chooser measures the column itself — the same
    /// safe fallback every other absent statistic takes.
    /// </remarks>
    internal bool WidthsBroken;

    /// <summary>Which accumulator <see cref="Min"/> and <see cref="Max"/> read.</summary>
    internal BoundDomain Domain;

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
    /// Exact, and that is the property stage 1 built the accumulators for: counts add, bounds take
    /// the extreme of the two, and the domain comes from whichever side has one. A chunk is a whole
    /// number of blocks by construction (docs/11-write-strategy.md §3.1), so this is the only merge
    /// the chooser ever needs.
    /// </remarks>
    /// <param name="other">The summary to fold in; a default one is a no-op.</param>
    internal void Merge(in BlockStats other)
    {
        if (!other.IsPresent)
        {
            return;
        }

        // The first block folded in decides where the range starts, so its own leading boundary is
        // the one `RunCount` discounts. Every later block's leading boundary is interior to the
        // range and counts.
        if (!IsPresent)
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

        // A progression survives a merge only if both halves are one AND they climb by the same
        // step -- the step across the seam is already in `other`, which saw the row before it.
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

    /// <summary>
    /// Records the step a range walked, the range having already checked that every pair agrees.
    /// </summary>
    /// <remarks>
    /// The check belongs in the caller's loop, on LOCALS: a method call and a field read per row was
    /// measurably more expensive than the walk this replaces (`sequence` 0,16 -&gt; 0,22 before it
    /// was moved out).
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
    /// the bound depend on the batching. docs/07-dotnet-mapping.md counts the two as distinct
    /// values, and Math.Min/Max are the functions that order them.
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
