namespace Vorticity.Writing;

/// <summary>
/// What the parent knows about a child column, so the chooser need not rediscover it: a child that
/// enters the chooser blind is walked half a dozen times over to establish a shape its parent had
/// already established before producing it.
/// </summary>
/// <remarks>
/// Every skip declared here is a proof, written beside the flag it justifies, and not a heuristic.
/// That is what makes this a shortcut through the chooser rather than a change to it: the plan a
/// child gets is the plan it would have got, so the bytes written are unchanged. A shortcut that
/// merely usually agreed would be a different kind of change, and would have to be priced rather
/// than proved.
/// </remarks>
internal readonly struct Cascade
{
    private Cascade(bool runs, bool dictionary, bool sequence, ulong? reference, bool valuesChild = false)
    {
        RunsAreDead = runs;
        DictionaryIsDead = dictionary;
        SequenceIsDead = sequence;
        Reference = reference;
        IsValuesChild = valuesChild;
    }

    /// <summary>
    /// Whether this column is the values child of a dictionary or a run-end, so that zstd's size
    /// gate — a cost guard for whole columns — does not close on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size gate exists so that a zstd pass is not paid on a column too small to repay it, and
    /// it is lifted here because a values child of a small parent is exactly where zstd still wins:
    /// the entries are few but they repeat, and closing the gate on them costs bytes in the file.
    /// </para>
    /// <para>
    /// The minimum-bytes guard in front of FSST is not lifted with it. The trainer allocates its
    /// tables per call whatever the input, so training on a child of a few dozen entries costs far
    /// more memory than the output it saves.
    /// </para>
    /// </remarks>
    internal bool IsValuesChild { get; }

    /// <summary>
    /// For the storage of a timestamp, the number of its units in a second, so the chooser can
    /// price the instants split into days, seconds and subseconds; zero for any other column.
    /// </summary>
    internal long UnitsPerSecond { get; private init; }

    /// <summary>The storage of a timestamp in units of <c>1 / unitsPerSecond</c> seconds.</summary>
    internal static Cascade TimestampStorage(long unitsPerSecond) =>
        new Cascade(runs: false, dictionary: false, sequence: false, reference: null) { UnitsPerSecond = unitsPerSecond };

    /// <summary>The values child of a dictionary or a run-end: a column like any other, ungated.</summary>
    internal static Cascade ValuesChild() =>
        new Cascade(runs: false, dictionary: false, sequence: false, reference: null, valuesChild: true);

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
    /// Run-end is dead because the code map is injective: equal values get equal codes and different
    /// values get different ones, so the codes have exactly the parent's run boundaries. The parent
    /// reached the dictionary only by declining run-end — the edition does not carry it, or the
    /// count is above <c>rows / 4</c> — and the same verdict, on the same count, applies here.
    /// </para>
    /// <para>
    /// A dictionary is dead by arithmetic. A dictionary over the codes holds exactly
    /// <c>entries</c> distinct values, so its own codes are as wide as these ones
    /// (<c>IndexPType(entries)</c> either way) and cost the same bytes, before its values child and
    /// its own header. It cannot win, whatever the data.
    /// </para>
    /// <para>
    /// A progression is dead once there are at least two entries. A progression needs a constant
    /// step; with <c>entries &lt; rows</c> some pair of adjacent codes repeats, giving a step of
    /// zero, so a constant step forces every step to zero and every code to be equal — which is
    /// <c>entries == 1</c>. The one-entry case is therefore not claimed here, because a constant
    /// column does reach this point when the edition has no <c>vortex.runend</c>, and its codes
    /// really are a progression.
    /// </para>
    /// <para>
    /// The minimum is zero because codes are assigned in first-seen order from zero, so row 0 holds
    /// code 0 and no code is below it.
    /// </para>
    /// </remarks>
    /// <param name="entries">How many distinct values the dictionary holds.</param>
    internal static Cascade DictionaryCodes(int entries) =>
        new Cascade(runs: true, dictionary: true, sequence: entries > 1, reference: 0);

    /// <summary>A run-end's ends: strictly increasing, bounded by the row count.</summary>
    /// <remarks>
    /// <para>
    /// Run-end is dead because the ends are strictly increasing, so no two adjacent values are
    /// equal and every row is its own run: the count is the row count, which is four times the
    /// ceiling the scan would compare it against.
    /// </para>
    /// <para>
    /// A dictionary is dead for the same reason: strictly increasing means all distinct, so the
    /// dictionary holds one entry per row and its codes alone cost what the column costs.
    /// </para>
    /// <para>
    /// A progression is not dead, and this is the one that matters: a column of equal-length runs
    /// has ends that are an exact arithmetic progression, which <c>vortex.sequence</c> writes in a
    /// handful of bytes. The minimum is not known either — it is the first run's end, which the
    /// parent has but does not carry here.
    /// </para>
    /// </remarks>
    internal static Cascade RunEndEnds() =>
        new Cascade(runs: true, dictionary: true, sequence: false, reference: null);
}
