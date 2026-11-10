using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Vorticity.Writing;

namespace Vorticity.Indexes;

/// <summary>
/// Sums objects' counts of a column (<see cref="ColumnCounts"/>) and their sketches (<see cref="ColumnSketches"/>) into the
/// counts the objects would keep as one, with no row read: the entries of a value add up word for word, their quantile
/// sketches bucket by bucket and their registers register by register, so that each entry holds what the rows of its
/// value in every object would give. The merge carries the measures, the sketches and the wide columns every input
/// carries. A caller that sums by a grain of its own, a partition value say, keeps a merge for each.
/// </summary>
/// <remarks>
/// An input's entry of the values its budget left out may hold any value that input does not keep: a value one such input
/// does not keep goes with the values left out, so that every value's entry stays exact. Past its budget of entries, the
/// merge leaves out the values of fewest rows the same way.
/// </remarks>
internal sealed class CountsMerge
{
    // The groups' ids past the values': the nulls, the values left out, and an entry moved into theirs.
    private const int NullGroup = -1;
    private const int OthersGroup = -2;
    private const int Moved = int.MinValue;

    private readonly int _entryLimit;

    // What the merge carries: the measures every input carries, by field, whether every input sketches them, and the wide
    // columns every input attaches; null fields before the first input.
    private int[]? _fields;
    private MeasureKind[] _kinds = [];
    private bool _sketched;
    private int[] _attached = [];
    private int _width;

    // The values met, by their bytes, an id each; the entries, by group, an index each into the words, the sketches (each
    // measure's encoded bytes, or the sketch that merges them) and the registers (each wide column's encoded registers, or
    // the ranks that merge them).
    private readonly Dictionary<byte[], int> _ids = new Dictionary<byte[], int>(ByteKeys.Instance);
    private readonly List<byte[]> _values = [];
    private readonly Dictionary<int, int> _entries = [];
    private readonly List<int> _groups = [];
    private long[] _words = [];
    private object?[] _sketches = [];
    private object?[] _registers = [];

    // The values each input that left some out keeps.
    private readonly List<HashSet<int>> _hiding = [];

    // The one writer every sketch the merge seals is encoded through.
    private readonly ArrayBufferWriter<byte> _sealing = new ArrayBufferWriter<byte>();

    /// <param name="entryLimit">The entries the merge keeps at most, past which the values of fewest rows are left out.</param>
    internal CountsMerge(int entryLimit = ColumnCountsBuilder.EntryLimit) => _entryLimit = entryLimit;

    /// <summary>Adds the entries of <paramref name="counts"/>, with <paramref name="sketches"/> their sketches when it keeps any.</summary>
    internal void Add(ColumnCounts counts, ColumnSketches? sketches)
    {
        Carry(counts, sketches);
        int measures = _fields!.Length;

        // Where the input holds each measure the merge carries: its place among the input's, and the first of its words.
        int[] places = new int[measures];
        int[] offsets = new int[measures];
        for (int m = 0; m < measures; m++)
        {
            places[m] = Array.IndexOf(counts.MeasureFields, _fields[m]);
            offsets[m] = 1;
            for (int p = 0; p < places[m]; p++)
            {
                offsets[m] += MeasureLayout.Words(counts.Kinds[p] == MeasureKind.Float);
            }
        }

        int[] attached = new int[_attached.Length];
        for (int a = 0; a < attached.Length; a++)
        {
            attached[a] = Array.IndexOf(counts.AttachedFields, _attached[a]);
        }

        // The values an input that left some out keeps: a value it does not keep may hide in its entry of the others.
        foreach (CountGroup group in counts.Groups)
        {
            if (group.Kind == CountedValue.Others)
            {
                HashSet<int> kept = [];
                foreach (CountGroup held in counts.Groups)
                {
                    if (held.Kind == CountedValue.Value)
                    {
                        kept.Add(Id(held.Value!));
                    }
                }

                _hiding.Add(kept);
                break;
            }
        }

        long[] projected = new long[_width];
        foreach (CountGroup group in counts.Groups)
        {
            int id = group.Kind switch { CountedValue.Null => NullGroup, CountedValue.Others => OthersGroup, _ => Id(group.Value!) };
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                int entry = EntryOf(id);
                ReadOnlySpan<long> words = counts.WordsOf(e);
                projected[0] = words[0];
                for (int m = 0, at = 1; m < measures; m++)
                {
                    int length = MeasureLayout.Words(_kinds[m] == MeasureKind.Float);
                    words.Slice(offsets[m], length).CopyTo(projected.AsSpan(at, length));
                    at += length;
                }

                StateWords.Merge(_words.AsSpan(entry * _width, _width), projected, _kinds);
                for (int m = 0; _sketched && m < measures; m++)
                {
                    MergeSketch(ref _sketches[(entry * measures) + m], sketches!.SketchOf(e, places[m]));
                }

                for (int a = 0; a < attached.Length; a++)
                {
                    MergeRegisters(ref _registers[(entry * attached.Length) + a], sketches!.DistinctOf(e, attached[a]));
                }
            }
        }
    }

    /// <summary>The merged counts and their sketches, the latter null when the merge keeps none; null when no entry was added.</summary>
    internal (ColumnCounts Counts, ColumnSketches? Sketches)? Finish()
    {
        if (_fields is null || _entries.Count == 0)
        {
            return null;
        }

        // A value an input's entry of the values left out may hide in goes with them.
        for (int entry = 0; entry < _groups.Count; entry++)
        {
            int group = _groups[entry];
            if (group >= 0 && !KeptByAll(group))
            {
                MoveToOthers(entry);
            }
        }

        LeaveOut();

        // The entries left, the values left out's among them, sealed once every value moved.
        int measures = _sketched ? _fields.Length : 0;
        for (int entry = 0; entry < _groups.Count; entry++)
        {
            if (_groups[entry] == Moved)
            {
                continue;
            }

            for (int m = 0; m < measures; m++)
            {
                Seal(ref _sketches[(entry * measures) + m]);
            }

            for (int a = 0; a < _attached.Length; a++)
            {
                Seal(ref _registers[(entry * _attached.Length) + a]);
            }
        }

        return Written();
    }

    /// <summary>Whether every input that left values out keeps value <paramref name="group"/>.</summary>
    private bool KeptByAll(int group)
    {
        foreach (HashSet<int> kept in _hiding)
        {
            if (!kept.Contains(group))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Keeps of the merge's measures, sketches and wide columns those <paramref name="counts"/> carries too.</summary>
    private void Carry(ColumnCounts counts, ColumnSketches? sketches)
    {
        bool sketched = counts.Sketched && sketches is { } held && held.Entries == counts.Entries && held.Measures == counts.MeasureFields.Length;
        int[] attached = sketches is { } registers && registers.Entries == counts.Entries && registers.Attached == counts.AttachedFields.Length
            ? counts.AttachedFields
            : [];
        if (_fields is null)
        {
            (_fields, _kinds, _sketched, _attached, _width) = (counts.MeasureFields, counts.Kinds, sketched, attached, StateWords.Count(counts.Kinds));
            return;
        }

        List<int> measures = [];
        for (int m = 0; m < _fields.Length; m++)
        {
            int place = Array.IndexOf(counts.MeasureFields, _fields[m]);
            if (place >= 0 && counts.Kinds[place] == _kinds[m])
            {
                measures.Add(m);
            }
        }

        List<int> wide = [];
        for (int a = 0; a < _attached.Length; a++)
        {
            if (Array.IndexOf(attached, _attached[a]) >= 0)
            {
                wide.Add(a);
            }
        }

        if (measures.Count < _fields.Length || wide.Count < _attached.Length || (_sketched && !sketched))
        {
            Relayout([.. measures], _sketched && sketched, [.. wide]);
        }
    }

    /// <summary>Keeps the measures <paramref name="measures"/>, their sketches when <paramref name="sketched"/>, and the wide columns <paramref name="wide"/>, each by its place.</summary>
    private void Relayout(int[] measures, bool sketched, int[] wide)
    {
        int oldMeasures = _sketched ? _fields!.Length : 0;
        int[] fields = Array.ConvertAll(measures, m => _fields![m]);
        MeasureKind[] kinds = Array.ConvertAll(measures, m => _kinds[m]);
        int width = StateWords.Count(kinds);
        int[] starts = new int[_fields!.Length];
        for (int m = 0, at = 1; m < _fields.Length; m++)
        {
            starts[m] = at;
            at += MeasureLayout.Words(_kinds[m] == MeasureKind.Float);
        }

        int entries = _groups.Count;
        long[] words = new long[Math.Max(entries, 1) * width];
        object?[] sketches = sketched ? new object?[Math.Max(entries, 1) * measures.Length] : [];
        object?[] registers = new object?[Math.Max(entries, 1) * wide.Length];
        for (int entry = 0; entry < entries; entry++)
        {
            Span<long> into = words.AsSpan(entry * width, width);
            into[0] = _words[entry * _width];
            for (int k = 0, at = 1; k < measures.Length; k++)
            {
                int length = MeasureLayout.Words(kinds[k] == MeasureKind.Float);
                _words.AsSpan((entry * _width) + starts[measures[k]], length).CopyTo(into.Slice(at, length));
                at += length;
                if (sketched)
                {
                    sketches[(entry * measures.Length) + k] = _sketches[(entry * oldMeasures) + measures[k]];
                }
            }

            for (int k = 0; k < wide.Length; k++)
            {
                registers[(entry * wide.Length) + k] = _registers[(entry * _attached.Length) + wide[k]];
            }
        }

        (_fields, _kinds, _sketched, _attached, _width) = (fields, kinds, sketched, Array.ConvertAll(wide, a => _attached[a]), width);
        (_words, _sketches, _registers) = (words, sketches, registers);
    }

    /// <summary>The id of the value <paramref name="value"/>, given the first time it is met.</summary>
    private int Id(byte[] value)
    {
        if (!_ids.TryGetValue(value, out int id))
        {
            id = _values.Count;
            _ids.Add(value, id);
            _values.Add(value);
        }

        return id;
    }

    /// <summary>The entry of group <paramref name="group"/>, made with no row the first time.</summary>
    private int EntryOf(int group)
    {
        if (_entries.TryGetValue(group, out int entry))
        {
            return entry;
        }

        entry = _groups.Count;
        int measures = _sketched ? _fields!.Length : 0;
        if ((entry + 1) * _width > _words.Length)
        {
            int capacity = Math.Max(16, (entry + 1) * 2);
            Array.Resize(ref _words, capacity * _width);
            Array.Resize(ref _sketches, capacity * measures);
            Array.Resize(ref _registers, capacity * _attached.Length);
        }

        StateWords.Clear(_words.AsSpan(entry * _width, _width), _kinds);
        _entries.Add(group, entry);
        _groups.Add(group);
        return entry;
    }

    /// <summary>Merges the encoded sketch <paramref name="encoded"/> into <paramref name="slot"/>, opened from its bytes the second time.</summary>
    private static void MergeSketch(ref object? slot, byte[]? encoded)
    {
        if (encoded is null)
        {
            return;
        }

        switch (slot)
        {
            case null:
                slot = encoded;
                break;
            case byte[] first:
                QuantileSketch open = new QuantileSketch(ColumnCountsBuilder.SketchBits);
                open.MergeEncoded(first);
                open.MergeEncoded(encoded);
                slot = open;
                break;
            default:
                ((QuantileSketch)slot).MergeEncoded(encoded);
                break;
        }
    }

    /// <summary>Merges the encoded registers <paramref name="encoded"/> into <paramref name="slot"/>, opened from its bytes the second time.</summary>
    private static void MergeRegisters(ref object? slot, byte[]? encoded)
    {
        if (encoded is null)
        {
            return;
        }

        if (slot is null)
        {
            slot = encoded;
            return;
        }

        if (slot is byte[] first)
        {
            byte[] opened = new byte[ColumnSheet.Registers];
            HyperLogLog.Decode(first, opened);
            slot = new Ranks(opened);
        }

        HyperLogLog.Raise(encoded, ((Ranks)slot).Values);
    }

    /// <summary>
    /// Merges the sketch of a moved entry, <paramref name="from"/>, encoded or open, into <paramref name="into"/>, and lets
    /// it go: an open sketch merges as it is, where it was encoded to be opened again.
    /// </summary>
    private static void AbsorbSketch(ref object? into, ref object? from)
    {
        switch (from)
        {
            case byte[] encoded:
                MergeSketch(ref into, encoded);
                break;
            case QuantileSketch open:
                switch (into)
                {
                    case null:
                        into = open;
                        break;
                    case byte[] first:
                        open.MergeEncoded(first);
                        into = open;
                        break;
                    default:
                        ((QuantileSketch)into).Merge(open);
                        break;
                }

                break;
        }

        from = null;
    }

    /// <summary>Merges the registers of a moved entry, <paramref name="from"/>, encoded or open, into <paramref name="into"/>, and lets them go.</summary>
    private static void AbsorbRegisters(ref object? into, ref object? from)
    {
        switch (from)
        {
            case byte[] encoded:
                MergeRegisters(ref into, encoded);
                break;
            case Ranks open:
                switch (into)
                {
                    case null:
                        into = open;
                        break;
                    case byte[] first:
                        HyperLogLog.Raise(first, open.Values);
                        into = open;
                        break;
                    default:
                        HyperLogLog.Highest(open.Values, ((Ranks)into).Values);
                        break;
                }

                break;
        }

        from = null;
    }

    /// <summary>Encodes an open sketch or open registers in <paramref name="slot"/>.</summary>
    private void Seal(ref object? slot)
    {
        if (slot is QuantileSketch sketch)
        {
            _sealing.ResetWrittenCount();
            sketch.Encode(_sealing);
            slot = _sealing.WrittenSpan.ToArray();
        }
        else if (slot is Ranks ranks)
        {
            slot = HyperLogLog.Encode(ranks.Values);
        }
    }

    /// <summary>Moves entry <paramref name="entry"/> into the entry of the values left out.</summary>
    private void MoveToOthers(int entry)
    {
        int group = _groups[entry];
        int others = EntryOf(OthersGroup);
        StateWords.Merge(_words.AsSpan(others * _width, _width), _words.AsSpan(entry * _width, _width), _kinds);
        int measures = _sketched ? _fields!.Length : 0;
        for (int m = 0; m < measures; m++)
        {
            AbsorbSketch(ref _sketches[(others * measures) + m], ref _sketches[(entry * measures) + m]);
        }

        for (int a = 0; a < _attached.Length; a++)
        {
            AbsorbRegisters(ref _registers[(others * _attached.Length) + a], ref _registers[(entry * _attached.Length) + a]);
        }

        _entries.Remove(group);
        _groups[entry] = Moved;
    }

    /// <summary>Past the budget, leaves out the values of fewest rows, their entries moved into the entry of the values left out.</summary>
    private void LeaveOut()
    {
        if (_entries.Count <= _entryLimit)
        {
            return;
        }

        // Each value's rows and entry.
        long[] rows = new long[_values.Count];
        int[] entries = new int[_values.Count];
        Array.Fill(entries, -1);
        for (int entry = 0; entry < _groups.Count; entry++)
        {
            if (_groups[entry] is int value and >= 0)
            {
                rows[value] = _words[entry * _width];
                entries[value] = entry;
            }
        }

        int[] order = new int[_values.Count];
        for (int v = 0; v < order.Length; v++)
        {
            order[v] = v;
        }

        // A value at a time, the fewest rows first, until the merge holds its budget.
        Array.Sort(rows, order);
        for (int i = 0; i < order.Length && _entries.Count > _entryLimit; i++)
        {
            if (entries[order[i]] >= 0)
            {
                MoveToOthers(entries[order[i]]);
            }
        }
    }

    /// <summary>The counts and sketches of the entries: the nulls, the values left out, then the values by their bytes.</summary>
    private (ColumnCounts Counts, ColumnSketches? Sketches) Written()
    {
        int[] rank = new int[_values.Count];
        int[] byBytes = new int[_values.Count];
        for (int v = 0; v < byBytes.Length; v++)
        {
            byBytes[v] = v;
        }

        Array.Sort(byBytes, (left, right) => _values[left].AsSpan().SequenceCompareTo(_values[right]));
        for (int r = 0; r < byBytes.Length; r++)
        {
            rank[byBytes[r]] = r + 2;
        }

        List<int> live = new List<int>(_entries.Count);
        for (int entry = 0; entry < _groups.Count; entry++)
        {
            if (_groups[entry] != Moved)
            {
                live.Add(entry);
            }
        }

        int RankOf(int group) => group switch { NullGroup => 0, OthersGroup => 1, _ => rank[group] };
        live.Sort((left, right) => RankOf(_groups[left]).CompareTo(RankOf(_groups[right])));

        int measures = _sketched ? _fields!.Length : 0;
        long[] words = new long[live.Count * _width];
        byte[]?[]? sketches = measures > 0 ? new byte[]?[live.Count * measures] : null;
        byte[]?[]? registers = _attached.Length > 0 ? new byte[]?[live.Count * _attached.Length] : null;
        CountGroup[] groups = new CountGroup[live.Count];
        for (int i = 0; i < live.Count; i++)
        {
            int entry = live[i];
            int group = _groups[entry];
            _words.AsSpan(entry * _width, _width).CopyTo(words.AsSpan(i * _width, _width));
            for (int m = 0; m < measures; m++)
            {
                sketches![(i * measures) + m] = _sketches[(entry * measures) + m] as byte[];
            }

            for (int a = 0; a < _attached.Length; a++)
            {
                registers![(i * _attached.Length) + a] = _registers[(entry * _attached.Length) + a] as byte[];
            }

            groups[i] = new CountGroup(
                group switch { NullGroup => CountedValue.Null, OthersGroup => CountedValue.Others, _ => CountedValue.Value },
                group >= 0 ? _values[group] : null,
                i,
                1);
        }

        ColumnCounts counts = new ColumnCounts(_fields!, _kinds, groups, words)
        {
            Sketched = measures > 0,
            AttachedFields = _attached,
        };
        return (counts, measures > 0 || registers is not null
            ? new ColumnSketches(live.Count, measures, sketches, _attached.Length, registers)
            : null);
    }

    /// <summary>Registers opened from their bytes, which other registers raise.</summary>
    private sealed class Ranks(byte[] values)
    {
        internal byte[] Values { get; } = values;
    }
}
