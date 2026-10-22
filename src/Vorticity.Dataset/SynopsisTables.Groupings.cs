using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The tables of the groupings an application declares (<see cref="DatasetOptions.SynopsisGroupings"/>): columns that
/// questions filter and group by together, whatever columns the order places between them, of which no prefix table holds
/// the combinations but the deepest, whose rows tend to the file's. Each grouping keeps a table by hour and one by object,
/// coarse tables of its keys in its own order, their chunks bounded by its first key's values.
/// </summary>
/// <remarks>
/// A slice's rows are grouped by their keys' codes, a pair of a group and a code a key after the other, and its groups go
/// in the order of their keys to a run of the hour's store; the hour's end merges its runs into the hour's table and a run
/// of the object's store, whose merge at the end gives the object's table. The groups are weighed as the prefixes' are,
/// by their distinct keys projected over the hour and the object, and given up as they grow past the budget.
/// </remarks>
internal sealed partial class SynopsisTables
{
    /// <summary>
    /// The groupings of synopsis columns, by name, whose tables the object writes beside the prefixes of its order: each a
    /// top-level column or a key of a variant column written as a filter reads it. A grouping that names a column the
    /// order does not hold, or names one twice, writes none.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<string>>? Groupings { get; init; }

    // Each grouping's tables and what builds and weighs them, which the first slice settles; and the rows' groups of the
    // slice counted, reused from slice to slice.
    private Grouping[] _groupings = [];
    private int[] _groupingRows = [];

    /// <summary>One grouping: its keys' places in the order, in its own order, their shapes, its tables and their runs.</summary>
    private sealed class Grouping(int[] positions, KeyShape[] shapes)
    {
        internal int[] Positions { get; } = positions;

        internal KeyShape[] Shapes { get; } = shapes;

        internal Candidate? Hourly { get; init; }

        internal Candidate? Whole { get; set; }

        // The distinct keys of the hour and of the object as their slices come, and those of the hours merged.
        internal Growth? HourGrowth { get; init; }

        internal Growth? ObjectGrowth { get; init; }

        internal long HourSum { get; set; }

        // The hour's runs of slices, in the hour's store, and the object's runs of hours, in the object's.
        internal List<RunSpan> HourRuns { get; } = [];

        internal List<RunSpan> WholeRuns { get; } = [];

        internal bool Kept => Hourly is { Dead: false } || Whole is { Dead: false };
    }

    /// <summary>The tables of the declared groupings whose columns the order holds, once its keys are settled.</summary>
    private void SettleGroupings()
    {
        List<Grouping> settled = [];
        List<HashSet<int>> seen = [];
        foreach (IReadOnlyList<string> names in Groupings ?? [])
        {
            if (PositionsOf(names) is not { Length: >= 2 } positions)
            {
                continue;
            }

            HashSet<int> set = [.. positions];
            if (set.Count != positions.Length || seen.Exists(other => other.SetEquals(set)))
            {
                continue;
            }

            seen.Add(set);
            int[] keys = new int[positions.Length];
            KeyShape[] shapes = new KeyShape[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                keys[i] = _keys[positions[i]];
                shapes[i] = _keyShapes[positions[i]];
            }

            Candidate hourly = Add(SynopsisTableKind.Coarse, SynopsisBucket.Hour, keys, shapes, bounded: true);
            Candidate? whole = _spansHours ? Add(SynopsisTableKind.Coarse, SynopsisBucket.Object, keys, shapes, bounded: true) : null;
            settled.Add(new Grouping(positions, shapes)
            {
                Hourly = hourly,
                Whole = whole,
                HourGrowth = _objectRows > 0 ? new Growth() : null,
                ObjectGrowth = _objectRows > 0 && whole is not null ? new Growth() : null,
            });
        }

        _groupings = [.. settled];
    }

    /// <summary>The places in the order of the columns <paramref name="names"/> names; null when one is not a key of it.</summary>
    private int[]? PositionsOf(IReadOnlyList<string> names)
    {
        if (names.Count is < 2 or > 4)
        {
            return null;
        }

        int[] positions = new int[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int entry = _schema.IndexOfField(names[i]);
            if (entry < 0 && VortexDataset.ShreddedPath(names[i], _schema) is { } path)
            {
                for (int p = 0; p < _paths.Count && entry < 0; p++)
                {
                    if (_paths[p].Field == path.Field && string.Equals(_paths[p].Key, path.Key, StringComparison.Ordinal) && _paths[p].Class == path.Class)
                    {
                        entry = SynopsisPath.Entry(p);
                    }
                }
            }

            positions[i] = entry < 0 ? -1 : Array.IndexOf(_keys, entry);
            if (positions[i] < 0)
            {
                return null;
            }
        }

        return positions;
    }

    /// <summary>
    /// Counts the slice into each grouping still kept: its rows grouped by their keys'
    /// codes <paramref name="keyCodes"/>, the measures folded into the groups, and the groups in the order of their keys,
    /// read off the key columns <paramref name="columns"/>, a run of the hour's store.
    /// </summary>
    private async ValueTask AddGroupingsAsync(
        CanonicalArena arena, int root, int rows, List<int[]> keyCodes, int[] columns, CancellationToken cancellationToken)
    {
        foreach (Grouping grouping in _groupings)
        {
            if (!grouping.Kept)
            {
                continue;
            }

            if (_groupingRows.Length < rows)
            {
                _groupingRows = new int[rows];
            }

            // A pair of a group and a code a key after the other, numbered in the order of their first rows.
            int[] groups = _groupingRows;
            Array.Clear(groups, 0, rows);
            int count = 1;
            foreach (int position in grouping.Positions)
            {
                count = Pairs(keyCodes[position], groups, groups, rows, _pairs);
            }

            Accumulators states = Fold(arena, root, rows, groups, count);
            int[] firsts = Firsts(groups, count, rows);
            int[] order = new int[count];
            for (int g = 0; g < count; g++)
            {
                order[g] = g;
            }

            Array.Sort(order, (left, right) =>
            {
                foreach (int position in grouping.Positions)
                {
                    int compared = keyCodes[position][firsts[left]].CompareTo(keyCodes[position][firsts[right]]);
                    if (compared != 0)
                    {
                        return compared;
                    }
                }

                return left.CompareTo(right);
            });

            Growth? hour = Counts(grouping.Hourly, grouping.HourGrowth);
            Growth? whole = Counts(grouping.Whole, grouping.ObjectGrowth);
            _run.Clear();
            foreach (int g in order)
            {
                _key.Clear();
                for (int k = 0; k < grouping.Positions.Length; k++)
                {
                    KeyCodec.Append(_key, arena, columns[grouping.Positions[k]], firsts[g], grouping.Shapes[k]);
                }

                ulong hash = CardinalitySlot.Hash(_key.Span);
                hour?.Sketch.Add(hash);
                whole?.Sketch.Add(hash);
                states.Words(g, _state);
                _run.Bytes(_key.Span);
                _run.Words(_state);
            }

            grouping.HourRuns.Add(await AppendAsync(HourStore(), grouping.Positions.Length, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Notes the distinct keys of each grouping still kept after the rows counted.</summary>
    private void NoteGroupings(long hourRows, long objectRows)
    {
        foreach (Grouping grouping in _groupings)
        {
            Counts(grouping.Hourly, grouping.HourGrowth)?.Note(hourRows);
            Counts(grouping.Whole, grouping.ObjectGrowth)?.Note(objectRows);
        }
    }

    /// <summary>
    /// Ends the hour of each grouping: its slices' runs merged, a group a key, into the rows of the hour's table and a run
    /// of the object's store.
    /// </summary>
    private async ValueTask CloseGroupingsAsync(CancellationToken cancellationToken)
    {
        foreach (Grouping grouping in _groupings)
        {
            if (grouping.HourRuns.Count > 0 && grouping.Kept)
            {
                bool wholeRuns = grouping.Whole is { Dead: false };
                long runStart = -1;
                long runLength = 0;
                _run.Clear();
                using (RunMerger merger = await RunMerger.OpenAsync(
                    _hourScratch!, grouping.HourRuns, grouping.Shapes, grouping.Positions.Length, _kinds, cancellationToken).ConfigureAwait(false))
                {
                    while (await merger.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        grouping.HourSum++;
                        if (grouping.Hourly is { Dead: false } table && Hold(table, _hour, merger.Key, merger.Words))
                        {
                            await FlushAsync(table, cancellationToken).ConfigureAwait(false);
                        }

                        if (wholeRuns)
                        {
                            _run.Bytes(merger.Key);
                            _run.Words(merger.Words);
                            if (_run.Length >= RunFlushBytes)
                            {
                                await FlushWholeRunAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }

                if (wholeRuns)
                {
                    await FlushWholeRunAsync().ConfigureAwait(false);
                    if (runLength > 0)
                    {
                        grouping.WholeRuns.Add(new RunSpan(runStart, runLength, grouping.Positions.Length));
                    }
                }

                // The object's run so far, appended to its store after its earlier bytes.
                async ValueTask FlushWholeRunAsync()
                {
                    if (_run.Length == 0)
                    {
                        return;
                    }

                    _wholeScratch ??= NewStore();
                    long at = await _wholeScratch.AppendAsync(_run.Memory, cancellationToken).ConfigureAwait(false);
                    runStart = runStart < 0 ? at : runStart;
                    runLength += _run.Length;
                    _run.Clear();
                }
            }

            grouping.HourRuns.Clear();
            grouping.HourGrowth?.Clear();
            if (grouping.Hourly is { } hourly && hourly.Stage.RawBytes >= _chunking.HourBytes)
            {
                await FlushAsync(hourly, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The object's table of each grouping, its hours' runs merged, when the object spans hours; none for an object of one
    /// hour, whose table of the hour says the same.
    /// </summary>
    private async ValueTask FinishGroupingsAsync(CancellationToken cancellationToken)
    {
        foreach (Grouping grouping in _groupings)
        {
            if (grouping.Whole is not { } whole)
            {
                continue;
            }

            if (_hours <= 1)
            {
                whole.Dead = true;
                whole.Held = -1;
                whole.Stage.Release();
                continue;
            }

            if (whole.Dead || grouping.WholeRuns.Count == 0)
            {
                continue;
            }

            using RunMerger merger = await RunMerger.OpenAsync(
                _wholeScratch!, grouping.WholeRuns, grouping.Shapes, grouping.Positions.Length, _kinds, cancellationToken).ConfigureAwait(false);
            while (await merger.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Hold(whole, 0, merger.Key, merger.Words))
                {
                    await FlushAsync(whole, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Gives up the groupings' tables their distinct keys rule out against the budget of the whole object, as
    /// <see cref="Prune"/> does the prefixes': the hour's, the keys of the hours merged and the hour's projected to its rows,
    /// scaled to the object's; the object's, its keys projected to its rows.
    /// </summary>
    private void PruneGroupings(Action<Candidate, double, double, double> ruleOut, long hourRows, double scale, double perRow)
    {
        foreach (Grouping grouping in _groupings)
        {
            if (Counts(grouping.Hourly, grouping.HourGrowth) is { } hour)
            {
                ruleOut(grouping.Hourly!, grouping.HourSum + hour.Distinct, (grouping.HourSum + hour.Projected(hourRows)) * scale, perRow);
            }

            if (Counts(grouping.Whole, grouping.ObjectGrowth) is { } all)
            {
                ruleOut(grouping.Whole!, all.Distinct, all.Projected(_objectRows), perRow);
            }
        }
    }
}
