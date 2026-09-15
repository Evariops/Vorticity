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
    private long _minSigned;
    private long _maxSigned;
    private ulong _minUnsigned;
    private ulong _maxUnsigned;
    private double _minFloat;
    private double _maxFloat;

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

    /// <summary>Which accumulator <see cref="Min"/> and <see cref="Max"/> read.</summary>
    internal BoundDomain Domain;

    /// <summary>The smallest non-null, non-NaN value.</summary>
    internal FilterLiteral Min => Domain switch
    {
        BoundDomain.Signed => FilterLiteral.From(_minSigned),
        BoundDomain.Unsigned => FilterLiteral.From(_minUnsigned),
        BoundDomain.Float => FilterLiteral.From(_minFloat),
        _ => default,
    };

    /// <summary>The largest non-null, non-NaN value.</summary>
    internal FilterLiteral Max => Domain switch
    {
        BoundDomain.Signed => FilterLiteral.From(_maxSigned),
        BoundDomain.Unsigned => FilterLiteral.From(_maxUnsigned),
        BoundDomain.Float => FilterLiteral.From(_maxFloat),
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

        if (!other.HasBounds)
        {
            return;
        }

        switch (other.Domain)
        {
            case BoundDomain.Signed:
                MergeSigned(other._minSigned, other._maxSigned);
                return;
            case BoundDomain.Unsigned:
                MergeUnsigned(other._minUnsigned, other._maxUnsigned);
                return;
            default:
                MergeFloat(other._minFloat, other._maxFloat);
                return;
        }
    }

    /// <summary>Folds one signed bound pair in, widening to the accumulator's domain.</summary>
    /// <param name="min">The smallest value the caller saw.</param>
    /// <param name="max">The largest.</param>
    internal void MergeSigned(long min, long max)
    {
        if (!HasBounds)
        {
            Domain = BoundDomain.Signed;
            HasBounds = true;
            _minSigned = min;
            _maxSigned = max;
            return;
        }

        if (min < _minSigned)
        {
            _minSigned = min;
        }

        if (max > _maxSigned)
        {
            _maxSigned = max;
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
            _minUnsigned = min;
            _maxUnsigned = max;
            return;
        }

        if (min < _minUnsigned)
        {
            _minUnsigned = min;
        }

        if (max > _maxUnsigned)
        {
            _maxUnsigned = max;
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
            _minFloat = min;
            _maxFloat = max;
            return;
        }

        _minFloat = Math.Min(_minFloat, min);
        _maxFloat = Math.Max(_maxFloat, max);
    }
}
