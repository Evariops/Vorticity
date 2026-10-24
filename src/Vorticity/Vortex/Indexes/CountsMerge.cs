using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Aggregating;
using Vorticity.Buffers;
using Vorticity.Writing;

namespace Vorticity.Indexes;

/// <summary>
/// Sums columns' counts (<see cref="ColumnCounts"/>) and their sketches (<see cref="ColumnSketches"/>) into buckets of
/// time coarser than theirs, each named by its first hour since the Unix epoch, with no row read: the hours of a day's
/// objects into the day, the days of a month into the month. The entries of a value and a bucket add up word for word,
/// their quantile sketches bucket by bucket and their registers register by register, so that each entry holds what the
/// rows of its value in its bucket would give. The merge carries the measures, the sketches and the wide columns every
/// input carries.
/// </summary>
/// <remarks>
/// <para>
/// An input's entry of the values its budget left out may hold any value that input does not keep: in a bucket such an
/// entry reaches, a value one such input does not keep goes with the values left out, so that every value's entry stays
/// exact. Past its budget of entries, the merge leaves out the values of fewest rows the same way.
/// </para>
/// <para>
/// A bucket's sketches stay open while inputs reach it, and are encoded once <see cref="Close"/> says none will any more,
/// so that a month of days holds one day's open sketches at a time.
/// </para>
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

    // The values met, by their bytes, an id each; the entries, by group and bucket, an index each into the words, the
    // sketches (each measure's encoded bytes, or the sketch that merges them while its bucket is open) and the registers
    // (each wide column's encoded registers, or the ranks that merge them).
    private readonly Dictionary<byte[], int> _ids = new Dictionary<byte[], int>(ByteKeys.Instance);
    private readonly List<byte[]> _values = [];
    private readonly Dictionary<(int Group, long Bucket), int> _entries = [];
    private readonly List<(int Group, long Bucket)> _keys = [];
    private readonly Dictionary<long, List<int>> _open = [];
    private long[] _words = [];
    private object?[] _sketches = [];
    private object?[] _registers = [];

    // Per bucket an input's values left out reach, the values each such input keeps.
    private readonly Dictionary<long, List<HashSet<int>>> _hiding = [];

    /// <param name="entryLimit">The entries the merge keeps at most, past which the values of fewest rows are left out.</param>
    internal CountsMerge(int entryLimit = ColumnCountsBuilder.EntryLimit) => _entryLimit = entryLimit;

    /// <summary>The entries the merge holds.</summary>
    internal int Entries => _entries.Count;

    /// <summary>
    /// Adds the entries of <paramref name="counts"/>, with <paramref name="sketches"/> their sketches when it keeps any,
    /// each into the bucket <paramref name="bucketOf"/> names for its hour; an entry it names none for is left out.
    /// </summary>
    internal void Add(ColumnCounts counts, ColumnSketches? sketches, Func<long, long?> bucketOf)
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
        HashSet<int>? kept = null;
        foreach (CountGroup group in counts.Groups)
        {
            if (group.Kind == CountedValue.Others)
            {
                kept = [];
                foreach (CountGroup held in counts.Groups)
                {
                    if (held.Kind == CountedValue.Value)
                    {
                        kept.Add(Id(held.Value!));
                    }
                }

                break;
            }
        }

        long[] projected = new long[_width];
        foreach (CountGroup group in counts.Groups)
        {
            int id = group.Kind switch { CountedValue.Null => NullGroup, CountedValue.Others => OthersGroup, _ => Id(group.Value!) };
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                if (bucketOf(counts.Hours[e]) is not long bucket)
                {
                    continue;
                }

                if (id == OthersGroup)
                {
                    Hide(bucket, kept!);
                }

                int entry = EntryOf(id, bucket);
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

    /// <summary>Encodes the open sketches and registers of the entries of <paramref name="bucket"/>, which no later input reaches.</summary>
    internal void Close(long bucket)
    {
        if (!_open.Remove(bucket, out List<int>? entries))
        {
            return;
        }

        int measures = _sketched ? _fields!.Length : 0;
        foreach (int entry in entries)
        {
            for (int m = 0; m < measures; m++)
            {
                Seal(ref _sketches[(entry * measures) + m]);
            }

            for (int a = 0; a < _attached.Length; a++)
            {
                Seal(ref _registers[(entry * _attached.Length) + a]);
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

        foreach (long bucket in new List<long>(_open.Keys))
        {
            Close(bucket);
        }

        // A value an input's entry of the values left out may hide in goes with them in each bucket that entry reaches.
        for (int entry = 0; entry < _keys.Count; entry++)
        {
            (int group, long bucket) = _keys[entry];
            if (group >= 0 && _hiding.TryGetValue(bucket, out List<HashSet<int>>? hiding) && !hiding.TrueForAll(values => values.Contains(group)))
            {
                MoveToOthers(entry);
            }
        }

        LeaveOut();
        return Written();
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

        int entries = _keys.Count;
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

    /// <summary>Says that the values <paramref name="kept"/> leaves out may hide in <paramref name="bucket"/>'s entry of the values left out.</summary>
    private void Hide(long bucket, HashSet<int> kept)
    {
        if (!_hiding.TryGetValue(bucket, out List<HashSet<int>>? hiding))
        {
            _hiding[bucket] = hiding = [];
        }

        if (hiding.Count == 0 || !ReferenceEquals(hiding[^1], kept))
        {
            hiding.Add(kept);
        }
    }

    /// <summary>The entry of group <paramref name="group"/> in <paramref name="bucket"/>, made with no row the first time.</summary>
    private int EntryOf(int group, long bucket)
    {
        if (_entries.TryGetValue((group, bucket), out int entry))
        {
            return entry;
        }

        entry = _keys.Count;
        int measures = _sketched ? _fields!.Length : 0;
        if ((entry + 1) * _width > _words.Length)
        {
            int capacity = Math.Max(16, (entry + 1) * 2);
            Array.Resize(ref _words, capacity * _width);
            Array.Resize(ref _sketches, capacity * measures);
            Array.Resize(ref _registers, capacity * _attached.Length);
        }

        StateWords.Clear(_words.AsSpan(entry * _width, _width), _kinds);
        _entries.Add((group, bucket), entry);
        _keys.Add((group, bucket));
        if (!_open.TryGetValue(bucket, out List<int>? open))
        {
            _open[bucket] = open = [];
        }

        open.Add(entry);
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

        byte[] ranks = ((Ranks)slot).Values;
        byte[] other = ArrayPool<byte>.Shared.Rent(ranks.Length);
        try
        {
            HyperLogLog.Decode(encoded, other.AsSpan(0, ranks.Length));
            for (int r = 0; r < ranks.Length; r++)
            {
                ranks[r] = Math.Max(ranks[r], other[r]);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(other);
        }
    }

    /// <summary>Encodes an open sketch or open registers in <paramref name="slot"/>.</summary>
    private static void Seal(ref object? slot)
    {
        if (slot is QuantileSketch sketch)
        {
            ArrayBufferWriter<byte> writer = new ArrayBufferWriter<byte>();
            sketch.Encode(writer);
            slot = writer.WrittenSpan.ToArray();
        }
        else if (slot is Ranks ranks)
        {
            slot = HyperLogLog.Encode(ranks.Values);
        }
    }

    /// <summary>Moves entry <paramref name="entry"/> into its bucket's entry of the values left out.</summary>
    private void MoveToOthers(int entry)
    {
        (int group, long bucket) = _keys[entry];
        int others = EntryOf(OthersGroup, bucket);
        StateWords.Merge(_words.AsSpan(others * _width, _width), _words.AsSpan(entry * _width, _width), _kinds);
        int measures = _sketched ? _fields!.Length : 0;
        for (int m = 0; m < measures; m++)
        {
            MergeSketch(ref _sketches[(others * measures) + m], _sketches[(entry * measures) + m] as byte[]);
            Seal(ref _sketches[(others * measures) + m]);
        }

        for (int a = 0; a < _attached.Length; a++)
        {
            MergeRegisters(ref _registers[(others * _attached.Length) + a], _registers[(entry * _attached.Length) + a] as byte[]);
            Seal(ref _registers[(others * _attached.Length) + a]);
        }

        _entries.Remove((group, bucket));
        _keys[entry] = (Moved, bucket);
    }

    /// <summary>Past the budget, leaves out the values of fewest rows, their entries moved into those of the values left out.</summary>
    private void LeaveOut()
    {
        if (_entries.Count <= _entryLimit)
        {
            return;
        }

        // Each value's rows and entries.
        long[] rows = new long[_values.Count];
        List<int>?[] entries = new List<int>?[_values.Count];
        for (int entry = 0; entry < _keys.Count; entry++)
        {
            if (_keys[entry].Group is int value and >= 0)
            {
                rows[value] += _words[entry * _width];
                (entries[value] ??= []).Add(entry);
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
            foreach (int entry in entries[order[i]] ?? [])
            {
                MoveToOthers(entry);
            }
        }
    }

    /// <summary>The counts and sketches of the entries: the nulls, the values left out, then the values by their bytes, each by bucket.</summary>
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
        for (int entry = 0; entry < _keys.Count; entry++)
        {
            if (_keys[entry].Group != Moved)
            {
                live.Add(entry);
            }
        }

        int RankOf(int group) => group switch { NullGroup => 0, OthersGroup => 1, _ => rank[group] };
        live.Sort((left, right) =>
        {
            int groups = RankOf(_keys[left].Group).CompareTo(RankOf(_keys[right].Group));
            return groups != 0 ? groups : _keys[left].Bucket.CompareTo(_keys[right].Bucket);
        });

        int measures = _sketched ? _fields!.Length : 0;
        long[] words = new long[live.Count * _width];
        long[] hours = new long[live.Count];
        byte[]?[]? sketches = measures > 0 ? new byte[]?[live.Count * measures] : null;
        byte[]?[]? registers = _attached.Length > 0 ? new byte[]?[live.Count * _attached.Length] : null;
        List<CountGroup> groups = [];
        for (int i = 0; i < live.Count; i++)
        {
            int entry = live[i];
            (int group, long bucket) = _keys[entry];
            hours[i] = bucket;
            _words.AsSpan(entry * _width, _width).CopyTo(words.AsSpan(i * _width, _width));
            for (int m = 0; m < measures; m++)
            {
                sketches![(i * measures) + m] = _sketches[(entry * measures) + m] as byte[];
            }

            for (int a = 0; a < _attached.Length; a++)
            {
                registers![(i * _attached.Length) + a] = _registers[(entry * _attached.Length) + a] as byte[];
            }

            if (groups.Count == 0 || _keys[live[i - 1]].Group != group)
            {
                groups.Add(new CountGroup(
                    group switch { NullGroup => CountedValue.Null, OthersGroup => CountedValue.Others, _ => CountedValue.Value },
                    group >= 0 ? _values[group] : null,
                    i,
                    0));
            }

            groups[^1] = groups[^1] with { Entries = groups[^1].Entries + 1 };
        }

        ColumnCounts counts = new ColumnCounts(_fields!, _kinds, [.. groups], hours, words)
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
