using System;
using System.Buffers;
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
/// The paths of a variant column found among the rows a question keeps, with their rows by class: the union of what the
/// path directories of its objects say of the groups of rows it keeps, and of what censuses counted of the rows it keeps.
/// A path is held once, its keys as bytes, and becomes strings once, when the answer is made: a directory adds its
/// thousands of entries with nothing made of those met before.
/// </summary>
internal sealed class PathTally
{
    // Each path and class: a length and the UTF-8 bytes of each of its keys, then the class; its rows, and whether they are exact.
    private readonly Dictionary<byte[], Tallied> _paths = new Dictionary<byte[], Tallied>(ByteKeys.Instance);
    private readonly Dictionary<byte[], Tallied>.AlternateLookup<ReadOnlySpan<byte>> _byBytes;

    // The key being built.
    private byte[] _key = new byte[256];

    internal PathTally() => _byBytes = _paths.GetAlternateLookup<ReadOnlySpan<byte>>();

    /// <summary>Whether a directory that left paths out, or a census past its keys, answered for some rows: a path may be missing.</summary>
    internal bool Partial { get; private set; }

    /// <summary>The paths and classes tallied.</summary>
    internal int Count => _paths.Count;

    /// <summary>
    /// Adds what the path directory region <paramref name="region"/> says of the groups of its object's rows
    /// <paramref name="judge"/> keeps: each entry that names a group kept, its rows exact when every group it names is kept
    /// whole, in proportion to the groups kept among them otherwise.
    /// </summary>
    /// <param name="region">The directory's region, as the object stores it.</param>
    /// <param name="judge">What the question keeps of the object's rows.</param>
    /// <param name="exact">Whether only exact rows are taken: a directory that would estimate, or left paths out, is not.</param>
    /// <returns>Whether the directory was taken; false, with nothing added, when it does not decode, or <paramref name="exact"/> refuses it.</returns>
    internal bool AddDirectory(ReadOnlySpan<byte> region, IGroupJudge judge, bool exact)
    {
        Grouped grouped = new Grouped(this, judge, exact);
        try
        {
            return VariantDirectory.Visit(region, ref grouped) && !grouped.Refused;
        }
        finally
        {
            grouped.Release();
        }
    }

    /// <summary>Adds what <paramref name="census"/> counted: each path and class the rows held, exactly; partial when it stopped counting keys.</summary>
    internal void AddCensus(PathCensus census)
    {
        Partial |= census.Partial;
        Walk(census.Root, 0);
    }

    private void Walk(PathCensus.PathCount count, int length)
    {
        for (PathClass @class = PathClass.Null; @class <= PathClass.Other; @class++)
        {
            long rows = count.Read(@class);
            if (rows > 0)
            {
                Add(length, @class, rows, exact: true);
            }
        }

        foreach (KeyValuePair<string, PathCensus.PathCount> key in count.Keys)
        {
            int byteCount = Encoding.UTF8.GetByteCount(key.Key);
            int at = Room(length, byteCount);
            at += Encoding.UTF8.GetBytes(key.Key, _key.AsSpan(at));
            Walk(key.Value, at);
        }
    }

    /// <summary>The path's keys from <paramref name="entry"/>, as this tally holds them, and its rows added.</summary>
    private void Add(in DirectoryEntry entry, double rows, bool exact)
    {
        int length = 0;
        for (int k = 0; k < entry.Depth; k++)
        {
            ReadOnlySpan<byte> key = entry.Key(k);
            length = Room(length, key.Length);
            key.CopyTo(_key.AsSpan(length));
            length += key.Length;
        }

        Add(length, entry.Class, rows, exact);
    }

    /// <summary>The key built so far, its <paramref name="length"/> first bytes, closed by <paramref name="class"/>, and its rows added.</summary>
    private void Add(int length, PathClass @class, double rows, bool exact)
    {
        Ensure(length + 1);
        _key[length] = (byte)@class;
        ref Tallied held = ref CollectionsMarshal.GetValueRefOrAddDefault(_byBytes, _key.AsSpan(0, length + 1), out bool exists);
        held.Rows += rows;
        held.Exact = exists ? held.Exact && exact : exact;
    }

    /// <summary>
    /// Room in the key after its <paramref name="length"/> first bytes for a key of <paramref name="bytes"/> bytes, its length
    /// written there first, seven bits a byte, so that no two paths are the same bytes: where the key's bytes go.
    /// </summary>
    private int Room(int length, int bytes)
    {
        Ensure(length + 5 + bytes + 1);
        uint left = (uint)bytes;
        while (left >= 0x80)
        {
            _key[length++] = (byte)(left | 0x80);
            left >>= 7;
        }

        _key[length++] = (byte)left;
        return length;
    }

    /// <summary>Room in the key for <paramref name="most"/> bytes.</summary>
    private void Ensure(int most)
    {
        if (_key.Length < most)
        {
            Array.Resize(ref _key, Math.Max(most, 2 * _key.Length));
        }
    }

    /// <summary>The paths of <paramref name="column"/> tallied: their keys as strings, ordinal, then their class.</summary>
    internal VariantPaths ToPaths(string column)
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

            // An estimate is of a path some kept granule holds: a row at least.
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
        return new VariantPaths(column, paths, complete: !Partial);
    }

    /// <summary>A path and class's rows, summed over the objects, and whether every one of them was exact.</summary>
    private struct Tallied
    {
        internal double Rows;
        internal bool Exact;
    }

    /// <summary>
    /// A directory walked as it lies into the tally: its head judged group by group, a bit a group kept and one a group kept
    /// whole, then each entry of a group kept added: exact when every group it names is kept whole; otherwise estimated, its
    /// values spread evenly over the groups it names, those of a group kept whole counted, and of a group kept in part the
    /// share of its rows the question keeps.
    /// </summary>
    private struct Grouped(PathTally tally, IGroupJudge judge, bool exact) : IDirectoryVisitor
    {
        // A bit a group the question keeps a row of, and one a group it keeps whole, rented; the groups, one for a directory
        // whose entries name none, those kept and those kept whole; and the share of the rows of a group kept in part it keeps.
        private byte[]? _kept;
        private byte[]? _whole;
        private int _bytes;
        private int _groups;
        private int _keptGroups;
        private int _wholeGroups;
        private double _share;

        /// <summary>Whether the directory was refused: it would estimate, or it left paths out, and only exact rows were asked for.</summary>
        internal bool Refused { get; private set; }

        public void Head(long rows, long groupRows, bool partial)
        {
            _groups = Math.Max(VariantDirectory.Groups(rows, groupRows), 1);
            long size = groupRows > 0 ? groupRows : Math.Max(rows, 1);
            _bytes = (_groups + 7) / 8;
            _kept = ArrayPool<byte>.Shared.Rent(_bytes);
            _whole = ArrayPool<byte>.Shared.Rent(_bytes);
            _kept.AsSpan(0, _bytes).Clear();
            _whole.AsSpan(0, _bytes).Clear();
            long wholeRows = 0;
            long partRows = 0;
            for (int g = 0; g < _groups; g++)
            {
                long start = g * size;
                (bool kept, bool whole, long held) = judge.Judge(new RowRange(start, Math.Max(start, Math.Min(start + size, rows))));
                if (!kept)
                {
                    continue;
                }

                _kept[g >> 3] |= (byte)(1 << (g & 7));
                _keptGroups++;
                if (whole)
                {
                    _whole[g >> 3] |= (byte)(1 << (g & 7));
                    _wholeGroups++;
                    wholeRows += held;
                }
                else
                {
                    partRows += held;
                }
            }

            Refused = exact && (partial || _wholeGroups != _keptGroups);
            _share = judge.Share(wholeRows, partRows);
            if (!Refused && partial && _keptGroups > 0)
            {
                tally.Partial = true;
            }
        }

        public void Entry(in DirectoryEntry entry)
        {
            if (Refused || _keptGroups == 0)
            {
                return;
            }

            (int held, int heldKept, int heldWhole) = !entry.HasGroups || _groups == 1
                ? (_groups, _keptGroups, _wholeGroups)
                : Overlap(entry.Groups, _kept.AsSpan(0, _bytes), _whole.AsSpan(0, _bytes));
            if (heldKept == 0)
            {
                return;
            }

            bool whole = heldWhole == held;
            tally.Add(in entry, whole ? entry.Values : entry.Values * (heldWhole + (_share * (heldKept - heldWhole))) / held, whole);
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

        /// <summary>The bits back to the pool.</summary>
        internal readonly void Release()
        {
            if (_kept is not null)
            {
                ArrayPool<byte>.Shared.Return(_kept);
            }

            if (_whole is not null)
            {
                ArrayPool<byte>.Shared.Return(_whole);
            }
        }
    }
}
