// What a parent already knows about the child it is about to write -- docs/11-write-strategy.md
// §3.4.4, "A child never re-enters `Choose` blind".
//
// THE COST OF ENTERING BLIND, MEASURED ON THE CORPUS. A dictionary's codes column goes through the
// whole chooser: a sequence walk, a run-end scan that rents two `int[rows]`, a frame-of-reference
// minimum (two walks), a width histogram, a patch gather, and a dictionary probe over every code.
// Seven walks over a million codes, on a column whose shape the parent had already established
// before it produced it. Five of the eight worst encodings on the write axis are dictionaries.
//
// EVERY SKIP HERE IS A PROOF, NOT A HEURISTIC, and the proof is written next to the flag it
// justifies. That is what makes this a chooser shortcut rather than a chooser change: the plan the
// child gets is the plan it would have got, so not a byte moves. A shortcut that merely *usually*
// agrees would belong to a different kind of change, priced against `WrittenSizeTests` rather than
// verified against it.
namespace Vorticity.Writing;

/// <summary>What the parent knows about a child column, so the chooser need not rediscover it.</summary>
internal readonly struct Cascade
{
    private Cascade(bool runs, bool dictionary, bool sequence, ulong? reference)
    {
        RunsAreDead = runs;
        DictionaryIsDead = dictionary;
        SequenceIsDead = sequence;
        Reference = reference;
    }

    /// <summary>
    /// Whether run-end is provably declined, so the scan that would decline it need not run.
    /// </summary>
    internal bool RunsAreDead { get; }

    /// <summary>
    /// Whether a dictionary is provably declined, so the probe that would decline it need not run.
    /// </summary>
    internal bool DictionaryIsDead { get; }

    /// <summary>Whether the column is provably not an arithmetic progression.</summary>
    internal bool SequenceIsDead { get; }

    /// <summary>
    /// The column's minimum in raw bits when the parent knows it, sparing the pass that finds it.
    /// </summary>
    internal ulong? Reference { get; }

    /// <summary>A dictionary's codes: one code per row, dense in <c>[0, entries)</c>.</summary>
    /// <remarks>
    /// <para>
    /// RUN-END IS DEAD because the code map is injective: equal values get equal codes and different
    /// values get different ones, so the codes have EXACTLY the parent's run boundaries. The parent
    /// reached the dictionary only by declining run-end — the edition does not carry it, or the
    /// count is above <c>rows / 4</c> — and the same verdict, on the same count, applies here.
    /// </para>
    /// <para>
    /// A DICTIONARY IS DEAD by arithmetic. A dictionary over the codes holds exactly
    /// <c>entries</c> distinct values, so its own codes are as wide as these ones
    /// (<c>IndexPType(entries)</c> either way) and cost the same bytes, before its values child and
    /// its 512 bytes of overhead. It cannot win, whatever the data.
    /// </para>
    /// <para>
    /// A PROGRESSION IS DEAD once there are at least two entries. A progression needs a constant
    /// step; with <c>entries &lt; rows</c> some pair of adjacent codes repeats, giving a step of
    /// zero, so a constant step forces every step to zero and every code to be equal — which is
    /// <c>entries == 1</c>. The one-entry case is therefore NOT claimed here, because a constant
    /// column does reach this point when the edition has no <c>vortex.runend</c>, and its codes
    /// really are a progression.
    /// </para>
    /// <para>
    /// THE MINIMUM IS ZERO because codes are assigned in first-seen order from zero, so row 0 holds
    /// code 0 and no code is below it.
    /// </para>
    /// </remarks>
    /// <param name="entries">How many distinct values the dictionary holds.</param>
    internal static Cascade DictionaryCodes(int entries) =>
        new Cascade(runs: true, dictionary: true, sequence: entries > 1, reference: 0);

    /// <summary>A run-end's ends: strictly increasing, bounded by the row count.</summary>
    /// <remarks>
    /// <para>
    /// RUN-END IS DEAD because the ends are strictly increasing, so no two adjacent values are
    /// equal and every row is its own run: the count is the row count, which is four times the
    /// ceiling the scan would compare it against.
    /// </para>
    /// <para>
    /// A DICTIONARY IS DEAD for the same reason: strictly increasing means all distinct, so the
    /// dictionary holds one entry per row and its codes alone cost what the column costs.
    /// </para>
    /// <para>
    /// A PROGRESSION IS NOT dead, and this is the one that matters: a column of equal-length runs
    /// has ends that are an exact arithmetic progression, which <c>vortex.sequence</c> writes in
    /// about thirty bytes. Nor is the minimum known — it is the first run's end, which the parent
    /// has but does not carry here yet.
    /// </para>
    /// </remarks>
    internal static Cascade RunEndEnds() =>
        new Cascade(runs: true, dictionary: true, sequence: false, reference: null);
}
