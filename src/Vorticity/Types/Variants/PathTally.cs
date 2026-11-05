using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Buffers;
using Vorticity.Indexes;

namespace Vorticity.Types.Variants;

/// <summary>Which groups of an object's rows a question keeps a row of, which it keeps every row of, and what share of the others.</summary>
internal interface IGroupJudge
{
    /// <summary>
    /// Whether the question keeps a row of <paramref name="rows"/>, the object's own, whether it keeps every one, and how many
    /// of them lie among its rows.
    /// </summary>
    (bool Kept, bool Whole, long Held) Judge(RowRange rows);

    /// <summary>
    /// The share of the rows of the groups it keeps in part, <paramref name="part"/> of them, that the question keeps, those
    /// of the groups it keeps whole being <paramref name="whole"/>; 1 when it cannot say.
    /// </summary>
    double Share(long whole, long part);
}

/// <summary>
/// What a question keeps of the groups of rows a path directory names (<see cref="IGroupJudge"/>), judged once a directory
/// before its entries are walked: a bit a group kept and one a group kept whole, their counts and rows, and the share of
/// the rows of the groups kept in part, when no census of them says more.
/// </summary>
internal sealed class GroupVerdicts
{
    private byte[] _kept = [];
    private byte[] _whole = [];

    /// <summary>The object's rows, as the directory says.</summary>
    internal long Rows { get; private set; }

    /// <summary>The rows of a group: the directory's, or every row for a directory whose entries name none.</summary>
    internal long GroupRows { get; private set; }

    /// <summary>The groups, one for a directory whose entries name none.</summary>
    internal int Groups { get; private set; }

    /// <summary>Whether the directory names groups at all.</summary>
    internal bool Grouped { get; private set; }

    /// <summary>Whether the directory left paths out.</summary>
    internal bool Partial { get; private set; }

    /// <summary>The groups the question keeps a row of, and those it keeps every row of.</summary>
    internal int KeptGroups { get; private set; }

    internal int WholeGroups { get; private set; }

    /// <summary>The groups it keeps some rows of but not all.</summary>
    internal int PartGroups => KeptGroups - WholeGroups;

    /// <summary>The object's rows in the groups kept whole, and in the groups kept in part.</summary>
    internal long WholeRows { get; private set; }

    internal long PartRows { get; private set; }

    /// <summary>The share of the rows of the groups kept in part the question keeps; 1 until it is said.</summary>
    internal double Share { get; set; } = 1;

    internal ReadOnlySpan<byte> Kept => _kept.AsSpan(0, Bytes);

    internal ReadOnlySpan<byte> Whole => _whole.AsSpan(0, Bytes);

    private int Bytes => (Groups + 7) / 8;

    /// <summary>Judges the groups of the directory <paramref name="body"/> by <paramref name="judge"/>, in place of any judged before.</summary>
    internal void Judge(VariantDirectory.DirectoryBody body, IGroupJudge judge) => Judge(body.Rows, body.GroupRows, body.Partial, judge);

    /// <summary>Judges the groups of a directory of that head by <paramref name="judge"/>, in place of any judged before.</summary>
    internal void Judge(long rows, long groupRows, bool partial, IGroupJudge judge)
    {
        Rows = rows;
        Partial = partial;
        Grouped = groupRows > 0;
        Groups = Math.Max(VariantDirectory.Groups(rows, groupRows), 1);
        GroupRows = Grouped ? groupRows : Math.Max(rows, 1);
        if (_kept.Length < Bytes)
        {
            // The tally's own, grown and kept: a directory met again judges with nothing made.
            _kept = new byte[Math.Max(Bytes, 2 * _kept.Length)];
            _whole = new byte[_kept.Length];
        }

        _kept.AsSpan(0, Bytes).Clear();
        _whole.AsSpan(0, Bytes).Clear();
        KeptGroups = 0;
        WholeGroups = 0;
        WholeRows = 0;
        PartRows = 0;
        Share = 1;
        for (int g = 0; g < Groups; g++)
        {
            long start = g * GroupRows;
            (bool kept, bool whole, long held) = judge.Judge(new RowRange(start, Math.Max(start, Math.Min(start + GroupRows, Rows))));
            if (!kept)
            {
                continue;
            }

            _kept[g >> 3] |= (byte)(1 << (g & 7));
            KeptGroups++;
            if (whole)
            {
                _whole[g >> 3] |= (byte)(1 << (g & 7));
                WholeGroups++;
                WholeRows += held;
            }
            else
            {
                PartRows += held;
            }
        }
    }

    /// <summary>
    /// The groups kept in part judged again by <paramref name="kept"/>, the rows the question keeps of each group counted, those
    /// of <paramref name="rows"/> alone, the object's rows its scan covers: a group none of whose rows are kept is not kept, one
    /// all of whose rows are, inside the scan's rows, is kept whole, and the others stay kept in part, the share of their rows
    /// kept then exact. What the zone maps cannot prove, a filter on columns whose statistics do not settle it, the rows say.
    /// A group counted -1, whose rows a count could not tell from another's, stays kept in part, out of the share.
    /// </summary>
    internal void Refine(ReadOnlySpan<long> kept, RowRange rows)
    {
        long keptInPart = 0;
        long counted = 0;
        for (int g = 0; g < Groups; g++)
        {
            if (!IsPart(g))
            {
                continue;
            }

            RowRange group = new RowRange(g * GroupRows, Math.Max(g * GroupRows, Math.Min((g + 1) * GroupRows, Rows)));
            long held = group.Intersect(rows).Length;
            if (kept[g] < 0)
            {
                continue;
            }

            if (kept[g] == 0)
            {
                _kept[g >> 3] &= (byte)~(1 << (g & 7));
                KeptGroups--;
                PartRows -= held;
            }
            else if (kept[g] == group.Length)
            {
                _whole[g >> 3] |= (byte)(1 << (g & 7));
                WholeGroups++;
                PartRows -= held;
                WholeRows += held;
            }
            else
            {
                keptInPart += kept[g];
                counted += held;
            }
        }

        Share = counted > 0 ? Math.Clamp(keptInPart / (double)counted, 0, 1) : 1;
    }

    /// <summary>
    /// Whether a census of the rows of the groups kept in part costs little enough to take: they are at most four groups,
    /// the two edges of two runs of rows a key range keeps, or hold at most a quarter of the rows of the groups kept whole.
    /// A filter on a column the rows are not sorted by keeps most groups in part, whose census would read the window's
    /// variants whole, what <see cref="PathsOptions.Exact"/> asks for and the default answer does not.
    /// </summary>
    internal bool EdgesWorthCounting => PartGroups > 0 && (PartGroups <= 4 || PartRows <= WholeRows / 4);

    /// <summary>Whether group <paramref name="group"/> is kept in part.</summary>
    internal bool IsPart(int group) =>
        (uint)group < (uint)Groups && (_kept[group >> 3] & ~_whole[group >> 3] & (1 << (group & 7))) != 0;

    /// <summary>Whether <paramref name="other"/> judged the same groups the same way: the census of one's edges is the other's.</summary>
    internal bool SameAs(GroupVerdicts other) =>
        Rows == other.Rows && GroupRows == other.GroupRows && Groups == other.Groups
        && Kept.SequenceEqual(other.Kept) && Whole.SequenceEqual(other.Whole);
}

/// <summary>
/// The paths of a variant column among the rows a question keeps of the groups it keeps in part, counted, and among those
/// it leaves of them: what a directory's counts are set against there, path by path and class by class.
/// </summary>
internal sealed class EdgeCounts
{
    private readonly Dictionary<byte[], Edge> _paths = new Dictionary<byte[], Edge>(ByteKeys.Instance);
    private readonly Dictionary<byte[], Edge>.AlternateLookup<ReadOnlySpan<byte>> _byBytes;
    private readonly PathKey _key = new PathKey();

    internal EdgeCounts() => _byBytes = _paths.GetAlternateLookup<ReadOnlySpan<byte>>();

    /// <summary>Whether a census stopped counting keys: a path the rows held may be missing.</summary>
    internal bool Partial { get; private set; }

    /// <summary>The paths and classes counted.</summary>
    internal int Count => _paths.Count;

    /// <summary>Adds the counts of <paramref name="kept"/>, a census of the rows kept, and of <paramref name="left"/>, one of the others.</summary>
    internal void Add(PathCensus kept, PathCensus left)
    {
        Partial |= kept.Partial || left.Partial;
        _key.Walk(kept.Root, 0, (rows, length) => Add(_key.Span(length), rows, 0));
        _key.Walk(left.Root, 0, (rows, length) => Add(_key.Span(length), 0, rows));
    }

    /// <summary>Adds <paramref name="kept"/> rows kept and <paramref name="left"/> others to the path and class <paramref name="key"/> names.</summary>
    internal void Add(ReadOnlySpan<byte> key, long kept, long left)
    {
        ref Edge edge = ref CollectionsMarshal.GetValueRefOrAddDefault(_byBytes, key, out _);
        edge.Kept += kept;
        edge.Left += left;
    }

    /// <summary>The rows of the path <paramref name="key"/> names, as a tally holds it, kept and left; marked met.</summary>
    internal (long Kept, long Left) Meet(ReadOnlySpan<byte> key)
    {
        ref Edge edge = ref CollectionsMarshal.GetValueRefOrNullRef(_byBytes, key);
        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref edge))
        {
            return (0, 0);
        }

        edge.Met = true;
        return (edge.Kept, edge.Left);
    }

    /// <summary>Each path and class counted, as a tally holds it, with its rows kept and left.</summary>
    internal IEnumerable<(byte[] Key, long Kept, long Left)> Entries()
    {
        foreach (KeyValuePair<byte[], Edge> path in _paths)
        {
            yield return (path.Key, path.Value.Kept, path.Value.Left);
        }
    }

    /// <summary>The paths kept rows held that no entry met, each with its rows: those a directory that left paths out did not name.</summary>
    internal IEnumerable<(byte[] Key, long Kept)> Unmet()
    {
        foreach (KeyValuePair<byte[], Edge> path in _paths)
        {
            if (!path.Value.Met && path.Value.Kept > 0)
            {
                yield return (path.Key, path.Value.Kept);
            }
        }
    }

    private struct Edge
    {
        internal long Kept;
        internal long Left;
        internal bool Met;
    }
}

/// <summary>
/// A path and class as bytes, built in place: a length and the UTF-8 bytes of each key, seven bits a byte so that no two paths
/// are the same bytes, then the class.
/// </summary>
internal sealed class PathKey
{
    private byte[] _bytes = new byte[256];

    /// <summary>The first <paramref name="length"/> bytes built.</summary>
    internal ReadOnlySpan<byte> Span(int length) => _bytes.AsSpan(0, length);

    /// <summary>The key of <paramref name="entry"/>'s path and class; its length.</summary>
    internal int Of(in DirectoryEntry entry)
    {
        int length = 0;
        for (int k = 0; k < entry.Depth; k++)
        {
            ReadOnlySpan<byte> key = entry.Key(k);
            length = Room(length, key.Length);
            key.CopyTo(_bytes.AsSpan(length));
            length += key.Length;
        }

        return Close(length, entry.Class);
    }

    /// <summary>The keys built so far, their <paramref name="length"/> first bytes, followed by <paramref name="key"/>; the new length.</summary>
    internal int Append(int length, ReadOnlySpan<byte> key)
    {
        length = Room(length, key.Length);
        key.CopyTo(_bytes.AsSpan(length));
        return length + key.Length;
    }

    /// <summary>The keys built so far, their <paramref name="length"/> first bytes, closed by <paramref name="class"/>; the key's length.</summary>
    internal int Close(int length, PathClass @class)
    {
        Ensure(length + 1);
        _bytes[length] = (byte)@class;
        return length + 1;
    }

    /// <summary>Hands <paramref name="add"/> the rows of each path and class <paramref name="count"/> holds, below the key built so far, and the key's length.</summary>
    internal void Walk(PathCensus.PathCount count, int length, Action<long, int> add)
    {
        for (PathClass @class = PathClass.Null; @class <= PathClass.Other; @class++)
        {
            long rows = count.Read(@class);
            if (rows > 0)
            {
                add(rows, Close(length, @class));
            }
        }

        foreach (KeyValuePair<string, PathCensus.PathCount> key in count.Keys)
        {
            int byteCount = Encoding.UTF8.GetByteCount(key.Key);
            int at = Room(length, byteCount);
            at += Encoding.UTF8.GetBytes(key.Key, _bytes.AsSpan(at));
            Walk(key.Value, at, add);
        }
    }

    /// <summary>
    /// Room in the key after its <paramref name="length"/> first bytes for a key of <paramref name="bytes"/> bytes, its length
    /// written there first: where the key's bytes go.
    /// </summary>
    private int Room(int length, int bytes)
    {
        Ensure(length + 5 + bytes + 1);
        uint left = (uint)bytes;
        while (left >= 0x80)
        {
            _bytes[length++] = (byte)(left | 0x80);
            left >>= 7;
        }

        _bytes[length++] = (byte)left;
        return length;
    }

    private void Ensure(int most)
    {
        if (_bytes.Length < most)
        {
            Array.Resize(ref _bytes, Math.Max(most, 2 * _bytes.Length));
        }
    }
}

/// <summary>
/// The paths of a variant column found among the rows a question keeps, with their rows by class: the union of what the
/// path directories of its objects say of the groups of rows it keeps, of what censuses of the rows it keeps of the groups
/// it keeps in part counted, and of what censuses counted of the rows it keeps where no directory answers. A path is held
/// once, its keys as bytes, and becomes strings once, when the answer is made: a directory adds its thousands of entries
/// with nothing made of those met before.
/// </summary>
internal sealed class PathTally
{
    // Each path and class (PathKey); its rows, and whether they are exact.
    private readonly Dictionary<byte[], Tallied> _paths = new Dictionary<byte[], Tallied>(ByteKeys.Instance);
    private readonly Dictionary<byte[], Tallied>.AlternateLookup<ReadOnlySpan<byte>> _byBytes;
    private readonly PathKey _key = new PathKey();

    internal PathTally() => _byBytes = _paths.GetAlternateLookup<ReadOnlySpan<byte>>();

    /// <summary>Whether a directory that left paths out, or a census past its keys, answered for some rows: a path may be missing.</summary>
    internal bool Partial { get; private set; }

    /// <summary>The paths and classes tallied.</summary>
    internal int Count => _paths.Count;

    /// <summary>The verdicts of the directory being added, the tally's own: judged (<see cref="GroupVerdicts.Judge(VariantDirectory.DirectoryBody, IGroupJudge)"/>), then added.</summary>
    internal GroupVerdicts Verdicts { get; } = new GroupVerdicts();

    /// <summary>
    /// Adds what the path directory region <paramref name="region"/> says of the groups of its object's rows
    /// <paramref name="judge"/> keeps, as <see cref="AddDirectory(VariantDirectory.DirectoryBody, EdgeCounts?, bool)"/> does
    /// with no census of the groups kept in part.
    /// </summary>
    /// <returns>Whether the directory was taken; false, with nothing added, when it does not decode, or <paramref name="exact"/> refuses it.</returns>
    internal bool AddDirectory(ReadOnlySpan<byte> region, IGroupJudge judge, bool exact)
    {
        // Judged at its head and walked in one visit, nothing made: a directory met again allocates nothing.
        Judging judging = new Judging(this, judge, exact);
        return VariantDirectory.Visit(region, ref judging) && !judging.Refused;
    }

    /// <summary>A directory judged at its head (<see cref="GroupVerdicts.Judge(long, long, bool, IGroupJudge)"/>), then its entries walked into the tally.</summary>
    private struct Judging(PathTally tally, IGroupJudge judge, bool exact) : IDirectoryVisitor
    {
        private bool _taken;

        /// <summary>Whether the directory was refused: it would estimate, or it left paths out, and only exact rows were asked for.</summary>
        internal bool Refused { get; private set; }

        public void Head(long rows, long groupRows, bool partial)
        {
            GroupVerdicts verdicts = tally.Verdicts;
            verdicts.Judge(rows, groupRows, partial, judge);
            verdicts.Share = judge.Share(verdicts.WholeRows, verdicts.PartRows);
            Refused = exact && (verdicts.Partial || verdicts.PartGroups > 0);
            _taken = !Refused && verdicts.KeptGroups > 0;
            tally.Partial |= _taken && verdicts.Partial;
        }

        public readonly void Entry(in DirectoryEntry entry)
        {
            if (_taken)
            {
                new Grouped(tally, tally.Verdicts, null).Entry(in entry);
            }
        }
    }

    /// <summary>
    /// Adds what the directory <paramref name="body"/> says of the groups of its object's rows <see cref="Verdicts"/> judged
    /// kept. An entry's rows are exact where they lie in groups kept whole, and in groups kept in part when
    /// <paramref name="edges"/> counted the rows kept there; the rows it holds in groups kept whole are its values spread
    /// evenly over the groups it names outside those counted, and with no census of the groups kept in part, its values in
    /// them are spread too, in the share of their rows kept. A path that no kept row holds by the census of the groups kept
    /// in part, and that no group kept whole holds, is not listed.
    /// </summary>
    /// <param name="body">The directory, opened.</param>
    /// <param name="edges">The census of the rows of the groups kept in part, kept and left; null when they were not counted.</param>
    /// <param name="exact">Whether only exact rows are taken: a directory that would estimate, or left paths out, is not.</param>
    /// <returns>Whether the directory was taken; false, with nothing added, when <paramref name="exact"/> refuses it.</returns>
    internal bool AddDirectory(VariantDirectory.DirectoryBody body, EdgeCounts? edges, bool exact)
    {
        GroupVerdicts verdicts = Verdicts;
        if (exact && (verdicts.Partial || verdicts.PartGroups > 0))
        {
            return false;
        }

        if (verdicts.KeptGroups == 0)
        {
            return true;
        }

        Partial |= verdicts.Partial || edges?.Partial == true;
        Grouped grouped = new Grouped(this, verdicts, edges);
        body.Visit(ref grouped);
        if (edges is not null && (verdicts.Partial || edges.Partial))
        {
            // The paths the directory left out that kept rows of the groups counted hold: their rows there, and what groups kept
            // whole hold of them unknown.
            foreach ((byte[] key, long kept) in edges.Unmet())
            {
                Add(key, kept, exact: false);
            }
        }

        return true;
    }

    /// <summary>Adds what <paramref name="census"/> counted: each path and class the rows held, exactly; partial when it stopped counting keys.</summary>
    internal void AddCensus(PathCensus census)
    {
        Partial |= census.Partial;
        _key.Walk(census.Root, 0, (rows, length) => Add(_key.Span(length), rows, exact: true));
    }

    /// <summary>Adds what <paramref name="other"/> tallied: each path and class's rows, exact where both were.</summary>
    internal void Merge(PathTally other)
    {
        Partial |= other.Partial;
        foreach (KeyValuePair<byte[], Tallied> path in other._paths)
        {
            ref Tallied held = ref CollectionsMarshal.GetValueRefOrAddDefault(_paths, path.Key, out bool exists);
            held.Rows += path.Value.Rows;
            held.Exact = exists ? held.Exact && path.Value.Exact : path.Value.Exact;
        }
    }

    /// <summary>The path and class <paramref name="key"/> names, with its rows added.</summary>
    private void Add(ReadOnlySpan<byte> key, double rows, bool exact)
    {
        ref Tallied held = ref CollectionsMarshal.GetValueRefOrAddDefault(_byBytes, key, out bool exists);
        held.Rows += rows;
        held.Exact = exists ? held.Exact && exact : exact;
    }

    /// <summary>The paths of <paramref name="column"/> tallied: their keys as strings, ordinal, then their class.</summary>
    internal VariantPaths ToPaths(string column, PathsReading? reading = null)
    {
        VariantPath[] paths = new VariantPath[_paths.Count];
        int at = 0;
        foreach (KeyValuePair<byte[], Tallied> path in _paths)
        {
            byte[] key = path.Key;
            List<string> keys = [];
            int i = 0;
            while (i < key.Length - 1)
            {
                int length = 0;
                for (int shift = 0; ; shift += 7)
                {
                    byte b = key[i++];
                    length |= (b & 0x7F) << shift;
                    if (b < 0x80)
                    {
                        break;
                    }
                }

                keys.Add(Encoding.UTF8.GetString(key, i, length));
                i += length;
            }

            // An estimate is of a path some kept group holds: a row at least.
            long rows = path.Value.Exact ? (long)path.Value.Rows : Math.Max(1, (long)Math.Round(path.Value.Rows));
            paths[at++] = new VariantPath(keys.ToArray(), (VariantPathClass)key[^1], rows, path.Value.Exact);
        }

        Array.Sort(paths, static (a, b) =>
        {
            int common = Math.Min(a.Keys.Count, b.Keys.Count);
            for (int k = 0; k < common; k++)
            {
                int order = string.CompareOrdinal(a.Keys[k], b.Keys[k]);
                if (order != 0)
                {
                    return order;
                }
            }

            return a.Keys.Count != b.Keys.Count ? a.Keys.Count.CompareTo(b.Keys.Count) : a.Class.CompareTo(b.Class);
        });
        return new VariantPaths(column, paths, complete: !Partial, reading ?? new PathsReading(0, 0, 0, 0, 0, 0));
    }

    /// <summary>A path and class's rows, summed over the objects, and whether every one of them was exact.</summary>
    private struct Tallied
    {
        internal double Rows;
        internal bool Exact;
    }

    /// <summary>A directory's entries walked as they lie into the tally, by the verdicts on its groups and the census of its edges.</summary>
    private readonly struct Grouped(PathTally tally, GroupVerdicts verdicts, EdgeCounts? edges) : IDirectoryVisitor
    {
        public void Head(long rows, long groupRows, bool partial)
        {
        }

        public void Entry(in DirectoryEntry entry)
        {
            (int held, int heldKept, int heldWhole) = !entry.HasGroups || verdicts.Groups == 1
                ? (verdicts.Groups, verdicts.KeptGroups, verdicts.WholeGroups)
                : Overlap(entry.Groups, verdicts.Kept, verdicts.Whole);
            if (heldKept == 0)
            {
                return;
            }

            int heldPart = heldKept - heldWhole;
            int length = tally._key.Of(in entry);
            double rows;
            bool exact;
            if (edges is not null && heldPart > 0)
            {
                // Its rows in the groups kept in part counted, kept and left; its values elsewhere spread over the groups it names
                // that were not counted, those kept whole among them, exact when it names no group the question leaves.
                (long kept, long left) = edges.Meet(tally._key.Span(length));
                int outside = held - heldPart;
                if (heldWhole == 0)
                {
                    if (kept == 0)
                    {
                        return;
                    }

                    (rows, exact) = (kept, true);
                }
                else
                {
                    rows = kept + (Math.Max(0, entry.Values - kept - left) * (double)heldWhole / outside);
                    exact = heldWhole == outside;
                }
            }
            else if (edges is not null)
            {
                // In no group kept in part: its values spread over the groups it names, those kept whole counted.
                (rows, exact) = heldWhole == held ? (entry.Values, true) : (entry.Values * (double)heldWhole / held, false);
            }
            else
            {
                // Its values spread evenly over the groups it names, those of a group kept whole counted, and of a group kept in
                // part the share of its rows the question keeps.
                exact = heldWhole == held;
                rows = exact ? entry.Values : entry.Values * (heldWhole + (verdicts.Share * heldPart)) / held;
            }

            tally.Add(tally._key.Span(length), rows, exact);
        }

        /// <summary>The groups <paramref name="groups"/> names, those of them kept, and those of them kept whole.</summary>
        private static (int Held, int Kept, int Whole) Overlap(ReadOnlySpan<byte> groups, ReadOnlySpan<byte> kept, ReadOnlySpan<byte> whole)
        {
            int held = 0;
            int heldKept = 0;
            int heldWhole = 0;
            int words = groups.Length / sizeof(ulong);
            ReadOnlySpan<ulong> g = MemoryMarshal.Cast<byte, ulong>(groups[..(words * sizeof(ulong))]);
            ReadOnlySpan<ulong> k = MemoryMarshal.Cast<byte, ulong>(kept[..(words * sizeof(ulong))]);
            ReadOnlySpan<ulong> w = MemoryMarshal.Cast<byte, ulong>(whole[..(words * sizeof(ulong))]);
            for (int i = 0; i < g.Length; i++)
            {
                held += BitOperations.PopCount(g[i]);
                heldKept += BitOperations.PopCount(g[i] & k[i]);
                heldWhole += BitOperations.PopCount(g[i] & w[i]);
            }

            for (int i = words * sizeof(ulong); i < groups.Length; i++)
            {
                held += BitOperations.PopCount((uint)groups[i]);
                heldKept += BitOperations.PopCount((uint)(groups[i] & kept[i]));
                heldWhole += BitOperations.PopCount((uint)(groups[i] & whole[i]));
            }

            return (held, heldKept, heldWhole);
        }
    }
}
