using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The tables of sketches of an object's synopsis: per hour, and per object when it spans hours, at each depth down to
/// the deepest sketched, every combination of the keys with the states of every measure and a sketch of each one's
/// values (<see cref="QuantileSketch"/>, on the grid of <see cref="SynopsisTableInfo.SketchBits"/>), which a distribution
/// or a quantile merges where it read the values of every row.
/// </summary>
/// <remarks>
/// <para>
/// A slice's values go to its groups at the deepest depth sketched, found by their keys among the hour's, a run of a
/// group's rows at a time where the groups are runs of the sorted rows, as a distribution of the rows adds them. At the
/// end of the hour, its groups in the order of their keys give every depth of its tables in one pass, a group of a depth
/// ending where its prefix of the keys does, as the merge of the coarse tables does; they then go on to the object's
/// groups, which give its tables at the end.
/// </para>
/// <para>
/// A sketch holds a bucket for each value at most and for each bucket of its grid the values reach at most: a few
/// hundred over a range of durations whatever their count, as many as the values over a group of a few rows, which the
/// table of states of its grain holds a row of anyway. The depth sketched is the deepest whose groups the first slice
/// holds no more than <see cref="SketchedGroups"/> of; an hour or an object whose groups outgrow
/// <see cref="HeldSketchGroups"/> all the same, keys whose values change from slice to slice, gives up its deepest
/// tables of sketches, its groups merged into their prefixes, so that the groups held are bounded. The tables go to the
/// choice as every other, which weighs a distribution of their grain as a question of its own.
/// </para>
/// </remarks>
internal sealed partial class SynopsisTables
{
    /// <summary>The most groups the first slice holds at the deepest depth sketched.</summary>
    private const int SketchedGroups = 4096;

    /// <summary>The most groups an hour or the object holds at the depth sketched, past which its deepest tables of sketches are given up.</summary>
    private const int HeldSketchGroups = 4 * SketchedGroups;

    // The tables of sketches by depth, of the hour and of the object; the depth of the groups the hour's slices fill, -1
    // when none is kept; those groups and the object's; and the bytes of a sketch encoded.
    private Candidate?[] _sketchedHours = [];
    private Candidate?[] _sketchedWhole = [];
    private int _sketchDepth = -1;
    private bool _sketchesSettled;
    private SketchGroups? _hourSketches;
    private SketchGroups? _wholeSketches;
    private readonly ArrayBufferWriter<byte> _sketchBytes = new ArrayBufferWriter<byte>();

    // Each group of the slice counted's place among the hour's groups, by its number.
    private int[] _sketchPlaces = [];

    /// <summary>
    /// Settles the tables of sketches on the first slice, its groups counted at each depth down to <paramref name="used"/>
    /// in <paramref name="counts"/>: none without a measure.
    /// </summary>
    private void SettleSketches(int[] counts, int used)
    {
        int deepest = -1;
        for (int d = 0; d <= used && d <= _keys.Length && _measures.Length > 0 && counts[d] <= SketchedGroups; d++)
        {
            deepest = d;
        }

        if (deepest < 0)
        {
            return;
        }

        _sketchedHours = new Candidate?[deepest + 1];
        _sketchedWhole = new Candidate?[deepest + 1];
        for (int d = deepest; d >= 0; d--)
        {
            _sketchedHours[d] = Add(SynopsisTableKind.Sketches, SynopsisBucket.Hour, _keys[..d], _keyShapes[..d]);
        }

        for (int d = deepest; d >= 0 && _spansHours; d--)
        {
            _sketchedWhole[d] = Add(SynopsisTableKind.Sketches, SynopsisBucket.Object, _keys[..d], _keyShapes[..d]);
        }

        _sketchDepth = deepest;
        _hourSketches = new SketchGroups(_kinds, _words, deepest, _keyShapes);
        _wholeSketches = _spansHours ? new SketchGroups(_kinds, _words, deepest, _keyShapes) : null;
    }

    /// <summary>The deepest depth of a table of sketches of the hour or of the object still kept; -1 for none.</summary>
    private int DeepestSketched() => Math.Max(Deepest(_sketchedHours), Deepest(_sketchedWhole));

    /// <summary>
    /// Adds the slice's rows to the hour's groups at the depth sketched: each group's states, which its rows folded,
    /// and each measure's values to its sketch, a group's run of rows at a time where the groups are runs, its rows'
    /// groups in <see cref="_groups"/> otherwise.
    /// </summary>
    private void AddSketches(
        CanonicalArena arena, int root, int rows, int[] counts, int[][] firsts, int[]?[] placed, Accumulators[] states, int leading)
    {
        int d = _sketchDepth;
        SketchGroups hour = _hourSketches!;
        int count = counts[d];
        if (_sketchPlaces.Length < count)
        {
            _sketchPlaces = new int[Math.Max(count, _sketchPlaces.Length * 2)];
        }

        // The groups, in the order of their keys, which states holds them in; each one's number, its first row's order.
        for (int p = 0; p < count; p++)
        {
            int group = placed[d]?[p] ?? p;
            int at = hour.Of(_sliceKeys.Of(d, group));
            states[d].Words(p, _state);
            StateWords.Merge(hour.Words(at), _state, _kinds);
            _sketchPlaces[group] = at;
        }

        CanonicalNode node = arena.GetNode(root);
        int[]? groupOf = d <= leading ? null : _groups[d];
        for (int m = 0; m < _measures.Length; m++)
        {
            int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, ColumnOf(node, _measures[m])));
            ref readonly CanonicalRecord record = ref arena.RecordRef(column);
            ReadOnlySpan<byte> values = record.BufferA.Span;
            ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, column);
            switch (record.PType)
            {
                case PType.I8:
                    Sketch<sbyte>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.I16:
                    Sketch<short>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.I32:
                    Sketch<int>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.I64:
                    Sketch<long>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.U8:
                    Sketch<byte>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.U16:
                    Sketch<ushort>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.U32:
                    Sketch<uint>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.U64:
                    Sketch<ulong>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                case PType.F32:
                    Sketch<float>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
                default:
                    Sketch<double>(hour, m, values, valid, count, firsts[d], groupOf, rows);
                    break;
            }
        }

        while (hour.Count > HeldSketchGroups && _sketchDepth >= 0)
        {
            GiveUpSketches(hour.Depth, FormattableString.Invariant($"given up as its groups grew: {hour.Count} groups of sketches in an hour, past {HeldSketchGroups}"));
        }
    }

    /// <summary>
    /// Gives up the tables of sketches of depth <paramref name="depth"/>, for the reason <paramref name="verdict"/>, the
    /// hour's and the object's, and merges the groups held into those of the deepest depth still kept.
    /// </summary>
    private void GiveUpSketches(int depth, string verdict)
    {
        foreach (Candidate? table in (ReadOnlySpan<Candidate?>)[_sketchedHours[depth], _sketchedWhole[depth]])
        {
            if (table is not null)
            {
                Drop(table, verdict);
            }
        }

        _sketchDepth = Math.Min(_sketchDepth, DeepestSketched());
        _hourSketches?.Coarsen(_sketchDepth);
        _wholeSketches?.Coarsen(Math.Min(_wholeSketches.Depth, Deepest(_sketchedWhole)));
    }

    /// <summary>
    /// Adds measure <paramref name="measure"/>'s values to the sketches of their groups: a run of a group's rows at a
    /// time from its first row in <paramref name="firsts"/>, or each row to the group <paramref name="groupOf"/> gives it;
    /// a null left out, as a distribution leaves it out.
    /// </summary>
    private void Sketch<TValue>(
        SketchGroups groups, int measure, ReadOnlySpan<byte> bytes, ReadOnlySpan<ulong> valid, int count, int[] firsts, int[]? groupOf, int rows)
        where TValue : unmanaged, INumberBase<TValue>
    {
        ReadOnlySpan<TValue> values = MemoryMarshal.Cast<byte, TValue>(bytes)[..rows];
        if (groupOf is null)
        {
            for (int g = 0; g < count; g++)
            {
                int start = firsts[g];
                int end = g + 1 < count ? firsts[g + 1] : rows;
                QuantileSketch sketch = groups.Sketch(_sketchPlaces[g], measure);
                if (valid.IsEmpty)
                {
                    sketch.AddAll(values[start..end]);
                    continue;
                }

                for (int row = start; row < end; row++)
                {
                    if ((valid[row >> 6] & (1UL << row)) != 0)
                    {
                        sketch.Add(double.CreateTruncating(values[row]));
                    }
                }
            }

            return;
        }

        for (int row = 0; row < rows; row++)
        {
            if (valid.IsEmpty || (valid[row >> 6] & (1UL << row)) != 0)
            {
                groups.Sketch(_sketchPlaces[groupOf[row]], measure).Add(double.CreateTruncating(values[row]));
            }
        }
    }

    /// <summary>
    /// Ends the hour's sketches: its groups as rows of the hour's tables of sketches, then into the object's groups,
    /// at the depth of its tables still kept.
    /// </summary>
    private async ValueTask CloseSketchesAsync(CancellationToken cancellationToken)
    {
        if (_hourSketches is not { Count: > 0 } hour)
        {
            return;
        }

        if (Deepest(_sketchedHours) >= 0)
        {
            await EmitSketchesAsync(hour, _sketchedHours, _hour, cancellationToken).ConfigureAwait(false);
        }

        int whole = Deepest(_sketchedWhole);
        if (_wholeSketches is { } object_ && whole >= 0)
        {
            object_.Coarsen(Math.Min(whole, hour.Depth));
            object_.MergeFrom(hour);
            while (object_.Count > HeldSketchGroups && object_.Depth >= 0)
            {
                if (_sketchedWhole[object_.Depth] is { } table)
                {
                    Drop(table, FormattableString.Invariant($"given up as its groups grew: {object_.Count} groups of sketches in the object, past {HeldSketchGroups}"));
                }

                object_.Coarsen(Math.Min(object_.Depth - 1, Deepest(_sketchedWhole)));
            }
        }

        hour.Reset();
    }

    /// <summary>
    /// The object's tables of sketches, once its last hour is closed: its groups' rows when its hours are several, none
    /// when it holds one, whose tables of the hour say the same.
    /// </summary>
    private async ValueTask FinishSketchesAsync(CancellationToken cancellationToken)
    {
        if (_hours > 1 && _wholeSketches is { Count: > 0 } whole && Deepest(_sketchedWhole) >= 0)
        {
            await EmitSketchesAsync(whole, _sketchedWhole, 0, cancellationToken).ConfigureAwait(false);
        }
        else if (_hours <= 1)
        {
            foreach (Candidate? table in _sketchedWhole)
            {
                if (table is not null)
                {
                    table.Dead = true;
                    table.Held = -1;
                    table.Stage.Release();
                }
            }
        }

        _hourSketches = null;
        _wholeSketches = null;
    }

    /// <summary>
    /// The rows of <paramref name="groups"/>'s tables of sketches <paramref name="tables"/>, in bucket
    /// <paramref name="bucket"/>: the groups in the order of their keys, each a row at their depth, and at each depth
    /// above it a row for each prefix of the keys, its states and sketches its groups' merged, as it ends.
    /// </summary>
    private async ValueTask EmitSketchesAsync(SketchGroups groups, Candidate?[] tables, long bucket, CancellationToken cancellationToken)
    {
        int depth = groups.Depth;
        int[] order = groups.Sorted();
        long[][] words = new long[depth][];
        QuantileSketch[][] sketches = new QuantileSketch[depth][];
        for (int d = 0; d < depth; d++)
        {
            words[d] = new long[_words];
            StateWords.Clear(words[d], _kinds);
            sketches[d] = Fresh();
        }

        byte[]? last = null;
        foreach (int group in order)
        {
            if (!groups.Holds(group))
            {
                continue;
            }

            byte[] key = groups.Key(group);
            if (last is not null)
            {
                KeyCodec.Compare(_keyShapes, depth, last, key, out int differs);
                for (int d = depth - 1; d >= differs + 1; d--)
                {
                    await EmitAsync(d, last).ConfigureAwait(false);
                }
            }

            if (depth < tables.Length && tables[depth] is { Dead: false } table && HoldSketched(table, bucket, key, groups.Words(group), groups.Sketches(group)))
            {
                await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            }

            if (depth > 0)
            {
                StateWords.Merge(words[depth - 1], groups.Words(group), _kinds);
                Merge(sketches[depth - 1], groups.Sketches(group));
            }

            last = key;
        }

        for (int d = depth - 1; d >= 0 && last is not null; d--)
        {
            await EmitAsync(d, last).ConfigureAwait(false);
        }

        // The group of depth d the next key leaves, named by the prefix of the last key it held.
        async ValueTask EmitAsync(int d, byte[] key)
        {
            int prefix = KeyCodec.Prefix(_keyShapes, d, key);
            if (d < tables.Length && tables[d] is { Dead: false } table && HoldSketched(table, bucket, key.AsSpan(0, prefix), words[d], sketches[d]))
            {
                await FlushAsync(table, cancellationToken).ConfigureAwait(false);
            }

            if (d > 0)
            {
                StateWords.Merge(words[d - 1], words[d], _kinds);
                Merge(sketches[d - 1], sketches[d]);
            }

            StateWords.Clear(words[d], _kinds);
            sketches[d] = Fresh();
        }
    }

    /// <summary>A sketch of each measure, of no value.</summary>
    private QuantileSketch[] Fresh()
    {
        QuantileSketch[] sketches = new QuantileSketch[_measures.Length];
        for (int m = 0; m < sketches.Length; m++)
        {
            sketches[m] = new QuantileSketch(SynopsisTableInfo.SketchBits);
        }

        return sketches;
    }

    private static void Merge(QuantileSketch[] into, QuantileSketch[] from)
    {
        for (int m = 0; m < into.Length; m++)
        {
            into[m].Merge(from[m]);
        }
    }

    /// <summary>Holds a row of a table of sketches, its states and its measures' sketches encoded; whether its stage is full, for the caller to flush.</summary>
    private bool HoldSketched(Candidate table, long bucket, ReadOnlySpan<byte> key, ReadOnlySpan<long> words, QuantileSketch[] sketches)
    {
        if (table.Dead)
        {
            return false;
        }

        bool full = Hold(table, bucket, key, words);
        for (int s = 0; s < table.Sketched.Length; s++)
        {
            _sketchBytes.ResetWrittenCount();
            sketches[table.Sketched[s]].Encode(_sketchBytes);
            table.Stage.Sketch(s, _sketchBytes.WrittenSpan);
        }

        return full;
    }

    /// <summary>
    /// The groups of an hour or of the object at a depth sketched: each one's key, its states, and a sketch of each
    /// measure's values, found by the key's bytes, a float's zeros and NaNs written alike first, as a merge compares them.
    /// </summary>
    private sealed class SketchGroups(MeasureKind[] kinds, int words, int depth, KeyShape[] shapes)
    {
        private Dictionary<byte[], int> _at = new Dictionary<byte[], int>(KeyBytes.Instance);
        private List<byte[]> _keys = [];
        private List<QuantileSketch[]> _sketches = [];
        private long[] _words = [];
        private byte[] _normal = new byte[64];

        /// <summary>The depth of the groups: how many of the keys they hold.</summary>
        internal int Depth { get; private set; } = depth;

        internal int Count => _keys.Count;

        /// <summary>The group of the key <paramref name="key"/>, of <see cref="Depth"/> components: a new one of no row when it has none.</summary>
        internal int Of(ReadOnlySpan<byte> key)
        {
            key = Normal(key);
            Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> lookup = _at.GetAlternateLookup<ReadOnlySpan<byte>>();
            if (lookup.TryGetValue(key, out int group))
            {
                return group;
            }

            group = _keys.Count;
            byte[] held = key.ToArray();
            _at[held] = group;
            _keys.Add(held);
            QuantileSketch[] sketches = new QuantileSketch[kinds.Length];
            for (int m = 0; m < sketches.Length; m++)
            {
                sketches[m] = new QuantileSketch(SynopsisTableInfo.SketchBits);
            }

            _sketches.Add(sketches);
            if (_words.Length < (group + 1) * words)
            {
                Array.Resize(ref _words, Math.Max((group + 1) * words, _words.Length * 2));
            }

            StateWords.Clear(_words.AsSpan(group * words, words), kinds);
            return group;
        }

        internal byte[] Key(int group) => _keys[group];

        internal Span<long> Words(int group) => _words.AsSpan(group * words, words);

        internal QuantileSketch[] Sketches(int group) => _sketches[group];

        internal QuantileSketch Sketch(int group, int measure) => _sketches[group][measure];

        /// <summary>The groups in the order of their keys.</summary>
        internal int[] Sorted()
        {
            int[] order = new int[_keys.Count];
            for (int g = 0; g < order.Length; g++)
            {
                order[g] = g;
            }

            Array.Sort(order, (a, b) => KeyCodec.Compare(shapes, Depth, _keys[a], _keys[b], out _));
            return order;
        }

        /// <summary>Merges the groups of <paramref name="other"/>, as deep or deeper, into these: each into the group of its prefix.</summary>
        internal void MergeFrom(SketchGroups other)
        {
            for (int g = 0; g < other.Count; g++)
            {
                if (!other.Holds(g))
                {
                    continue;
                }

                byte[] key = other._keys[g];
                int into = Of(key.AsSpan(0, KeyCodec.Prefix(shapes, Depth, key)));
                StateWords.Merge(Words(into), other.Words(g), kinds);
                Merge(_sketches[into], other._sketches[g]);
            }
        }

        /// <summary>
        /// Merges the groups into those of their prefixes of <paramref name="depth"/> components, when they are deeper;
        /// forgets them all for a depth below the first, which no table keeps.
        /// </summary>
        internal void Coarsen(int depth)
        {
            if (depth >= Depth)
            {
                return;
            }

            if (depth < 0)
            {
                Clear(depth);
                return;
            }

            SketchGroups deeper = new SketchGroups(kinds, words, Depth, shapes) { _at = _at, _keys = _keys, _sketches = _sketches, _words = _words };
            _at = new Dictionary<byte[], int>(KeyBytes.Instance);
            _keys = [];
            _sketches = [];
            _words = [];
            Depth = depth;
            MergeFrom(deeper);
        }

        /// <summary>Forgets every group, the next ones of <paramref name="depth"/> components.</summary>
        internal void Clear(int depth)
        {
            _at.Clear();
            _keys.Clear();
            _sketches.Clear();
            Depth = depth;
        }

        /// <summary>
        /// Takes every group that holds rows back to none, its key and its sketches' arrays kept, and forgets the others:
        /// an hour's groups are mostly the hour before's, whose sketches had grown to their values, and a group of no row
        /// is no row of a table.
        /// </summary>
        internal void Reset()
        {
            int kept = 0;
            for (int g = 0; g < _keys.Count; g++)
            {
                if (!Holds(g))
                {
                    _at.Remove(_keys[g]);
                    continue;
                }

                if (kept != g)
                {
                    _keys[kept] = _keys[g];
                    _sketches[kept] = _sketches[g];
                    _at[_keys[kept]] = kept;
                }

                StateWords.Clear(Words(kept), kinds);
                foreach (QuantileSketch sketch in _sketches[kept])
                {
                    sketch.Clear();
                }

                kept++;
            }

            _keys.RemoveRange(kept, _keys.Count - kept);
            _sketches.RemoveRange(kept, _sketches.Count - kept);
        }

        /// <summary>Whether group <paramref name="group"/> holds rows: one the groups were reset since holds none.</summary>
        internal bool Holds(int group) => _words[group * words] > 0;

        /// <summary>The key with its float components written as a merge compares them: either zero as +0, every NaN as one.</summary>
        private ReadOnlySpan<byte> Normal(ReadOnlySpan<byte> key)
        {
            bool floats = false;
            for (int k = 0; k < Depth && !floats; k++)
            {
                floats = shapes[k].Form == KeyForm.Float;
            }

            if (!floats)
            {
                return key;
            }

            if (_normal.Length < key.Length)
            {
                _normal = new byte[Math.Max(key.Length, _normal.Length * 2)];
            }

            Span<byte> normal = _normal.AsSpan(0, key.Length);
            key.CopyTo(normal);
            int at = 0;
            for (int k = 0; k < Depth; k++)
            {
                at = KeyCodec.Read(key, at, shapes[k], out bool valid, out ReadOnlySpan<byte> value);
                if (valid && shapes[k].Form == KeyForm.Float)
                {
                    Span<byte> bits = normal.Slice(at - value.Length, value.Length);
                    if (value.Length == sizeof(double))
                    {
                        double number = BitConverter.ToDouble(value);
                        BitConverter.TryWriteBytes(bits, number == 0 ? 0.0 : double.IsNaN(number) ? double.NaN : number);
                    }
                    else
                    {
                        float number = BitConverter.ToSingle(value);
                        BitConverter.TryWriteBytes(bits, number == 0 ? 0f : float.IsNaN(number) ? float.NaN : number);
                    }
                }

            }

            return normal;
        }
    }

    /// <summary>Keys equal by their bytes, looked up by a span of them as well as by an array.</summary>
    private sealed class KeyBytes : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        internal static KeyBytes Instance { get; } = new KeyBytes();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            HashCode hash = default;
            hash.AddBytes(alternate);
            return hash.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}
