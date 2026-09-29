// The distinct count over a fixed-width column as it read a dictionary block, clearing and
// sweeping a table of the dictionary's size at every range: the original that
// `DictionaryDistinctBenchmarks` measures the library against.
using System;
using System.Collections.Generic;

using Vorticity.Aggregating;
using Vorticity.Arrays;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>The distinct non-null values of each group, one set for every group of the partition.</summary>
internal sealed class FixedDistinctSlotBefore<TValue> : AggregateSlot<long>
    where TValue : unmanaged, IEquatable<TValue>
{
    private readonly StorageKind _kind;
    private readonly HashSet<DistinctEntry<TValue>> _seen = [];
    private long[] _counts = [];
    private int _groups;
    private ValuesCache<TValue> _values;
    private MaskCache _rows;
    private bool[] _present = [];

    internal FixedDistinctSlotBefore(StorageKind kind) => _kind = kind;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            Array.Resize(ref _counts, Math.Max(groups, _counts.Length * 2));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (FixedReader.EncodingOf(arena, node, _kind))
        {
            case ColumnEncoding.Constant:
                if (RowMasks.Count(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end) > 0)
                {
                    Add(group, FixedReader.Constant<TValue>(arena, node, _kind));
                }

                return;

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, runs, _kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = _rows.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int r = Runs.FirstEndingAfter(ends, start);
                int runStart = r == 0 ? 0 : (int)ends[r - 1];
                for (; r < ends.Length && runStart < end; r++)
                {
                    int runEnd = Math.Min((int)ends[r], input.Rows);
                    if (StorageValues.IsValid(valid, r) && RowMasks.Count(rows, Math.Max(runStart, start), Math.Min(runEnd, end)) > 0)
                    {
                        Add(group, values[r]);
                    }

                    runStart = runEnd;
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
                Scratch.Grow(ref _present, dictionary.Length);
                Span<bool> present = _present.AsSpan(0, dictionary.Length);
                present.Clear();
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end);
                while (rows.Next(out int row))
                {
                    present[(int)codes[row]] = true;
                }

                for (int code = 0; code < present.Length; code++)
                {
                    if (present[code] && StorageValues.IsValid(valid, code))
                    {
                        Add(group, dictionary[code]);
                    }
                }

                return;
            }

            default:
            {
                ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), start, end);
                while (rows.Next(out int row))
                {
                    Add(group, values[row]);
                }

                return;
            }
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        if (FixedReader.EncodingOf(arena, node, _kind) == ColumnEncoding.Dictionary)
        {
            int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
            ReadOnlySpan<TValue> dictionary = _values.Of(arena, input.Batch, entries, _kind, out ReadOnlySpan<ulong> valid);
            RowCursor coded = new RowCursor(_rows.And(input, input.Selection, ArenaWords.Validity(arena, node)), 0, input.Rows);
            while (coded.Next(out int row))
            {
                int code = (int)codes[row];
                if (StorageValues.IsValid(valid, code))
                {
                    Add(groups[row], dictionary[code]);
                }
            }

            return;
        }

        ReadOnlySpan<TValue> values = _values.Of(arena, input.Batch, node, _kind, out ReadOnlySpan<ulong> validity);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, validity), 0, input.Rows);
        while (rows.Next(out int row))
        {
            Add(groups[row], values[row]);
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> map)
    {
        foreach (DistinctEntry<TValue> entry in ((FixedDistinctSlotBefore<TValue>)other)._seen)
        {
            Add(map[entry.Group], entry.Value);
        }
    }

    internal override long Result(int group) => _counts[group];

    private void Add(int group, TValue value)
    {
        if (_seen.Add(new DistinctEntry<TValue>(group, value)))
        {
            _counts[group]++;
        }
    }
}
