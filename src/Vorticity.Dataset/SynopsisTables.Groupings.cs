using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The tables of the groupings an application declares (<see cref="DatasetOptions.SynopsisGroupings"/>): columns that
/// questions filter and group by together, whatever columns the order places between them, of which no prefix table holds
/// the combinations but the deepest, whose rows tend to the file's. A grouping of two columns or more keeps a table of
/// states by hour and one by object, coarse tables of its keys in its own order, their chunks bounded by its first key's
/// values; every grouping keeps tables of sketches of its measures by slice, by hour and by object, which a quantile or a
/// distribution over a window that cuts an hour reads slice by slice.
/// </summary>
/// <remarks>
/// <para>
/// A slice's rows are grouped by their keys' codes, a pair of a group and a code a key after the other, and its groups go
/// in the order of their keys to a run of the hour's store; the hour's end merges its runs into the hour's table and a run
/// of the object's store, whose merge at the end gives the object's table. The groups are weighed as the prefixes' are,
/// by their distinct keys projected over the hour and the object, and given up as they grow past the budget.
/// </para>
/// <para>
/// The slice's groups also fold their measures' values into sketches, which go out as the rows of the slice's table of
/// sketches and into the hour's groups, the hour's into the object's. Groups of sketches past
/// <see cref="HeldSketchGroups"/> in an hour or the object, or past <see cref="HeldSketchBytes"/>, give their tables of
/// sketches up, as the prefixes' do.
/// </para>
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

    /// <summary>One grouping: its keys' places in the order, in its own order, their shapes, its tables and what feeds them.</summary>
    private sealed class Grouping(int[] positions, SynopsisKeyShape[] shapes)
    {
        internal int[] Positions { get; } = positions;

        internal SynopsisKeyShape[] Shapes { get; } = shapes;

        // Its tables of states, by hour and by object; none for a grouping of one column, whose values' counts the order keeps.
        internal Candidate? Hourly { get; init; }

        internal Candidate? Whole { get; init; }

        // Its tables of sketches, by slice, by hour and by object, and the groups that feed them.
        internal Candidate? SliceSketches { get; init; }

        internal Candidate? HourSketches { get; init; }

        internal Candidate? WholeSketches { get; init; }

        internal SketchGroups? SliceGroups { get; set; }

        internal SketchGroups? HourGroups { get; set; }

        internal SketchGroups? WholeGroups { get; set; }

        // The distinct keys of the hour and of the object as their slices come, and those of the hours merged.
        internal Growth? HourGrowth { get; init; }

        internal Growth? ObjectGrowth { get; init; }

        internal long HourSum { get; set; }

        // The hour's runs of slices, in the hour's store, and the object's runs of hours, in the object's.
        internal List<RunSpan> HourRuns { get; } = [];

        internal List<RunSpan> WholeRuns { get; } = [];

        /// <summary>Whether a table of states of the grouping is still kept.</summary>
        internal bool Kept => Hourly is { Dead: false } || Whole is { Dead: false };

        /// <summary>Whether a table of sketches of the grouping is still kept, and its groups fed.</summary>
        internal bool Sketching => SliceGroups is not null
            && (SliceSketches is { Dead: false } || HourSketches is { Dead: false } || WholeSketches is { Dead: false });
    }

    /// <summary>The tables of the declared groupings whose columns the order holds, once its keys are settled.</summary>
    private void SettleGroupings()
    {
        List<Grouping> settled = [];
        List<HashSet<int>> seen = [];
        foreach (IReadOnlyList<string> names in Groupings ?? [])
        {
            if (PositionsOf(names) is not { } positions)
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
            SynopsisKeyShape[] shapes = new SynopsisKeyShape[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                keys[i] = _keys[positions[i]];
                shapes[i] = _keyShapes[positions[i]];
            }

            bool states = positions.Length >= 2;
            bool sketches = _measures.Length > 0;
            Candidate? hourly = states ? Add(SynopsisTableKind.Coarse, SynopsisBucket.Hour, keys, shapes, bounded: true) : null;
            Candidate? whole = states && _spansHours ? Add(SynopsisTableKind.Coarse, SynopsisBucket.Object, keys, shapes, bounded: true) : null;
            settled.Add(new Grouping(positions, shapes)
            {
                Hourly = hourly,
                Whole = whole,
                SliceSketches = sketches && _cutsHours ? Add(SynopsisTableKind.Sketches, SynopsisBucket.Slice, keys, shapes, bounded: true, distinctBelow: false) : null,
                HourSketches = sketches ? Add(SynopsisTableKind.Sketches, SynopsisBucket.Hour, keys, shapes, bounded: true, distinctBelow: false) : null,
                WholeSketches = sketches && _spansHours ? Add(SynopsisTableKind.Sketches, SynopsisBucket.Object, keys, shapes, bounded: true, distinctBelow: false) : null,
                SliceGroups = sketches ? new SketchGroups(_kinds, _words, keys.Length, shapes, new bool[keys.Length]) : null,
                HourGroups = sketches ? new SketchGroups(_kinds, _words, keys.Length, shapes, new bool[keys.Length]) : null,
                WholeGroups = sketches && _spansHours ? new SketchGroups(_kinds, _words, keys.Length, shapes, new bool[keys.Length]) : null,
                HourGrowth = _objectRows > 0 && hourly is not null ? new Growth() : null,
                ObjectGrowth = _objectRows > 0 && whole is not null ? new Growth() : null,
            });
        }

        _groupings = [.. settled];
    }

    /// <summary>The places in the order of the columns <paramref name="names"/> names; null when one is not a key of it.</summary>
    private int[]? PositionsOf(IReadOnlyList<string> names)
    {
        if (names.Count is < 1 or > 4)
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
    /// Counts the slice starting at <paramref name="start"/> into each grouping still kept: its rows grouped by their keys'
    /// codes <paramref name="keyCodes"/>, the measures folded into the groups, and the groups in the order of their keys,
    /// read off the key columns <paramref name="columns"/>, a run of the hour's store; the measures' values into the
    /// groups' sketches, the rows of the slice's table of sketches and the hour's groups.
    /// </summary>
    private async ValueTask AddGroupingsAsync(
        long start, CanonicalArena arena, int root, int rows, List<int[]> keyCodes, int[] columns, CancellationToken cancellationToken)
    {
        foreach (Grouping grouping in _groupings)
        {
            bool kept = grouping.Kept;
            bool sketching = grouping.Sketching;
            if (!kept && !sketching)
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
            SketchGroups? slice = sketching ? grouping.SliceGroups : null;
            if (slice is not null && _sketchPlaces.Length < count)
            {
                _sketchPlaces = new int[Math.Max(count, _sketchPlaces.Length * 2)];
            }

            _run.Clear();
            foreach (int g in order)
            {
                _key.Clear();
                for (int k = 0; k < grouping.Positions.Length; k++)
                {
                    KeyCodec.Append(_key, arena, columns[grouping.Positions[k]], firsts[g], grouping.Shapes[k]);
                }

                states.Words(g, _state);
                if (kept)
                {
                    ulong hash = CardinalitySlot.Hash(_key.Span);
                    hour?.Sketch.Add(hash);
                    whole?.Sketch.Add(hash);
                    _run.Bytes(_key.Span);
                    _run.Words(_state);
                }

                if (slice is not null)
                {
                    int at = slice.Of(_key.Span);
                    StateWords.Merge(slice.Words(at), _state, _kinds);
                    _sketchPlaces[g] = at;
                }
            }

            if (kept)
            {
                grouping.HourRuns.Add(await AppendAsync(HourStore(), grouping.Positions.Length, cancellationToken).ConfigureAwait(false));
            }

            if (slice is not null)
            {
                await SketchSliceAsync(grouping, slice, start, arena, root, rows, groups, count, firsts, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The slice's values of each measure in the sketches of <paramref name="grouping"/>'s groups of the slice, its rows'
    /// groups <paramref name="groups"/>; then those groups as the rows of its table of sketches by slice, and into the
    /// hour's groups. Past the groups or the bytes an hour holds, the grouping's tables of sketches are given up.
    /// </summary>
    private async ValueTask SketchSliceAsync(
        Grouping grouping, SketchGroups slice, long start, CanonicalArena arena, int root, int rows, int[] groups, int count, int[] firsts,
        CancellationToken cancellationToken)
    {
        CanonicalNode node = arena.GetNode(root);
        for (int m = 0; m < _measures.Length; m++)
        {
            int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, ColumnOf(node, _measures[m])));
            ref readonly CanonicalRecord record = ref arena.RecordRef(column);
            ReadOnlySpan<byte> values = record.BufferA.Span;
            ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, column);
            switch (record.PType)
            {
                case PType.I8:
                    Sketch<sbyte>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.I16:
                    Sketch<short>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.I32:
                    Sketch<int>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.I64:
                    Sketch<long>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.U8:
                    Sketch<byte>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.U16:
                    Sketch<ushort>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.U32:
                    Sketch<uint>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.U64:
                    Sketch<ulong>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                case PType.F32:
                    Sketch<float>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
                default:
                    Sketch<double>(slice, m, values, valid, count, firsts, groups, rows);
                    break;
            }
        }

        if (grouping.SliceSketches is { Dead: false } table)
        {
            await EmitGroupingSketchesAsync(slice, table, start, cancellationToken).ConfigureAwait(false);
        }

        // The hour's groups, which the tables of the hour and of the object read; where only the slices' table is kept,
        // the slice's alone, bounded alike.
        bool hourly = grouping.HourSketches is { Dead: false } || grouping.WholeSketches is { Dead: false };
        if (hourly)
        {
            grouping.HourGroups!.MergeFrom(slice);
        }

        slice.Reset();
        SketchGroups held = hourly ? grouping.HourGroups! : slice;
        if (held.Count > HeldSketchGroups)
        {
            GiveUpGroupingSketches(grouping, FormattableString.Invariant(
                $"given up as its groups grew: {held.Count} groups of sketches in an hour, past {HeldSketchGroups}"));
        }
        else if (held.Bytes() is var bytes && bytes > HeldSketchBytes)
        {
            GiveUpGroupingSketches(grouping, FormattableString.Invariant(
                $"given up as its groups grew: {bytes} bytes of sketches in an hour, past {HeldSketchBytes}"));
        }
    }

    /// <summary>Gives up <paramref name="grouping"/>'s tables of sketches, for the reason <paramref name="verdict"/>, and stops feeding them.</summary>
    private void GiveUpGroupingSketches(Grouping grouping, string verdict)
    {
        foreach (Candidate? table in (ReadOnlySpan<Candidate?>)[grouping.SliceSketches, grouping.HourSketches, grouping.WholeSketches])
        {
            if (table is { Dead: false })
            {
                Drop(table, verdict);
            }
        }

        grouping.SliceGroups = null;
        grouping.HourGroups = null;
        grouping.WholeGroups = null;
    }

    /// <summary>The groups of <paramref name="groups"/> that hold rows, in the order of their keys, as rows of <paramref name="table"/> in bucket <paramref name="bucket"/>.</summary>
    private async ValueTask EmitGroupingSketchesAsync(SketchGroups groups, Candidate table, long bucket, CancellationToken cancellationToken)
    {
        foreach (int group in groups.Sorted())
        {
            if (groups.Holds(group) && HoldSketched(table, bucket, groups.Key(group), groups.Words(group), groups.Sketches(group), []))
            {
                await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            }
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
    /// of the object's store; its groups of sketches as the rows of the hour's table of sketches, then into the object's.
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
                using (SynopsisRunMerger merger = await SynopsisRunMerger.OpenAsync(
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
            if (grouping.Sketching && grouping.HourGroups is { Count: > 0 } hourGroups)
            {
                if (grouping.HourSketches is { Dead: false } hourSketches)
                {
                    await EmitGroupingSketchesAsync(hourGroups, hourSketches, _hour, cancellationToken).ConfigureAwait(false);
                }

                if (grouping.WholeSketches is { Dead: false } && grouping.WholeGroups is { } wholeGroups)
                {
                    wholeGroups.MergeFrom(hourGroups);
                    if (wholeGroups.Count > HeldSketchGroups || wholeGroups.Bytes() > HeldSketchBytes)
                    {
                        Drop(grouping.WholeSketches, FormattableString.Invariant(
                            $"given up as its groups grew: {wholeGroups.Count} groups of sketches in the object, {wholeGroups.Bytes()} bytes"));
                        grouping.WholeGroups = null;
                    }
                }

                hourGroups.Reset();
            }

            foreach (Candidate? table in (Candidate?[])[grouping.Hourly, grouping.SliceSketches, grouping.HourSketches])
            {
                if (table is { Dead: false } && table.Stage.RawBytes >= _chunking.HourBytes)
                {
                    await FlushAsync(table, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// The object's tables of each grouping, its hours' runs and groups of sketches merged, when the object spans hours;
    /// none for an object of one hour, whose tables of the hour say the same.
    /// </summary>
    private async ValueTask FinishGroupingsAsync(CancellationToken cancellationToken)
    {
        foreach (Grouping grouping in _groupings)
        {
            foreach (Candidate? table in (ReadOnlySpan<Candidate?>)[grouping.Whole, grouping.WholeSketches])
            {
                if (table is not null && _hours <= 1)
                {
                    table.Dead = true;
                    table.Held = -1;
                    table.Stage.Release();
                }
            }

            if (_hours <= 1)
            {
                continue;
            }

            if (grouping.Whole is { Dead: false } whole && grouping.WholeRuns.Count > 0)
            {
                using SynopsisRunMerger merger = await SynopsisRunMerger.OpenAsync(
                    _wholeScratch!, grouping.WholeRuns, grouping.Shapes, grouping.Positions.Length, _kinds, cancellationToken).ConfigureAwait(false);
                while (await merger.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Hold(whole, 0, merger.Key, merger.Words))
                    {
                        await FlushAsync(whole, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (grouping.WholeSketches is { Dead: false } wholeSketches && grouping.WholeGroups is { Count: > 0 } wholeGroups)
            {
                await EmitGroupingSketchesAsync(wholeGroups, wholeSketches, 0, cancellationToken).ConfigureAwait(false);
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
